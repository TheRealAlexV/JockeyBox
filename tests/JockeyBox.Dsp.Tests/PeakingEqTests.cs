namespace JockeyBox.Dsp.Tests;

public class PeakingEqTests
{
    private const int SampleRate = 48000;

    [Theory]
    [InlineData(6.0)]
    [InlineData(-6.0)]
    public void Gain_at_the_centre_frequency_matches_the_requested_db(double gainDb)
    {
        var eq = new PeakingEq(SampleRate, frequencyHz: 1000, q: 1.41, gainDb: gainDb);

        double measuredDb = TestSignals.SteadyStateGainDb(eq, 1000, SampleRate, SampleRate);

        Assert.True(Math.Abs(measuredDb - gainDb) <= 0.1,
            $"A peaking EQ set to {gainDb:F1} dB should measure {gainDb:F1} dB at its centre, measured {measuredDb:F3} dB");
    }

    [Fact]
    public void Zero_gain_leaves_the_buffer_bit_exact()
    {
        var eq = new PeakingEq(SampleRate, frequencyHz: 1000, q: 1.41, gainDb: 0);

        var input = TestSignals.WhiteNoise(4096, seed: 23);
        var output = (float[])input.Clone();

        eq.Process(output);

        TestSignals.AssertBitExact(input, output, "a peaking EQ at 0 dB must be an exact identity");
    }

    [Fact]
    public void Bypass_leaves_the_buffer_bit_exact()
    {
        var eq = new PeakingEq(SampleRate, frequencyHz: 1000, q: 1.41, gainDb: 6) { Bypass = true };

        var input = TestSignals.WhiteNoise(4096, seed: 24);
        var output = (float[])input.Clone();

        eq.Process(output);

        TestSignals.AssertBitExact(input, output, "a bypassed peaking EQ must leave the buffer untouched");
    }
}
