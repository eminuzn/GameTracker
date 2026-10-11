using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace GameTracker.Infrastructure;

public sealed record PrefetchExecution(string ExecutableName, string ExecutablePath, DateTimeOffset LastRunAt);

// Read only embedded execution timestamps, never filesystem modification/access
// dates. Layout reference: libyal/libscca Windows Prefetch File (PF) format.
public static class WindowsPrefetch
{
    public const int MaxFileBytes = 4 * 1024 * 1024;
    public static DateTimeOffset? FindLatest(IReadOnlyList<string> executables, DateTimeOffset now, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var volumes = new List<(uint Serial, string RelativePath)>();
        foreach (var path in executables)
        {
            ct.ThrowIfCancellationRequested(); paths.Add(path);
            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 2); // VOLUME_NAME_NT
                if (length > 0 && length < buffer.Capacity) paths.Add(buffer.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var root = new StringBuilder(32768);
            if (GetVolumePathName(path, root, (uint)root.Capacity)
                && GetVolumeInformation(root.ToString(), null, 0, out var serial, out _, out _, null, 0)
                && path.StartsWith(root.ToString(), StringComparison.OrdinalIgnoreCase))
                volumes.Add((serial, "\\" + path[root.Length..]));
        }
        return ReadLatest(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"),
            paths.Select(HeaderName).ToHashSet(StringComparer.OrdinalIgnoreCase),
            p => paths.Contains(p) || volumes.Any(v => MatchesVolumePath(p, v.RelativePath, v.Serial)), now, ct);
    }

    public static DateTimeOffset? FindLatestInDirectory(string directory, IEnumerable<string> executablePaths, DateTimeOffset now, CancellationToken ct)
    {
        var paths = executablePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return null;
        return ReadLatest(directory, paths.Select(HeaderName).ToHashSet(StringComparer.OrdinalIgnoreCase), paths.Contains, now, ct);
    }

    // Windows 10/11 also records paths using volume creation time + serial,
    // instead of drive letters or NT device names. Require the current volume's
    // serial AND full relative path; a suffix-only match is insufficient.
    public static bool MatchesVolumePath(string recordedPath, string relativePath, uint serial)
    {
        if (!recordedPath.StartsWith("\\VOLUME{", StringComparison.OrdinalIgnoreCase)) return false;
        var end = recordedPath.IndexOf('}');
        var dash = recordedPath.IndexOf('-', 8);
        return dash > 8 && end == dash + 9
            && uint.TryParse(recordedPath.AsSpan(dash + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var recordedSerial)
            && recordedSerial == serial && recordedPath[(end + 1)..].Equals(relativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ReadLatest(string directory, HashSet<string> names, Func<string, bool> matches, DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset? latest = null;
        // A missing or inaccessible system directory is handled by the optional
        // import caller; one corrupt/locked record must not discard other matches.
        foreach (var file in Directory.EnumerateFiles(directory, "*.pf"))
        {
            ct.ThrowIfCancellationRequested();
            var filename = Path.GetFileNameWithoutExtension(file);
            var separator = filename.LastIndexOf('-');
            if (separator < 1 || !names.Contains(filename[..separator])) continue;
            try
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length < 8 || input.Length > MaxFileBytes) continue;
                var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
                if (Parse(bytes, now) is { } entry && matches(entry.ExecutablePath) && (latest is null || entry.LastRunAt > latest)) latest = entry.LastRunAt;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return latest;
    }

    private static string HeaderName(string path)
    {
        var name = path.Replace('\\', '/').Split('/').Last();
        return name[..Math.Min(29, name.Length)];
    }

    public static PrefetchExecution? Parse(byte[] bytes, DateTimeOffset now)
    {
        if (bytes.Length < 8 || bytes.Length > MaxFileBytes) return null;
        if (bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 77, 65, 77, 4 }))
        {
            if (!OperatingSystem.IsWindows()) return null;
            var decompressed = Decompress(bytes);
            if (decompressed is null) return null;
            bytes = decompressed;
        }
        if (bytes.Length < 152 || !bytes.AsSpan(4, 4).SequenceEqual("SCCA"u8)) return null;
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        var version = U32(0);
        var minimumSize = version switch { 17 => 152, 23 => 240, 26 => 304, 30 or 31 => 296, _ => 0 };
        if (minimumSize == 0 || bytes.Length < minimumSize || U32(12) != bytes.Length) return null;
        var executableName = Encoding.Unicode.GetString(bytes, 16, 60).Split('\0')[0];
        if (string.IsNullOrWhiteSpace(executableName)) return null;
        var stringsOffset = U32(100); var stringsSize = U32(104);
        if (stringsOffset < minimumSize || (stringsSize & 1) != 0 || stringsOffset > bytes.Length || stringsSize > bytes.Length - stringsOffset) return null;
        var names = Encoding.Unicode.GetString(bytes, (int)stringsOffset, (int)stringsSize).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var executablePaths = names.Where(p => HeaderName(p).Equals(executableName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Matching only an EXE basename can attribute another installation's run
        // to this game. Multiple full-path identities are deliberately ambiguous.
        if (executablePaths.Length != 1) return null;
        DateTimeOffset? latest = null;
        var timestampOffset = version == 17 ? 120 : 128;
        var count = version is 17 or 23 ? 1 : 8;
        for (var i = 0; i < count; i++)
        {
            var rawTime = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(timestampOffset + i * 8, 8));
            if (rawTime <= 0) continue;
            try
            {
                var time = DateTimeOffset.FromFileTime(rawTime).ToUniversalTime();
                if (time >= DateTimeOffset.UnixEpoch && time <= now && (latest is null || time > latest)) latest = time;
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return latest is { } value ? new(executableName, executablePaths[0], value) : null;
    }

    private static byte[]? Decompress(byte[] bytes)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (size < 152 || size > MaxFileBytes) return null;
        if (RtlGetCompressionWorkSpaceSize(4, out _, out var workspaceSize) != 0 || workspaceSize > MaxFileBytes) return null;
        var workspace = Marshal.AllocHGlobal((int)workspaceSize);
        try
        {
            var result = new byte[(int)size]; var compressed = bytes.AsSpan(8).ToArray();
            return RtlDecompressBufferEx(4, result, size, compressed, (uint)compressed.Length, out var finalSize, workspace) == 0 && finalSize == size ? result : null;
        }
        finally { Marshal.FreeHGlobal(workspace); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathName(string path, StringBuilder root, uint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformation(string root, StringBuilder? volumeName, uint volumeNameSize,
        out uint serial, out uint componentLength, out uint flags, StringBuilder? fileSystemName, uint fileSystemNameSize);
    [DllImport("ntdll.dll")] private static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferSize, out uint fragmentSize);
    [DllImport("ntdll.dll")] private static extern int RtlDecompressBufferEx(ushort format, [Out] byte[] output, uint outputSize, byte[] input, uint inputSize, out uint finalSize, IntPtr workspace);
}
