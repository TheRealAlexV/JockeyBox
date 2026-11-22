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

    [Fact]
    public void Makeup_gain_is_added_after_compression()
    {
        var compressor = new Compressor(SampleRate, thresholdDb: -20, ratio: 4,
            attackMs: 10, releaseMs: 100, kneeDb: 6, makeupDb: 6);

        // -14 dBFS RMS is 6 dB over the threshold: 4.5 dB of compression, plus 6 dB makeup,
        // leaves +1.5 dB of net gain. The 0.5 dB window absorbs the knee and detector ripple.
        double amplitude = Math.Pow(10, -14 / 20.0) * Math.Sqrt(2);
        var input = TestSignals.Sine(SampleRate, 1000, SampleRate, amplitude);
        var output = (float[])input.Clone();

        compressor.Process(output);

        int start = SampleRate / 5;
        int count = SampleRate / 5;
        double measuredDb = TestSignals.RmsDb(output.AsSpan(start, count))
                            - TestSignals.RmsDb(input.AsSpan(start, count));

        Assert.InRange(measuredDb, 1.0, 2.0);
        Assert.InRange(compressor.MakeupDb, 6.0, 6.0);
        // The gain reduction readout stays pure compression; makeup is applied on output only.
        Assert.InRange(compressor.GainReductionDb, -5.0, -4.0);
    }

    [Fact]
    public void Release_recovers_to_zero_db_reduction_after_two_seconds_of_silence()
    {
        var compressor = new Compressor(SampleRate, thresholdDb: -20, ratio: 4,
            attackMs: 10, releaseMs: 100, kneeDb: 6, makeupDb: 0);

        var loud = TestSignals.Sine(SampleRate, 1000, SampleRate, Math.Pow(10, -14 / 20.0) * Math.Sqrt(2));
        compressor.Process(loud);
        Assert.True(compressor.GainReductionDb < -1.0,
            $"the compressor must actually be reducing before release is tested, read {compressor.GainReductionDb:F3} dB");

        compressor.Process(new float[2 * SampleRate]);

        Assert.True(Math.Abs(compressor.GainReductionDb) <= 0.1,
            $"release should return to 0 dB within 0.1 dB after 2 s of silence, read {compressor.GainReductionDb:F3} dB");
    }

    [Fact]
    public void Threshold_above_the_signal_gives_exactly_zero_reduction()
    {
        var compressor = new Compressor(SampleRate, thresholdDb: 10, ratio: 4,
            attackMs: 10, releaseMs: 100, kneeDb: 6, makeupDb: 0);

        var input = TestSignals.Sine(SampleRate, 1000, SampleRate, Math.Pow(10, -14 / 20.0) * Math.Sqrt(2));
        compressor.Process(input);

        // 24 dB below the threshold is outside even the 6 dB knee, so the target is unity.
        Assert.Equal(0.0, compressor.GainReductionDb);
    }

    [Fact]
    public void Ratio_and_timing_setters_round_trip_and_clamp()
    {
        var compressor = new Compressor(SampleRate);

        compressor.Ratio = 0.5;
        compressor.AttackMs = 0;
        compressor.ReleaseMs = 0;
        compressor.KneeDb = -3;
        compressor.ThresholdDb = -12;

        Assert.True(compressor.Ratio >= 1.0, $"ratio must clamp to 1 or more, read {compressor.Ratio}");
        Assert.True(compressor.AttackMs > 0, $"attack must stay positive, read {compressor.AttackMs}");
        Assert.True(compressor.ReleaseMs > 0, $"release must stay positive, read {compressor.ReleaseMs}");
        Assert.Equal(0.0, compressor.KneeDb);
        Assert.Equal(-12.0, compressor.ThresholdDb);
    }

    [Fact]
    public void Reset_clears_the_envelope_so_a_silent_block_is_not_compressed()
    {
        var compressor = new Compressor(SampleRate, thresholdDb: -20, ratio: 4,
            attackMs: 10, releaseMs: 100, kneeDb: 6, makeupDb: 0);

        compressor.Process(TestSignals.Sine(SampleRate, 1000, SampleRate, Math.Pow(10, -10 / 20.0) * Math.Sqrt(2)));
        Assert.True(compressor.GainReductionDb < -1.0);

        compressor.Reset();

        Assert.Equal(0.0, compressor.GainReductionDb);
    }
}
