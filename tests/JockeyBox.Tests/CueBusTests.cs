using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The operator's cue (MON) bus is the <i>only</i> monitor of a deck the program
/// faders have turned down: a DJ cues the next track with its fader at zero. It is
/// asserted on the offline render loop, per bus, the way <see cref="BusRoutingTests"/>
/// does - no device is opened and no block is rendered twice.
///
/// <para><b>The bug these pin.</b> The cue sum used to read the deck blocks after
/// deck volume and duck had been multiplied in, so a deck at volume 0 made the cue
/// exactly silent. The cue must read the deck <i>dry</i> and apply only its own send.</para>
/// </summary>
public sealed class CueBusTests
{
    private const int Block = AudioEngine.BlockFrames;
    private const int Channels = AudioEngine.Channels;

    /// <summary>Frame sampled after the first block: past any start-up transient.</summary>
    private const int ProbeFrame = Block + 10;

    /// <summary>Every block of every bus, copied (the engine reuses its buffers).</summary>
    private sealed class Tap
    {
        private readonly object _gate = new();
        private readonly Dictionary<BusKind, List<float[]>> _buses = new();

        public IReadOnlyList<float[]> Bus(BusKind kind)
        {
            lock (_gate)
                return _buses.TryGetValue(kind, out var list) ? list : Array.Empty<float[]>();
        }

        public void OnBus(BusKind kind, float[] buffer, int frames)
        {
            var copy = new float[frames * Channels];
            Array.Copy(buffer, copy, copy.Length);
            lock (_gate)
            {
                if (!_buses.TryGetValue(kind, out var list)) _buses[kind] = list = new List<float[]>();
                list.Add(copy);
            }
        }
    }

    private static Tap Run(AudioEngine engine, int blocks)
    {
        var tap = new Tap();
        engine.StartOffline(
            capture: (_, _) => { },
            seconds: blocks * Block / (double)AudioEngine.Rate,
            monitorCapture: null,
            busCapture: (kind, buffer, frames) => tap.OnBus(kind, buffer, frames));
        while (engine.IsRunning) Thread.Sleep(1);
        return tap;
    }

    private static float SampleAt(IReadOnlyList<float[]> blocks, int frame)
    {
        int block = frame / Block;
        if (block >= blocks.Count) return float.NaN;
        return blocks[block][(frame % Block) * Channels];
    }

    /// <summary>Engine with one constant 0.5 deck on A, no EQ, no delay, no mics.</summary>
    private static async Task<(AudioEngine Engine, string Wav)> ConstantDeckA()
    {
        string wav = TestAudio.WriteConstantWav(0.5f);
        var engine = new AudioEngine();
        engine.OutputDelayMs = 0;
        engine.MonitorAGain = 1f;
        engine.MonitorBGain = 0f;
        await engine.DeckA.LoadAsync(wav);
        return (engine, wav);
    }

    [Fact]
    public async Task Cue_hears_a_deck_whose_program_fader_is_down()
    {
        var (engine, wav) = await ConstantDeckA();
        try
        {
            using (engine)
            {
                engine.Fader.Snap(1.0, 0);    // crossfader fully to deck B
                engine.DeckAVolume = 0f;      // deck A fader down
                engine.MonitorEnabled = true;

                var tap = Run(engine, 4);

                // Program is correctly silent, and that is exactly the case where the
                // cue has to carry the deck: dry 0.5 x cue send 1.0.
                Assert.Equal(0f, SampleAt(tap.Bus(BusKind.DeckA), ProbeFrame));
                float cue = SampleAt(tap.Bus(BusKind.Monitor), ProbeFrame);
                Assert.True(cue > 0.4f,
                    $"deck at volume 0 with its cue on must reach the cue bus, got {cue}");
                Assert.Equal(0.5f, cue, 3);
            }
        }
        finally { TryDelete(wav); }
    }

    [Fact]
    public async Task Cue_is_silent_for_a_deck_whose_cue_is_off()
    {
        var (engine, wav) = await ConstantDeckA();
        try
        {
            using (engine)
            {
                engine.Fader.Snap(1.0, 0);
                engine.DeckAVolume = 0f;
                engine.MonitorEnabled = true;
                engine.DeckACueEnabled = false;

                var tap = Run(engine, 4);

                Assert.Equal(0f, SampleAt(tap.Bus(BusKind.DeckA), ProbeFrame));
                Assert.Equal(0f, SampleAt(tap.Bus(BusKind.Monitor), ProbeFrame));
            }
        }
        finally { TryDelete(wav); }
    }

    [Fact]
    public async Task Cue_is_silent_when_monitor_is_off()
    {
        var (engine, wav) = await ConstantDeckA();
        try
        {
            using (engine)
            {
                engine.Fader.Snap(0.0, 0);
                engine.DeckAVolume = 1f;
                engine.MonitorEnabled = false;

                var tap = Run(engine, 4);

                Assert.True(SampleAt(tap.Bus(BusKind.DeckA), ProbeFrame) > 0.4f);
                Assert.Equal(0f, SampleAt(tap.Bus(BusKind.Monitor), ProbeFrame));
            }
        }
        finally { TryDelete(wav); }
    }

    [Fact]
    public async Task Program_is_bit_identical_at_every_fader_position()
    {
        // Reference captured on c89ab3b, BEFORE the cue tap moved: dry 0.5 at deck
        // volume 0.8, one value per fader position. Any change here changes what the
        // audience hears.
        double[] positions = { 0.0, 0.25, 0.5, 0.75, 1.0 };
        float[] reference = { 0.4f, 0.3695518f, 0.28284273f, 0.15307337f, 2.4492937E-17f };

        string wav = TestAudio.WriteConstantWav(0.5f);
        var got = new float[positions.Length];
        try
        {
            for (int p = 0; p < positions.Length; p++)
            {
                using var engine = new AudioEngine();
                engine.OutputDelayMs = 0;
                engine.DeckAVolume = 0.8f;
                engine.Fader.Snap(positions[p], 0);
                await engine.DeckA.LoadAsync(wav);

                var tap = Run(engine, 3);
                got[p] = SampleAt(tap.Bus(BusKind.DeckA), ProbeFrame);
                Console.WriteLine($"REF fader={positions[p]:0.00} sample={got[p]:R}");
            }
        }
        finally { TryDelete(wav); }

        for (int p = 0; p < positions.Length; p++)
            Assert.True(reference[p] == got[p],
                $"fader {positions[p]:0.00}: expected {reference[p]:R}, got {got[p]:R}");
    }

    /// <summary>
    /// The cue device is often one of the program devices (the operator's headphones
    /// are sharing an output). Summing the cue and that deck's program would deliver
    /// the deck twice - +6 dB at unity - so on a shared device the deck is delivered
    /// once, at the louder of the two gains.
    /// </summary>
    [Fact]
    public async Task Cue_and_program_on_one_device_play_the_deck_once_at_the_louder_gain()
    {
        string wav = TestAudio.WriteConstantWav(0.5f);
        try
        {
            // dry 0.5, deck B volume 0.6, cue send 0.5:
            //   fader up   -> program 0.30, cue 0.25, max 0.30 (a sum would be 0.55)
            //   fader down -> program 0.00, cue 0.25, max 0.25 (zero would be silence)
            double[] faders = { 1.0, 0.0 };   // crossfader on deck B, then off it
            float[] expected = { 0.3f, 0.25f };
            float[] summed = { 0.55f, 0.25f };

            for (int i = 0; i < faders.Length; i++)
            {
                using var engine = new AudioEngine();
                engine.OutputDelayMs = 0;
                engine.DeckBVolume = 0.6f;
                engine.MonitorEnabled = true;
                engine.MonitorBGain = 0.5f;
                engine.Fader.Snap(faders[i], 0);
                engine.SetRouteForOffline(new AudioRoute(DeckBDeviceId: "dev-cue", MonitorDeviceId: "dev-cue"));
                await engine.DeckB.LoadAsync(wav);

                var tap = Run(engine, 3);
                float program = SampleAt(tap.Bus(BusKind.DeckB), ProbeFrame);
                float cue = SampleAt(tap.Bus(BusKind.Monitor), ProbeFrame);
                float heard = program + cue;

                Assert.True(expected[i] == heard,
                    $"fader {faders[i]:0.0}: device hears {heard:R}, expected max {expected[i]:R} " +
                    $"(program {program:R} + cue {cue:R}; the program plus cue sum would be {summed[i]:R})");
                Assert.True(heard != summed[i] || summed[i] == expected[i],
                    $"fader {faders[i]:0.0}: deck was summed into the device");
            }
        }
        finally { TryDelete(wav); }
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { }
    }
}
