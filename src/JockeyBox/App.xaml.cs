using System;
using System.Threading;
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

        // A set-but-unusable JOCKEYBOX_HOME must not quietly become %APPDATA%\JockeyBox.
        // That silent fallback is how a launcher carrying a trailing space in the
        // value (or an unrooted relative path, an empty string, an absent drive)
        // reached the operator's real profile. Refusing to start is the only choice
        // that cannot write to data the operator did not name: every store, the log
        // and the single-instance lock are all rooted at the home, so "start on the
        // live profile" would be the same silent fallback with a warning nobody reads.
        // Reported on both channels a launch can have: stderr for a console/CI run,
        // and a modal dialog for the windowed launch that has no console at all.
        // Deliberately no log file: the log lives under the home that just failed
        // validation, and writing it is the one thing this guard exists to prevent.
        if (Tools.AppPaths.HomeProblem is { } homeProblem)
        {
            Console.Error.WriteLine(homeProblem);
            MessageBox.Show(homeProblem, "JockeyBox: JOCKEYBOX_HOME cannot be used",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(Tools.AppPaths.BadHomeExitCode);
            return;
        }

        // The per-second audio trace is opt-in (a normal launch leaves it off: one
        // line a second is 86 000 lines a day). The headless probes were built on
        // it - --audiotrace prints it by design, the capture modes and the selftest
        // read it to tell a device-side fault from a graph fault - so they keep it.
        if (AudioTrace.Requested(e.Args) || CaptureHomeGuard.CaptureRequested(e.Args))
            JockeyBox.Audio.Graph.AudioEngine.TraceEnabled = true;

        // A capture must refuse the live profile before anything resolves a store.
        // The old guard was a note in a brief and it did not hold: a cmd.exe "set"
        // never reached the child, so --uishot rewrote the operator's library.db,
        // library.json, playlist-cache.json and settings.json in %APPDATA%\JockeyBox.
        // The windowed app is deliberately exempt: that home is where it belongs.
        if (CaptureHomeGuard.Refusal(e.Args) is { } homeRefusal)
        {
            Console.Error.WriteLine(homeRefusal);
            Environment.Exit(CaptureHomeGuard.RefusalExitCode);
            return;
        }

        // One instance per resolved home, taken before any store is opened so a
        // refused launch never touches library.db. Two processes on one home are
        // two independent SQLite writers - the writer gate in Data/JockeyBoxDatabase
        // is in-process only - so the only reliable prevention is not starting.
        // Headless modes are exempt from *focus*, never from the guard.
        Tools.SingleInstance.Headless =
            SelftestRequested(e.Args) || LibraryDump.Requested(e.Args) ||
            UiShot.Requested(e.Args) || PerfProbe.Requested(e.Args) ||
            PreviewCheck.Requested(e.Args) || LingerCheck.Requested(e.Args) ||
            LyricsProbe.Requested(e.Args) || AudioTrace.Requested(e.Args);

        if (!Tools.SingleInstance.TryAcquire(Tools.AppPaths.Home, out string owner, out int ownerPid))
        {
            // A refused capture must fail loudly. Every verification step in this
            // project runs a headless mode and trusts its exit status, so exit 0
            // with no PNG written is indistinguishable from a success that left a
            // stale file on disk. A second windowed launch stays 0: it is a normal
            // thing for the operator to do, and the existing window is coming
            // forward anyway.
            int refusalCode = Tools.SingleInstance.Headless ? 1 : 0;

            if (!Tools.SingleInstance.Headless)
            {
                Tools.SingleInstance.SignalExisting(Tools.AppPaths.Home, ownerPid);

                // An owner with no window cannot be brought forward: Activate() on a
                // zero handle does nothing, which is the "close the app, open it
                // again, nothing happens" the operator reported. Give the owner a
                // moment to finish showing one (a relaunch during startup is the
                // common case), then say plainly what is wrong on the console as well
                // as in the log, so a person with no log tail is not left guessing.
                DateTime grace = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < grace && !Tools.SingleInstance.HasWindow(ownerPid))
                    Thread.Sleep(150);

                if (!Tools.SingleInstance.HasWindow(ownerPid))
                {
                    // Distinct from 0 (the normal "brought the window forward"
                    // refusal) and from 1 (a refused headless capture), so a script
                    // can tell a windowless owner apart from both.
                    //
                    // "No window" is genuinely ambiguous: the owner may have none,
                    // or it may have one this process cannot see because it runs on
                    // another desktop or in another session. Both mean the same
                    // thing here - Activate() on a zero handle would do nothing - so
                    // the message says what was observed rather than guessing which.
                    const int windowlessCode = 3;
                    string text =
                        $"JockeyBox is already running as pid {ownerPid}, but no window for that process could be found from here. " +
                        $"It may have no window, or its window may be on another desktop or in another session, where this launch cannot see or raise it. " +
                        $"Nothing will be opened, because the home is still owned - close pid {ownerPid} in Task Manager, then start JockeyBox again. " +
                        $"(This launch exits with code {windowlessCode}.)";
                    Console.Error.WriteLine(text);
                    Tools.Log.Warn($"single-instance: {text}");
                    Environment.Exit(windowlessCode);
                    return;
                }
            }

            Tools.Log.Warn(
                $"single-instance: refused this launch: home {Tools.AppPaths.Home} is already owned by pid {owner}. " +
                $"Close that window first; this process exits with code {refusalCode}, without opening the database or showing a window.");

            // Not Shutdown(): the WPF shutdown path spends ~6 s tearing down an
            // application that never opened a store or a window, and a second
            // launch is expected to vanish. Nothing here needs unwinding - the
            // pid file and the mutex belong to the owner, and the OS closes this
            // process's handles.
            Environment.Exit(refusalCode);
            return;
        }

        Exit += (_, _) => Tools.SingleInstance.Release();

        // --linger-check: close the main window exactly as its X does and exit 0
        // only when the process then ends by itself (see Selftest/LingerCheck.cs).
        // It arms the check and returns to the app's own message loop instead of
        // calling Shutdown(): the last-window-close path is what is under test, and
        // a Shutdown() here would pass the check whether or not that path works.
        if (LingerCheck.Requested(e.Args))
        {
            LingerCheck.Arm(e.Args);
            return;
        }

        if (AudioTrace.Requested(e.Args))
        {
            int traceExit = AudioTrace.Run(e.Args);
            Console.Out.Flush();
            Shutdown(traceExit);
            return;
        }

        // --null-render: offline render of both decks straight to raw float32, so a
        // 0 ms build can be diffed against another build sample for sample.
        if (NullRender.Requested(e.Args))
        {
            int nullExit = NullRender.Run(e.Args);
            Console.Out.Flush();
            Shutdown(nullExit);
            return;
        }

        if (SelftestRequested(e.Args))
        {
            int exitCode = SelftestRunner.Run(Console.Out, verbose: HasFlag(e.Args, "--selftest-verbose"));
            Console.Out.Flush();
            Shutdown(exitCode);
            return;
        }

        // --preview-check: audition one upload through the real yt-dlp + libVLC
        // path and exit 0 only when the position actually moved
        // (see Selftest/PreviewCheck.cs). Console mode: no window, no tray.
        if (PreviewCheck.Requested(e.Args))
        {
            int checkExit = 1;

            // A hung check must not read as a passing one: the mode exits
            // non-zero on its own deadline instead of waiting forever.
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(PreviewCheck.TimeoutMs);
                Console.Error.WriteLine("preview-check: gave up waiting");
                Console.Error.Flush();
                Environment.Exit(3);
            }) { IsBackground = true };
            watchdog.Start();

            try
            {
                checkExit = PreviewCheck.Run(e.Args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("preview-check failed: " + ex);
            }

            Console.Out.Flush();
            Shutdown(checkExit);
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

        // --lyrics-probe "Artist|Title": one real lookup through every provider,
        // printed as per-provider outcomes (see Selftest/LyricsProbe.cs).
        if (LyricsProbe.Requested(e.Args))
        {
            int probeExit = 1;
            try
            {
                probeExit = LyricsProbe.Run(e.Args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("lyrics-probe failed: " + ex);
            }
            Shutdown(probeExit);
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

        // The holder's half of a refused second launch: bring this window forward.
        // MainWindow arrives with StartupUri, so it exists by the first dispatcher
        // pump; the null check covers that gap. A headless run never gets here.
        Tools.SingleInstance.ListenForActivation(() => Dispatcher.Invoke(() =>
        {
            var window = MainWindow;
            if (window is null) return;

            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            Tools.Log.Info($"single-instance: brought the window forward for a second launch (pid {Environment.ProcessId})");
        }));
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
