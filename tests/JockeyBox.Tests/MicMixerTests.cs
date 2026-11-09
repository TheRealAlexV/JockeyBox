using System;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The recording-device mixer: per-device volume, mute and monitor, summed into
/// the Recording and Monitor buses. No hardware: strips are fed a synthetic
/// source, which is the same <see cref="IMicSource"/> seam the live capture uses.
/// </summary>
public sealed class MicMixerTests
{
    private const int Frames = AudioEngine.BlockFrames;
    private const int Channels = AudioEngine.Channels;

    [Fact]
    public void Recording_sums_only_unmuted_devices()
    {
        var mixer = new MicMixer();
        mixer.SetStrips(new[]
        {
            Strip("a", 0.10f),
            Strip("b", 0.20f),
            Strip("c", 0.40f, muted: true),
        });

        var recording = new float[Frames * Channels];
        var monitor = new float[Frames * Channels];
        mixer.Render(Frames, recording, monitor, Channels);

        Assert.Equal(0.30f, recording[0], 5);
        Assert.Equal(0f, monitor[0], 5);
    }

    [Fact]
    public void Monitor_includes_a_muted_device_and_excludes_unflagged_ones()
    {
        var mixer = new MicMixer();
        mixer.SetStrips(new[]
        {
            Strip("audience", 0.30f),
            Strip("cue", 0.10f, muted: true, monitor: true),
        });

        var recording = new float[Frames * Channels];
        var monitor = new float[Frames * Channels];
        mixer.Render(Frames, recording, monitor, Channels);

        Assert.Equal(0.30f, recording[0], 5);
        Assert.Equal(0.10f, monitor[0], 5);
    }

    [Fact]
    public void Volume_is_app_domain_and_scales_both_sends()
    {
        var mixer = new MicMixer();
        mixer.SetStrips(new[] { Strip("a", 0.40f, volume: 0.5f, monitor: true) });

        var recording = new float[Frames * Channels];
        var monitor = new float[Frames * Channels];
        mixer.Render(Frames, recording, monitor, Channels);

        Assert.Equal(0.20f, recording[0], 5);
        Assert.Equal(0.20f, monitor[0], 5);
    }

    [Fact]
    public void Volume_is_clamped_to_the_app_range()
    {
        var strip = Strip("a", 0.40f, volume: 9f);
        Assert.Equal(2f, strip.Volume, 5);

        strip.Volume = -1f;
        Assert.Equal(0f, strip.Volume, 5);
    }

    [Fact]
    public void Peak_is_post_gain_and_unaffected_by_mute()
    {
        var mixer = new MicMixer();
        mixer.SetStrips(new[] { Strip("a", 0.25f, volume: 2f, muted: true) });

        var recording = new float[Frames * Channels];
        var monitor = new float[Frames * Channels];
        mixer.Render(Frames, recording, monitor, Channels);

        Assert.Equal(0f, recording[0], 5);      // muted
        Assert.Equal(0.50f, mixer.PeakAt(0), 5); // metered before mute
        Assert.Equal(0f, mixer.PeakAt(3), 5);    // out of range
    }

    [Fact]
    public void Empty_mixer_is_silence_and_zero_blocks()
    {
        var mixer = new MicMixer();
        var recording = new float[Frames * Channels];
        var monitor = new float[Frames * Channels];
        Array.Fill(recording, 1f);
        Array.Fill(monitor, 1f);
        mixer.Render(Frames, recording, monitor, Channels);

        Assert.Equal(0f, recording[0], 5);
        Assert.Equal(0f, monitor[0], 5);
        Assert.Equal(0, mixer.Count);
    }

    private static MicStrip Strip(string id, float value, float volume = 1f, bool muted = false, bool monitor = false)
    {
        var strip = new MicStrip(id, new TestAudio.ConstantMic(value));
        strip.Volume = volume;
        strip.Muted = muted;
        strip.Monitor = monitor;
        return strip;
    }
}
