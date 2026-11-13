using System;
using System.IO;
using System.Runtime.CompilerServices;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// Points <c>JOCKEYBOX_HOME</c> at a throwaway directory before any test touches
/// <see cref="AppPaths.Home"/>, and seeds it with a settings file that has no
/// audio route.
///
/// <para><b>Why the home.</b> Only a few fixtures scope the home themselves;
/// everything else that happens to boot a store or write a log line inherited a
/// null override and wrote into the operator's live
/// <c>%APPDATA%\JockeyBox\logs\jockeybox.log</c>. A suite run then polluted the
/// very log used to diagnose a field failure, with thousands of lines that read
/// exactly like real ones. A module initializer runs when this assembly loads,
/// which is before any test or fixture constructor, so the default is safe for
/// every test rather than for the ones that remembered.</para>
///
/// <para>The per-fixture scoping is unaffected: those fixtures read the current
/// value as their "previous" and restore it on dispose, so they still get their
/// own directory and hand back this one.</para>
///
/// <para><b>Why the silent route.</b> A <see cref="JockeyBox.ViewModels.MainViewModel"/>
/// builds a real <c>DualDeckTransport</c>, whose constructor snapshots
/// <see cref="JockeyBox.Settings.AppSettings"/> and starts the engine when that
/// settings file carries a route: real WASAPI endpoints plus a render thread per
/// view model. With no settings file, <c>BuildRoute</c> deliberately falls back to
/// the system default output ("None" only means "the operator turned it off" once
/// a routing choice has been saved), so every test that constructed a view model
/// opened a device and never closed one. That is a leak in the harness, not in the
/// app, and it eventually took the whole test host down mid-run. Writing
/// <c>AudioRoutingConfigured: true</c> with every endpoint empty makes
/// <c>BuildRoute</c> return a route with no output, so <c>Engine.Start()</c> is
/// never reached: the suite renders nothing and touches no device.</para>
/// </summary>
internal static class TestHome
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        string home = Path.Combine(
            Path.GetTempPath(), "jb-tests-home", Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(home);
        WriteSilentSettings(home);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);
    }

    /// <summary>
    /// Every bus explicitly empty with the "a choice was saved" flag set, which is
    /// the one combination <c>BuildRoute</c> honours as None rather than falling
    /// back to the default endpoint.
    /// </summary>
    private static void WriteSilentSettings(string home)
    {
        const string json = """
            {
              "AudioRoutingConfigured": true,
              "MainOutputDeviceId": null,
              "ProgramOutputDeviceId": null,
              "KaraokeAidOutputDeviceId": null,
              "RecordingOutputDeviceId": null,
              "SoundboardOutputDeviceId": null,
              "BridgeOutputDeviceId": null,
              "MonitorOutputDeviceId": null,
              "SecondOutputDeviceId": null,
              "DeckADeviceId": null,
              "DeckBDeviceId": null,
              "MicDevices": [],
              "OutputDelayMs": 0,
              "MasterVolume": 1
            }
            """;

        File.WriteAllText(Path.Combine(home, "settings.json"), json);

        // Read-only on purpose. Saves from the app are best-effort and swallow their
        // own failures, but a half-written file is not readable, and TryLoad answers
        // an unreadable file with defaults - which is exactly the "no routing chosen
        // yet" state that opens the default endpoint. Locking the file means every
        // load in the suite sees the same silent route, whatever any test happens to
        // be saving at the time.
        File.SetAttributes(
            Path.Combine(home, "settings.json"), FileAttributes.ReadOnly);
    }
}
