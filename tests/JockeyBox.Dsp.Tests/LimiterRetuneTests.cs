namespace JockeyBox.Dsp.Tests;

/// <summary>
/// A limiter must be retunable while it is running: the engine changes the ceiling or the
/// release from an API call on a limiter that is already processing blocks.
/// </summary>
public class LimiterRetuneTests
{
    private const int SampleRate = 48000;
    private const int LookaheadFrames = 240;

    [Fact]
    public void Ceiling_change_applies_to_a_running_limiter_within_one_block()
    {
        var limiter = new Limiter(SampleRate, thresholdDb: 0, lookaheadMs: 5, releaseMs: 50);
        Assert.Equal(LookaheadFrames, limiter.LatencyFrames);

        // 6 dB over a 0 dBFS ceiling, so the limiter is firmly in limiting and gain is pinned.
        var loud = TestSignals.Sine(4800, 1000, SampleRate, 2.0);
        limiter.Process(loud);

        double before = MaxAbs(loud);
        Assert.InRange(before, 0.95, 1.05);

        var block = TestSignals.Sine(LookaheadFrames, 1000, SampleRate, 2.0);

        // Both passes go through the real retune path: the first sets -3 dB, the counted second
        // sets -6 dB, so neither the setter's coefficient rebuild nor its tiered JIT is charged
        // to the measurement. Warming with the current value would only exercise the no-op path.
        double ceiling = -3;
        long allocated = AllocationAssert.Measure(() =>
        {
            limiter.CeilingDb = ceiling;
            ceiling = -6;
            limiter.Process(block);
        });

        // A no-op setter leaves the output at ~0 dBFS and this assertion fails.
        double after = MaxAbs(block);
        Assert.True(after <= 0.5012 * 1.01, $"peak after a -6 dB retune was {after:F4}, expected <= -6 dBFS");

        Assert.Equal(-6, limiter.CeilingDb, 9);
        Assert.Equal(LookaheadFrames, limiter.LatencyFrames);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Shorter_release_recovers_towards_unity_faster_than_a_longer_one()
    {
        static double TailPeak(double releaseMs)
        {
            var limiter = new Limiter(SampleRate, thresholdDb: 0, lookaheadMs: 5, releaseMs: releaseMs);

            // Pin the gain at 0.5 (2.0 peak against a 1.0 ceiling).
            var burst = new float[4800];
            Array.Fill(burst, 2.0f);
            limiter.Process(burst);

            // Drop to a quiet signal; the gain climbs back towards unity at the release rate.
            var tail = new float[2400];
            Array.Fill(tail, 0.1f);
            limiter.Process(tail);

            // Skip the lookahead: those frames still carry the burst out of the delay line.
            return MaxAbs(tail.AsSpan(LookaheadFrames));
        }

        double fast = TailPeak(releaseMs: 10);
        double slow = TailPeak(releaseMs: 500);

        Assert.True(fast > 0.09, $"a 10 ms release should have recovered most of the way, got {fast:F4}");
        Assert.True(slow < 0.08, $"a 500 ms release should still be climbing, got {slow:F4}");
        Assert.True(fast > slow * 1.5, $"10 ms tail {fast:F4} should clearly exceed 500 ms tail {slow:F4}");
    }

    private static double MaxAbs(ReadOnlySpan<float> samples)
    {
        double peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double magnitude = Math.Abs(samples[i]);
            if (magnitude > peak) peak = magnitude;
        }

        return peak;
    }
}
