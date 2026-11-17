using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using JockeyBox.Tools;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--stall-ui &lt;ms&gt;</c>: the UI watchdog's end-to-end proof, and nothing
/// else. It arms the real watchdog, runs a real dispatcher heartbeat for 300 ms
/// (so the heartbeat is provably live), then blocks the UI thread on purpose
/// while holding a breadcrumb.
///
/// <para>The expected outcome is that this method never returns: the watchdog
/// writes its report under the home's <c>diagnostics</c> folder and terminates
/// the process with <see cref="UiWatchdog.ExitCode"/>. Reaching the line after
/// the sleep means the net has a hole, so the run fails with 1.</para>
/// </summary>
internal static class StallUi
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--stall-ui") != null;

    public static int Run(string[] args)
    {
        var blockMs = 0;
        var raw = Harness.Flag(args, "--stall-ui");
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1000)
            blockMs = parsed;

        // The stall must outlast the threshold by more than detection + report +
        // flush (~1.5 s), or the UI thread wakes first and the proof is void.
        if (blockMs == 0) blockMs = UiWatchdog.ThresholdMs + 5000;

        var threshold = UiWatchdog.ThresholdMs;
        Console.WriteLine($"stall-ui: {UiWatchdog.EnvVar}={threshold} ms, blocking the UI thread for {blockMs} ms");

        if (!UiWatchdog.Enabled)
        {
            Console.WriteLine("stall-ui: watchdog disabled by the environment; nothing to prove");
            return 1;
        }

        MeasureCost();

        UiWatchdog.Start();

        // A real heartbeat: DispatcherTimer on this (UI) thread, pumped for 300 ms
        // before the stall, so the report's staleness is measured from a live tick.
        var beat = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
        beat.Tick += (_, _) => UiBreadcrumb.NoteUiTick();
        beat.Start();
        UiBreadcrumb.NoteUiTick();

        var frame = new DispatcherFrame();
        var warm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        warm.Tick += (_, _) =>
        {
            warm.Stop();
            frame.Continue = false;
        };
        warm.Start();
        Dispatcher.PushFrame(frame);
        beat.Stop();

        Console.WriteLine($"stall-ui: heartbeat live, ui tick age {UiWatchdog.LastTickAgeMs()} ms; stalling now");

        // The deliberate stall. The breadcrumb is what the report will name.
        UiBreadcrumb.Enter("--stall-ui selftest: blocking the UI thread on purpose");
        Thread.Sleep(blockMs);

        Console.WriteLine("stall-ui: FAILED - the UI thread woke up and no watchdog fired");
        return 1;
    }

    /// <summary>
    /// The cost the operator pays for the net, printed as numbers rather than
    /// asserted: 200 000 iterations of each hot path, divided.
    /// </summary>
    private static void MeasureCost()
    {
        const int N = 200_000;

        UiBreadcrumb.NoteUiTick(); // warm the JIT path
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < N; i++) UiBreadcrumb.NoteUiTick();
        sw.Stop();
        var heartbeatNs = sw.Elapsed.TotalMilliseconds * 1e6 / N;

        sw.Restart();
        for (var i = 0; i < N; i++)
        {
            using (UiBreadcrumb.Enter("bench"))
            {
            }
        }
        sw.Stop();
        var crumbNs = sw.Elapsed.TotalMilliseconds * 1e6 / N;

        sw.Restart();
        for (var i = 0; i < N; i++) _ = UiWatchdog.LastTickAgeMs();
        sw.Stop();
        var sampleNs = sw.Elapsed.TotalMilliseconds * 1e6 / N;

        var line =
            "stall-ui: cost per ui heartbeat " + heartbeatNs.ToString("F1", CultureInfo.InvariantCulture) + " ns, " +
            "per breadcrumb enter+exit " + crumbNs.ToString("F1", CultureInfo.InvariantCulture) + " ns, " +
            "per watchdog sample " + sampleNs.ToString("F1", CultureInfo.InvariantCulture) + " ns";
        Console.WriteLine(line);

        // A WinExe launched from a console often loses stdout, and the cost numbers
        // are evidence the operator asked for, so keep a copy beside the reports.
        try
        {
            var dir = Path.Combine(AppPaths.Home, "diagnostics");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stall-ui-cost.txt"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Evidence, not behaviour: a failed copy must not fail the run.
        }
    }
}
