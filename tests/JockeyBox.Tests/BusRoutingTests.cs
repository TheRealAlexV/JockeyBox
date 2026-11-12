using System;
using System.Threading.Tasks;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The 2.1 bus model, asserted end to end on the offline render loop: one block,
/// six buses, no device. Each test reads the same <see cref="BusKind"/> taps the
/// live engine feeds to <c>EndpointSink</c>.
/// </summary>
public sealed class BusRoutingTests
{
    private static MicStrip Mic(string id, float value, float volume = 1f, bool muted = false, bool monitor = false)
    {
        var strip = new MicStrip(id, new TestAudio.ConstantMic(value));
        strip.Volume = volume;
        strip.Muted = muted;
        strip.Monitor = monitor;
        return strip;
    }

    [Fact]
    public void Main_carries_no_microphone_signal()
    {
        using var engine = new AudioEngine();
        engine.Mics.SetStrips(new[] { Mic("dev1", 0.30f) });
        engine.Fader.Snap(0, 0);

        var tap = new TestAudio.BusTap();
        TestAudio.RenderBlocks(engine, tap, blocks: 12);

        Assert.Equal(0f, tap.Peak(BusKind.Main), 4);
        Assert.Equal(0.30f, tap.Peak(BusKind.Recording), 3);
        Assert.Equal(0.30f, tap.Peak(BusKind.Bridge), 3);
    }

    [Fact]
    public void Muted_device_with_monitor_reaches_only_Monitor()
    {
        using var engine = new AudioEngine();
        engine.Mics.SetStrips(new[]
        {
            Mic("open", 0.30f),
            Mic("cue", 0.10f, muted: true, monitor: true),
        });
        engine.Fader.Snap(0, 0);

        var tap = new TestAudio.BusTap();
        TestAudio.RenderBlocks(engine, tap, blocks: 12);

        // Recording and Bridge see the unmuted device only; the muted cue device
        // is audible to the operator alone.
        Assert.Equal(0.30f, tap.Peak(BusKind.Recording), 3);
        Assert.Equal(0.30f, tap.Peak(BusKind.Bridge), 3);
        Assert.Equal(0.10f, tap.Peak(BusKind.Monitor), 3);
        Assert.Equal(0f, tap.Peak(BusKind.Main), 4);

        // Per-device meters stay live even while muted: muting is not a dead device.
        var peaks = engine.MicPeaks;
        Assert.Equal(2, peaks.Count);
        Assert.Equal(0.30f, peaks[0], 3);
        Assert.Equal(0.10f, peaks[1], 3);
    }

    [Fact]
    public async Task Bridge_equals_Main_plus_microphones_plus_soundboard_minus_monitor()
    {
        string deckPath = TestAudio.WriteConstantWav(0.20f);
        string sbPath = TestAudio.WriteConstantWav(0.10f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);
            // Keep the soundboard out of Main so both terms of the equation are
            // visible independently; the Bridge must still carry it.
            engine.SoundboardFollowsMain = false;
            engine.Mics.SetStrips(new[]
            {
                Mic("open", 0.30f),
                Mic("cue", 0.10f, muted: true, monitor: true),
            });

            await engine.DeckA.LoadAsync(deckPath);
            var voice = engine.Soundboard.GetOrAdd("tone", 2);
            await voice.LoadAsync(sbPath);
            voice.Play();

            var tap = new TestAudio.BusTap();
            TestAudio.RenderBlocks(engine, tap, blocks: 30);

            float main = tap.Peak(BusKind.Main);
            float mics = tap.Peak(BusKind.Recording);
            float monitor = tap.Peak(BusKind.Monitor);
            float bridge = tap.Peak(BusKind.Bridge);

            Assert.Equal(0.20f, main, 3);
            Assert.Equal(0.30f, mics, 3);
            Assert.Equal(0.10f, monitor, 3);

            // Bridge = Main + mics + soundboard (0.10), and never the monitor cue.
            Assert.Equal(0.60f, bridge, 3);
            Assert.Equal(main + mics + 0.10f, bridge, 3);
            Assert.Equal(main + mics + 0.10f - monitor, bridge - monitor, 3);
        }
        finally
        {
            TestAudio.TryDelete(deckPath);
            TestAudio.TryDelete(sbPath);
        }
    }

    [Fact]
    public async Task Karaoke_aid_stays_silent_while_both_stems_play_in_the_mix()
    {
        string original = TestAudio.WriteConstantWav(0.20f);
        string karaoke = TestAudio.WriteConstantWav(0.50f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);
            await engine.DeckA.LoadAsync(original, karaoke);

            var tap = new TestAudio.BusTap();
            TestAudio.RenderBlocks(engine, tap, blocks: 12);

            Assert.Equal(0f, tap.Peak(BusKind.KaraokeAid), 4);
            // No global karaoke mode: a deck's sub-stem stays in the mix, with the
            // deck's karaoke gain (0.5 by default) on it, and the aid bus is silent.
            Assert.Equal(0.45f, tap.Peak(BusKind.Main), 3);
        }
        finally
        {
            TestAudio.TryDelete(original);
            TestAudio.TryDelete(karaoke);
        }
    }
}
