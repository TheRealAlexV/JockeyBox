using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using JockeyBox.Tools;
using Xunit;
using Xunit.Abstractions;

namespace JockeyBox.Tests;

/// <summary>
/// Proves the stall watchdog can see a stall before anyone trusts its numbers.
/// Disposable diagnostic branch: these tests exist to validate the instrument,
/// not to guard product behaviour.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class StallWatchTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;
    private readonly string? _prevHome;

    public StallWatchTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "jb-stallwatch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        // Keep every log write out of the operator's %APPDATA%\JockeyBox.
        _prevHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _prevHome);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A real stall: hold the log gate for ~300 ms while a line waits for it.
    /// The recorder must report the wait, not zero.
    /// </summary>
    [Fact]
    public void LogGateBlockedFor300ms_IsReportedInMicroseconds()
    {
        string csv = Csv();
        StallWatch.StartForTest(csv);

        object gate = typeof(Log)
            .GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        using var entered = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                entered.Set();
                Thread.Sleep(300);
            }
        }) { IsBackground = true, Name = "stall-holder" };
        holder.Start();

        _out.WriteLine($"armed={StallWatch.Enabled}");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "holder never took the gate");
        Log.Info("probe line that has to wait for the log gate");
        holder.Join(TimeSpan.FromSeconds(5));

        StallWatch.SampleOnce();
        StallWatch.Stop();

        _out.WriteLine("--- csv ---");
        _out.WriteLine(File.ReadAllText(csv));
        long gateUs = Column(csv, "logGateMaxUs");
        _out.WriteLine($"logGateMaxUs={gateUs} logGateCount={Column(csv, "logGateCount")}");
        Assert.True(gateUs >= 250_000, $"logGateMaxUs was {gateUs} us, expected >= 250000");
    }

    /// <summary>Nothing stalls: the instrument must report near-zero, not noise.</summary>
    [Fact]
    public void QuietRun_StaysNearZero()
    {
        string csv = Csv();
        StallWatch.StartForTest(csv);
        Thread.Sleep(400);
        StallWatch.SampleOnce();
        StallWatch.Stop();

        long gateUs = Column(csv, "logGateMaxUs");
        long gateCount = Column(csv, "logGateCount");
        long appendUs = Column(csv, "logAppendTotalUs");
        long uiTick = Column(csv, "uiTickMaxMs");

        _out.WriteLine($"logGateMaxUs={gateUs} logGateCount={gateCount} " +
                       $"logAppendTotalUs={appendUs} uiTickMaxMs={uiTick}");

        Assert.Equal(0, gateCount);
        Assert.Equal(0, gateUs);
        Assert.Equal(0, appendUs);
        Assert.Equal(0, uiTick);
    }

    /// <summary>
    /// Zero allocation, zero lock, no formatting: the added per-line cost is two
    /// timestamps and two Interlocked updates. Timed here so the report can quote it.
    /// </summary>
    [Fact]
    public void HotPathCost_IsNanosecondsPerLine()
    {
        const int n = 1_000_000;
        StallWatch.StartForTest(Csv());

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            long ts = Stopwatch.GetTimestamp();
            StallWatch.RecordLogGate(ts);
            StallWatch.RecordLogAppend(ts);
        }
        sw.Stop();
        StallWatch.Stop();

        double ns = sw.Elapsed.TotalMilliseconds * 1_000_000 / n;
        _out.WriteLine($"{n:N0} hook pairs in {sw.Elapsed.TotalMilliseconds:F1} ms = {ns:F1} ns per log line");
        Assert.True(ns < 2000, $"per-line cost {ns:F1} ns is not negligible");
    }

    /// <summary>
    /// The instrument must not be the disease: an ordinary log workload with the
    /// watchdog off versus on. Floored at the fastest of three rounds to blunt disk noise.
    /// </summary>
    [Fact]
    public void OrdinaryLogWorkload_WatchdogOffVersusOn()
    {
        // Interleaved rounds: disk and AV drift across a run, so comparing
        // three off-rounds against three later on-rounds would measure the drift.
        double off = double.MaxValue, hooksOnly = double.MaxValue, full = double.MaxValue;
        var modes = new bool?[] { null, false, true };
        for (int round = 0; round < 3; round++)
        {
            // Rotate the order: whichever mode runs first on a cold file looks fastest.
            for (int k = 0; k < modes.Length; k++)
            {
                bool? mode = modes[(k + round) % modes.Length];
                double t = TimeLogWork(mode);
                if (mode is null) off = Math.Min(off, t);
                else if (mode == false) hooksOnly = Math.Min(hooksOnly, t);
                else full = Math.Min(full, t);
            }
        }
        _out.WriteLine($"400 log lines: off={off:F1} ms hooksOnly={hooksOnly:F1} ms " +
                       $"full={full:F1} ms (delta off->full {full - off:F2} ms)");
        Assert.True(full < off * 2 + 50, $"watchdog cost {full - off:F1} ms is not negligible");
    }

    private double TimeLogWork(bool? sampler)
    {
        if (sampler is bool s) StallWatch.StartForTest(Csv(), s);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 400; i++) Log.Info($"overhead probe {i}");
        sw.Stop();
        StallWatch.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private string Csv() =>
        Path.Combine(_root, "stall-" + Guid.NewGuid().ToString("N")[..6] + ".csv");

    private static long Column(string csv, string name)
    {
        string[] lines = File.ReadAllLines(csv);
        string[] header = lines[0].Split(',');
        int idx = Array.IndexOf(header, name);
        Assert.True(idx >= 0, $"no column {name}");

        // Last data line that is a sample (skip START/STOP rows, which still carry
        // the same shape but reset counters, so take the one before the first STOP).
        long value = 0;
        for (int i = 1; i < lines.Length; i++)   // 0 is the header
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            string[] cells = line.Split(',');
            if (cells.Length != header.Length) continue;
            if (cells[^1] is "START" or "STOP") continue;
            value = long.Parse(cells[idx], CultureInfo.InvariantCulture);
        }
        return value;
    }
}
