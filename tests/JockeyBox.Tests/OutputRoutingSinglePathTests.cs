using System;
using System.IO;
using System.Linq;
using JockeyBox.Audio;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The deck-window output combos and the Settings routing boxes are two views of
/// one configuration: the route fields the engine reads.
/// <see cref="AppSettings.MainOutputDeviceId"/> is deck A,
/// <see cref="AppSettings.KaraokeAidOutputDeviceId"/> is deck B. They used to be
/// separate - the deck combos wrote <c>DeckADeviceId</c>/<c>DeckBDeviceId</c>,
/// which a schema-4 profile never reads, so moving a deck selector changed
/// nothing the engine listened to.
///
/// <para><b>The trap these tests lock down.</b> On the operator's profile the
/// stale <c>DeckBDeviceId</c> names the Realtek speakers while deck B actually
/// plays to the Bluetooth headphones through
/// <see cref="AppSettings.KaraokeAidOutputDeviceId"/>. A migration that treated
/// the deck fields as authoritative would silently move deck B off the
/// headphones, so the deck ids are dropped, never promoted.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class OutputRoutingSinglePathTests : IDisposable
{
    private const string Speakers =
        "{0.0.0.00000000}.{b120335b-db56-4c51-9757-f11e6f84819f}";

    private const string Headphones =
        "{0.0.0.00000000}.{9d0c4d55-6f2a-4c11-8a3f-6b1c0a0a1111}";

    private readonly string _home;
    private readonly HomeLease _lease;

    public OutputRoutingSinglePathTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "jb-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _lease = HomeLease.Enter(_home, nameof(OutputRoutingSinglePathTests));
    }

    public void Dispose()
    {
        _lease.Dispose();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* a render thread may still hold a log file */ }
    }

    private static void WriteSettings(string json) =>
        File.WriteAllText(AppSettings.SettingsPath, json);

    /// <summary>An empty routing choice, so a view model never opens a real endpoint.</summary>
    private static void WriteSilentRoute(string? main = null, string? karaokeAid = null)
    {
        var settings = AppSettings.Load();
        settings.AudioRoutingConfigured = true;
        settings.MainOutputDeviceId = main;
        settings.KaraokeAidOutputDeviceId = karaokeAid;
        settings.RecordingOutputDeviceId = null;
        settings.SoundboardOutputDeviceId = null;
        settings.MonitorOutputDeviceId = null;
        settings.Save();
    }

    [Fact]
    public void A_schema_4_profile_keeps_deck_B_on_its_route_field_and_drops_the_deck_ids()
    {
        WriteSettings($$"""
            {
              "SettingsSchemaVersion": 4,
              "AudioRoutingConfigured": true,
              "MainOutputDeviceId": "{{Speakers}}",
              "KaraokeAidOutputDeviceId": "{{Headphones}}",
              "DeckADeviceId": "{{Speakers}}",
              "DeckBDeviceId": "{{Speakers}}",
              "SecondOutputDeviceId": "{{Speakers}}"
            }
            """);

        var loaded = AppSettings.Load();

        // The route the engine reads, not the stale deck field.
        Assert.Equal(Speakers, loaded.MainOutputDeviceId);
        Assert.Equal(Headphones, loaded.KaraokeAidOutputDeviceId);
        Assert.Equal(Headphones, DualDeckTransport.BuildRouteFromAppSettings(loaded).DeckBDeviceId);

        // The migration ran during load and saved: the dead keys are gone.
        var json = File.ReadAllText(AppSettings.SettingsPath);
        Assert.DoesNotContain("DeckADeviceId", json);
        Assert.DoesNotContain("DeckBDeviceId", json);
        Assert.DoesNotContain("SecondOutputDeviceId", json);
        Assert.Contains("\"SettingsSchemaVersion\": 5", json);

        // Idempotent: a second load leaves both the route and the file alone.
        var again = AppSettings.Load();
        Assert.Equal(Speakers, again.MainOutputDeviceId);
        Assert.Equal(Headphones, again.KaraokeAidOutputDeviceId);
        Assert.Equal(json, File.ReadAllText(AppSettings.SettingsPath));
    }

    [Fact]
    public void A_schema_3_profile_migrates_program_to_main_and_never_promotes_a_deck_id()
    {
        WriteSettings($$"""
            {
              "SettingsSchemaVersion": 3,
              "ProgramOutputDeviceId": "{{Speakers}}",
              "DeckADeviceId": "{{Headphones}}",
              "DeckBDeviceId": "{{Headphones}}"
            }
            """);

        var loaded = AppSettings.Load();

        // Program becomes Main with the value it already had...
        Assert.Equal(Speakers, loaded.MainOutputDeviceId);
        // ...and the deck send does not become a route for either deck.
        Assert.Null(loaded.KaraokeAidOutputDeviceId);

        var route = DualDeckTransport.BuildRouteFromAppSettings(loaded);
        Assert.Equal(Speakers, route.DeckADeviceId);
        Assert.Null(route.DeckBDeviceId);

        var json = File.ReadAllText(AppSettings.SettingsPath);
        Assert.DoesNotContain("DeckADeviceId", json);
        Assert.DoesNotContain("DeckBDeviceId", json);
        Assert.Contains("\"SettingsSchemaVersion\": 5", json);
    }

    [Fact]
    public void Choosing_a_device_on_a_deck_combo_writes_the_route_field_the_engine_reads()
    {
        WriteSilentRoute();

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            var devices = new[]
            {
                new AudioDeviceInfo("dev-a", "Speakers (Realtek)"),
                new AudioDeviceInfo("dev-b", "soundcore Sport X20"),
            };
            UiThread.Run(() => vm.DeckA.RefreshDevices(devices));
            UiThread.Run(() => vm.DeckB.RefreshDevices(devices));

            UiThread.Run(() => vm.DeckA.SelectedDevice =
                vm.DeckA.Devices.First(d => d.Id == "dev-a"));
            UiThread.Run(() => vm.DeckB.SelectedDevice =
                vm.DeckB.Devices.First(d => d.Id == "dev-b"));

            var saved = AppSettings.Load();
            Assert.Equal("dev-a", saved.MainOutputDeviceId);
            Assert.Equal("dev-b", saved.KaraokeAidOutputDeviceId);

            var route = DualDeckTransport.BuildRouteFromAppSettings(saved);
            Assert.Equal("dev-a", route.DeckADeviceId);
            Assert.Equal("dev-b", route.DeckBDeviceId);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    [Fact]
    public void A_null_route_field_shows_None_rather_than_a_device_nothing_is_routed_to()
    {
        WriteSilentRoute(main: null, karaokeAid: null);

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            var devices = new[] { new AudioDeviceInfo("dev-a", "Speakers (Realtek)") };
            UiThread.Run(() => vm.DeckA.RefreshDevices(devices));
            UiThread.Run(() => vm.DeckB.RefreshDevices(devices));

            // The bus is on None; the combo must say so instead of falling back to
            // the first enumerated endpoint and implying a route that does not exist.
            Assert.Equal("", vm.DeckA.SelectedDevice?.Id);
            Assert.Equal("", vm.DeckB.SelectedDevice?.Id);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    [Fact]
    public void A_saved_device_that_is_not_connected_stays_listed_and_selected()
    {
        WriteSilentRoute(main: "gone-device");

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() => vm.DeckA.RefreshDevices(
                new[] { new AudioDeviceInfo("dev-a", "Speakers (Realtek)") }));

            var row = Assert.Single(vm.DeckA.Devices.Where(d => d.Id == "gone-device"));
            Assert.Equal("Saved device (not connected)", row.FriendlyName);
            Assert.Equal("gone-device", vm.DeckA.SelectedDevice?.Id);

            // The same three rows Settings shows: None, the live endpoints, the saved one.
            Assert.Contains(vm.DeckA.Devices, d => d.Id == "" && d.FriendlyName == "None");
            Assert.Contains(vm.DeckA.Devices, d => d.Id == "dev-a");
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }
}
