using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace CS2Manager;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Nur eine Instanz: zwei Manager würden sich gegenseitig die Wiederherstellung durcheinanderbringen.
        _mutex = new Mutex(true, @"Local\CS2Manager_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Der CS2 Manager läuft bereits (siehe Infobereich der Taskleiste).",
                "CS2 Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
