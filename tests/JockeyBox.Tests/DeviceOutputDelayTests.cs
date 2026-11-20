using System;
using System.Collections.Generic;
using System.IO;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A per-device output delay: the offset applied to <i>one physical endpoint</i>
/// after every bus feeding it has been summed. Its purpose is the Bluetooth case -
/// the wired path is early, so it is delayed to land with the wireless path - and
/// it is therefore not the global bus delay (<c>OutputDelayMs</c>) and not a deck's
/// own output delay.
///
/// <para><b>Why the two devices in one run.</b> The feature only earns its keep if
/// changing device A cannot move device B. Both devices are rendered in the same
/// test with one impulse each, and the offsets are read from the same block stream;
/// a single shared line could not pass.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class DeviceOutputDelayTests
{
    private const int Rate = AudioEngine.Rate;
    private const int Ch = AudioEngine.Channels;
    private const int Block = AudioEngine.BlockFrames;

    /// <summary>Captures exactly what <see cref="DeviceOutput"/> hands the device.</summary>
    private sealed class Capture
    {
        private readonly List<float[]> _blocks = new();

        public IReadOnlyList<float[]> Blocks => _blocks;

        public void Write(float[] buffer, int offset, int frames)
        {
            var copy = new float[frames * Ch];
            Array.Copy(buffer, offset, copy, 0, copy.Length);
            _blocks.Add(copy);
        }

        /// <summary>Frame index of the first sample above the floor, or -1.</summary>
        public int FirstAudible(float floor = 0.01f)
        {
            int frame = 0;
            foreach (var block in _blocks)
                for (int i = 0; i < block.Length; i += Ch, frame++)
                    if (Math.Abs(block[i]) > floor) return frame;
            return -1;
        }

        public float Sample(int frame) =>
            _blocks[frame / Block][(frame % Block) * Ch];
    }

    private static AudioBus ImpulseBus(BusKind kind, float amplitude)
    {
        var bus = new AudioBus(kind, Rate, Ch, Block);
        Array.Clear(bus.Input);
        bus.Input[0] = amplitude;
        return bus;
    }

    private static AudioBus ConstantBus(BusKind kind, float value)
    {
        var bus = new AudioBus(kind, Rate, Ch, Block);
        Array.Fill(bus.Input, value);
        return bus;
    }

    /// <summary>
    /// 10 ms at 48 kHz is 480 frames. Device A is delayed, device B is not; the
    /// impulse proves A moved by exactly the requested samples and B did not move
    /// at all, in the same render.
    /// </summary>
    [Fact]
    public void Device_delay_shifts_only_that_device_and_by_exact_frames()
    {
        var wired = new Capture();
        var wireless = new Capture();
        using var outputA = new DeviceOutput("wired", wired.Write, Block, Ch, Rate, 10);
        using var outputB = new DeviceOutput("wireless", wireless.Write, Block, Ch, Rate, 0);

        var busA = ImpulseBus(BusKind.DeckA, 0.5f);
        var busB = ImpulseBus(BusKind.DeckB, 0.25f);
        outputA.Attach(busA);
        outputB.Attach(busB);

        for (int i = 0; i < 40; i++)
        {
            busA.Render(Block);
            busB.Render(Block);
            // The engine overwrites each bus's Input every block; an impulse must
            // not be re-rendered or it would appear once per block.
            Array.Clear(busA.Input);
            Array.Clear(busB.Input);
            outputA.Write(Block);
            outputB.Write(Block);
        }

        Assert.Equal(480, wired.FirstAudible());      // 10 ms @ 48 kHz
        Assert.Equal(0, wireless.FirstAudible());
        Assert.Equal(0f, wired.Sample(0));            // silent before the shift
        Assert.Equal(0.5f, wired.Sample(480));
        Assert.Equal(0.25f, wireless.Sample(0));
        Assert.Equal(0f, wireless.Sample(480));       // B was not dragged along
    }

    /// <summary>
    /// A delay change mid-playback must not click. The buffer crossfades between
    /// the old and new taps over 50 ms; with a constant signal those taps carry the
    /// same value, so every output sample must stay exactly 0.5 across the fade.
    /// A one-sample discontinuity, a stale tap or a missing unity gain shows here.
    /// </summary>
    [Fact]
    public void Delay_change_mid_playback_crossfades_instead_of_stepping()
    {
        var capture = new Capture();
        using var output = new DeviceOutput("wired", capture.Write, Block, Ch, Rate, 0);
        var bus = ConstantBus(BusKind.DeckA, 0.5f);
        output.Attach(bus);

        // Prime more than the 1000 ms target delay so the far tap holds audio too.
        for (int i = 0; i < 130; i++)
        {
            bus.Render(Block);
            output.Write(Block);
        }

        output.SetDelayMs(1000);
        Assert.True(output.IsDelayCrossfading);
        Assert.Equal(1000, output.DelayMs);

        int firstFadeBlock = capture.Blocks.Count;
        for (int i = 0; i < 10; i++) // 100 ms, twice the 50 ms fade
        {
            bus.Render(Block);
            output.Write(Block);
        }

        Assert.False(output.IsDelayCrossfading);

        for (int b = firstFadeBlock; b < capture.Blocks.Count; b++)
            foreach (var sample in capture.Blocks[b])
                // Equal power normalises to unity; the residual is float rounding
                // (~2e-5), not a click - a stepped tap would jump by ~0.5.
                Assert.InRange(sample, 0.499f, 0.501f);
    }

    /// <summary>
    /// A device left at 0 ms must be bit-identical to the old path: sum the buses
    /// straight into the device. No scaling, no rounding, no one-block latency.
    /// </summary>
    [Fact]
    public void Zero_delay_is_bit_identical_to_the_undelayed_sum()
    {
        var capture = new Capture();
        using var output = new DeviceOutput("default", capture.Write, Block, Ch, Rate, 0);

        var bus = new AudioBus(BusKind.DeckA, Rate, Ch, Block);
        for (int i = 0; i < bus.Input.Length; i++)
            bus.Input[i] = (i % 997) * 0.001f;
        bus.Render(Block);
        output.Attach(bus);
        output.Write(Block);

        Assert.Equal(bus.Output, capture.Blocks[0]);

        // Two buses summed: 0.25 + 0.5 is exactly representable, so any gain or
        // delay in the zero path changes a bit and fails here.
        var first = ConstantBus(BusKind.DeckA, 0.25f);
        var second = ConstantBus(BusKind.DeckB, 0.5f);
        var both = new Capture();
        using var summed = new DeviceOutput("default", both.Write, Block, Ch, Rate, 0);
        summed.Attach(first);
        summed.Attach(second);

        first.Render(Block);
        second.Render(Block);
        summed.Write(Block);

        foreach (var sample in both.Blocks[^1])
            Assert.Equal(0.75f, sample);
    }

    /// <summary>
    /// The lyric/waveform clock must subtract the delay of the device the deck is
    /// routed to, and only that deck's device. Deck A is on the delayed device,
    /// deck B is not.
    /// </summary>
    [Fact]
    public async Task Audible_clock_subtracts_the_decks_own_device_delay()
    {
        string path = TestAudio.WriteConstantWav(0.5f);
        try
        {
            using var engine = new AudioEngine();
            engine.SetRouteForTest(new AudioRoute(DeckADeviceId: "wired", DeckBDeviceId: "wireless"));
            engine.SetDeviceDelay("wired", 250);
            engine.SetDeviceDelay("wireless", 0);

            await engine.DeckA.LoadAsync(path);
            await engine.DeckB.LoadAsync(path);

            var buffer = new float[Block * Ch];
            for (int i = 0; i < 100; i++)
            {
                engine.DeckA.Read(buffer, 0, buffer.Length);
                engine.DeckB.Read(buffer, 0, buffer.Length);
            }

            Assert.InRange(engine.DeckA.PositionSeconds - engine.AudibleSeconds(engine.DeckA),
                0.249, 0.251);
            Assert.InRange(engine.DeckB.PositionSeconds - engine.AudibleSeconds(engine.DeckB),
                -0.001, 0.001);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    /// <summary>
    /// The engine clamps to the delay buffer's own maximum at both ends, and a
    /// change on one device leaves the other untouched.
    /// </summary>
    [Fact]
    public void Engine_clamps_device_delays_and_keeps_them_independent()
    {
        using var engine = new AudioEngine();
        engine.SetDeviceDelay("wired", 99_999);
        engine.SetDeviceDelay("wireless", 40);
        engine.SetDeviceDelay("wireless", -5);

        Assert.Equal(DelayBuffer.MaxDelayMs, engine.DeviceDelayMs("wired"));
        Assert.Equal(0, engine.DeviceDelayMs("wireless"));
        Assert.Equal(0, engine.DeviceDelayMs("never-configured"));
    }

    /// <summary>
    /// The Settings dialog's three-way merge must treat an edited device delay as
    /// an edit. The snapshot is a <c>Clone()</c>; if that clone shared the dictionary
    /// instance, all three sides would read the new value and the disk copy would
    /// silently win.
    /// </summary>
    [Fact]
    public void Settings_merge_keeps_an_edited_device_delay()
    {
        var snapshot = new AppSettings();
        snapshot.DeviceOutputDelays["wired"] = 0;
        snapshot = snapshot.Clone();

        var disk = snapshot.Clone();                 // disk still says 0
        var edited = snapshot.Clone();
        edited.DeviceOutputDelays["wired"] = 400;    // operator typed 400

        var merged = SettingsMerge.Merge(snapshot, disk, edited);
        Assert.Equal(400d, merged.DeviceOutputDelays["wired"]);
    }

    /// <summary>
    /// Persistence: keyed by endpoint id, survives a save/load round trip, and is
    /// clamped on load so a hand-edited 5000 or -20 cannot reach the buffer.
    /// </summary>
    [Fact]
    public void Device_delays_round_trip_through_settings_and_clamp_on_load()
    {
        string home = Path.Combine(
            Path.GetTempPath(), "jb-device-delay", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            var settings = new AppSettings();
            settings.DeviceOutputDelays["wired"] = 137;
            settings.DeviceOutputDelays["bluetooth"] = 5_000;
            settings.DeviceOutputDelays["gone"] = -20;
            settings.Save();

            var loaded = AppSettings.Load();
            Assert.Equal(137d, loaded.DeviceOutputDelays["wired"]);
            Assert.Equal((double)DelayBuffer.MaxDelayMs, loaded.DeviceOutputDelays["bluetooth"]);
            Assert.Equal(0d, loaded.DeviceOutputDelays["gone"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
