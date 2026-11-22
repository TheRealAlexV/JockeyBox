namespace JockeyBox.Dsp.Tests;

public class TenBandEqTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Flat_is_bit_exact()
    {
        var eq = new TenBandEq(SampleRate);
        Assert.True(eq.IsFlat);

        var input = TestSignals.WhiteNoise(SampleRate, seed: 20261122);
        var output = (float[])input.Clone();

        eq.Process(output);

        TestSignals.AssertBitExact(input, output, "a flat 10-band EQ must not touch the buffer");
    }

    [Fact]
    public void Bypassed_is_bit_exact()
    {
        var eq = new TenBandEq(SampleRate) { Bypass = true };
        for (int band = 0; band < TenBandEq.CentresHz.Length; band++)
        {
            eq.SetGainDb(band, 9);
        }

        Assert.False(eq.IsFlat);
        var input = TestSignals.WhiteNoise(SampleRate, seed: 7);
        var output = (float[])input.Clone();

        eq.Process(output);

        TestSignals.AssertBitExact(input, output, "a bypassed EQ must not touch the buffer");
    }

    [Fact]
    public void Band_gain_changes_the_signal()
    {
        var eq = new TenBandEq(SampleRate);
        eq.SetGainDb(5, 6); // 1 kHz band

        var input = TestSignals.Sine(SampleRate * 2, 1000, SampleRate, 0.25);
        var output = (float[])input.Clone();
        eq.Process(output);

        int start = SampleRate;
        double measuredDb = TestSignals.RmsDb(output.AsSpan(start, SampleRate))
                            - TestSignals.RmsDb(input.AsSpan(start, SampleRate));

        Assert.True(Math.Abs(measuredDb - 6) <= 0.5, $"1 kHz band should add 6 dB, measured {measuredDb:F3} dB");
    }

    [Fact]
    public void Gain_is_clamped_to_plus_minus_fifteen_db()
    {
        var eq = new TenBandEq(SampleRate);
        eq.SetGainDb(0, 40);
        Assert.Equal(15, eq.GainDb(0));
        eq.SetGainDb(0, -40);
        Assert.Equal(-15, eq.GainDb(0));
    }
}
