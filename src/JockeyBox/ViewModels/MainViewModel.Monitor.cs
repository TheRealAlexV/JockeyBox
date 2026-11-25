using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Audio;
using JockeyBox.Playback;

namespace JockeyBox.ViewModels;

/// <summary>
/// MON: the monitor bus's two deck sends, on the main window's header - a switch,
/// one gain per deck and a link between them.
///
/// <para><b>This is a control surface, not the feature.</b> The engine already
/// mixes it: <c>AudioEngine.MonitorEnabled</c> puts deck A ×
/// <c>MonitorAGain</c> plus deck B × <c>MonitorBGain</c> on the Monitor device,
/// with the monitored microphones, while the main outputs stay exactly as they
/// were. So nothing here can change what the room hears: the monitor is a second
/// destination for the same audio.</para>
///
/// <para><b>Where the state lives.</b> The two gains and the switch are settings,
/// so a restart comes back where the operator left them; the link is not, because
/// MVSYNC does not describe any audio - it says how the two sliders respond to a
/// hand, which is a property of this window and not of the mix.</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>Send both decks to the monitor output. Off by default.</summary>
    [ObservableProperty] private bool _monitorEnabled;

    /// <summary>Deck A's send into the monitor bus, 0..1.2 (100% is unity, above is boost).</summary>
    [ObservableProperty] private double _monitorAGain = 0.7;

    /// <summary>Deck B's send into the monitor bus, 0..1.2.</summary>
    [ObservableProperty] private double _monitorBGain = 0.7;

    /// <summary>
    /// MVSYNC: moving either monitor slider moves the other. Session state, not a
    /// setting - see the class remarks.
    /// </summary>
    [ObservableProperty] private bool _monitorSyncEnabled;

    /// <summary>
    /// CUE A: deck A's send to its own endpoint. On by default; the button
    /// switches this deck's cue <i>off</i>.
    /// </summary>
    [ObservableProperty] private bool _deckACueEnabled = true;

    /// <summary>CUE B: deck B's send to its own endpoint. On by default.</summary>
    [ObservableProperty] private bool _deckBCueEnabled = true;

    /// <summary>Monitor endpoints (None, the live ones, and the saved-but-unconnected one).</summary>
    public ObservableCollection<AudioDeviceInfo> MonitorDeviceChoices { get; } = new();

    /// <summary>
    /// The endpoint MON sends to; null = None. Writes
    /// <see cref="Settings.AppSettings.MonitorOutputDeviceId"/>, the same route
    /// field the Settings dialog edits, so the two cannot disagree.
    /// </summary>
    [ObservableProperty] private string? _selectedMonitorDeviceId;

    /// <summary>Set while the dropdown is being rebuilt, so adopting a row does not re-save.</summary>
    private bool _adoptingMonitorChoice;

    /// <summary>
    /// Adopt the saved monitor switch and gains. Called from the window next to
    /// <see cref="InitializeMic"/>; the transport owns an engine from construction,
    /// so unlike the mic there is no "not yet" case, but the guard stays for the
    /// tests that build a view model without one.
    /// </summary>
    public void InitializeMonitor()
    {
        MonitorEnabled = _settings.MonitorEnabled;
        MonitorAGain = _settings.MonitorAGain;
        MonitorBGain = _settings.MonitorBGain;
        DeckACueEnabled = _settings.DeckACueEnabled;
        DeckBCueEnabled = _settings.DeckBCueEnabled;
        ApplyMonitor();

        // The deck combos already enumerate the endpoints; the monitor dropdown
        // follows that same refresh instead of opening MMDevAPI a second time.
        DeckA.Devices.CollectionChanged += (_, _) => RebuildMonitorDeviceChoices();
        RebuildMonitorDeviceChoices();
    }

    partial void OnMonitorEnabledChanged(bool value)
    {
        _settings.MonitorEnabled = value;
        ApplyMonitor();
    }

    partial void OnDeckACueEnabledChanged(bool value)
    {
        _settings.DeckACueEnabled = value;
        _settings.Save();
        if (_transport?.Engine is { } engine) engine.DeckACueEnabled = value;
    }

    partial void OnDeckBCueEnabledChanged(bool value)
    {
        _settings.DeckBCueEnabled = value;
        _settings.Save();
        if (_transport?.Engine is { } engine) engine.DeckBCueEnabled = value;
    }

    partial void OnSelectedMonitorDeviceIdChanged(string? value)
    {
        if (_adoptingMonitorChoice) return;

        // None is null, never "" - the engine and BuildRoute read null as None.
        string? id = string.IsNullOrWhiteSpace(value) ? null : value;
        if (string.Equals(_settings.MonitorOutputDeviceId, id, StringComparison.Ordinal)) return;

        _settings.MonitorOutputDeviceId = id;
        _settings.Save();

        // Same single path as the deck combos: the transport rebuilds from the
        // document the write above just changed, and only a changed route reopens.
        _transport.ApplyDeckSettingsChange(PlaybackDeck.A, _settings);
    }

    private void RebuildMonitorDeviceChoices()
    {
        // Deck A's list is None + live endpoints + its own saved row; the live
        // endpoints are what the monitor list needs.
        var live = DeckA.Devices
            .Where(d => !string.IsNullOrWhiteSpace(d.Id))
            .Where(d => !string.Equals(d.FriendlyName, OutputDeviceChoices.NotConnectedName,
                StringComparison.Ordinal));

        var saved = _settings.MonitorOutputDeviceId;
        var rows = OutputDeviceChoices.Build(live, saved);

        _adoptingMonitorChoice = true;
        try
        {
            MonitorDeviceChoices.Clear();
            foreach (var row in rows) MonitorDeviceChoices.Add(row);
            SelectedMonitorDeviceId = OutputDeviceChoices.Select(MonitorDeviceChoices, saved).Id;
        }
        finally { _adoptingMonitorChoice = false; }
    }

    partial void OnMonitorAGainChanged(double value)
    {
        _settings.MonitorAGain = (float)value;
        if (_transport?.Engine is { } engine) engine.MonitorAGain = (float)value;

        // The link, not a copy: the other slider follows the hand that is moving.
        // The tolerance is what stops the pair from chasing each other - deck B's
        // handler would otherwise write deck A back, which fires this one again.
        if (MonitorSyncEnabled && Math.Abs(MonitorBGain - value) > 1e-4) MonitorBGain = value;
    }

    partial void OnMonitorBGainChanged(double value)
    {
        _settings.MonitorBGain = (float)value;
        if (_transport?.Engine is { } engine) engine.MonitorBGain = (float)value;
        if (MonitorSyncEnabled && Math.Abs(MonitorAGain - value) > 1e-4) MonitorAGain = value;
    }

    /// <summary>
    /// Push the three engine values. Also called after the settings dialog, which
    /// reopens the monitor device and needs the switch and gains to land on the new
    /// one - the engine keeps them across a device change, but the gains are floats
    /// the dialog may have written behind the view model's back.
    /// </summary>
    private void ApplyMonitor()
    {
        if (_transport?.Engine is not { } engine) return;
        engine.MonitorEnabled = MonitorEnabled;
        engine.MonitorAGain = (float)MonitorAGain;
        engine.MonitorBGain = (float)MonitorBGain;
    }
}
