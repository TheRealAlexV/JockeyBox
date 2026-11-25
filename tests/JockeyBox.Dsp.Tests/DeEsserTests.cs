namespace JockeyBox.Dsp.Tests;

public class DeEsserTests
{
    private const int SampleRate = 48000;

    // One second: the 1 ms detector and 50 ms release have long settled before the second
    // half, which is the window SteadyStateGainDb measures.
    private const int Length = SampleRate;

    private static DeEsser Enabled() => new(SampleRate, DeEsserParameters.Neutral with { Enabled = true });

    [Fact]
    public void DeEsser_ducks_a_7kHz_tone_above_threshold_and_leaves_a_1kHz_tone_alone()
    {
        // A 7 kHz tone sits in the sidechain band, so the high band must be ducked.
        double highGainDb = TestSignals.SteadyStateGainDb(Enabled(), 7000, SampleRate, Length);

        // A 1 kHz tone never reaches the detector and lives entirely in the low band, so the
        // subtraction split must leave it alone.
        double lowGainDb = TestSignals.SteadyStateGainDb(Enabled(), 1000, SampleRate, Length);

        Assert.True(highGainDb <= -6.0,
            $"Expected the 7 kHz tone to lose at least 6 dB, measured {highGainDb:F2} dB");
        Assert.True(Math.Abs(lowGainDb) < 0.1,
            $"Expected the 1 kHz tone within 0.1 dB, measured {lowGainDb:F4} dB");
    }

    [Fact]
    public void DeEsser_at_neutral_is_bit_transparent()
    {
        var deEsser = new DeEsser(SampleRate, DeEsserParameters.Neutral);
        Assert.True(deEsser.IsNeutral, "Neutral parameters must report IsNeutral");
        Assert.False(deEsser.Bypass, "The neutral path must not depend on Bypass");

        var input = TestSignals.Chirp(Length, 100, 14000, SampleRate, 0.5);
        var output = (float[])input.Clone();

        deEsser.Process(output);

        TestSignals.AssertBitExact(input, output, "the neutral de-esser must leave the buffer bit-identical");
    }
}
