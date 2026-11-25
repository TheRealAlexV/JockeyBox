using JockeyBox.Dsp.Reverb;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The reverb send as it is wired into the device block: a parallel return that never becomes
/// part of the channel chain, so it adds no processor and no latency, and costs nothing while
/// neutral.
/// </summary>
public class ReverbStripTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BlockFrames = 480;

    private static ReverbParameters Enabled() =>
        new(true, 0.6, 0.25);

    private static InterleavedMixerStrip Strip() => new(SampleRate, Channels, BlockFrames);

    [Fact]
    public void Neutral_reverb_strip_is_bit_identical_to_a_strip_without_the_send()
    {
        var withSend = Strip();
        withSend.SetReverb(ReverbParameters.Neutral);
        var withoutSend = Strip();

        Assert.True(withSend.IsPassthrough);

        var input = TestSignals.WhiteNoise(BlockFrames * Channels, seed: 13);
        var a = (float[])input.Clone();
        var b = (float[])input.Clone();

        withSend.Process(a, 0, BlockFrames);
        withoutSend.Process(b, 0, BlockFrames);

        TestSignals.AssertBitExact(b, a, "a neutral send must not change the block");
        TestSignals.AssertBitExact(input, a, "a neutral send must leave the block untouched");
    }

    [Fact]
    public void Switching_the_send_off_restores_the_bit_exact_fast_path()
    {
        var strip = Strip();
        strip.SetReverb(Enabled());
        Assert.False(strip.IsPassthrough);

        var warmup = TestSignals.WhiteNoise(BlockFrames * Channels, seed: 3);
        strip.Process(warmup, 0, BlockFrames); // fills the delay lines

        strip.SetReverb(ReverbParameters.Neutral);
        Assert.True(strip.IsPassthrough);

        var input = TestSignals.WhiteNoise(BlockFrames * Channels, seed: 4);
        var output = (float[])input.Clone();
        strip.Process(output, 0, BlockFrames);

        TestSignals.AssertBitExact(input, output,
            "turning the send off must restore the transparent fast path");
    }

    [Fact]
    public void Reverb_on_is_not_a_passthrough_and_neutral_is()
    {
        var strip = Strip();
        Assert.True(strip.IsPassthrough);

        strip.SetReverb(Enabled());
        Assert.False(strip.IsPassthrough);

        strip.SetReverb(ReverbParameters.Neutral);
        Assert.True(strip.IsPassthrough);
    }

    [Fact]
    public void Reverb_adds_no_processor_and_no_latency()
    {
        var strip = Strip();
        Assert.Equal(6, strip.ProcessorCount);
        Assert.Equal(0, strip.LatencyFrames);

        strip.SetReverb(Enabled());
        Assert.Equal(6, strip.ProcessorCount);
        Assert.Equal(0, strip.LatencyFrames);

        // The limiter is the one stage that does add a processor and latency; reverb on top of
        // it must not change the count. The base 6 -> 7 fact lives in MixerStripTests, which is
        // where the limiter is built.
        strip.Apply(StripParameters.Neutral with
        {
            Limiter = new LimiterParameters(Enabled: true),
            Reverb = Enabled(),
        });

        Assert.Equal(7, strip.ProcessorCount);
    }

    /// <summary>
    /// With the send live the strip output is not the dry input: the wet return is audible,
    /// stays inside full scale, and differs between left and right. Covers three gaps at once -
    /// silent wet, an unbounded sum, and a collapsed stereo image.
    /// </summary>
    [Fact]
    public void Reverb_on_adds_an_audible_bounded_stereo_wet_return()
    {
        var strip = Strip();
        strip.SetReverb(Enabled());
        Assert.False(strip.IsPassthrough);

        const int blocks = 24;
        var tone = TestSignals.Sine(BlockFrames * blocks, 440, SampleRate, 0.5);

        var lastDry = new float[BlockFrames * Channels];
        var lastOutput = new float[BlockFrames * Channels];
        float peak = 0;

        for (int b = 0; b < blocks; b++)
        {
            var dry = new float[BlockFrames * Channels];
            for (int i = 0; i < BlockFrames; i++)
            {
                float sample = tone[b * BlockFrames + i];
                dry[Channels * i] = sample;
                dry[Channels * i + 1] = sample;
            }

            var output = (float[])dry.Clone();
            strip.Process(output, 0, BlockFrames);

            for (int i = 0; i < output.Length; i++) peak = Math.Max(peak, Math.Abs(output[i]));

            lastDry = dry;
            lastOutput = output;
        }

        Assert.True(peak <= 1.0f, $"the wet sum must stay inside full scale, peak {peak:F3}");

        bool differs = false, stereoDiffers = false;
        for (int i = 0; i < BlockFrames; i++)
        {
            if (lastOutput[Channels * i] != lastDry[Channels * i]) differs = true;
            if (lastOutput[Channels * i] != lastOutput[Channels * i + 1]) stereoDiffers = true;
        }

        Assert.True(differs, "reverb on must change the block: the wet return is audible");
        Assert.True(stereoDiffers, "the left and right wet returns must differ");
    }

    /// <summary>
    /// The send taps the strip output, not the raw input: a 200 Hz low-pass on channel 0
    /// silences its 8 kHz content before the send sees it, so the left wet return must be far
    /// quieter than on a strip whose channel 0 is untouched. If the tap moved ahead of
    /// MixerStrip.Process, both cases would hand the send the same raw tone and the wet would
    /// match. The limiter sits inside MixerStrip.Process, so this also pins the tap after it.
    /// </summary>
    [Fact]
    public void Reverb_send_taps_the_strip_output_not_the_raw_input()
    {
        const int blocks = 16;
        var tone = TestSignals.Sine(blocks * BlockFrames * Channels, 8000, SampleRate, 0.5);

        double filteredWet = LeftWetRms(lowPassOnChannel0: true, tone);
        double flatWet = LeftWetRms(lowPassOnChannel0: false, tone);

        Assert.True(flatWet > 1e-6,
            $"the open strip must produce audible wet on the left, measured RMS {flatWet:E3}");
        Assert.True(filteredWet < flatWet * 0.1,
            "the low-passed channel 0 must starve the send, so the tap is post-strip: " +
            $"filtered RMS {filteredWet:E3} vs open RMS {flatWet:E3}");
    }

    /// <summary>RMS of the left wet return, isolated as (send on) - (send off) over the same strip.</summary>
    private static double LeftWetRms(bool lowPassOnChannel0, float[] interleavedTone)
    {
        var withSend = Strip();
        withSend.SetReverb(Enabled());
        var withoutSend = Strip();

        if (lowPassOnChannel0)
        {
            var lowPass = new FilterParameters(Enabled: true, FrequencyHz: 200);
            withSend[0].Apply(withSend[0].Parameters with { LowPass = lowPass });
            withoutSend[0].Apply(withoutSend[0].Parameters with { LowPass = lowPass });
        }

        int blocks = interleavedTone.Length / (BlockFrames * Channels);
        var a = (float[])interleavedTone.Clone();
        var b = (float[])interleavedTone.Clone();
        for (int blk = 0; blk < blocks; blk++)
        {
            int offset = blk * BlockFrames * Channels;
            withSend.Process(a, offset, BlockFrames);
            withoutSend.Process(b, offset, BlockFrames);
        }

        double sum = 0;
        int count = 0;
        for (int i = 0; i < a.Length; i += Channels)
        {
            double wetLeft = a[i] - b[i];
            sum += wetLeft * wetLeft;
            count++;
        }

        return Math.Sqrt(sum / count);
    }
}
