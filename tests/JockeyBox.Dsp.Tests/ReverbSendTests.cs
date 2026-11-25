using JockeyBox.Dsp.Reverb;

namespace JockeyBox.Dsp.Tests;

public class ReverbSendTests
{
    private const int SampleRate = 48000;
    private const int BlockFrames = 480;

    private static ReverbSend Enabled() =>
        new(SampleRate, new ReverbParameters(true, 0.5, 0.25));

    [Fact]
    public void Off_send_clears_the_wet_buffer()
    {
        var send = new ReverbSend(SampleRate);
        Assert.False(send.Active, "A send at its neutral parameters must report inactive");
        Assert.True(send.IsNeutral);

        var dry = TestSignals.Sine(BlockFrames * 2, 1000, SampleRate, 0.5);
        var wet = new float[BlockFrames * 2];
        Array.Fill(wet, 0.25f);

        send.Process(dry, wet, BlockFrames);

        Assert.All(wet, sample => Assert.Equal(0f, sample));
    }

    /// <summary>
    /// Ratio of the send's wet left channel to a bare <see cref="Freeverb"/> driven by the same
    /// mono signal, measured over the settled second half. The constant wet gain cancels, so the
    /// ratio is the wet high-pass response alone.
    /// </summary>
    private static (double Ratio, double WetRms) Band(double frequencyHz, int frames = SampleRate)
    {
        var mono = TestSignals.Sine(frames, frequencyHz, SampleRate, 0.5);
        var dry = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            dry[2 * i] = mono[i];
            dry[2 * i + 1] = mono[i];
        }

        var reference = new Freeverb(SampleRate);
        var freeverbOut = new float[frames];
        for (int i = 0; i < frames; i++) freeverbOut[i] = reference.ProcessSample(mono[i]);

        var send = new ReverbSend(SampleRate,
            new ReverbParameters(true, 0.5, ReverbParameters.MaxWet));
        var wet = new float[frames * 2];
        send.Process(dry, wet, frames);

        var wetLeft = new float[frames];
        for (int i = 0; i < frames; i++) wetLeft[i] = wet[2 * i];

        int from = frames / 2;
        double freeverbRms = TestSignals.Rms(freeverbOut.AsSpan(from));
        double wetRms = TestSignals.Rms(wetLeft.AsSpan(from));
        return (wetRms / freeverbRms, wetRms);
    }

    [Fact]
    public void Wet_return_is_high_passed_at_200Hz()
    {
        var low = Band(100);
        var high = Band(1000);

        // Both ratios carry the same constant wet gain, so their difference is the high-pass
        // attenuation at 100 Hz relative to the 1 kHz passband. A second-order corner at 200 Hz
        // lands near 12 dB; a 400 Hz corner measures about 24 dB and a 1 kHz corner about 37 dB,
        // so a tight band around 12 dB actually pins the corner instead of accepting any value
        // above 6 dB.
        double lowAttenuationDb = TestSignals.Db(high.Ratio) - TestSignals.Db(low.Ratio);

        Assert.InRange(lowAttenuationDb, 10.0, 15.0);

        Assert.True(high.WetRms > 1e-4,
            $"Expected audible wet energy above the corner, measured RMS {high.WetRms:E3}. " +
            $"100 Hz vs 1 kHz attenuation {lowAttenuationDb:F1} dB " +
            $"(100 Hz ratio {low.Ratio:F4}, 1 kHz ratio {high.Ratio:F4})");
    }

    [Fact]
    public void Two_sends_do_not_share_a_tail()
    {
        const int frames = SampleRate;

        // Only A is driven; B sees silence and must produce silence of its own.
        var a = Enabled();
        var b = Enabled();

        var impulseA = new float[frames * 2];
        impulseA[0] = 1f;
        impulseA[1] = 1f;

        var wetA = new float[frames * 2];
        var wetB = new float[frames * 2];

        a.Process(impulseA, wetA, frames);
        b.Process(new float[frames * 2], wetB, frames);

        Assert.Contains(wetA, sample => Math.Abs(sample) > 1e-9);
        Assert.All(wetB, sample => Assert.Equal(0f, sample));

        // Different impulses in two more sends must give different tails.
        var c = Enabled();
        var d = Enabled();

        var impulseC = new float[frames * 2];
        impulseC[0] = 1f;
        impulseC[1] = 1f;

        var impulseD = new float[frames * 2];
        impulseD[2 * 17] = 1f;
        impulseD[2 * 17 + 1] = 1f;

        var wetC = new float[frames * 2];
        var wetD = new float[frames * 2];

        c.Process(impulseC, wetC, frames);
        d.Process(impulseD, wetD, frames);

        Assert.False(wetC.AsSpan().SequenceEqual(wetD),
            "Two sends fed different impulses must not produce the same wet output");
    }

    /// <summary>
    /// The two chains are independent stereo sends, not one mono tail copied twice. The right
    /// chain's delays are spread from the left, so the same impulse gives different outputs on
    /// the two sides, each decays, and a left-only impulse leaves the right chain silent.
    /// </summary>
    [Fact]
    public void Left_and_right_chains_are_independent_and_differ()
    {
        const int frames = SampleRate;

        var send = Enabled();
        var impulse = new float[frames * 2];
        impulse[0] = 1f;
        impulse[1] = 1f;

        var wet = new float[frames * 2];
        send.Process(impulse, wet, frames);

        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = wet[2 * i];
            right[i] = wet[2 * i + 1];
        }

        Assert.Contains(left, sample => Math.Abs(sample) > 1e-9);
        Assert.Contains(right, sample => Math.Abs(sample) > 1e-9);
        Assert.False(left.AsSpan().SequenceEqual(right),
            "the stereo spread must make the left and right wet outputs differ, not duplicate one tail");

        double early = TestSignals.Rms(left.AsSpan(0, frames / 4));
        double late = TestSignals.Rms(left.AsSpan(3 * frames / 4, frames / 4));
        Assert.True(late < early,
            $"the wet tail must decay, early RMS {early:E3}, late RMS {late:E3}");

        // No cross-feed: a left-only impulse must ring the left chain and leave the right silent.
        var leftOnly = new float[frames * 2];
        leftOnly[0] = 1f;
        var wetLeftOnly = new float[frames * 2];
        Enabled().Process(leftOnly, wetLeftOnly, frames);

        bool leftHeard = false, rightHeard = false;
        for (int i = 0; i < frames; i++)
        {
            if (Math.Abs(wetLeftOnly[2 * i]) > 1e-9) leftHeard = true;
            if (Math.Abs(wetLeftOnly[2 * i + 1]) > 1e-9) rightHeard = true;
        }

        Assert.True(leftHeard, "a left-only impulse must ring the left chain");
        Assert.False(rightHeard, "a left-only impulse must not leak into the right chain");
    }

    [Fact]
    public void Process_allocates_nothing_with_reverb_on()
    {
        var send = Enabled();
        var dry = TestSignals.Sine(BlockFrames * 2, 1000, SampleRate, 0.5);
        var wet = new float[BlockFrames * 2];

        long bytes = AllocationAssert.Measure(() => send.Process(dry, wet, BlockFrames));

        Assert.Equal(0, bytes);
    }
}
