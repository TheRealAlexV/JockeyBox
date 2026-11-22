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

    [Fact]
    public void BandQ_confines_a_boost_to_about_one_octave()
    {
        // BandQ is the one-octave Q; a wider band would carry the boost much further out.
        Assert.Equal(1.41, TenBandEq.BandQ);

        var centre = new TenBandEq(SampleRate);
        centre.SetGainDb(5, 12); // 1 kHz band, +12 dB
        double atCentre = TestSignals.SteadyStateGainDb(centre, 1000, SampleRate, SampleRate);
        Assert.True(Math.Abs(atCentre - 12) <= 0.5,
            $"a +12 dB band should measure +12 dB at its centre, measured {atCentre:F3} dB");

        var oneOctaveUp = new TenBandEq(SampleRate);
        oneOctaveUp.SetGainDb(5, 12);
        double atTwoK = TestSignals.SteadyStateGainDb(oneOctaveUp, 2000, SampleRate, SampleRate);
        Assert.True(atTwoK > 0.5 && atTwoK < 12.0,
            $"one octave up the boost must be well below the centre, measured {atTwoK:F3} dB");

        var twoOctavesUp = new TenBandEq(SampleRate);
        twoOctavesUp.SetGainDb(5, 12);
        double atFourK = TestSignals.SteadyStateGainDb(twoOctavesUp, 4000, SampleRate, SampleRate);
        Assert.True(Math.Abs(atFourK) <= 1.0,
            $"two octaves up a Q 1.41 band must be flat within 1 dB, measured {atFourK:F3} dB");
    }

    [Fact]
    public void Higher_Q_narrows_the_affected_band()
    {
        // Same +12 dB boost and same off-centre probe: a higher Q must reach less far out.
        var wide = new PeakingEq(SampleRate, frequencyHz: 1000, q: 0.7, gainDb: 12);
        var narrow = new PeakingEq(SampleRate, frequencyHz: 1000, q: 2.8, gainDb: 12);

        double wideDb = TestSignals.SteadyStateGainDb(wide, 2000, SampleRate, SampleRate);
        double narrowDb = TestSignals.SteadyStateGainDb(narrow, 2000, SampleRate, SampleRate);

        Assert.True(narrowDb < wideDb,
            $"Q 2.8 must boost 2 kHz less than Q 0.7 does, got {narrowDb:F3} dB vs {wideDb:F3} dB");
        Assert.True(wideDb > 3.0 && narrowDb < 2.0,
            $"Q 0.7 should still lift 2 kHz while Q 2.8 leaves it near flat, got {wideDb:F3} / {narrowDb:F3} dB");
    }
}
