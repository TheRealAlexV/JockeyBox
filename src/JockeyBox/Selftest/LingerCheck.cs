using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.Views;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--linger-check [--linger-check-ms ms] [--throw]</c>: bring the real app up,
/// leave the Find on YouTube window open the way a user leaves it, close the main
/// window exactly as its X does, and exit 0 only when the process then ends by
/// itself.
///
/// <para><b>Why it is a mode.</b> The operator closed the window and the process
/// lived on for over an hour, holding the home's single-instance lock and port
/// 17890, and had to be stopped by pid. A one-off sighting cannot tell "the close
/// path is broken" from "that run did something odd", so the close path gets an
/// exit code.</para>
///
/// <para><b>How it decides.</b> Nothing here calls <c>Shutdown()</c> and
/// <c>ShutdownMode</c> is left at the app's real default, because the question is
/// whether the app's own last-window-close path ends the process. A pass is the
/// process leaving on its own; a failure is the deadline expiring, reported with
/// the surviving pid, every window the Application still counts, whether the
/// close path returned, and whether the clean-exit marker was written.</para>
///
/// <para><b>Why <c>--throw</c>.</b> A check that only exercises the happy path
/// cannot prove a fix for a failure path: on a healthy tree it passes before the
/// fix and after it, and says nothing. <c>--throw</c> makes the view-model
/// teardown fail through <see cref="MainViewModel.TeardownFault"/>, which is
/// exactly where a store fails in the field. The catch in
/// <c>MainWindow.CloseViewModel</c> is then the only difference between a process
/// that dies and one that strands, so the two runs are the falsification.</para>
/// </summary>
internal static class LingerCheck
{
    /// <summary>Whole-run deadline. The real close path is seconds, not minutes.</summary>
    internal const int DefaultDeadlineMs = 20000;

    private const int SettleMs = 1500;

    /// <summary>The line a clean exit writes, and the only step after the seam.</summary>
    private const string ExitMarker = "library.json saved at shutdown";

    private static volatile bool _closeReturned;

    private static volatile bool _injected;

    private static volatile string _beforeClose = "(not reached)";

    private static volatile string _afterClose = "(not reached)";

    private static volatile string _settled = "(not reached)";

    private static volatile string _shutdownMode = "(not read)";

    private static volatile string _escaped = "(none)";

    private static long _logBytesAtArm;

    public static bool Requested(string[] args) => Harness.Flag(args, "--linger-check") != null;

    /// <summary>
    /// Arms the check and returns at once: the app then builds its window and runs
    /// its own message loop, and either exits by itself or the watchdog reports.
    /// Nothing here ends the process on the happy path, so "exit 0" means the close
    /// path itself worked.
    /// </summary>
    public static void Arm(string[] args)
    {
        int deadlineMs = DefaultDeadlineMs;
        string? deadlineText = Harness.Flag(args, "--linger-check-ms");
        if (deadlineText != null && int.TryParse(deadlineText, out int parsed))
            deadlineMs = Math.Clamp(parsed, 1000, 120000);

        _injected = Harness.Flag(args, "--throw") != null;
        if (_injected)
        {
            // Fail where the field fails: mid-teardown, after the stores that hold
            // the operator's work, before the playlist drain and the exit marker.
            MainViewModel.TeardownFault = () =>
                throw new InvalidOperationException("injected teardown fault (--linger-check --throw)");
        }

        _shutdownMode = Application.Current.ShutdownMode.ToString();
        _logBytesAtArm = LogSize();

        Say($"home     : {AppPaths.Home}");
        Say($"log      : {Log.FilePath}");
        Say($"deadline : {deadlineMs} ms");
        Say($"shutdown : {_shutdownMode} (the app default, never pinned)");
        Say($"throw    : {(_injected ? "yes - the teardown fails mid-way on purpose" : "no - the ordinary close path")}");

        // A check must never stop on a modal dialog. The real App shows a MessageBox
        // here, which is invisible from a session with no desktop and would block
        // the close forever; report the exception and keep going so the deadline
        // can describe what is left standing.
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            string text = e.Exception.ToString();
            _escaped = text;
            Console.Error.WriteLine("linger-check: an exception reached the dispatcher and was swallowed, so the app keeps running:");
            Console.Error.WriteLine(text);
            Console.Error.Flush();
            e.Handled = true;
        };

        Application.Current.Exit += (_, _) =>
        {
            Console.Out.WriteLine("linger-check: PASS the process ended by itself after the main window closed");
            Console.Out.Flush();
        };

        // The store every view model reads at construction, exactly as a normal
        // start boots it. Without it the window under test is not the real one.
        Data.LibraryBoot.Boot();

        StartWatchdog(deadlineMs);

        // StartupUri builds the operator's window once this method returns (the app
        // relies on that in App.OnStartup), so wait for that moment rather than
        // constructing a twin that nobody uses.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(DriveCheck));
    }

    /// <summary>Runs on the UI thread once the app's own window is up.</summary>
    private static void DriveCheck()
    {
        Window? main = Application.Current.MainWindow;
        if (main is null)
        {
            Console.Error.WriteLine("linger-check: FAIL the app did not create its main window");
            Console.Error.Flush();
            Environment.Exit(2);
            return;
        }

        // Offscreen and unactivated: a check must run in a session with no desktop
        // and must never take focus from a live one. StartupUri already showed the
        // window, so a single frame may land at its default spot.
        try
        {
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -6000;
            main.Top = -6000;
            main.ShowInTaskbar = false;
        }
        catch (Exception ex)
        {
            Say($"note     : could not move the window offscreen: {ex.GetType().Name}");
        }

        try
        {
            OpenSearchWindowAsSpotifyDoes(main);
        }
        catch (Exception ex)
        {
            Say($"note     : the Find on YouTube window did not open: {ex.GetType().Name}: {ex.Message}");
        }

        Pump(SettleMs);
        _beforeClose = DescribeWindows();
        Say($"opened   : {_beforeClose}");

        // The operator's X, on the dispatcher. Deliberately not wrapped in a
        // try/catch: a throw inside the Closed handlers has to reach the dispatcher
        // the way it does in the real app, because surviving there is the defect.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            main.Close();
            _closeReturned = true;
            _afterClose = DescribeWindows();
            Say($"closed   : {_afterClose}");
            Say("linger-check: the main window is closed; waiting for the process to end by itself");

            // A window can leave the collection a beat after it closes. Sample
            // again, on the UI thread, so the failure report describes the state
            // that actually holds rather than the instant right after Close().
            var settle = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(2500),
            };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                _settled = DescribeWindows();
            };
            settle.Start();
        }));
    }

    /// <summary>
    /// The Find on YouTube window the way <c>SpotifyViewModel.FindYouTubeVersion</c>
    /// opens it: same view model, same Owner rule, same modeless Show. Modeless on
    /// purpose - that is the state the operator leaves behind when the main window
    /// is closed while it is still up.
    /// </summary>
    private static YouTubeSearchWindow? OpenSearchWindowAsSpotifyDoes(Window owner)
    {
        var vm = new YouTubeSearchViewModel(
            AppSettings.Load(),
            Playback.StreamPreviewTransport.CreateDefault(),
            "Daft Punk Around The World",
            "Daft Punk \u2014 Around The World",
            pick: _ => { });

        var window = new YouTubeSearchWindow(vm);
        if (owner.IsVisible && !ReferenceEquals(owner, window)) window.Owner = owner;
        window.Show();
        return window;
    }

    private static void StartWatchdog(int deadlineMs)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(deadlineMs);

            var report = new StringBuilder();
            report.AppendLine($"linger-check: FAIL the process is still running {deadlineMs} ms after the main window closed");
            report.AppendLine($"  surviving pid  : {Environment.ProcessId} (no window, still holding this home's single-instance lock and port 17890)");
            report.AppendLine(_closeReturned
                ? "  close path     : returned - the window closed and no shutdown followed"
                : "  close path     : never returned from main.Close() - the teardown threw inside it");
            report.AppendLine($"  clean exit     : the '{ExitMarker}' marker was {MarkerState()}");
            report.AppendLine($"  shutdown mode  : {_shutdownMode}");
            report.AppendLine($"  windows before : {_beforeClose}");
            report.AppendLine($"  windows after  : {_afterClose}");
            report.AppendLine($"  windows settled: {_settled}");
            report.AppendLine($"  threads        : {SafeThreadCount()} (every thread this app creates is IsBackground=true)");
            report.AppendLine("  escaped        : the first dispatcher exception, if any");
            report.AppendLine(Indent(_escaped));

            Console.Error.Write(report.ToString());
            Console.Error.Flush();
            Environment.Exit(4);
        })
        {
            IsBackground = true,
            Name = "JockeyBox linger-check watchdog",
        };
        watchdog.Start();
    }

    /// <summary>
    /// Whether the clean-exit marker was appended to this home's log since the
    /// check armed. A surviving process is expected to have written none: the
    /// marker is the last teardown step, which the failure skips.
    /// </summary>
    private static string MarkerState()
    {
        try
        {
            string path = Log.FilePath;
            if (!File.Exists(path)) return "not written (no log file)";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= _logBytesAtArm) return "not written";

            stream.Seek(_logBytesAtArm, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string tail = reader.ReadToEnd();
            return tail.Contains(ExitMarker, StringComparison.Ordinal) ? "written" : "not written";
        }
        catch (Exception ex)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }

    private static long LogSize()
    {
        try
        {
            var info = new FileInfo(Log.FilePath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Every window the Application still counts, with whether it has a real
    /// handle. A window object that was never shown counts here and has no HWND.
    /// </summary>
    private static string DescribeWindows()
    {
        var text = new StringBuilder();
        try
        {
            var windows = Application.Current.Windows;
            int visible = 0;
            foreach (Window window in windows)
            {
                try { if (window.IsVisible) visible++; } catch { }
            }

            text.Append($"count={windows.Count} visible={visible}");
            foreach (Window window in windows)
            {
                try
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    text.Append($"{Environment.NewLine}    {window.GetType().Name} visible={window.IsVisible} loaded={window.IsLoaded} hwnd={(handle == IntPtr.Zero ? "none" : "yes")} owner={(window.Owner is null ? "none" : window.Owner.GetType().Name)} title=\"{window.Title}\"");
                }
                catch (Exception ex)
                {
                    text.Append($"{Environment.NewLine}    <unreadable: {ex.GetType().Name}>");
                }
            }
        }
        catch (Exception ex)
        {
            text.Append($"<unreadable: {ex.GetType().Name}: {ex.Message}>");
        }
        return text.ToString();
    }

    private static string Indent(string text) =>
        "    " + text.Replace(Environment.NewLine, $"{Environment.NewLine}    ");

    private static int SafeThreadCount()
    {
        try { return Process.GetCurrentProcess().Threads.Count; }
        catch { return -1; }
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Say(string text)
    {
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }
}
