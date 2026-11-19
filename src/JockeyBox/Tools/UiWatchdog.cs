using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace JockeyBox.Tools;

/// <summary>
/// The safety net: if the UI thread stops heartbeating, write down what it was
/// doing and take the process down, so the machine recovers and the next
/// occurrence arrives with a file instead of a hard power-off.
///
/// <para><b>Why a kill.</b> The observed failure was one thread (the UI thread)
/// blocked for minutes while the rest of the machine still ran: the process could
/// not be closed, a dialog could not be shown, and the operator had to power the
/// box off. A watchdog that only logs is useless here; the only recovery that
/// does not need the wedged thread is terminating the process.</para>
///
/// <para><b>Off by construction.</b> <c>JOCKEYBOX_UI_WATCHDOG_MS=0</c> means
/// <see cref="Start"/> returns before it creates a thread, so a disabled
/// watchdog costs nothing at all: no thread, no timer, no sampling. With no
/// variable set the threshold is <see cref="DefaultThresholdMs"/> (8 s of
/// starvation, which a healthy launch never approaches).</para>
///
/// <para><b>Cost when armed.</b> One BelowNormal background thread that wakes a
/// few times a second and reads a long. The UI side is one timestamp store per
/// tick (<see cref="UiBreadcrumb.NoteUiTick"/>) - see the numbers printed by
/// <c>--stall-ui</c>.</para>
/// </summary>
public static class UiWatchdog
{
    public const string EnvVar = "JOCKEYBOX_UI_WATCHDOG_MS";

    /// <summary>Eight seconds: long enough to ride out a GC pause or a slow disk, short enough to recover the box.</summary>
    public const int DefaultThresholdMs = 8000;

    /// <summary>Distinct from 0 (clean), 1 (crash/selftest failure), 5 and 6 (launch refusals).</summary>
    public const int ExitCode = 86;

    private static int _started;
    private static int _fired;

    /// <summary>Set by <see cref="Arm"/>: 0 until the window has rendered.</summary>
    private static int _armed;

    /// <summary>Stopwatch ticks at <see cref="Arm"/>; 0 while disarmed.</summary>
    private static long _armedTicks;

    /// <summary>0 disables the watchdog; anything else is the starvation threshold.</summary>
    public static int ThresholdMs { get; } = ReadThreshold();

    public static bool Enabled => ThresholdMs > 0;

    private static int ReadThreshold() => ParseThreshold(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    /// Pure parse of the setting. Unset or unreadable means the default: a broken
    /// setting must not silently remove the net. Non-positive means off.
    /// </summary>
    internal static int ParseThreshold(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultThresholdMs;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return DefaultThresholdMs;
        if (ms <= 0) return 0; // explicit off
        // Below 100 ms any healthy GC pause would trip it; a kill is destructive, so
        // refuse values that cannot mean what they say rather than arming a hair trigger.
        return Math.Max(100, ms);
    }

    /// <summary>
    /// The whole decision, isolated from time, threads and I/O so it can be tested
    /// without starving anything. True when the UI thread has been silent for at
    /// least the threshold, and the watchdog is armed at all.
    /// </summary>
    public static bool ShouldFire(long lastTickAgeMs, int thresholdMs) =>
        thresholdMs > 0 && lastTickAgeMs >= thresholdMs;

    /// <summary>Start the sampler. A no-op when disabled, and idempotent.</summary>
    public static void Start()
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        // A tick of our own, so the first sample measures the watchdog's own
        // startup rather than "never ticked" (which reads as an infinite age).
        UiBreadcrumb.NoteUiTick();

        var thread = new Thread(SampleLoop)
        {
            IsBackground = true,
            Name = "JockeyBox UI watchdog",
            // BelowNormal: the sampler must never compete with audio or the UI.
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Age of the UI thread's last heartbeat, in ms. 0 when it never ticked.</summary>
    public static long LastTickAgeMs() => AgeMs(UiBreadcrumb.LastTickTicks);

    /// <summary>
    /// Begin measuring. Start-up is not a stall: opening the database, loading
    /// settings, enumerating audio endpoints and building the window are work the
    /// launch asked for, and the heartbeat timer cannot tick until the dispatcher
    /// pumps anyway. Until this is called the sampler only sleeps, so the clock
    /// cannot include construction. Called once from the window's
    /// <c>ContentRendered</c>, when a real frame has rendered with the UI live.
    /// Idempotent and a no-op when the watchdog is disabled.
    /// </summary>
    public static void Arm()
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        Volatile.Write(ref _armedTicks, Stopwatch.GetTimestamp());
    }

    private static void SampleLoop()
    {
        var threshold = ThresholdMs;
        // A few samples per threshold, capped at 4/s: detection lands within 250 ms
        // of the deadline without the sampler ever being busy.
        var periodMs = Math.Clamp(threshold / 8, 50, 250);

        while (true)
        {
            Thread.Sleep(periodMs);
            // Not armed yet: the window is still being built, which is not the
            // failure this net exists for.
            var armTicks = Volatile.Read(ref _armedTicks);
            if (armTicks == 0) continue;

            // Starvation is measured from whichever came later: the last real UI
            // tick, or the moment the net was armed.
            var ageMs = AgeMs(Math.Max(UiBreadcrumb.LastTickTicks, armTicks));
            if (!ShouldFire(ageMs, threshold)) continue;
            if (Interlocked.Exchange(ref _fired, 1) != 0) return;

            var reportPath = Report(ageMs, threshold);

            // Give the report a chance to reach the disk before the process dies.
            // Nothing user-visible happens between the decision and the kill.
            Thread.Sleep(250);
            KillHard(ExitCode, reportPath);
        }
    }

    /// <summary>
    /// Write the diagnostic file and one bounded log line. Returns the file path,
    /// or "" when the file could not be written (the log line still gets out).
    /// </summary>
    private static string Report(long ageMs, int thresholdMs)
    {
        var path = "";
        try
        {
            path = WriteDiagnostic(ageMs, thresholdMs);
        }
        catch (Exception ex)
        {
            TryStderr("ui watchdog: diagnostic write failed: " + ex.GetType().Name + ": " + ex.Message);
        }

        var held = UiBreadcrumb.Held;
        var heldMs = held.Length == 0 ? 0 : AgeMs(UiBreadcrumb.HeldSinceTicks);
        var line =
            $"ui watchdog: UI thread silent {ageMs} ms (threshold {thresholdMs} ms); " +
            (held.Length == 0 ? "no breadcrumb held" : $"held breadcrumb \"{held}\" for {heldMs} ms") +
            (path.Length == 0 ? "; diagnostic file FAILED" : $"; diagnostics {path}") +
            $"; terminating pid {Environment.ProcessId} with exit code {ExitCode}";

        // Bounded: the log's gate may be held by the thread that stalled. Skipping
        // the line beats joining the stall we are supposed to end.
        if (!Log.TryInfo(line, 200)) TryStderr(line);
        return path;
    }

    private static string WriteDiagnostic(long ageMs, int thresholdMs)
    {
        var dir = Path.Combine(AppPaths.Home, "diagnostics");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(
            dir, "ui-stall-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");

        var held = UiBreadcrumb.Held;
        var heldMs = held.Length == 0 ? 0 : AgeMs(UiBreadcrumb.HeldSinceTicks);

        var sb = new StringBuilder(4096);
        sb.AppendLine("JockeyBox UI-thread stall report");
        sb.AppendLine("written (UTC)      " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("staleness (ms)     " + ageMs.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("threshold (ms)     " + thresholdMs.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("breadcrumb         " + (held.Length == 0 ? "(none held)" : held));
        sb.AppendLine("breadcrumb held ms " + heldMs.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("ui tick age (ms)   " + LastTickAgeMs().ToString(CultureInfo.InvariantCulture));

        using var self = Process.GetCurrentProcess();
        sb.AppendLine("pid                " + self.Id.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("exe                " + Safe(() => self.MainModule?.FileName));
        sb.AppendLine("version            " + Safe(() => VersionOf(self)));
        sb.AppendLine("exe md5            " + Safe(() => Md5Of(self)));
        sb.AppendLine("working set (MB)   " + (self.WorkingSet64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("handles            " + Safe(() => self.HandleCount.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine("threads            " + Safe(() => self.Threads.Count.ToString(CultureInfo.InvariantCulture)));

        sb.AppendLine();
        sb.AppendLine("Threads (OS view: id, state, wait reason, cpu ms):");
        try
        {
            foreach (ProcessThread t in self.Threads)
            {
                sb.AppendLine("  " + Safe(() =>
                {
                    string cpu;
                    try { cpu = t.TotalProcessorTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture); }
                    catch { cpu = "?"; }
                    string state;
                    try { state = t.ThreadState.ToString(); }
                    catch { state = "?"; }
                    string wait;
                    try { wait = t.WaitReason.ToString(); }
                    catch { wait = "?"; }
                    return $"{t.Id,6}  {state,-12} {wait,-18} {cpu}";
                }));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  (thread enumeration failed: " + ex.GetType().Name + ")");
        }

        sb.AppendLine();
        sb.AppendLine("Watchdog thread managed stack (Environment.StackTrace):");
        sb.AppendLine(Safe(() => Environment.StackTrace));

        sb.AppendLine();
        sb.AppendLine("What this file proves: the UI thread stopped calling NoteUiTick for the");
        sb.AppendLine("staleness above while the breadcrumb was held for the duration shown, so");
        sb.AppendLine("the app was in that operation when it stopped responding.");
        sb.AppendLine();
        sb.AppendLine("What it does NOT prove: where inside that operation the UI thread is");
        sb.AppendLine("blocked. .NET exposes no way to walk another thread's managed stack from");
        sb.AppendLine("inside the process (Environment.StackTrace describes only its caller), so");
        sb.AppendLine("this file carries the OS thread table and the watchdog's own stack, not");
        sb.AppendLine("the wedged thread's frames. The breadcrumb plus the operation's own code");
        sb.AppendLine("is the evidence; a stack of the stall would need a profiler or a dump.");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static void KillHard(int exitCode, string reportPath)
    {
        TryStderr($"ui watchdog: killing pid {Environment.ProcessId} (exit {exitCode}); report {(reportPath.Length == 0 ? "not written" : reportPath)}");
        try
        {
            // Process.Kill() makes exactly this call; TerminateProcess lets us pick
            // the code, so Event Viewer and a harness can tell a watchdog kill (86)
            // from a crash (1) or a clean exit (0).
            if (Native.TerminateProcess(Native.GetCurrentProcess(), (uint)exitCode)) return;
        }
        catch
        {
            // Fall through to the runtime's own kill.
        }

        try { Process.GetCurrentProcess().Kill(); } catch { /* nothing left to try */ }
        Environment.Exit(exitCode);
    }

    private static long AgeMs(long sinceTicks)
    {
        if (sinceTicks == 0) return 0;
        var now = Stopwatch.GetTimestamp();
        if (sinceTicks >= now) return 0;
        return (long)((now - sinceTicks) * 1000.0 / Stopwatch.Frequency);
    }

    private static string VersionOf(Process p)
    {
        var exe = p.MainModule?.FileName;
        return exe == null ? "?" : (FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?");
    }

    private static string Md5Of(Process p)
    {
        var exe = p.MainModule?.FileName;
        if (exe == null) return "?";
        using var stream = File.OpenRead(exe);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? "?"; }
        catch (Exception ex) { return "? (" + ex.GetType().Name + ")"; }
    }

    private static void TryStderr(string line)
    {
        try { Console.Error.WriteLine(line); } catch { /* no console in a windowed launch */ }
    }

    private static class Native
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);
    }
}
