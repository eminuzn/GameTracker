using System.Diagnostics;

namespace GameTracker.Core;

public sealed record ScanResult(long Bytes, int Skipped, List<ExeCandidate> Candidates);

public static class GameScanner
{
    private static readonly string[] ToolTokens = ["setup", "install", "installer", "uninstall", "uninstaller", "redist", "redistributable", "redistributables", "vcredist", "dxsetup", "updater", "benchmark", "patch", "patcher", "yama", "kaldir", "crashreporter", "crashhandler", "errorreporter", "reporter", "support", "tools", "7za", "beservice"];
    public static bool IsTool(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var key = TitleMatcher.Key(name);
        var tokens = TitleMatcher.Clean(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TitleMatcher.Key);
        return key.StartsWith("unins", StringComparison.Ordinal) || key.Contains("yamakald", StringComparison.Ordinal)
            || key.Contains("crashreport", StringComparison.Ordinal) || key.Contains("crashhandler", StringComparison.Ordinal)
            || key.Contains("errorreport", StringComparison.Ordinal) || key.StartsWith("vcredist", StringComparison.Ordinal)
            || key.StartsWith("beservice", StringComparison.Ordinal) || tokens.Any(ToolTokens.Contains);
    }

    public static ScanResult Scan(string folder, string name, CancellationToken ct)
    {
        long bytes = 0; int skipped = 0;
        var candidates = new List<ExeCandidate>();
        var dirs = new Stack<string>(); dirs.Push(folder);
        while (dirs.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) { dirs.Push(entry); continue; }
                        bytes += new FileInfo(entry).Length;
                        if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) candidates.Add(Score(folder, name, entry));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(bytes, skipped, candidates.OrderByDescending(x => x.Score).ThenBy(x => x.RelativePath).ToList());
    }

    public static ExeCandidate Score(string folder, string title, string exe)
    {
        var relative = Path.GetRelativePath(folder, exe); int score = 0;
        var reasons = new List<string>();
        void Add(int points, string reason) { score += points; reasons.Add(reason); }
        var stem = TitleMatcher.Key(Path.GetFileNameWithoutExtension(exe));
        var key = TitleMatcher.Key(title);
        // Exact filename evidence is sufficient without optional version resources.
        var runtimeStem = stem.EndsWith("win64shipping", StringComparison.Ordinal) ? stem[..^13] : stem;
        if (key.Length > 2 && runtimeStem == key) Add(70, "Dosya adı oyun adıyla tam eşleşiyor");
        else if (key.Length > 2 && (stem.Contains(key) || key.Contains(stem) && stem.Length > 3)) Add(35, "Oyun adıyla benzer");
        try
        {
            var metadata = FileVersionInfo.GetVersionInfo(exe);
            if (TitleMatcher.Key(metadata.ProductName ?? "") == key || TitleMatcher.Key(metadata.FileDescription ?? "") == key)
                Add(20, "Ürün bilgisi eşleşiyor");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(x => new[] { "bin", "binaries", "win64", "x64" }.Contains(x.ToLowerInvariant()))) Add(15, "Oyun binary dizini");
        if (stem.EndsWith("win64shipping")) Add(15, "Unreal runtime");
        if (PathPolicy.Equal(Path.GetDirectoryName(exe)!, folder)) Add(5, "Oyun kökünde");
        if (Directory.Exists(Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "_Data"))) Add(10, "Unity verisi mevcut");
        if (stem.Contains("launcher")) Add(-10, "Başlatıcı olabilir");
        var helper = IsTool(exe) || parts.SkipLast(1).Any(x => new[] { "_commonredist", "redist", "redistributables", "support", "installer", "tools", "crashreporter", "battleye" }.Contains(x.ToLowerInvariant()));
        if (helper) Add(-150, "Yardımcı araç · otomatik seçim dışında");
        return new(relative, score, string.Join(" · ", reasons), helper);
    }

    public static ExeCandidate? Suggest(IReadOnlyList<ExeCandidate> candidates)
    {
        var sorted = candidates.Where(x => !x.IsHelper).OrderByDescending(x => x.Score).ToArray();
        return sorted.Length > 0 && sorted[0].Score >= 60 && (sorted.Length == 1 || sorted[0].Score - sorted[1].Score >= 20) ? sorted[0] : null;
    }
}
