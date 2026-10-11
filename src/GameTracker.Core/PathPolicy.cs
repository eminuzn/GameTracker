namespace GameTracker.Core;

public static class PathPolicy
{
    public static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Equal(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);
    public static bool Within(string parent, string child) => Normalize(child).StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, Comparison);
    public static bool DirectChild(string parent, string child) => !Equal(parent, child) && Equal(Path.GetDirectoryName(Normalize(child)) ?? child, parent);

    public static void EnsureNoLinks(string path)
    {
        FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (info is not null)
        {
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Bağlantı/junction içeren yollar bu işlemde kullanılamaz.");
            info = info switch { DirectoryInfo d => d.Parent, FileInfo f => f.Directory, _ => null };
        }
    }

    public static void ValidateRoot(string path, IEnumerable<LibraryRoot> existing)
    {
        var full = Normalize(path);
        if (!Directory.Exists(full)) throw new IOException("Ana klasör bulunamadı.");
        EnsureNoLinks(full);
        if (Equal(full, Path.GetPathRoot(full)!)) throw new IOException("Diskin tamamı oyun klasörü olarak seçilemez.");
        var protectedPaths = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppContext.BaseDirectory };
        foreach (var p in protectedPaths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (Equal(full, p) || Within(full, p)) throw new IOException("Bu konum korunan klasörleri içeriyor. Sadece oyunların bulunduğu alt klasörü seç.");
            if ((p == protectedPaths[0] || p == protectedPaths[3] || p == protectedPaths[4]) && Within(p, full))
                throw new IOException("Bu sistem/uygulama konumu oyun kökü olarak kullanılamaz.");
        }
        foreach (var root in existing)
            if (Equal(root.Path, full) || Within(root.Path, full) || Within(full, root.Path))
                throw new IOException("Bu klasör veya iç içe bir oyun kökü zaten eklenmiş.");
    }

    public static void ValidateGame(Game game)
    {
        if (!Directory.Exists(game.RootPath) || !Directory.Exists(game.FolderPath)) throw new IOException("Oyun klasörüne erişilemiyor.");
        if (!DirectChild(game.RootPath, game.FolderPath)) throw new IOException("Oyun dizini ana klasörün doğrudan çocuğu olmalı.");
        ValidateRoot(game.RootPath, []);
        EnsureNoLinks(game.FolderPath);
    }

    public static string ResolveExe(Game game, string relative)
    {
        ValidateGame(game);
        var full = Normalize(Path.Combine(game.FolderPath, relative));
        if (!Within(game.FolderPath, full) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new IOException("Geçerli exe oyun klasörünün içinde olmalı.");
        EnsureNoLinks(full);
        return full;
    }
}
