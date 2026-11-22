namespace JockeyBox.Dsp.Tests;

public class HighPassFilterTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Gain_at_cutoff_is_minus_three_db()
    {
        var filter = new HighPassFilter(SampleRate, frequencyHz: 1000, q: 0.7071067811865476);

        double measuredDb = TestSignals.SteadyStateGainDb(filter, 1000, SampleRate, SampleRate);

        Assert.True(Math.Abs(measuredDb + 3.0) <= 0.5,
            $"A Q 0.7071 high-pass should be -3 dB at its cutoff, measured {measuredDb:F3} dB");
    }

    [Fact]
    public void Gain_two_octaves_below_cutoff_is_below_minus_twenty_db()
    {
        var filter = new HighPassFilter(SampleRate, frequencyHz: 1000);

        double measuredDb = TestSignals.SteadyStateGainDb(filter, 250, SampleRate, SampleRate);

        Assert.True(measuredDb < -20.0,
            $"A 12 dB/oct high-pass should be below -20 dB two octaves down, measured {measuredDb:F3} dB");
    }

    [Fact]
    public void Bypass_leaves_the_buffer_bit_exact()
    {
        var filter = new HighPassFilter(SampleRate, frequencyHz: 1000) { Bypass = true };

        var input = TestSignals.WhiteNoise(4096, seed: 21);
        var output = (float[])input.Clone();

        filter.Process(output);

        TestSignals.AssertBitExact(input, output, "a bypassed high-pass must leave the buffer untouched");
    }
}
