namespace JockeyBox.Dsp.Tests;

public class CompressorTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Gain_reduction_is_within_half_a_db()
    {
        var compressor = new Compressor(SampleRate, thresholdDb: -20, ratio: 4,
            attackMs: 10, releaseMs: 100, kneeDb: 6, makeupDb: 0);

        // -14 dBFS RMS is 6 dB above the -20 dBFS threshold; at ratio 4 the expected
        // reduction is 6 * (1 - 1/4) = 4.5 dB, which sits inside 4.0..5.0.
        double amplitude = Math.Pow(10, -14 / 20.0) * Math.Sqrt(2);
        var input = TestSignals.Sine(SampleRate, 1000, SampleRate, amplitude);
        var output = (float[])input.Clone();

        compressor.Process(output);

        // Measure 200..400 ms, after the 10 ms attack has settled.
        int start = SampleRate / 5;
        int count = SampleRate / 5;
        double measuredDb = TestSignals.RmsDb(output.AsSpan(start, count))
                            - TestSignals.RmsDb(input.AsSpan(start, count));

        Assert.InRange(measuredDb, -5.0, -4.0);
        Assert.InRange(compressor.GainReductionDb, -5.0, -4.0);
    }

    [Fact]
    public void Signal_below_threshold_is_not_compressed()
    {
        var compressor = new Compressor(SampleRate, thresholdDb: -20, ratio: 4,
            attackMs: 10, releaseMs: 100, kneeDb: 6, makeupDb: 0);

        // -30 dBFS RMS: below threshold, so gain stays at unity.
        var input = TestSignals.Sine(SampleRate, 1000, SampleRate, Math.Pow(10, -30 / 20.0) * Math.Sqrt(2));
        var output = (float[])input.Clone();

        compressor.Process(output);

        double measuredDb = TestSignals.RmsDb(output)
                            - TestSignals.RmsDb(input);
        Assert.True(Math.Abs(measuredDb) <= 0.1, $"Expected no gain change, measured {measuredDb:F3} dB");
        Assert.True(Math.Abs(compressor.GainReductionDb) <= 0.1);
    }
}
