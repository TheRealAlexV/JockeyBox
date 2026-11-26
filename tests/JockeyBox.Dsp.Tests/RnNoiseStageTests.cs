using JockeyBox.Dsp.RnNoise;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The stage logic that runs on the render thread — framing, int16 scaling, delay alignment,
/// Mix, stereo independence and neutral transparency — with the native engine replaced by a
/// scripted <see cref="IDenoiseFrameProcessor"/>.
/// <para>
/// This is where the bugs live, and none of it needs the Windows DLL, so all of it runs on Linux.
/// The real library is exercised in <see cref="RnNoiseInteropTests"/>.
/// </para>
/// </summary>
public class RnNoiseStageTests
{
    private const int FrameSize = 480;

    /// <summary>
    /// A deterministic engine with a declared latency: applies a scripted gain per call and
    /// records what it was given. Used for scaling, Mix and reset checks.
    /// </summary>
    private sealed class FakeEngine : IDenoiseFrameProcessor
    {
        private readonly float[] _gains;
        private readonly List<float[]> _seen = new();

        public FakeEngine(int frameSize, int latencySamples = 0, params float[] gains)
        {
            FrameSize = frameSize;
            LatencySamples = latencySamples;
            _gains = gains.Length == 0 ? new[] { 1f } : gains;
        }

        public int FrameSize { get; }
        public int LatencySamples { get; }
        public int Calls { get; private set; }
        public IReadOnlyList<float[]> Seen => _seen;

        public float ProcessFrame(float[] frame)
        {
            _seen.Add((float[])frame.Clone());
            float gain = _gains[Math.Min(Calls, _gains.Length - 1)];
            Calls++;
            for (int i = 0; i < frame.Length; i++) frame[i] *= gain;
            return 1f;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// An engine that really delays: output sample k is input sample k-latency, times a gain.
    /// This is the shape the native engine was measured to have.
    /// </summary>
    private sealed class DelayedGainEngine : IDenoiseFrameProcessor
    {
        private readonly float[] _ring;
        private readonly float _gain;
        private int _write;

        public DelayedGainEngine(int frameSize, int latencySamples, float gain)
        {
            FrameSize = frameSize;
            LatencySamples = latencySamples;
            _ring = new float[latencySamples];
            _gain = gain;
        }

        public int FrameSize { get; }
        public int LatencySamples { get; }

        public float ProcessFrame(float[] frame)
        {
            if (LatencySamples == 0)
            {
                for (int i = 0; i < frame.Length; i++) frame[i] *= _gain;
                return 1f;
            }

            for (int i = 0; i < frame.Length; i++)
            {
                float current = frame[i];
                frame[i] = _ring[_write] * _gain;
                _ring[_write] = current;
                if (++_write == LatencySamples) _write = 0;
            }

            return 1f;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// An engine whose output depends on its own history, so a state shared between channels
    /// would show up as cross-talk.
    /// </summary>
    private sealed class EchoEngine : IDenoiseFrameProcessor
    {
        private float _last;

        public EchoEngine(int frameSize) => FrameSize = frameSize;

        public int FrameSize { get; }
        public int LatencySamples => 0;

        public float ProcessFrame(float[] frame)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                float x = frame[i];
                frame[i] = 0.5f * x + 0.5f * _last;
                _last = x;
            }

            return 0.5f;
        }

        public void Dispose()
        {
        }
    }

    private static RnNoiseStage Enabled(Func<IDenoiseFrameProcessor> factory, double mix = 1.0)
    {
        var stage = new RnNoiseStage(factory);
        stage.Apply(new NoiseSuppressionParameters(Enabled: true, Mix: mix));
        return stage;
    }

    [Fact]
    public void Frame_size_is_read_from_the_engine_not_hardcoded()
    {
        var engine = new FakeEngine(240); // not 480
        using var stage = Enabled(() => engine);

        // Two frames of 240 fit in one 480-sample block. A hardcoded 480 would call the engine
        // once and leave half the block unprocessed.
        var block = new float[480];
        stage.Process(block);

        Assert.Equal(240, stage.FrameSize);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public void Samples_reach_the_engine_at_int16_scale()
    {
        var engine = new FakeEngine(FrameSize);
        using var stage = Enabled(() => engine);

        var block = new float[FrameSize];
        Array.Fill(block, 0.5f);
        stage.Process(block);

        Assert.Single(engine.Seen);
        // 0.5 * 32768. Without the multiply this is 0.5 and the test fails.
        Assert.Equal(16384f, engine.Seen[0][0], 3);
    }

    [Fact]
    public void Removing_the_down_scale_would_leave_the_output_at_int16_level()
    {
        var engine = new FakeEngine(FrameSize); // passes the scaled frame through
        using var stage = Enabled(() => engine);

        var block = new float[FrameSize];
        Array.Fill(block, 0.5f);
        stage.Process(block);

        // The divide by 32768 restores [-1,1]. Without it the sample would be ~16384.
        Assert.Equal(0.5f, block[0], 4);
    }

    [Fact]
    public void Engine_delay_is_reported_and_the_dry_reference_is_aligned()
    {
        // A two-frame delay at gain 0.5: wet is the input from two frames ago, half level.
        using var stage = Enabled(() => new DelayedGainEngine(FrameSize, 960, 0.5f), mix: 0.5);

        var block = new float[FrameSize * 3];
        block[0] = 1f;
        stage.Process(block);

        Assert.Equal(960, stage.LatencyFrames);

        // Aligned dry/wet: output[0] = 0.5 + 0.5*(0.5 - 0.5) = 0.5, not zero.
        // (The stack places the marker at input[0]; the delayed copy appears at output[960].)
        Assert.Equal(0.75f, block[960], 4);

        // If the dry reference were not delayed, the current input's marker would leak into
        // output[0] as 1 + 0.5*(0 - 1) = 0.5. The delay alignment is what the test proves.
        Assert.Equal(0f, block[1], 6);
    }

    [Fact]
    public void Neutral_is_bit_exact_and_never_touches_the_factory()
    {
        bool created = false;
        using var stage = new RnNoiseStage(() =>
        {
            created = true;
            return new FakeEngine(FrameSize);
        });

        stage.Apply(NoiseSuppressionParameters.Neutral);

        var input = TestSignals.WhiteNoise(4096, seed: 11);
        var output = (float[])input.Clone();
        stage.Process(output);

        Assert.False(created, "a neutral stage must not create an engine");
        Assert.True(stage.IsNeutral);
        Assert.False(stage.HasEngine);
        Assert.Equal(0, stage.LatencyFrames);
        TestSignals.AssertBitExact(input, output, "a neutral denoiser must leave the buffer untouched");
    }

    [Fact]
    public void Mix_zero_is_the_same_as_off()
    {
        bool created = false;
        using var stage = new RnNoiseStage(() =>
        {
            created = true;
            return new FakeEngine(FrameSize);
        });

        stage.Apply(new NoiseSuppressionParameters(Enabled: true, Mix: 0));

        var input = TestSignals.WhiteNoise(4096, seed: 12);
        var output = (float[])input.Clone();
        stage.Process(output);

        Assert.False(created, "Mix 0 must not spin up the engine");
        Assert.True(stage.IsNeutral);
        TestSignals.AssertBitExact(input, output, "Mix 0 must be bit-identical to off");
    }

    [Fact]
    public void Mix_controls_the_wet_amount()
    {
        var engine = new FakeEngine(FrameSize, 0, 0f); // wet is silence, no delay
        using var stage = Enabled(() => engine, mix: 0.5);

        var block = new float[FrameSize];
        Array.Fill(block, 1.0f);
        stage.Process(block);

        // 1.0 + 0.5 * (0 - 1.0) = 0.5, on the first frame.
        Assert.Equal(0.5f, block[0], 4);
    }

    [Fact]
    public void A_block_that_is_not_whole_frames_fails_loudly()
    {
        using var stage = Enabled(() => new FakeEngine(240));

        var block = new float[300];
        var ex = Assert.Throws<ArgumentException>(() => stage.Process(block));
        Assert.Contains("240", ex.Message);
    }

    [Fact]
    public void Latency_comes_from_the_engine()
    {
        using var zero = Enabled(() => new FakeEngine(FrameSize));
        Assert.Equal(0, zero.LatencyFrames);

        var silence = new float[FrameSize];
        zero.Process(silence);

        var block = new float[FrameSize];
        const int impulse = 100;
        block[impulse] = 1f;
        zero.Process(block);

        // No delay in the engine means no shift in the output.
        Assert.Equal(impulse, TestSignals.IndexOfPeak(block));

        using var delayed = Enabled(() => new DelayedGainEngine(FrameSize, 960, 1f));
        Assert.Equal(960, delayed.LatencyFrames);
    }

    [Fact]
    public void Reset_reproduces_a_fresh_state()
    {
        int engines = 0;
        using var stage = new RnNoiseStage(() =>
        {
            engines++;
            return new FakeEngine(FrameSize, 0, 0f, 0.014f, 0.939f);
        });
        stage.Apply(new NoiseSuppressionParameters(Enabled: true));

        var input = TestSignals.WhiteNoise(FrameSize * 8, seed: 13);

        var first = (float[])input.Clone();
        stage.Process(first);

        stage.Reset();

        var second = (float[])input.Clone();
        stage.Process(second);

        Assert.Equal(2, engines);
        TestSignals.AssertBitExact(first, second, "reset must reproduce a fresh state's output");
    }

    [Fact]
    public void Left_and_right_are_independent()
    {
        // A fresh state is created per channel, so each channel's history stays its own.
        var strip = new InterleavedMixerStrip(48000, 2, FrameSize, () => new EchoEngine(FrameSize));
        strip.Apply(StripParameters.Neutral with
        {
            Noise = new NoiseSuppressionParameters(Enabled: true),
        });

        const int blocks = 8;
        int samples = blocks * FrameSize;
        var interleaved = new float[samples * 2];
        for (int n = 0; n < samples; n++)
        {
            interleaved[2 * n] = (float)(0.6 * Math.Sin(2 * Math.PI * 300 * n / 48000));
            interleaved[2 * n + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 1700 * n / 48000));
        }

        // One device block per call, exactly as the engine drives the strip.
        var output = (float[])interleaved.Clone();
        for (int b = 0; b < blocks; b++)
        {
            strip.Process(output, b * FrameSize * 2, FrameSize);
        }

        // Right channel, processed on its own by an identical fresh engine, is the reference.
        var rightReference = new float[samples];
        for (int n = 0; n < samples; n++) rightReference[n] = interleaved[2 * n + 1] * 32768f;
        var engine = new EchoEngine(FrameSize);
        var frame = new float[FrameSize];
        for (int b = 0; b < blocks; b++)
        {
            Array.Copy(rightReference, b * FrameSize, frame, 0, FrameSize);
            engine.ProcessFrame(frame);
            Array.Copy(frame, 0, rightReference, b * FrameSize, FrameSize);
        }

        for (int n = 0; n < samples; n++) rightReference[n] /= 32768f;

        for (int n = 0; n < samples; n++)
        {
            Assert.Equal(rightReference[n], output[2 * n + 1], 5);
        }
    }
}
