using System.Windows;

namespace GameTracker.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\GameTracker.Desktop", out var created);
        if (!created) { MessageBox.Show("GameTracker zaten açık. Sistem tepsisinden açabilirsin.", "GameTracker"); Shutdown(); return; }
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show("GameTracker açılamadı: " + ex.Message, "GameTracker"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
