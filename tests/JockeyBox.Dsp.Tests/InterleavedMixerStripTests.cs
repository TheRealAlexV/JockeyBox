namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The stereo wrapper that sits between the engine's interleaved device block and the mono
/// <see cref="MixerStrip"/>.
/// </summary>
public class InterleavedMixerStripTests
{
    private const int SampleRate = 48000;
    private const int BlockFrames = 512;
    private const int Channels = 2;

    [Fact]
    public void Fully_neutral_stereo_strip_is_bit_exact()
    {
        var strip = new InterleavedMixerStrip(SampleRate, Channels, BlockFrames);

        Assert.True(strip.IsPassthrough);
        Assert.Equal(0, strip.LatencyFrames);
        Assert.Equal(6, strip.ProcessorCount);

        var input = TestSignals.WhiteNoise(BlockFrames * Channels, seed: 77);
        var output = (float[])input.Clone();

        strip.Process(output, 0, BlockFrames);

        TestSignals.AssertBitExact(input, output, "a neutral stereo strip must leave the block untouched");
    }

    [Fact]
    public void Twelve_dB_of_eq_on_channel_0_leaves_channel_1_byte_identical()
    {
        var strip = new InterleavedMixerStrip(SampleRate, Channels, BlockFrames);
        strip[0].Apply(strip[0].Parameters with
        {
            Eq = StripParameters.Neutral.Eq with { Gain1kDb = 12 },
        });

        Assert.False(strip.IsPassthrough);

        var input = new float[BlockFrames * Channels];
        for (int i = 0; i < BlockFrames; i++)
        {
            float sample = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / SampleRate));
            input[Channels * i] = sample;
            input[Channels * i + 1] = sample;
        }

        var output = (float[])input.Clone();
        strip.Process(output, 0, BlockFrames);

        for (int i = 0; i < BlockFrames; i++)
        {
            Assert.Equal(input[Channels * i + 1], output[Channels * i + 1]);
        }

        double before = ChannelRms(input, 0);
        double after = ChannelRms(output, 0);
        Assert.True(after > before * 2, $"channel 0 should be boosted, {before:F4} -> {after:F4}");
    }

    [Fact]
    public void Latency_agrees_with_channel_0_when_only_channel_0_has_a_limiter()
    {
        var strip = new InterleavedMixerStrip(SampleRate, Channels, BlockFrames);
        strip[0].Apply(strip[0].Parameters with
        {
            Limiter = new LimiterParameters(Enabled: true, CeilingDb: -6, ReleaseMs: 50),
        });

        // The pair mirrors channel 0: a device block is compensated as one delay, never per
        // channel, so the wrapper reports the 240 frames the block actually carries.
        Assert.Equal(240, strip.LatencyFrames);
        Assert.Equal(strip[0].LatencyFrames, strip.LatencyFrames);

        // Channel 1 itself still reports its own latency, 0, because it has no limiter. The
        // wrapper deliberately does not surface that: compensating the pair by 0 would drift
        // channel 0 against channel 1.
        Assert.Equal(0, strip[1].LatencyFrames);
    }

    [Fact]
    public void Reset_clears_both_channels()
    {
        var strip = new InterleavedMixerStrip(SampleRate, Channels, BlockFrames);
        strip.Apply(strip[0].Parameters with
        {
            Limiter = new LimiterParameters(Enabled: true, CeilingDb: -6, ReleaseMs: 50),
        });

        var loud = new float[BlockFrames * Channels];
        Array.Fill(loud, 1.5f);
        strip.Process(loud, 0, BlockFrames);

        strip.Reset();

        var silence = new float[BlockFrames * Channels];
        strip.Process(silence, 0, BlockFrames);

        for (int i = 0; i < silence.Length; i++)
        {
            Assert.Equal(0f, silence[i]);
        }
    }

    [Fact]
    public void Apply_reaches_both_channels()
    {
        var strip = new InterleavedMixerStrip(SampleRate, Channels, BlockFrames);
        StripParameters applied = StripParameters.Neutral with
        {
            Eq = StripParameters.Neutral.Eq with { Gain1kDb = 12 },
        };

        strip.Apply(applied);

        Assert.Equal(applied, strip[0].Parameters);
        Assert.Equal(applied, strip[1].Parameters);

        var input = new float[BlockFrames * Channels];
        for (int i = 0; i < BlockFrames; i++)
        {
            float sample = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / SampleRate));
            input[Channels * i] = sample;
            input[Channels * i + 1] = sample;
        }

        var output = (float[])input.Clone();
        strip.Process(output, 0, BlockFrames);

        for (int channel = 0; channel < Channels; channel++)
        {
            Assert.True(ChannelRms(output, channel) > ChannelRms(input, channel) * 2,
                $"channel {channel} did not receive the applied EQ");
        }
    }

    [Fact]
    public void Non_stereo_channel_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InterleavedMixerStrip(SampleRate, 1, BlockFrames));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InterleavedMixerStrip(SampleRate, 4, BlockFrames));
    }

    private static double ChannelRms(ReadOnlySpan<float> interleaved, int channel)
    {
        double sum = 0;
        int frames = interleaved.Length / Channels;
        for (int i = 0; i < frames; i++)
        {
            double sample = interleaved[Channels * i + channel];
            sum += sample * sample;
        }

        return Math.Sqrt(sum / frames);
    }
}
