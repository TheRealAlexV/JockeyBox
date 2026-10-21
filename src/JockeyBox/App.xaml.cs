using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace JockeyBox;

/// <summary>
/// Application entry point. Velopack hooks must run before any UI is shown
/// so that install/update/uninstall events are handled correctly.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack installer/updater lifecycle hooks (no-op when run portable).
        VelopackApp.Build().Run();

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "JockeyBox - Unexpected Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
