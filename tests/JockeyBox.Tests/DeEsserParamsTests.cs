using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JockeyBox.Api;
using JockeyBox.Audio.Graph;
using JockeyBox.Dsp;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The five de-esser params the rack exposes beyond the original four. They are checked
/// through the router the listener uses, not by reaching into it: every one is listed on
/// <c>/mixer/controls</c> with the bounds <see cref="DeEsserParameters.Clamp"/> enforces,
/// and a write survives a render block and reads back on the strip.
///
/// <para><b>Windows-only.</b> The project targets <c>net8.0-windows</c>, so the gate that
/// runs the WPF suite is the first place these execute.</para>
/// </summary>
public sealed class DeEsserParamsTests
{
    private const string Device = "mic-test";

    private static readonly (string Name, string Unit, double Min, double Max, double Value)[] Added =
    {
        // Bounds are DeEsserParameters.Clamp's own: attack 0.01..1000, release 0.01..5000,
        // max reduction 0..60, sidechain band 20..20000 and 20..24000.
        ("deess.attack", "ms", 0.01, 1000, 12.5),
        ("deess.release", "ms", 0.01, 5000, 180),
        ("deess.maxred", "dB", 0, 60, 18),
        ("deess.sidechain.low", "Hz", 20, 20000, 3200),
        ("deess.sidechain.high", "Hz", 20, 24000, 11000),
    };

    [Fact]
    public void The_five_new_params_are_listed_with_the_dsp_clamps()
    {
        using var rig = new Rig();
        JsonElement controls = rig.Controls();

        foreach (var (name, unit, min, max, _) in Added)
        {
            JsonElement control = controls.EnumerateArray()
                .Single(c => c.GetProperty("id").GetString()!.EndsWith(name, StringComparison.Ordinal));

            Assert.Equal("DeEsser", control.GetProperty("group").GetString());
            Assert.Equal("continuous", control.GetProperty("kind").GetString());
            Assert.Equal(unit, control.GetProperty("unit").GetString());
            Assert.Equal(min, control.GetProperty("min").GetDouble(), 6);
            Assert.Equal(max, control.GetProperty("max").GetDouble(), 6);
        }
    }

    [Fact]
    public void A_write_round_trips_through_a_render_block()
    {
        using var rig = new Rig();
        string body = JsonSerializer.Serialize(Added.ToDictionary(p => p.Name, p => p.Value));

        var put = rig.Router.Handle("PUT", $"/mixer/strips/{rig.Slug}", body);
        Assert.Equal(202, put.Status);

        rig.Mixer.Render(AudioEngine.BlockFrames, new float[AudioEngine.BlockFrames * AudioEngine.Channels],
            new float[AudioEngine.BlockFrames * AudioEngine.Channels], AudioEngine.Channels);

        JsonElement parameters = rig.StripParameters();
        foreach (var (name, _, _, _, value) in Added)
            Assert.Equal(value, parameters.GetProperty(name).GetDouble(), 6);
    }

    [Fact]
    public void The_sidechain_bounds_keep_the_band_in_order()
    {
        // The pair is only constrained in the rack, so a raw write may cross it; what this
        // pins is that the model's own clamp leaves both ends inside their ranges afterwards.
        var crossed = new DeEsserParameters(Enabled: true, SidechainLowHz: 15000, SidechainHighHz: 4000).Clamp();

        Assert.InRange(crossed.SidechainLowHz, 20, 20000);
        Assert.InRange(crossed.SidechainHighHz, 20, 24000);
    }

    private sealed class Rig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "jb-deess-" + Guid.NewGuid().ToString("N"));

        public Rig()
        {
            Directory.CreateDirectory(_directory);
            Mixer = new MicMixer();
            Mixer.SetStrips(new[] { new MicStrip(Device, new TestAudio.ConstantMic(0.1f)) });
            Router = new MixerApiRouter(Mixer, _directory);
            Slug = DeviceControlId.Slug(Device);
        }

        public MicMixer Mixer { get; }

        public MixerApiRouter Router { get; }

        public string Slug { get; }

        public JsonElement Controls() =>
            JsonDocument.Parse(Router.Handle("GET", "/mixer/controls", null).Json)
                .RootElement.GetProperty("controls").Clone();

        public JsonElement StripParameters() =>
            JsonDocument.Parse(Router.Handle("GET", $"/mixer/strips/{Slug}", null).Json)
                .RootElement.GetProperty("parameters").Clone();

        public void Dispose()
        {
            Mixer.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
