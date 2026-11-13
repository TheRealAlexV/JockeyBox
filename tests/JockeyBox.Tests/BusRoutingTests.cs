using System;
using System.Threading.Tasks;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The 2.2 bus model, asserted end to end on the offline render loop: one block,
/// five buses, no device. Each test reads the same <see cref="BusKind"/> taps the
/// live engine feeds to <see cref="DeviceOutput"/>.
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
    public void Deck_A_carries_no_microphone_signal()
    {
        using var engine = new AudioEngine();
        engine.Mics.SetStrips(new[] { Mic("dev1", 0.30f) });
        engine.Fader.Snap(0, 0);

        var tap = new TestAudio.BusTap();
        TestAudio.RenderBlocks(engine, tap, blocks: 12);

        Assert.Equal(0f, tap.Peak(BusKind.DeckA), 4);
        Assert.Equal(0f, tap.Peak(BusKind.DeckB), 4);
        Assert.Equal(0.30f, tap.Peak(BusKind.Recording), 3);
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

        // Recording sees the unmuted device only; the muted cue device is audible
        // to the operator alone.
        Assert.Equal(0.30f, tap.Peak(BusKind.Recording), 3);
        Assert.Equal(0.10f, tap.Peak(BusKind.Monitor), 3);
        Assert.Equal(0f, tap.Peak(BusKind.DeckA), 4);

        // Per-device meters stay live even while muted: muting is not a dead device.
        var peaks = engine.MicPeaks;
        Assert.Equal(2, peaks.Count);
        Assert.Equal(0.30f, peaks[0], 3);
        Assert.Equal(0.10f, peaks[1], 3);
    }

    [Fact]
    public async Task Soundboard_with_no_device_of_its_own_rides_Deck_A()
    {
        string deckPath = TestAudio.WriteConstantWav(0.20f);
        string sbPath = TestAudio.WriteConstantWav(0.10f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);
            engine.SoundboardFollowsMain = true;
            var voice = engine.Soundboard.GetOrAdd("tone", 2);
            await voice.LoadAsync(sbPath);
            voice.Play();
            await engine.DeckA.LoadAsync(deckPath);

            var tap = new TestAudio.BusTap();
            TestAudio.RenderBlocks(engine, tap, blocks: 30);

            // Deck A is deck A plus the soundboard when the soundboard has nowhere
            // else to go; deck B is a different bus and never sees either of them.
            Assert.Equal(0.30f, tap.Peak(BusKind.DeckA), 3);
            Assert.Equal(0f, tap.Peak(BusKind.DeckB), 4);
            Assert.Equal(0f, tap.Peak(BusKind.Soundboard), 4);
        }
        finally
        {
            TestAudio.TryDelete(deckPath);
            TestAudio.TryDelete(sbPath);
        }
    }

    [Fact]
    public async Task Deck_B_bus_stays_silent_while_deck_A_plays()
    {
        string deckPath = TestAudio.WriteConstantWav(0.20f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);
            await engine.DeckA.LoadAsync(deckPath);

            var tap = new TestAudio.BusTap();
            TestAudio.RenderBlocks(engine, tap, blocks: 12);

            Assert.Equal(0.20f, tap.Peak(BusKind.DeckA), 3);
            Assert.Equal(0f, tap.Peak(BusKind.DeckB), 4);
        }
        finally
        {
            TestAudio.TryDelete(deckPath);
        }
    }

    [Fact]
    public async Task Mix_mode_is_unity_gain_with_the_original_and_karaoke_takes()
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

            // Raw sum is 0.20 + 0.50 * 0.5 = 0.45 at the 1:0.5 balance; the
            // summer normalises by the gains present (1 + 0.5 = 1.5) so the peak
            // is 0.30 instead of 0.45. The balance is unchanged.
            Assert.Equal(0.30f, tap.Peak(BusKind.DeckA), 3);
        }
        finally
        {
            TestAudio.TryDelete(original);
            TestAudio.TryDelete(karaoke);
        }
    }

    [Fact]
    public async Task Monitor_bus_carries_both_decks_at_their_own_gains_when_MON_is_on()
    {
        string deckA = TestAudio.WriteConstantWav(0.20f);
        string deckB = TestAudio.WriteConstantWav(0.10f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);
            engine.MonitorEnabled = true;
            engine.MonitorAGain = 0.5f;
            engine.MonitorBGain = 1.0f;
            await engine.DeckA.LoadAsync(deckA);
            await engine.DeckB.LoadAsync(deckB);

            var tap = new TestAudio.BusTap();
            TestAudio.RenderBlocks(engine, tap, blocks: 12);

            // The monitor tap is pre-fader: deck B sits on the far side of the
            // crossfader and is still audible to the operator at its own gain:
            //   0.20 * 0.5 + 0.10 * 1.0 = 0.20
            Assert.Equal(0.20f, tap.Peak(BusKind.Monitor), 3);

            engine.MonitorEnabled = false;
            var tap2 = new TestAudio.BusTap();
            TestAudio.RenderBlocks(engine, tap2, blocks: 6);
            Assert.Equal(0f, tap2.Peak(BusKind.Monitor), 4);
        }
        finally
        {
            TestAudio.TryDelete(deckA);
            TestAudio.TryDelete(deckB);
        }
    }
}
