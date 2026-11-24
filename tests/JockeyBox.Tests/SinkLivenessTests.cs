using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The silent stall: a device that keeps reporting Playing while its consumer pulls
/// nothing. Pacing against it stops the whole render loop, so every bus goes quiet
/// with no exception and no log line. These pin the two halves of the fix — the
/// health signal, and the render loop surviving the drop.
///
/// The offline harness cannot cover this: OfflineLoop calls RenderBlock directly and
/// never passes through RenderLoop or its pacing gate.
/// </summary>
public sealed class SinkLivenessTests
{
    private const int Rate = 48_000;
    private const int Block = 480;
    private const int Channels = 2;

    [Fact]
    public void EndpointSink_health_trips_on_a_consumer_that_stops_reading()
    {
        var ring = new JitterRing(Rate, Channels);
        var buffer = new float[Block * Channels];

        // 1. The reader stopped, but we are still inside the stall window.
        var monitor = new DrainMonitor(now: 1_000);
        ring.Read(buffer, 0, buffer.Length);                     // pulled at t = 1000
        Assert.False(monitor.Stalled(ring.ReadCalls, 1_000, EndpointSink.StallTimeoutMs));
        Assert.False(monitor.Stalled(ring.ReadCalls, 1_249, EndpointSink.StallTimeoutMs));
        Assert.Null(monitor.Reason);

        // 2. One millisecond past the window: failed, and it says why.
        Assert.True(monitor.Stalled(ring.ReadCalls, 1_251, EndpointSink.StallTimeoutMs));
        Assert.Contains("pulled no audio", monitor.Reason);

        // 3. A reader that keeps calling Read on an empty ring is healthy forever.
        //    Every call served zero frames — which is exactly why the counter is
        //    invocations, not frames: a starved-but-alive device would look dead.
        var underrun = new DrainMonitor(now: 1_000);
        long framesBefore = ring.DepthFrames;
        for (long t = 1_000; t <= 10_000; t += 10)
        {
            ring.Read(buffer, 0, buffer.Length);
            Assert.False(underrun.Stalled(ring.ReadCalls, t, EndpointSink.StallTimeoutMs));
        }
        Assert.Equal(framesBefore, ring.DepthFrames);             // still empty: 0 frames consumed
        Assert.True(ring.UnderrunFrames > 0);                     // it was genuinely underrunning
        Assert.Null(underrun.Reason);

        // 4. A consumer that never calls Read at all fails at the timeout exactly.
        var never = new DrainMonitor(now: 1_000);
        Assert.False(never.Stalled(0, 1_249, EndpointSink.StallTimeoutMs));
        Assert.True(never.Stalled(0, 1_250, EndpointSink.StallTimeoutMs));
        Assert.Contains("pulled no audio", never.Reason);
    }

    [Fact]
    public async Task Engine_keeps_publishing_the_virtual_ring_while_the_pacing_device_fails()
    {
        string section = "live" + Guid.NewGuid().ToString("N")[..12];
        var ringSink = VirtualBusSink.TryOpen(section, Rate, Channels, Block, out string? error);
        Assert.True(ringSink != null, $"virtual ring did not open: {error}");

        using var engine = new AudioEngine();
        var faults = new ConcurrentQueue<string>();
        var lines = new ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        engine.OutputFault += name => faults.Enqueue(name);
        try
        {
            // Start on the ring alone so the wall clock is the cadence, then hand the
            // loop a pacing sink that will fail the way the live device did.
            engine.AttachSinksForTest(null, ringSink);
            engine.Start();
            Assert.True(engine.IsRunning);

            var pacer = new FakePacer("stalling pacer " + section, failAfterChecks: 5);
            engine.AttachSinksForTest(pacer, ringSink);

            Assert.True(await WaitFor(() => !faults.IsEmpty, 3_000),
                "OutputFault never fired for the failed pacing sink");

            long atFault = ringSink!.BlocksPublished;

            // The point of the fix: publishing continues after the pacer is dropped.
            Assert.True(await WaitFor(() => ringSink.BlocksPublished >= atFault + 3, 3_000),
                $"the virtual ring stopped publishing: {atFault} blocks at the fault, " +
                $"{ringSink.BlocksPublished} now");

            Assert.Same(ringSink, engine.FirstSinkForTest);
            Assert.True(engine.IsRunning);
            Assert.True(pacer.Disposed);
            Assert.Equal(0, pacer.Writes);    // the pacer was paced against, never routed

            Assert.Single(faults);
            Assert.Equal(pacer.DeviceName, faults.ToArray()[0]);

            var warnLines = lines.ToArray()
                .Where(l => l.Contains("WARN") && l.Contains(pacer.DeviceName))
                .ToArray();
            Assert.Single(warnLines);
            Assert.Contains("pulled no audio", warnLines[0]);
        }
        finally
        {
            Log.LineWritten -= Handler;
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// A pacing sink that is not a routed device: the render loop asks it for health
    /// every pass and never writes to it, so the poll count is the honest trigger for
    /// "this device stopped working". It reports the same reason a stalled EndpointSink
    /// records.
    /// </summary>
    private sealed class FakePacer : IAudioSink
    {
        private readonly int _failAfterChecks;
        private int _checks;
        private int _writes;
        private int _disposed;

        public FakePacer(string name, int failAfterChecks)
        {
            DeviceName = name;
            _failAfterChecks = failAfterChecks;
        }

        public string DeviceName { get; }

        public bool HasFailed =>
            Interlocked.Increment(ref _checks) > _failAfterChecks;

        public string? FailureReason =>
            HasFailed ? "the device reported Playing but pulled no audio for 250 ms" : null;

        public int Writes => Volatile.Read(ref _writes);

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Write(float[] interleaved, int offset, int frames) =>
            Interlocked.Increment(ref _writes);

        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}
