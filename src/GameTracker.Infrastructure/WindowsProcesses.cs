using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameTracker.Core;
using Microsoft.Win32.SafeHandles;

namespace GameTracker.Infrastructure;

public sealed record ProcessInfo(int Pid, int ParentPid, DateTimeOffset StartedAt, string Path)
{
    public string Key => $"{Pid}:{StartedAt.UtcTicks}";
}

public static class WindowsProcesses
{
    public static ProcessInfo[] Snapshot()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var parents = new Dictionary<int, int>();
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle != new IntPtr(-1))
        {
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
                if (Process32First(handle, ref entry)) do { parents[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32Next(handle, ref entry));
            }
            finally { CloseHandle(handle); }
        }
        var result = new List<ProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    using var ph = OpenProcess(0x1000, false, process.Id);
                    var buffer = new StringBuilder(32768); uint size = (uint)buffer.Capacity;
                    if (!ph.IsInvalid && QueryFullProcessImageName(ph, 0, buffer, ref size))
                        result.Add(new(process.Id, parents.GetValueOrDefault(process.Id), new DateTimeOffset(process.StartTime.ToUniversalTime()), buffer.ToString()));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return result.ToArray();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public UIntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder buffer, ref uint size);
}

public sealed class SessionTracker(LocalStore store)
{
    private sealed class Active(Game game, DateTimeOffset now)
    {
        public Game Game = game;
        public PlaySession Session = new() { GameId = game.Id, StartedAt = now, LastSeenAt = now };
        public SessionClock Clock = new();
        public long BaseMs = game.PlayedMilliseconds;
        public long LastSaved;
    }
    private readonly Dictionary<Guid, Active> active = [];
    // Retained parent identity allows a launcher to exit before the next snapshot.
    private readonly Dictionary<int, (Guid GameId, DateTimeOffset Started, DateTimeOffset Seen)> lineage = [];
    public void RegisterLaunch(Game game, int pid, DateTimeOffset started) => lineage[pid] = (game.Id, started, DateTimeOffset.UtcNow);

    public void Observe(IReadOnlyList<Game> games, ProcessInfo[] processes, long monotonicMs, DateTimeOffset now)
    {
        var matched = new HashSet<Guid>();
        var byPid = processes.ToDictionary(x => x.Pid);
        // Resolve chains even when child processes appear earlier than parents.
        for (var pass = 0; pass < 3; pass++)
        foreach (var p in processes)
        {
            var game = games.FirstOrDefault(g => !g.Deleted && g.Available && IsKnown(g, p.Path));
            if (game is null && lineage.TryGetValue(p.ParentPid, out var parent) && parent.Started <= p.StartedAt && now - parent.Seen < TimeSpan.FromSeconds(30))
            {
                var validParent = !byPid.TryGetValue(p.ParentPid, out var liveParent) || liveParent.StartedAt == parent.Started;
                game = validParent ? games.FirstOrDefault(g => g.Id == parent.GameId && !g.Deleted && g.Available && PathPolicy.Within(g.FolderPath, p.Path) && !GameScanner.IsTool(p.Path)) : null;
            }
            if (game is null || GameScanner.IsTool(p.Path)) continue;
            matched.Add(game.Id); lineage[p.Pid] = (game.Id, p.StartedAt, now);
        }
        foreach (var key in lineage.Where(x => now - x.Value.Seen > TimeSpan.FromSeconds(30)).Select(x => x.Key).ToArray()) lineage.Remove(key);
        foreach (var game in games)
        {
            var present = matched.Contains(game.Id);
            if (present && !active.ContainsKey(game.Id))
            {
                var item = new Active(game, now); active[game.Id] = item;
                game.LastPlayedAt = now; game.LastPlayedFromPrefetch = false; store.SaveSession(item.Session, game);
            }
            if (active.TryGetValue(game.Id, out var current))
            {
                current.Clock.Observe(monotonicMs, present);
                if (present) current.Session.LastSeenAt = now;
                current.Session.DurationMs = current.Clock.DurationMs;
                game.PlayedMilliseconds = current.BaseMs + current.Session.DurationMs;
                if (!present && now - current.Session.LastSeenAt >= TimeSpan.FromSeconds(10))
                    Finish(game.Id, "Completed");
                else if (monotonicMs - current.LastSaved >= 15000)
                { store.SaveSession(current.Session, game); current.LastSaved = monotonicMs; }
            }
            game.IsRunning = active.ContainsKey(game.Id); game.Refresh();
        }
    }
    private static bool IsKnown(Game g, string path) => g.LaunchExe is not null && PathPolicy.Equal(Path.Combine(g.FolderPath, g.LaunchExe), path)
        || g.RuntimeExes.Any(x => PathPolicy.Equal(Path.Combine(g.FolderPath, x), path));
    private void Finish(Guid id, string status)
    {
        if (!active.Remove(id, out var a)) return;
        a.Session.EndedAt = a.Session.LastSeenAt; a.Session.Status = status;
        a.Game.IsRunning = false; store.SaveSession(a.Session, a.Game);
    }
    public void Suspend() { foreach (var a in active.Values) a.Clock.Suspend(); }
    public void Stop() { foreach (var id in active.Keys.ToArray()) Finish(id, "Interrupted"); }
}
