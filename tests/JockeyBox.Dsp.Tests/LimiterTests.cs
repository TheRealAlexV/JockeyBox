namespace JockeyBox.Dsp.Tests;

public class LimiterTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Declared_latency_equals_measured_group_delay()
    {
        var limiter = new Limiter(SampleRate, thresholdDb: 0, lookaheadMs: 5);
        Assert.Equal(240, limiter.LatencyFrames);

        var buffer = new float[4800];
        buffer[0] = 1f;

        limiter.Process(buffer);

        int peak = TestSignals.IndexOfPeak(buffer);
        Assert.Equal(limiter.LatencyFrames, peak);
    }

    [Fact]
    public void Signal_below_the_ceiling_passes_through_unattenuated()
    {
        var limiter = new Limiter(SampleRate, thresholdDb: 0, lookaheadMs: 5);
        var input = TestSignals.Sine(4800, 1000, SampleRate, 0.5);
        var output = (float[])input.Clone();

        limiter.Process(output);

        // Everything is delayed by the lookahead, so compare the delayed tail against the input.
        int latency = limiter.LatencyFrames;
        for (int i = latency; i < output.Length; i++)
        {
            Assert.Equal(input[i - latency], output[i], 6);
        }
    }
}
