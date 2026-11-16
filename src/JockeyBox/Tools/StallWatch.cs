using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace JockeyBox.Tools;

/// <summary>
/// Diagnostic-only stall watchdog. Measurement, not behaviour.
///
/// Inert unless <c>JOCKEYBOX_STALL_WATCH</c> holds an absolute CSV path. With the
/// variable unset <see cref="Start"/> allocates nothing, starts no thread, opens no
/// file, and every hot-path hook below is one predictable branch on a static bool.
///
/// When armed it samples once every 250 ms from one BelowNormal background thread
/// and appends a CSV line with AutoFlush on, so a hard power-off still leaves a
/// readable tail. The log measurements are Stopwatch timestamps plus Interlocked
/// counters: no allocation, no lock, no formatting on the log path.
/// </summary>
public static class StallWatch
{
    public const string EnvVar = "JOCKEYBOX_STALL_WATCH";

    /// <summary>True only when a CSV path was supplied. Hot path reads this first.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Audio render frames produced. Written by AudioEngine.RenderLoop.</summary>
    public static long Frames;

    // Raw Stopwatch ticks, reset with Interlocked.Exchange at each sample.
    private static long _logGateMaxTicks;
    private static long _logGateCount;
    private static long _logAppendMaxTicks;
    private static long _logAppendTotalTicks;

    private static long _uiTickLastTs;
    private static long _uiTickMaxTicks;
    private static long _uiTimerNextTs;
    private static bool _uiProbePrimed;
    private static int _uiLateMs;

    /// <summary>Set by the owner of the WriteQueue so the sampler can report its depth.</summary>
    public static Func<int>? QueueDepthProvider { get; set; }

    private static StreamWriter? _writer;
    private static Thread? _thread;
    private static volatile bool _running;
    private static DispatcherTimer? _uiProbe;
    private static Process? _proc;
    private static long _lastFrames;
    private static long _lastSampleTs;
    private static int _tick;
    private static int _intervalMs = 250;

    private const string Header =
        "timestamp,frames,framesDelta,uiLateMs,logGateMaxUs,logGateCount," +
        "logAppendMaxUs,logAppendTotalUs,logBytes,uiTickMaxMs,queueDepth,handles,privMB,marker";

    // ---- hot path -------------------------------------------------------

    /// <summary>One predictable branch when the watchdog is off.</summary>
    public static void RecordLogGate(long startTs)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _logGateCount);
        Max(ref _logGateMaxTicks, Stopwatch.GetTimestamp() - startTs);
    }

    /// <summary>One predictable branch when the watchdog is off.</summary>
    public static void RecordLogAppend(long startTs)
    {
        if (!Enabled) return;
        long ticks = Stopwatch.GetTimestamp() - startTs;
        Interlocked.Add(ref _logAppendTotalTicks, ticks);
        Max(ref _logAppendMaxTicks, ticks);
    }

    /// <summary>Called at the top of MainViewModel.OnUiTick.</summary>
    public static void NoteUiTick()
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _uiTickLastTs);
        Interlocked.Exchange(ref _uiTickLastTs, now);
        if (last != 0) Max(ref _uiTickMaxTicks, now - last);
    }

    private static void Max(ref long target, long value)
    {
        long cur = Interlocked.Read(ref target);
        while (value > cur)
        {
            long prev = Interlocked.CompareExchange(ref target, value, cur);
            if (prev == cur) return;
            cur = prev;
        }
    }

    // ---- lifecycle ------------------------------------------------------

    /// <summary>No-op without the env var. Call once, on the UI thread.</summary>
    public static void Start()
    {
        if (Enabled) return;
        string? path = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path))
        {
            Console.Error.WriteLine($"{EnvVar} must be an absolute path; watchdog disabled.");
            return;
        }
        StartCore(path, withUiProbe: true);
    }

    /// <summary>Test entry point: arms the recorder without the env var or UI probe.</summary>
    public static void StartForTest(string path, bool sampler = true) =>
        StartCore(path, withUiProbe: false, withSampler: sampler);

    private static void StartCore(string path, bool withUiProbe, bool withSampler = true)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            bool needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                     { AutoFlush = true };
            if (needHeader) _writer.WriteLine(Header);

            Enabled = true;
            _proc = Process.GetCurrentProcess();
            _lastFrames = Interlocked.Read(ref Frames);
            _lastSampleTs = Stopwatch.GetTimestamp();
            WriteLine("START");

            if (withUiProbe)
            {
                _uiTimerNextTs = Stopwatch.GetTimestamp() + Ms(_intervalMs);
                _uiProbe = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(_intervalMs),
                };
                _uiProbe.Tick += (_, _) => ProbeUiTimer();
                _uiProbe.Start();
            }

            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "JockeyBox.StallWatch",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }
        catch (Exception ex)
        {
            // A diagnostic must never take the app down with it.
            Enabled = false;
            try { _writer?.Dispose(); } catch { /* best effort */ }
            _writer = null;
            Console.Error.WriteLine($"stall watch failed to start: {ex.Message}");
        }
    }

    public static void Stop()
    {
        if (!Enabled) return;
        WriteLine("STOP");
        _running = false;
        try { _thread?.Join(1000); } catch { /* best effort */ }
        try { _uiProbe?.Stop(); } catch { /* best effort */ }
        Enabled = false;
        try { _writer?.Dispose(); } catch { /* best effort */ }
        _writer = null;
    }

    private static void Loop()
    {
        long interval = Ms(_intervalMs);
        long next = Stopwatch.GetTimestamp();
        while (_running)
        {
            WriteLine("");
            next += interval;
            long waitMs = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (waitMs > 0) Thread.Sleep((int)waitMs);
        }
    }

    /// <summary>Records how late the 250 ms UI probe actually fired.</summary>
    private static void ProbeUiTimer()
    {
        long now = Stopwatch.GetTimestamp();
        if (_uiProbePrimed)
        {
            long lateMs = (now - _uiTimerNextTs) * 1000 / Stopwatch.Frequency;
            _uiLateMs = (int)Math.Max(0, lateMs);
        }
        // The first tick lands whenever the dispatcher loop first runs, which has
        // nothing to do with UI health; it primes the schedule, it is not a sample.
        _uiProbePrimed = true;
        _uiTimerNextTs = now + Ms(_intervalMs);
    }

    // ---- sampling -------------------------------------------------------

    /// <summary>One CSV sample. Public so the selftest can drive it deterministically.</summary>
    public static void SampleOnce() => WriteLine("");

    private static void WriteLine(string mark)
    {
        StreamWriter? w = _writer;
        if (w == null) return;

        long now = Stopwatch.GetTimestamp();
        long frames = Interlocked.Read(ref Frames);
        long framesDelta = frames - _lastFrames;
        _lastFrames = frames;

        long gateTicks = Interlocked.Exchange(ref _logGateMaxTicks, 0);
        long gateCount = Interlocked.Exchange(ref _logGateCount, 0);
        long appendMaxTicks = Interlocked.Exchange(ref _logAppendMaxTicks, 0);
        long appendTotalTicks = Interlocked.Exchange(ref _logAppendTotalTicks, 0);
        long uiTickMaxTicks = Interlocked.Exchange(ref _uiTickMaxTicks, 0);

        int uiLate = _uiLateMs;
        _uiLateMs = 0;

        string marker = mark;
        if (marker.Length == 0)
        {
            if (framesDelta == 0) marker = "STALL-FRAMES";
            else if (uiLate > 500) marker = "UI-LATE";
        }

        long logBytes = 0;
        try { logBytes = File.Exists(Log.FilePath) ? new FileInfo(Log.FilePath).Length : 0; }
        catch { /* log may be rotating under us */ }

        int queue = 0;
        try { queue = QueueDepthProvider?.Invoke() ?? 0; } catch { /* diagnostics only */ }

        long handles = 0, privMB = 0;
        try
        {
            // Process.Refresh is the only expensive part of a sample; once a second
            // is plenty to correlate growth with a stall.
            if (_proc != null && (_tick++ & 3) == 0) _proc.Refresh();
            handles = _proc?.HandleCount ?? 0;
            privMB = (_proc?.PrivateMemorySize64 ?? 0) / (1024 * 1024);
        }
        catch { /* process may be exiting */ }

        _lastSampleTs = now;

        string line = string.Join(',',
            DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
            frames.ToString(CultureInfo.InvariantCulture),
            framesDelta.ToString(CultureInfo.InvariantCulture),
            uiLate.ToString(CultureInfo.InvariantCulture),
            ToUs(gateTicks).ToString(CultureInfo.InvariantCulture),
            gateCount.ToString(CultureInfo.InvariantCulture),
            ToUs(appendMaxTicks).ToString(CultureInfo.InvariantCulture),
            ToUs(appendTotalTicks).ToString(CultureInfo.InvariantCulture),
            logBytes.ToString(CultureInfo.InvariantCulture),
            (ToUs(uiTickMaxTicks) / 1000).ToString(CultureInfo.InvariantCulture),
            queue.ToString(CultureInfo.InvariantCulture),
            handles.ToString(CultureInfo.InvariantCulture),
            privMB.ToString(CultureInfo.InvariantCulture),
            marker);

        w.WriteLine(line);
    }

    private static long ToUs(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;

    private static long Ms(int ms) => ms * Stopwatch.Frequency / 1000;
}
