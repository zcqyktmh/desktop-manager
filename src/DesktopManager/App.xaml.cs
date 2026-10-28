using System.Configuration;
using System.Data;
using System.Windows;
using System.Threading;
using WpfApplication = System.Windows.Application;

namespace DesktopManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, "Local\\DesktopManagerApp", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var startInTray = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startInTray);
        MainWindow = window;
        if (!window.DesktopLayerReady) Shutdown();
        else window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow desktopWindow) desktopWindow.ShutdownDesktopLayer();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

