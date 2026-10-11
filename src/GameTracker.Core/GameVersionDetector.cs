using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameTracker.Core;

public sealed record LocalGameVersion(string Value, string Source, LocalVersionKind Kind);

public static partial class GameVersionDetector
{
    // Only game-owned files next to the selected executable or in its parent
    // directories are read. No registry, store manifest or install dependency.
    public static LocalGameVersion? Detect(Game game, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PathPolicy.ValidateGame(game);
        var exe = game.LaunchExe is null ? null : PathPolicy.ResolveExe(game, game.LaunchExe);
        var directories = new List<string>();
        LocalGameVersion? buildVersion = null;
        for (var dir = exe is null ? game.FolderPath : Path.GetDirectoryName(exe)!; ; dir = Path.GetDirectoryName(dir)!)
        {
            directories.Add(dir);
            if (PathPolicy.Equal(dir, game.FolderPath)) break;
        }
        // Explicit release files take priority over often generic PE resources.
        foreach (var dir in directories.AsEnumerable().Reverse())
        foreach (var name in new[] { "version.txt", "game.version", "product.version", "version.json", "build.txt" })
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(dir, name);
            try
            {
                if (!File.Exists(path)) continue;
                PathPolicy.EnsureNoLinks(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length > 16_384) continue;
                using var reader = new StreamReader(input);
                // Bound reads even if another process grows the file after opening.
                var buffer = new char[16_385]; var count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (count == buffer.Length) continue;
                var text = new string(buffer, 0, count).Trim();
                if (name.EndsWith(".json", StringComparison.Ordinal))
                {
                    using var json = JsonDocument.Parse(text);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                    text = string.Join("\n", json.RootElement.EnumerateObject()
                        .Where(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("gameVersion", StringComparison.OrdinalIgnoreCase))
                        .Select(p => "version=" + p.Value.ToString()));
                }
                if (ParseText(text) is { } value)
                {
                    var isBuild = name == "build.txt" || Regex.IsMatch(text, @"(?im)^\s*build(?:id)?\s*[:=]");
                    var result = new LocalGameVersion(value, Path.GetRelativePath(game.FolderPath, path), value.Contains('.') && !isBuild ? LocalVersionKind.Release : LocalVersionKind.Build);
                    if (result.Kind == LocalVersionKind.Release) return result;
                    buildVersion ??= result;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        ct.ThrowIfCancellationRequested();
        if (exe is null) return buildVersion;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            // Engine versions are not game versions, even when carried by the exe.
            if (Regex.IsMatch((info.ProductName ?? "") + " " + (info.FileDescription ?? ""), @"(?i)\b(unity|unreal engine|godot)\b")) return buildVersion;
            if (ParseText(info.ProductVersion ?? "") is { } product)
                return new(product, "Exe ürün sürümü", LocalVersionKind.ProductMetadata);
            if (ParseText(info.FileVersion ?? "") is { } file)
                return new(file, "Exe dosya sürümü", LocalVersionKind.FileMetadata);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        return buildVersion;
    }

    public static string? ParseText(string text)
    {
        if (PlainVersion().Match(text.Trim()) is { Success: true } plain) return NonZero(plain.Groups[1].Value);
        var versions = LabelledVersion().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToArray();
        return versions.Length == 1 ? NonZero(versions[0]) : null;
    }
    private static string? NonZero(string value) => value.Any(c => c is >= '1' and <= '9') ? value : null;

    // Precision must match: a build ID, PE resource version and a marketing
    // release number are different namespaces. Never invent a conversion.
    public static int? Compare(string local, string remote, LocalVersionKind kind)
    {
        if (kind is LocalVersionKind.Build or LocalVersionKind.FileMetadata or LocalVersionKind.Unknown) return null;
        var a = PlainVersion().Match(local.Trim()); var b = PlainVersion().Match(remote.Trim());
        if (!a.Success || !b.Success) return null;
        var left = a.Groups[1].Value.Split('.'); var right = b.Groups[1].Value.Split('.');
        if (left.Length < 2 || left.Length != right.Length) return null;
        for (var i = 0; i < left.Length; i++)
        {
            var diff = BigInteger.Parse(left[i]).CompareTo(BigInteger.Parse(right[i]));
            if (diff != 0) return diff;
        }
        return 0;
    }
    [GeneratedRegex(@"\A(?:v(?:ersion)?\s*)?(\d{1,12}(?:\.\d{1,12}){0,4})\z", RegexOptions.IgnoreCase)]
    private static partial Regex PlainVersion();
    [GeneratedRegex(@"^\s*(?:game[_ ]?version|product[_ ]?version|version|build(?:id)?)\s*[:=]\s*v?(\d{1,12}(?:\.\d{1,12}){0,4})\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LabelledVersion();
}
