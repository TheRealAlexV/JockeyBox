namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The pitch shifter as a monitor stage: neutral is bit-exact, active really moves pitch in tune,
/// the delay it carries is reported, and two channel instances share one write head.
/// </summary>
public class PitchShiftTests
{
    private const int SampleRate = 48000;
    private const int BlockFrames = 480;
    private const int Skip = 20000;
    private const int Analyse = 100000;

    private static PitchShiftStage Stage(double semitones) =>
        new(SampleRate, parameters: new PitchShiftParameters(true, semitones));

    private static float[] Render(PitchShiftStage stage, float[] input)
    {
        var output = (float[])input.Clone();
        for (int offset = 0; offset + BlockFrames <= output.Length; offset += BlockFrames)
        {
            stage.Process(output.AsSpan(offset, BlockFrames));
        }

        return output;
    }

    /// <summary>
    /// Frequency from the first and last positive-going zero crossing, with the crossing time
    /// interpolated between the two samples. Over a hundred cycles that is far more precise than
    /// counting crossings, and it does not need an FFT.
    /// </summary>
    private static double EstimateHz(ReadOnlySpan<float> samples)
    {
        double first = 0;
        double last = 0;
        int count = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0 && samples[i] > 0)
            {
                double t = (i - 1) + (double)(-samples[i - 1]) / (samples[i] - samples[i - 1]);
                if (count == 0) first = t;
                last = t;
                count++;
            }
        }

        if (count < 2)
            throw new InvalidOperationException("not enough zero crossings to measure the frequency");

        return (count - 1) * (double)SampleRate / (last - first);
    }

    private static double Cents(double measured, double expected) =>
        1200.0 * Math.Log2(measured / expected);

    private static double MeasuredHz(double semitones, double inputHz)
    {
        var input = TestSignals.Sine(Skip + Analyse, inputHz, SampleRate, 0.5);
        var output = Render(Stage(semitones), input);
        return EstimateHz(output.AsSpan(Skip, Analyse));
    }

    [Fact]
    public void Parameters_clamp_to_one_octave_and_fall_back_to_zero_on_NaN()
    {
        Assert.Equal(PitchShiftParameters.MaxSemitones, new PitchShiftParameters(true, 40).Semitones);
        Assert.Equal(PitchShiftParameters.MinSemitones, new PitchShiftParameters(true, -40).Semitones);

        var fromDefault = default(PitchShiftParameters);
        fromDefault.Semitones = 99;
        Assert.Equal(PitchShiftParameters.MaxSemitones, fromDefault.Semitones);

        Assert.Equal(PitchShiftParameters.DefaultSemitones,
            new PitchShiftParameters(true, double.NaN).Semitones);

        var once = new PitchShiftParameters(true, 30).Clamp();
        Assert.Equal(PitchShiftParameters.MaxSemitones, once.Semitones);
        Assert.Equal(once, once.Clamp());
    }

    [Fact]
    public void Neutral_is_off_or_exactly_zero_semitones()
    {
        Assert.True(PitchShiftParameters.Neutral.IsNeutral);
        Assert.True(new PitchShiftParameters(true, 0).IsNeutral);
        Assert.False(new PitchShiftParameters(true, 0.01).IsNeutral);
        Assert.False(new PitchShiftParameters(true, -3).IsNeutral);
    }

    [Fact]
    public void Neutral_stage_leaves_the_block_bit_exact()
    {
        var stage = Stage(0);
        Assert.True(stage.IsNeutral);

        var input = TestSignals.WhiteNoise(BlockFrames, seed: 21);
        var buffer = (float[])input.Clone();

        stage.Process(buffer);

        TestSignals.AssertBitExact(input, buffer, "zero semitones must not change a sample");
    }

    [Fact]
    public void Neutral_interleaved_strip_is_bit_exact_and_carries_no_latency()
    {
        var strip = new InterleavedMixerStrip(SampleRate, 2, BlockFrames);
        strip.Apply(StripParameters.Neutral);

        Assert.True(strip.IsPassthrough);
        Assert.Equal(0, strip.LatencyFrames);

        var input = TestSignals.WhiteNoise(BlockFrames * 2, seed: 22);
        var buffer = (float[])input.Clone();

        strip.Process(buffer, 0, BlockFrames);

        TestSignals.AssertBitExact(input, buffer, "a neutral pitch stage must not change the block");
    }

    [Fact]
    public void Active_stage_reports_its_measured_mean_delay_and_neutral_reports_zero()
    {
        var stage = Stage(5);
        Assert.False(stage.IsNeutral);
        Assert.Equal(PitchShiftStage.MeanUpDelayFramesAt48k, stage.LatencyFrames);
        Assert.NotEqual(0, stage.LatencyFrames);

        stage.SetParameters(new PitchShiftParameters(true, -5));
        Assert.Equal(PitchShiftStage.MeanDownDelayFramesAt48k, stage.LatencyFrames);

        stage.SetParameters(new PitchShiftParameters(false, 5));
        Assert.Equal(0, stage.LatencyFrames);

        stage.SetParameters(new PitchShiftParameters(true, 0));
        Assert.Equal(0, stage.LatencyFrames);
    }

    /// <summary>
    /// The defect this test exists for: the stage used to report the base delay (240) while the
    /// read head actually averaged far more, so the ledger padded every other channel to a
    /// figure the pitched channel did not meet.
    /// </summary>
    [Theory]
    [InlineData(7.0)]
    [InlineData(-5.0)]
    public void Reported_latency_matches_the_measured_mean_read_delay(double semitones)
    {
        var stage = new PitchShiftStage(SampleRate, parameters: new PitchShiftParameters(true, semitones));
        var input = TestSignals.Sine(Skip + Analyse, 440, SampleRate, 0.5);

        for (int offset = 0; offset + BlockFrames <= input.Length; offset += BlockFrames)
            stage.Process(input.AsSpan(offset, BlockFrames));

        int measured = stage.MeanReadDelayFrames;
        Assert.True(measured > 0);
        Assert.True(stage.MinReadDelayFrames >= PitchShiftStage.BaseDelayFramesAt48k,
            $"read delay fell to {stage.MinReadDelayFrames}, below the base delay");
        Assert.True(stage.MaxReadDelayFrames > measured && measured >= stage.MinReadDelayFrames,
            $"sweep did not move: min {stage.MinReadDelayFrames}, mean {measured}, max {stage.MaxReadDelayFrames}");

        int reported = stage.LatencyFrames;
        double error = Math.Abs(reported - measured) / (double)measured;
        Assert.True(error <= 0.05,
            $"reported {reported} frames vs measured mean {measured} frames ({error:P1}, band spread is documented)");
    }

    [Fact]
    public void Worst_case_strip_latency_stays_under_the_pad_ring_ceiling()
    {
        // The real worst case is all three latency stages at once: denoise 960, limiter 240 and
        // the pitch shifter's largest recorded down-shift. The pitch stage reports its tonal
        // mean (554) as a constant; white-noise down-shift was measured at 829, and using the
        // constant would hide 275 frames of the real cost. This test uses the recorded figure.
        var strip = new InterleavedMixerStrip(SampleRate, 2, BlockFrames,
            () => new FixedDenoiseEngine(480, 960));
        strip.Apply(StripParameters.Neutral with
        {
            Noise = new NoiseSuppressionParameters(Enabled: true, Mix: 1.0),
            Limiter = new LimiterParameters(Enabled: true),
            PitchShift = new PitchShiftParameters(true, -12),
        });

        int reported = strip.LatencyFrames; // 960 + 240 + 554
        int measuredWorst = reported
            - PitchShiftStage.MeanDownDelayFramesAt48k
            + 829; // PitchShiftStage's recorded white-noise down-shift
        int headroom = PadRing.MaxFrames - measuredWorst;

        Assert.True(headroom >= 0,
            $"three-stage worst case {measuredWorst} frames ({reported} reported, pitch at 829) " +
            $"leaves {headroom} of {PadRing.MaxFrames} frames of headroom");
    }

    [Fact]
    public void Interleaved_strip_latency_includes_an_active_pitch_stage()
    {
        var strip = new InterleavedMixerStrip(SampleRate, 2, BlockFrames);
        strip.Apply(StripParameters.Neutral with { PitchShift = new PitchShiftParameters(true, 4) });

        Assert.Equal(PitchShiftStage.MeanUpDelayFramesAt48k, strip.LatencyFrames);
        Assert.False(strip.IsPassthrough);
    }

    [Theory]
    [InlineData(7, 440.0)]
    [InlineData(-5, 440.0)]
    public void Pitch_shift_moves_a_sine_within_five_cents(double semitones, double inputHz)
    {
        double expected = inputHz * Math.Pow(2.0, semitones / 12.0);
        double measured = MeasuredHz(semitones, inputHz);

        Assert.True(Math.Abs(Cents(measured, expected)) <= 5.0,
            $"{semitones} semitones at {inputHz} Hz: {measured:F2} Hz vs {expected:F2} Hz");
    }

    /// <summary>
    /// The defect this test was added for: the fixed-offset dual-tap was exact at 400 Hz and
    /// 3% flat at 440 Hz in the same semitone, so a one-frequency test could not see it.
    /// </summary>
    [Fact]
    public void Seven_semitones_is_accurate_at_both_440_Hz_and_400_Hz()
    {
        foreach (double inputHz in new[] { 440.0, 400.0 })
        {
            double expected = inputHz * Math.Pow(2.0, 7.0 / 12.0);
            double measured = MeasuredHz(7, inputHz);
            Assert.True(Math.Abs(Cents(measured, expected)) <= 5.0,
                $"7 semitones at {inputHz} Hz: {measured:F2} Hz vs {expected:F2} Hz");
        }
    }

    /// <summary>
    /// Full grid: ten input frequencies against ten shifts. The old fixed-offset splice failed
    /// at 440 Hz and at 300/500/700 Hz; a real phase-aligned splice has to hold every corner.
    /// </summary>
    [Fact]
    public void Pitch_stays_within_five_cents_across_the_band_and_the_octave()
    {
        double[] semitones = { 12, 7, 5, 3, 1, -1, -3, -5, -7, -12 };
        double[] frequencies = { 200, 300, 400, 440, 500, 600, 700, 800, 900, 1000 };

        var misses = new List<string>();
        double worstCents = 0;
        string worstPoint = "";

        foreach (double s in semitones)
        {
            foreach (double f in frequencies)
            {
                double expected = f * Math.Pow(2.0, s / 12.0);
                double measured = MeasuredHz(s, f);
                double cents = Cents(measured, expected);

                if (Math.Abs(cents) > Math.Abs(worstCents))
                {
                    worstCents = cents;
                    worstPoint = $"{s:+#;-#;0} semitones at {f:F0} Hz: {measured:F2} Hz vs {expected:F2} Hz";
                }

                if (Math.Abs(cents) > 5.0)
                    misses.Add($"{s:+#;-#;0}@{f:F0} = {cents:F2} cents ({measured:F2} vs {expected:F2} Hz)");
            }
        }

        Assert.True(misses.Count == 0,
            $"worst {worstCents:F2} cents at {worstPoint}. Misses: {string.Join("; ", misses)}");
        Assert.True(Math.Abs(worstCents) <= 5.0, $"worst {worstCents:F2} cents at {worstPoint}");
    }

    [Fact]
    public void A_neutral_block_after_a_full_ring_stays_bit_exact()
    {
        var stage = Stage(7);
        var warmup = TestSignals.WhiteNoise(BlockFrames * 40, seed: 23);
        Render(stage, warmup);

        stage.SetParameters(PitchShiftParameters.Neutral);
        Assert.True(stage.IsNeutral);

        var input = TestSignals.WhiteNoise(BlockFrames, seed: 24);
        var buffer = (float[])input.Clone();

        stage.Process(buffer);

        TestSignals.AssertBitExact(input, buffer, "switching off must restore the untouched block");
    }

    [Fact]
    public void Processing_a_block_allocates_nothing()
    {
        var stage = Stage(7);
        var block = TestSignals.Sine(BlockFrames, 440, SampleRate, 0.5);

        long bytes = AllocationAssert.Measure(() => stage.Process(block.AsSpan()));

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Two_channel_instances_share_a_write_head_and_never_cross_channels()
    {
        static (float[] Left, float[] Right) Run(float rightHz)
        {
            var clock = new PitchShiftClock(PitchShiftStage.RingFramesFor(SampleRate), autoAdvance: false);
            var left = new PitchShiftStage(SampleRate, clock, new PitchShiftParameters(true, 7), "pitch.left");
            var right = new PitchShiftStage(SampleRate, clock, new PitchShiftParameters(true, -5), "pitch.right");

            var leftIn = TestSignals.Sine(BlockFrames * 4, 300, SampleRate, 0.4);
            var rightIn = TestSignals.Sine(BlockFrames * 4, rightHz, SampleRate, 0.4);
            var leftOut = (float[])leftIn.Clone();
            var rightOut = (float[])rightIn.Clone();

            for (int offset = 0; offset + BlockFrames <= leftOut.Length; offset += BlockFrames)
            {
                left.Process(leftOut.AsSpan(offset, BlockFrames));
                right.Process(rightOut.AsSpan(offset, BlockFrames));
                clock.Advance(BlockFrames);
            }

            return (leftOut, rightOut);
        }

        var (leftA, rightA) = Run(500);
        var (leftB, rightB) = Run(700);

        Assert.NotEqual(leftA, rightA);
        TestSignals.AssertBitExact(leftA, leftB, "the left shifter must not observe the right channel");
        Assert.NotEqual(rightA, rightB);
    }
}
