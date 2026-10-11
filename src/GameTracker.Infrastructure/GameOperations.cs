using System.Diagnostics;
using System.Runtime.InteropServices;
using GameTracker.Core;
using Microsoft.Win32.SafeHandles;

namespace GameTracker.Infrastructure;

public static class GameOperations
{
    public static (int Pid, DateTimeOffset Started) Launch(Game game)
    {
        if (game.IsRunning) throw new IOException("Oyun zaten çalışıyor.");
        ValidateIdentity(game);
        var exe = PathPolicy.ResolveExe(game, game.LaunchExe ?? throw new IOException("Önce oyun exe'sini seç."));
        var cwd = game.WorkingDirectory ?? Path.GetDirectoryName(exe)!;
        if (!PathPolicy.Equal(cwd, game.FolderPath) && !PathPolicy.Within(game.FolderPath, cwd)) throw new IOException("Çalışma dizini oyun klasörünün içinde olmalı.");
        PathPolicy.EnsureNoLinks(cwd);
        using var process = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = cwd, Arguments = game.Arguments, UseShellExecute = false }) ?? throw new IOException("Oyun başlatılamadı.");
        DateTimeOffset started;
        try { started = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { started = DateTimeOffset.UtcNow; }
        return (process.Id, started);
    }

    public static void Delete(Game game, bool permanent, ProcessInfo[] processes)
    {
        PathPolicy.ValidateGame(game);
        ValidateIdentity(game);
        if (game.IsRunning || processes.Any(x => PathPolicy.Within(game.FolderPath, x.Path))) throw new IOException("Oyun klasöründen çalışan süreç var. Önce oyunu ve araçlarını kapat.");
        var rootId = DirectoryIdentity(game.RootPath); var targetId = DirectoryIdentity(game.FolderPath);
        // Conservative alpha policy: reject the whole delete when a link occurs anywhere.
        var dirs = new Stack<string>(); dirs.Push(game.FolderPath);
        while (dirs.TryPop(out var dir))
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Oyun klasöründe bağlantı/junction var; otomatik silme engellendi.");
            if ((attributes & FileAttributes.Directory) != 0) dirs.Push(entry);
        }
        PathPolicy.ValidateGame(game);
        if (rootId != DirectoryIdentity(game.RootPath) || targetId != DirectoryIdentity(game.FolderPath)) throw new IOException("Klasör tarama sırasında değişti; silme iptal edildi.");
        if (permanent) Directory.Delete(game.FolderPath, true);
        else Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(game.FolderPath,
            Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        if (Directory.Exists(game.FolderPath)) throw new IOException("Klasör tamamen kaldırılamadı.");
    }

    public static void BindIdentity(Game game)
    {
        PathPolicy.ValidateGame(game);
        if (game.RootIdentity is null && game.FolderIdentity is null)
        { game.RootIdentity = DirectoryIdentity(game.RootPath); game.FolderIdentity = DirectoryIdentity(game.FolderPath); }
        else ValidateIdentity(game);
    }
    public static void ValidateIdentity(Game game)
    {
        PathPolicy.ValidateGame(game);
        if (game.RootIdentity is null || game.FolderIdentity is null || game.RootIdentity != DirectoryIdentity(game.RootPath) || game.FolderIdentity != DirectoryIdentity(game.FolderPath))
            throw new IOException("Oyun klasörünün fiziksel kimliği değişti. Klasörü yeniden bağla.");
    }
    private static string DirectoryIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new IOException("Klasör kimliği doğrulanamadı.");
        return $"{info.VolumeSerial}:{info.FileIndexHigh}:{info.FileIndexLow}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
