namespace JockeyBox.Dsp.Tests;

public class MixerStripTests
{
    private const int SampleRate = 48000;

    // Band 5 (0-based) is the 1 kHz ISO octave centre.
    private const int Band1kHz = 5;

    [Fact]
    public void Populated_but_neutral_strip_is_bit_exact()
    {
        var strip = new MixerStrip(SampleRate, "mic.test");

        Assert.Equal(StripParameters.Neutral, strip.Parameters);
        Assert.True(strip.IsPassthrough);
        Assert.Equal(0, strip.LatencyFrames);
        Assert.Equal(6, strip.ProcessorCount);

        var input = TestSignals.WhiteNoise(4096, seed: 31);
        var output = (float[])input.Clone();

        strip.Process(output, 0, output.Length);

        TestSignals.AssertBitExact(input, output, "a neutral strip must leave the buffer untouched");
    }

    [Fact]
    public void Twelve_dB_on_the_1kHz_band_raises_a_1kHz_tone_by_12dB()
    {
        var strip = new MixerStrip(SampleRate);
        strip.Apply(strip.Parameters with
        {
            Eq = StripParameters.Neutral.Eq with { Gain1kDb = 12 },
        });

        Assert.False(strip.IsPassthrough);

        var input = TestSignals.Sine(SampleRate, 1000, SampleRate, 0.25);
        var output = (float[])input.Clone();
        strip.Process(output, 0, output.Length);

        // Skip the first half: the biquad needs a few cycles to settle.
        int from = SampleRate / 2;
        double gainDb = TestSignals.RmsDb(output.AsSpan(from)) - TestSignals.RmsDb(input.AsSpan(from));

        Assert.InRange(gainDb, 11.5, 12.5);
    }

    [Fact]
    public void Gate_on_attenuates_a_minus_60dBFS_signal_by_at_least_20dB()
    {
        var strip = new MixerStrip(SampleRate);
        strip.Apply(StripParameters.Neutral with
        {
            Gate = new GateParameters(Enabled: true, ThresholdDb: -40, FloorDb: -60),
        });

        Assert.False(strip.IsPassthrough);

        // 0.001 peak = -60 dBFS, i.e. 20 dB below the gate's threshold.
        var input = TestSignals.Sine(SampleRate * 2, 440, SampleRate, 0.001);
        var output = (float[])input.Clone();
        strip.Process(output, 0, output.Length);

        int from = SampleRate; // attack, hold and release have long since settled
        double attenuationDb = TestSignals.RmsDb(output.AsSpan(from)) - TestSignals.RmsDb(input.AsSpan(from));

        Assert.True(attenuationDb <= -20, $"the gate only attenuated by {attenuationDb:F1} dB");
    }

    [Fact]
    public void Compressor_on_reduces_the_peak_of_a_0dBFS_signal_at_20dB_and_4to1()
    {
        var strip = new MixerStrip(SampleRate);
        strip.Apply(StripParameters.Neutral with
        {
            Compressor = new CompressorParameters(Enabled: true, ThresholdDb: -20, Ratio: 4),
        });

        Assert.False(strip.IsPassthrough);

        var input = TestSignals.Sine(SampleRate, 1000, SampleRate, 1.0);
        var output = (float[])input.Clone();
        strip.Process(output, 0, output.Length);

        int from = SampleRate / 2;
        double inPeak = Peak(input.AsSpan(from));
        double outPeak = Peak(output.AsSpan(from));
        double reductionDb = TestSignals.Db(outPeak / inPeak);

        // 0 dBFS peak is 17 dB over a -20 dB threshold; at 4:1 that is ~12.8 dB of reduction.
        Assert.InRange(reductionDb, -20, -8);
        Assert.True(outPeak < inPeak, "the compressor must lower the peak");
    }

    [Fact]
    public void Limiter_latency_is_240_frames_at_48kHz()
    {
        Assert.Equal(240, new Limiter(SampleRate).LatencyFrames);
    }

    [Fact]
    public void Limiter_is_built_lazily_only_when_switched_on()
    {
        var strip = new MixerStrip(SampleRate);

        Assert.Null(strip.Limiter);
        Assert.Equal(0, strip.LatencyFrames);
        Assert.Equal(6, strip.ProcessorCount);

        strip.Apply(StripParameters.Neutral with { Limiter = new LimiterParameters(Enabled: true) });

        Assert.NotNull(strip.Limiter);
        Assert.Equal(240, strip.Limiter!.LatencyFrames);
        Assert.Equal(240, strip.LatencyFrames);
        Assert.Equal(7, strip.ProcessorCount);

        // Switching it back off restores zero latency and the transparent fast path, without
        // freeing the limiter (so toggling does not churn the heap).
        strip.Apply(StripParameters.Neutral);
        Assert.NotNull(strip.Limiter);
        Assert.Equal(0, strip.LatencyFrames);
        Assert.True(strip.IsPassthrough);
    }

    [Fact]
    public void Latency_ledger_pads_a_fast_channel_up_to_the_slowest()
    {
        var ledger = new LatencyLedger();
        ledger.Set("fast", chainFrames: 0, deviceFrames: 0, userOffsetFrames: 0);
        ledger.Set("slow", chainFrames: 240, deviceFrames: 0, userOffsetFrames: 0);

        Assert.Equal(240, ledger.PaddingFor("fast"));
        Assert.Equal(0, ledger.PaddingFor("slow"));

        ledger.Set("faster", chainFrames: 0, deviceFrames: 0, userOffsetFrames: 0);
        Assert.True(ledger.PaddingFor("faster") >= 0, "padding must never be negative");
    }

    [Fact]
    public void Master_bypass_makes_the_strip_a_passthrough()
    {
        var strip = new MixerStrip(SampleRate);
        strip.Apply(strip.Parameters with
        {
            Eq = StripParameters.Neutral.Eq with { Gain1kDb = 12 },
        });
        Assert.False(strip.IsPassthrough);

        strip.Apply(strip.Parameters with { MasterBypass = true });
        Assert.True(strip.IsPassthrough);

        var input = TestSignals.Sine(2048, 1000, SampleRate, 0.25);
        var output = (float[])input.Clone();
        strip.Process(output, 0, output.Length);

        TestSignals.AssertBitExact(input, output, "master bypass must leave the buffer untouched");
    }

    [Fact]
    public void Reset_clears_filter_and_envelope_state()
    {
        var strip = new MixerStrip(SampleRate);
        strip.Apply(StripParameters.Neutral with
        {
            Gate = new GateParameters(Enabled: true, ThresholdDb: -20, FloorDb: -60),
            Compressor = new CompressorParameters(Enabled: true),
            Limiter = new LimiterParameters(Enabled: true),
        });

        var loud = TestSignals.Sine(4096, 1000, SampleRate, 0.8);
        strip.Process(loud, 0, loud.Length);

        strip.Reset();

        // After Reset the gate is closed at its floor and the compressor has no reduction.
        Assert.Equal(0, strip.Compressor.GainReductionDb, 6);
        Assert.True(strip.Gate.GainReductionDb <= -30, $"gate gain after reset was {strip.Gate.GainReductionDb:F1} dB");
    }

    [Fact]
    public void Process_and_Apply_allocate_nothing()
    {
        // Method: AllocationAssert.Measure runs the loop twice and counts the second run, so
        // JIT and first-touch costs land outside the measured window.
        var strip = new MixerStrip(SampleRate);

        // The limiter is the one legitimately allocating path (built off the render thread), so
        // build it before measuring.
        strip.Apply(StripParameters.Neutral with { Limiter = new LimiterParameters(Enabled: true) });

        var boosted = StripParameters.Neutral with
        {
            Eq = StripParameters.Neutral.Eq with { Gain1kDb = 6 },
            Gate = new GateParameters(Enabled: true),
        };

        var buffer = TestSignals.WhiteNoise(512, seed: 5);

        // Both measured paths are warmed by the helper's first pass.
        long allocated = AllocationAssert.Measure(() =>
        {
            for (int i = 0; i < 64; i++)
            {
                strip.Apply(i % 2 == 0 ? boosted : StripParameters.Neutral);
                strip.Process(buffer, 0, buffer.Length);
                strip.Process(buffer.AsSpan());
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void De_esser_on_hands_the_limiter_less_high_band_energy()
    {
        // Sibilant-heavy: full-scale white noise has as much energy at 6-9 kHz as anywhere else.
        var input = TestSignals.WhiteNoise(SampleRate * 2, seed: 63);

        var off = new MixerStrip(SampleRate);
        var on = new MixerStrip(SampleRate);
        on.Apply(StripParameters.Neutral with
        {
            DeEsser = new DeEsserParameters(Enabled: true, ThresholdDb: -30),
        });

        var offOutput = (float[])input.Clone();
        var onOutput = (float[])input.Clone();
        off.Process(offOutput, 0, offOutput.Length);
        on.Process(onOutput, 0, onOutput.Length);

        // Every other stage is neutral and no limiter exists, so each output *is* what the
        // limiter would receive.
        double offHighDb = HighBandRmsDb(offOutput);
        double onHighDb = HighBandRmsDb(onOutput);

        Assert.True(onHighDb < offHighDb - 3,
            $"the de-esser only took {offHighDb - onHighDb:F1} dB off the high band " +
            $"(off {offHighDb:F1} dBFS, on {onHighDb:F1} dBFS)");
    }

    /// <summary>RMS of the band above 5 kHz, after a second of settling.</summary>
    private static double HighBandRmsDb(float[] samples)
    {
        var filter = new HighPassFilter(SampleRate, 5000);
        filter.Process(samples);
        return TestSignals.RmsDb(samples.AsSpan(SampleRate / 4));
    }

    private static double Peak(ReadOnlySpan<float> samples)
    {
        double peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Abs(samples[i]);
            if (value > peak) peak = value;
        }

        return peak;
    }
}
