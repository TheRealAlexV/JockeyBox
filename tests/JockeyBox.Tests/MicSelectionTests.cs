using System;
using System.IO;
using System.Linq;
using System.Reflection;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Per-device microphone control: the main-window strip edits the <i>selected</i>
/// recording device, not every device at once, and the cue gates start on so MON
/// does not silently stay dark.
///
/// <para><b>The trap.</b> The 1.5 strip had one scalar per control and the engine
/// fanned each write out to every strip, so with two microphones a level change
/// moved both. And a schema migration forced both cue gates to false, so turning
/// MON on produced no deck audio with nothing on screen to explain it.</para>
///
/// <para>No hardware: the strips are synthetic <see cref="IMicSource"/>s, the
/// same seam the live capture uses.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class MicSelectionTests : IDisposable
{
    private const string MicA = "{0.0.1.00000000}.{aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa}";
    private const string MicB = "{0.0.1.00000000}.{bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb}";
    private const string MissingMic = "{0.0.1.00000000}.{cccccccc-3333-4333-8333-cccccccccccc}";

    private readonly string _home;
    private readonly HomeLease _lease;

    public MicSelectionTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "jb-micsel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _lease = HomeLease.Enter(_home, nameof(MicSelectionTests));
    }

    public void Dispose()
    {
        _lease.Dispose();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* a render thread may still hold a log file */ }
    }

    // ---------------------------------------------------------------- cue gates

    [Fact]
    public void Fresh_settings_file_yields_both_cue_flags_true()
    {
        AppSettings.Load().Save();

        var reloaded = AppSettings.Load();

        Assert.True(reloaded.DeckACueEnabled);
        Assert.True(reloaded.DeckBCueEnabled);
    }

    [Fact]
    public void Migration_no_longer_forces_the_cue_flags_off()
    {
        // A profile written before schema 5 could not have known the flags; the
        // migration used to write false into them, which made MON silent.
        File.WriteAllText(Path.Combine(_home, "settings.json"),
            "{\n  \"SettingsSchemaVersion\": 4\n}");

        var loaded = AppSettings.Load();

        Assert.True(loaded.DeckACueEnabled);
        Assert.True(loaded.DeckBCueEnabled);
    }

    [Fact]
    public void Cue_flag_change_writes_settings_and_engine()
    {
        var vm = UiThread.Run(() => new MainViewModel());

        vm.DeckACueEnabled = false;

        Assert.False(SettingsOf(vm).DeckACueEnabled);
        Assert.False(TransportOf(vm).Engine.DeckACueEnabled);
        Assert.True(TransportOf(vm).Engine.DeckBCueEnabled);
    }

    // ---------------------------------------------------------------- selection

    [Fact]
    public void Selection_survives_a_settings_reload()
    {
        Seed(settings =>
        {
            settings.MicDevices = Devices(MicA, MicB);
            settings.SelectedMicDeviceId = MicB;
            settings.MicAutoSelected = true;
        });

        var vm = UiThread.Run(() => new MainViewModel());
        InstallStrips(vm, (MicA, 0.2f), (MicB, 0.2f));
        UiThread.Run(vm.InitializeMic);

        Assert.Equal(MicB, vm.SelectedMicDeviceId);
    }

    [Fact]
    public void Saved_but_unplugged_selection_stays_selected_and_reports_unavailable()
    {
        Seed(settings =>
        {
            settings.MicDevices = Devices(MicA);
            settings.SelectedMicDeviceId = MissingMic;
            settings.MicAutoSelected = true;
        });

        var vm = UiThread.Run(() => new MainViewModel());
        InstallStrips(vm, (MicA, 0.2f));
        UiThread.Run(vm.InitializeMic);

        Assert.Equal(MissingMic, vm.SelectedMicDeviceId);
        Assert.Contains(MissingMic, vm.MicDeviceChoices.Select(c => c.Id));
        Assert.Contains("missing", vm.MicStatusText, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- per-device

    [Fact]
    public void MicLevel_moves_only_the_selected_strip()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        InstallStrips(vm, (MicA, 0.2f), (MicB, 0.2f));
        vm.SelectedMicDeviceId = MicA;

        vm.MicLevel = 0.4;

        var strips = TransportOf(vm).Engine.Mics.Strips;
        Assert.Equal(0.4f, strips[0].Volume, 3);
        Assert.Equal(1.0f, strips[1].Volume, 3);
    }

    [Fact]
    public void MicMuted_mutes_only_the_selected_device()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        InstallStrips(vm, (MicA, 0.2f), (MicB, 0.2f));
        vm.SelectedMicDeviceId = MicB;

        vm.MicMuted = true;

        var strips = TransportOf(vm).Engine.Mics.Strips;
        Assert.False(strips[0].Muted);
        Assert.True(strips[1].Muted);
    }

    [Fact]
    public void MicPeak_reports_the_selected_strip_not_the_loudest()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        InstallStrips(vm, (MicA, 0.8f), (MicB, 0.05f));
        vm.SelectedMicDeviceId = MicB;

        var engine = TransportOf(vm).Engine;
        engine.Mics.Render(AudioEngine.BlockFrames,
            new float[AudioEngine.BlockFrames * AudioEngine.Channels],
            new float[AudioEngine.BlockFrames * AudioEngine.Channels],
            AudioEngine.Channels);

        UiThread.Run(vm.PollMic);

        Assert.True(vm.MicPeak > 0.01 && vm.MicPeak < 0.2, $"MicPeak was {vm.MicPeak}");
    }

    // ---------------------------------------------------------------- helpers

    private static void Seed(Action<AppSettings> edit)
    {
        var settings = AppSettings.Load();
        edit(settings);
        settings.Save();
    }

    private static System.Collections.Generic.List<AppSettings.MicDeviceSettings> Devices(params string[] ids) =>
        ids.Select(id => new AppSettings.MicDeviceSettings { DeviceId = id }).ToList();

    private static void InstallStrips(MainViewModel vm, params (string Id, float Value)[] mics)
    {
        TransportOf(vm).Engine.Mics.SetStrips(
            mics.Select(m => new MicStrip(m.Id, new TestAudio.ConstantMic(m.Value))));
    }

    private static DualDeckTransport TransportOf(MainViewModel vm) =>
        (DualDeckTransport)typeof(MainViewModel)
            .GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private static AppSettings SettingsOf(MainViewModel vm) =>
        (AppSettings)typeof(MainViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;
}
