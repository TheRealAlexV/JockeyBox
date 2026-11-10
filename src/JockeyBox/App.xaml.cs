using System;
using System.Windows;
using System.Windows.Threading;
using JockeyBox.Selftest;
using Velopack;

namespace JockeyBox;

/// <summary>
/// Application entry point. Velopack hooks must run before any UI is shown
/// so that install/update/uninstall events are handled correctly.
///
/// <para><b><c>--selftest</c>.</b> A headless run of the offline audio graph, for
/// CI and for WSL-built binaries on the Windows host: it renders through the real
/// engine with no sound device, prints one line per case and exits with 0 (all
/// passed) or 1. Handled before the WPF application starts so no window, tray icon
/// or device enumeration is created, and the process exit code can gate a build.</para>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack installer/updater lifecycle hooks (no-op when run portable).
        // They must stay first: the hooks rewrite argv and may exit the process.
        VelopackApp.Build().Run();

        if (SelftestRequested(e.Args))
        {
            int exitCode = SelftestRunner.Run(Console.Out, verbose: HasFlag(e.Args, "--selftest-verbose"));
            Console.Out.Flush();
            Shutdown(exitCode);
            return;
        }

        // --uishot-dump: the same row objects the tree binds, printed as text.
        // A PNG proves "something rendered"; this proves which BPM/key text the
        // columns actually hold (see Selftest/LibraryDump.cs).
        if (LibraryDump.Requested(e.Args))
        {
            int dumpExit = 1;
            try
            {
                Data.LibraryBoot.Boot();
                dumpExit = LibraryDump.Run(e.Args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("dump failed: " + ex);
            }
            Shutdown(dumpExit);
            return;
        }

        // --uishot renders the real window to a PNG with no desktop and no
        // interactive session, so verification never touches the operator's screen.
        if (UiShot.Requested(e.Args))
        {
            int shotExit = 1;
            try
            {
                Data.LibraryBoot.Boot();
                shotExit = UiShot.Run(e.Args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("uishot failed: " + ex);
            }
            Shutdown(shotExit);
            return;
        }

        // --perf: the same real window, driven headlessly, printing the numbers
        // behind "browsing is laggy" (see Selftest/PerfProbe.cs).
        if (PerfProbe.Requested(e.Args))
        {
            int perfExit = 1;
            try
            {
                Data.LibraryBoot.Boot();
                perfExit = PerfProbe.Run(e.Args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("perf failed: " + ex);
            }
            Shutdown(perfExit);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // Library database comes up before the window exists: every view model
        // reads its playlists from LibraryDatabase.Current at construction, and
        // the legacy json import must have run by then. A failure here is a
        // warning, never a startup error - the json stores still work.
        Data.LibraryBoot.Boot();

        base.OnStartup(e);
    }

    /// <summary>
    /// Graceful shutdown: drain every deferred database write and compact the WAL
    /// before the process goes away, so a rename or a drag that happened seconds
    /// before closing is in <c>library.db</c> on the next start. Both steps are
    /// best-effort - the JSON file already holds the same state.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var stores = Sources.PlaylistStore.FlushAll();
            if (stores > 0) Tools.Log.Info($"shutdown: flushed {stores} playlist write queue(s)");
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"shutdown: flushing playlists failed ({ex.Message})");
        }

        try
        {
            Data.LibraryDatabase.Current?.Checkpoint();
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"shutdown: database checkpoint failed ({ex.Message})");
        }

        base.OnExit(e);
    }

    /// <summary>True when the command line asks for the headless self-test.</summary>
    internal static bool SelftestRequested(string[] args) => HasFlag(args, "--selftest");

    private static bool HasFlag(string[]? args, string flag)
    {
        if (args == null) return false;
        foreach (string arg in args)
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "JockeyBox - Unexpected Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
