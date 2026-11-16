using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.Tools;
using NAudio.Wave;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A deck's own output delay: the offset the audience hears from <i>that</i> deck.
/// It is not <c>DeckViewModel.DelayMs</c> (the karaoke stem trim inside a deck) and
/// not <c>OutputDelayMs</c> (the global A/V offset, applied per bus after the decks
/// are summed), so it is asserted here on the offline render loop where a frame
/// index is a fact: the delay line is read in frames, not milliseconds.
///
/// <para><b>Why the two decks in one run.</b> The point of the feature is
/// independence, so deck A is delayed and deck B is not, in the same render, and
/// both offsets are read out of the same block stream. A test that rendered each
/// deck alone could pass with one shared delay.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class DeckOutputDelayTests
{
    private const int Rate = AudioEngine.Rate;
    private const int Block = AudioEngine.BlockFrames;

    /// <summary>300 ms: the impulse must still be inside the 50-block window after a 250 ms shift.</summary>
    private const int Blocks = 60;

    /// <summary>
    /// One full-scale sample at frame 0 and silence after it: the first audible
    /// frame of the output <i>is</i> the delay, with no threshold heuristics.
    /// </summary>
    private static string WriteImpulseWav(float amplitude, double seconds = 1.0)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jb-imp-{Guid.NewGuid():N}.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        var data = new float[(int)(seconds * Rate) * 2];
        data[0] = amplitude;
        data[1] = amplitude;
        using (var writer = new WaveFileWriter(path, format))
            writer.WriteSamples(data, 0, data.Length);
        return path;
    }

    /// <summary>Every block of every tap, copied (the engine reuses its buffers).</summary>
    private sealed class Tap
    {
        private readonly object _gate = new();
        private readonly Dictionary<BusKind, List<float[]>> _buses = new();

        public List<float[]> Program { get; } = new();
        public List<float[]> Monitor { get; } = new();

        public IReadOnlyList<float[]> Bus(BusKind kind)
        {
            lock (_gate)
                return _buses.TryGetValue(kind, out var list) ? list : Array.Empty<float[]>();
        }

        public void OnProgram(float[] buffer, int frames) => Add(Program, buffer, frames);

        public void OnMonitor(float[] buffer, int frames) => Add(Monitor, buffer, frames);

        public void OnBus(BusKind kind, float[] buffer, int frames)
        {
            lock (_gate)
            {
                if (!_buses.TryGetValue(kind, out var list)) _buses[kind] = list = new List<float[]>();
                Add(list, buffer, frames);
            }
        }

        private void Add(List<float[]> list, float[] buffer, int frames)
        {
            var copy = new float[frames * AudioEngine.Channels];
            Array.Copy(buffer, copy, copy.Length);
            lock (_gate) list.Add(copy);
        }
    }

    /// <summary>Frame index of the first sample above the floor, or -1.</summary>
    private static int FirstAudible(IReadOnlyList<float[]> blocks, float floor = 0.01f)
    {
        int frame = 0;
        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Length; i += AudioEngine.Channels, frame++)
                if (Math.Abs(block[i]) > floor) return frame;
        }
        return -1;
    }

    /// <summary>Sample value at an absolute frame (left channel), or NaN when out of range.</summary>
    private static float SampleAt(IReadOnlyList<float[]> blocks, int frame)
    {
        int block = frame / Block;
        if (block >= blocks.Count) return float.NaN;
        return blocks[block][(frame % Block) * AudioEngine.Channels];
    }

    private static Tap Run(AudioEngine engine, int blocks)
    {
        var tap = new Tap();
        engine.StartOffline(tap.OnProgram, blocks * Block / (double)Rate, tap.OnMonitor,
            (kind, buffer, frames) => tap.OnBus(kind, buffer, frames));
        while (engine.IsRunning) Thread.Sleep(1);
        return tap;
    }

    [Fact]
    public async Task Deck_A_at_250ms_lags_deck_B_at_0ms_by_exactly_12000_frames()
    {
        // Different amplitudes so the two impulses are identifiable in the one sum.
        string a = WriteImpulseWav(0.5f);
        string b = WriteImpulseWav(0.25f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0.5, 0);
            engine.MonitorEnabled = true;
            engine.MonitorAGain = 1f;
            engine.MonitorBGain = 1f;
            engine.OutputDelayMs = 0;               // the global offset is not under test
            engine.SetDeckOutputDelayMs(true, 250);
            engine.SetDeckOutputDelayMs(false, 0);

            await engine.DeckA.LoadAsync(a);
            await engine.DeckB.LoadAsync(b);

            var tap = Run(engine, Blocks);

            int offsetA = FirstAudible(tap.Bus(BusKind.DeckA));
            int offsetB = FirstAudible(tap.Bus(BusKind.DeckB));
            Console.WriteLine($"deck A first audible frame {offsetA}, deck B {offsetB}");
            Console.WriteLine($"deck A impulse {SampleAt(tap.Bus(BusKind.DeckA), offsetA)}, " +
                              $"deck B impulse {SampleAt(tap.Bus(BusKind.DeckB), offsetB)}");

            // 250 ms at 48 kHz: 12 000 frames, sample-exact, and deck B has not moved.
            Assert.Equal(12_000, offsetA);
            Assert.Equal(0, offsetB);

            // Nothing wrapped: each deck carries exactly one impulse, at its offset.
            Assert.Equal(0f, SampleAt(tap.Bus(BusKind.DeckA), 0));
            Assert.Equal(0f, SampleAt(tap.Bus(BusKind.DeckB), 12_000));

            // Both impulses survived the delay line uncorrupted (fader at centre is
            // the equal-power 0.7071 on each deck).
            Assert.Equal(0.5f * 0.70710678f, SampleAt(tap.Bus(BusKind.DeckA), 12_000), 5);
            Assert.Equal(0.25f * 0.70710678f, SampleAt(tap.Bus(BusKind.DeckB), 0), 5);

            // The monitor tap is inside the deck stage, so it carries the same shift.
            int monitorA = FirstAudible(tap.Monitor);
            Assert.Equal(0, monitorA); // deck B's undelayed impulse is first in the sum
            Assert.Equal(0.25f, SampleAt(tap.Monitor, 0), 5);
            Assert.Equal(0.5f, SampleAt(tap.Monitor, 12_000), 5);

            // So does the program sum the audience/capture gets.
            Assert.Equal(0.25f * 0.70710678f, SampleAt(tap.Program, 0), 5);
            Assert.Equal(0.5f * 0.70710678f, SampleAt(tap.Program, 12_000), 5);
        }
        finally
        {
            TestAudio.TryDelete(a);
            TestAudio.TryDelete(b);
        }
    }

    [Fact]
    public async Task Zero_delay_leaves_the_deck_block_bit_identical()
    {
        // A constant is the strictest shape for this: every output sample must be
        // the source sample times exactly 1.0, so any scaling, rounding or
        // off-by-one in the delay path shows up as a different float.
        string path = TestAudio.WriteConstantWav(0.25f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);                // deck A at unity, deck B silent
            engine.OutputDelayMs = 0;
            engine.SetDeckOutputDelayMs(true, 0);

            await engine.DeckA.LoadAsync(path);

            var tap = Run(engine, 10);
            var bus = tap.Bus(BusKind.DeckA);
            Assert.NotEmpty(bus);
            foreach (var block in bus)
                foreach (float sample in block)
                    Assert.Equal(0.25f, sample);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    /// <summary>
    /// The proof the constant test above cannot give. A constant survives scaling,
    /// fading and rounding, so it shows the path does not change the <i>level</i>; it
    /// does not show that the block coming out is the block that went in. This drives
    /// a non-constant stream through the stage at 0 ms and compares the output array
    /// to the input array with <c>Assert.Equal</c> — the standard the EQ detent test
    /// already meets. The reference <i>is</i> the delay stage bypassed: the bytes that
    /// would reach the mix if the line were not there. A one-frame copy offset, a
    /// partial write or a crossfade rounding leaves a different float.
    ///
    /// <para>200 blocks is 2.13 s, so the ring buffer wraps and the write-head seam is
    /// inside the compared range as well.</para>
    /// </summary>
    [Fact]
    public void Zero_delay_stage_emits_the_input_block_unchanged()
    {
        const int frames = AudioEngine.BlockFrames;
        var line = new DelayBuffer(Rate, AudioEngine.Channels);
        line.SnapDelayMs(0);

        // Deterministic, so a failure reproduces bit for bit (Random is not stable
        // across runtimes and a flaky assertion here would be worthless).
        uint seed = 0x9E3779B9u;
        var input = new float[frames * AudioEngine.Channels];
        var bypassed = new float[input.Length];
        var output = new float[input.Length];

        for (int block = 0; block < 200; block++)
        {
            for (int i = 0; i < input.Length; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                input[i] = ((seed >> 8) / 16_777_216f - 0.5f) * 1.5f;   // -0.75…0.75
            }
            Array.Copy(input, bypassed, input.Length);

            line.Write(input, 0, frames);
            line.ReadDelayed(output, 0, frames);

            Assert.Equal(bypassed, output);
        }
    }

    /// <summary>
    /// The same comparison where it matters for the feature: the <i>deck path</i>, not
    /// the delay line alone. The reference is the identical file read through the same
    /// deck with the delay stage bypassed — a second engine whose deck block is pulled
    /// straight out of <see cref="DeckSource.Read"/>, so every stage the two runs share
    /// (decode, tempo, volume, duck) cancels out and the only stage that could differ
    /// is the delay line. Fader at deck A's unity, EQ flat: the remaining stages are
    /// identity at these settings.
    ///
    /// <para>Deliberately <i>not</i> compared to the WAV bytes: the deck's decode path
    /// already quantises the file (measured: the source value 1.96349531e-4 emerges as
    /// 6/32768 = 1.83105469e-4), which is older than this feature and would make a
    /// file-level reference fail for a reason that has nothing to do with delay.</para>
    ///
    /// <para>A non-constant source is the point: a constant would agree with itself
    /// even if the stage shifted it by a frame.</para>
    /// </summary>
    [Fact]
    public async Task Zero_delay_deck_path_emits_the_bypassed_block_unchanged()
    {
        string path = TestAudio.WriteRampWav(seconds: 2.0);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);                // deck A at unity, deck B silent
            engine.OutputDelayMs = 0;               // the global offset is not under test
            engine.SetDeckOutputDelayMs(true, 0);
            await engine.DeckA.LoadAsync(path);

            const int blocks = 10;
            var tap = Run(engine, blocks);
            var bus = tap.Bus(BusKind.DeckA);
            Assert.Equal(blocks, bus.Count);

            // The bypassed reference: the same file through the same deck, block by
            // block, with no delay line between the deck and the assertion.
            using var bypass = new AudioEngine();
            bypass.Fader.Snap(0, 0);
            bypass.OutputDelayMs = 0;
            await bypass.DeckA.LoadAsync(path);

            var raw = new float[Block * AudioEngine.Channels];
            for (int b = 0; b < blocks; b++)
            {
                int got = bypass.DeckA.Read(raw, 0, raw.Length);
                Assert.Equal(raw.Length, got);
                Assert.Equal(raw, bus[b]);
            }
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public async Task Changing_the_deck_delay_mid_stream_does_not_click()
    {
        // The DelayBuffer's promise, asserted on the deck path: a delay change is
        // the same signal at two taps, so the equal-power crossfade normalized by
        // its own sum keeps a constant source constant. A plain jump to the new tap
        // would drop the output to zero for the length of the new delay - a click.
        string path = TestAudio.WriteConstantWav(0.25f);
        try
        {
            using var engine = new AudioEngine();
            engine.Fader.Snap(0, 0);
            engine.OutputDelayMs = 0;
            engine.SetDeckOutputDelayMs(true, 0);
            await engine.DeckA.LoadAsync(path);

            int blockNo = 0;
            float worst = 0f;
            float previous = 0.25f;
            using var changed = new ManualResetEventSlim();

            engine.StartOffline((buffer, frames) =>
            {
                for (int i = 0; i < frames * AudioEngine.Channels; i += AudioEngine.Channels)
                {
                    float sample = buffer[i];
                    // Skip the deck's own start ramp: the first samples are the file
                    // fading in, not the delay line.
                    if (blockNo > 2) worst = Math.Max(worst, Math.Abs(sample - previous));
                    previous = sample;
                }

                blockNo++;
                if (blockNo == 60)
                {
                    engine.SetDeckOutputDelayMs(true, 250); // moved mid-song
                    changed.Set();
                }
            }, 120 * Block / (double)Rate);

            while (engine.IsRunning) Thread.Sleep(1);
            Assert.True(changed.IsSet);

            Console.WriteLine($"largest sample-to-sample step across the delay change: {worst}");
            // One equal-power step of a 50 ms fade at 48 kHz on a constant signal is
            // far below 1e-3; a tap jump would be the full 0.25.
            Assert.True(worst < 1e-3f, $"click: step {worst}");
            Assert.Equal(250, engine.DeckOutputDelayMs(true));
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public void Output_delay_is_clamped_at_the_engine_and_the_channel()
    {
        using var engine = new AudioEngine();

        engine.SetDeckOutputDelayMs(true, 9_999);
        engine.SetDeckOutputDelayMs(false, -50);
        Assert.Equal(DelayBuffer.MaxDelayMs, engine.DeckOutputDelayMs(true));
        Assert.Equal(2_000, engine.DeckOutputDelayMs(true));
        Assert.Equal(0, engine.DeckOutputDelayMs(false));

        // The channel is the second gate: a value from settings or a stepper can
        // never reach the delay line outside 0…2000.
        var channel = new DeckChannel("Deck A", new DeviceService());
        channel.OutputDelayMs = 5_000;
        Assert.Equal(DeckChannel.MaxOutputDelayMs, channel.OutputDelayMs);
        channel.OutputDelayMs = -1;
        Assert.Equal(0, channel.OutputDelayMs);
    }

    [Fact]
    public async Task Audible_clock_subtracts_its_own_decks_delay_only()
    {
        string path = TestAudio.WriteConstantWav(0.5f);
        try
        {
            using var engine = new AudioEngine();
            engine.SetDeckOutputDelayMs(true, 250);
            engine.SetDeckOutputDelayMs(false, 0);
            await engine.DeckA.LoadAsync(path);

            var buffer = new float[Block * AudioEngine.Channels];
            for (int i = 0; i < 100; i++) engine.DeckA.Read(buffer, 0, buffer.Length);

            // Deck A's clock trails the audible readout by its own 250 ms; the read
            // path is engine-side, so the raw position is unaffected.
            Assert.InRange(engine.DeckA.PositionSeconds - engine.AudibleSeconds(engine.DeckA), 0.249, 0.251);
            Assert.InRange(engine.AudibleSeconds(engine.DeckA),
                engine.DeckA.PositionSeconds - 0.251, engine.DeckA.PositionSeconds - 0.249);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    /// <summary>
    /// The audible clock reads <i>its own deck's</i> bus. Today one OutputDelayMs
    /// fans to every delayed bus, so deck A's and deck B's bus values are always
    /// equal and a shared read would look right; here they are made to diverge
    /// (A 100 ms, B 300 ms) and each deck must still subtract only its own, plus
    /// its own delay line (A 250 ms, B 0).
    /// </summary>
    [Fact]
    public async Task Audible_clock_reads_its_own_bus_when_the_buses_diverge()
    {
        string path = TestAudio.WriteConstantWav(0.5f);
        try
        {
            using var engine = new AudioEngine();

            // Buses deliberately unequal: the value deck B must not inherit.
            engine.Bus(BusKind.DeckA).SetDelayMs(100);
            engine.Bus(BusKind.DeckB).SetDelayMs(300);
            Assert.NotEqual(engine.Bus(BusKind.DeckA).DelayMs, engine.Bus(BusKind.DeckB).DelayMs);

            engine.SetDeckOutputDelayMs(true, 250);
            engine.SetDeckOutputDelayMs(false, 0);
            await engine.DeckA.LoadAsync(path);
            await engine.DeckB.LoadAsync(path);

            var buffer = new float[Block * AudioEngine.Channels];
            for (int i = 0; i < 100; i++)
            {
                engine.DeckA.Read(buffer, 0, buffer.Length);
                engine.DeckB.Read(buffer, 0, buffer.Length);
            }

            // Frames, so the claim is not rounded: A = 4800 bus + 12000 line,
            // B = 14400 bus + 0 line. Reading deck A's bus for deck B would give
            // B 4800 frames, 9600 frames short of the truth.
            double offA = (engine.DeckA.PositionSeconds - engine.AudibleSeconds(engine.DeckA)) * Rate;
            double offB = (engine.DeckB.PositionSeconds - engine.AudibleSeconds(engine.DeckB)) * Rate;

            Console.WriteLine($"audible offset frames: deckA={offA:0.####}, deckB={offB:0.####}");
            Assert.InRange(offA, 16_800 - 1, 16_800 + 1);
            Assert.InRange(offB, 14_400 - 1, 14_400 + 1);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public void Per_deck_output_delay_round_trips_and_is_clamped()
    {
        string home = Path.Combine(Path.GetTempPath(), "jb-deck-outdel", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            var saved = AppSettings.TryLoad() ?? new AppSettings();
            saved.DeckAOutputDelayMs = 250;
            saved.DeckBOutputDelayMs = 137;
            saved.Save();

            var loaded = AppSettings.TryLoad();
            Assert.NotNull(loaded);
            Assert.Equal(250, loaded!.DeckAOutputDelayMs);
            Assert.Equal(137, loaded.DeckBOutputDelayMs);

            // The stem trim keys are a different job and must survive untouched.
            Assert.Equal(saved.DeckADelayMs, loaded.DeckADelayMs);
            Assert.Equal(saved.DeckBDelayMs, loaded.DeckBDelayMs);

            // PostProcess is the clamp, exactly like ClampEqDb: an out-of-range
            // file comes back inside the engine's range, not dropped.
            saved.DeckAOutputDelayMs = 9_999;
            saved.DeckBOutputDelayMs = -1;
            saved.Save();

            var clamped = AppSettings.TryLoad();
            Assert.NotNull(clamped);
            Assert.Equal(DeckChannel.MaxOutputDelayMs, clamped!.DeckAOutputDelayMs);
            Assert.Equal(0, clamped.DeckBOutputDelayMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
