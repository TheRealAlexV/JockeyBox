using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;

namespace JockeyBox.ViewModels;

/// <summary>
/// The talk-over microphone strip on the main window. It edits <b>one</b> of the
/// enabled recording devices - the one <see cref="SelectedMicDeviceId"/> names -
/// so with two microphones a level change moves only that strip.
///
/// <para>Per device the strip owns an app-domain level, a mute, a voice-only
/// monitor send and a peak meter. The engine side is keyed by device id
/// (<see cref="AudioEngine.SetMicVolume"/> and friends); there is no scalar that
/// fans out to every strip any more.</para>
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- state

    /// <summary>Mic gain, 0..2 (0..200%) for the selected device. The Windows recording level is untouched.</summary>
    [ObservableProperty] private double _micLevel = 1.0;

    /// <summary>Send the selected device to the monitor output too. Off by default (feedback).</summary>
    [ObservableProperty] private bool _micMonitorEnabled;

    /// <summary>Mute the selected device. The capture stays open, so unmuting is instant.</summary>
    [ObservableProperty] private bool _micMuted;

    /// <summary>Meter fill, 0..1, of the selected device, decayed on the UI timer.</summary>
    [ObservableProperty] private double _micPeak;

    /// <summary>One-line mic state for the strip's tooltip: "off", "muted", "missing: ..." or the device name.</summary>
    [ObservableProperty] private string _micStatusText = "off";

    private DispatcherTimer? _micMeter;

    // ---------------------------------------------------------------- init

    /// <summary>
    /// Adopt the saved microphone settings and start the peak meter. Called once
    /// from the main window after the view model is constructed; a no-op when the
    /// transport has no engine yet.
    /// </summary>
    public void InitializeMic()
    {
        // First run with no saved choice: adopt the communications/console
        // microphone once (what Windows itself would use for a call), then leave
        // the choice alone - choosing "None" in the dialog sticks.
        if (!_settings.MicAutoSelected && string.IsNullOrEmpty(_settings.MicDeviceId))
        {
            string? defaultId = new DeviceService().GetDefaultCaptureDeviceId();
            if (!string.IsNullOrEmpty(defaultId))
            {
                _settings.MicDeviceId = defaultId;
                _settings.MicAutoSelected = true;
                _settings.Save();
            }
        }

        // Open the saved device set first, then bind the strip to the selection:
        // the strip's scalars are read from the selected strip.
        ApplyMicRoute();
        AdoptMicSelection();

        if (_micMeter == null)
        {
            // 50 ms is fast enough for a meter and slow enough to stay quiet on
            // the dispatcher while decks render.
            _micMeter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _micMeter.Tick += (_, _) => PollMic();
        }
        _micMeter.Start();
        PollMic();
    }

    /// <summary>Adopt a mic choice made in the settings dialog (the device may have changed).</summary>
    public void OnSettingsSavedMic()
    {
        ApplyMicRoute();
        AdoptMicSelection();
        PollMic();
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Unity gain, the same "reset to 100%" habit the master fader has.</summary>
    [RelayCommand]
    private void ResetMicLevel() => MicLevel = 1.0;

    // ---------------------------------------------------------------- wiring

    partial void OnMicLevelChanged(double value)
    {
        double clamped = Math.Clamp(value, 0, 2.0);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            MicLevel = clamped;
            return;
        }

        if (_adoptingMicSelection) return;

        // App gain only, and only for the selected device: no IAudioEndpointVolume,
        // no device property writes, no fan-out to the other microphones.
        var id = SelectedMicDeviceId;
        if (string.IsNullOrEmpty(id)) return;

        _transport?.Engine?.SetMicVolume(id, (float)clamped);
        if (MicSettingsFor(id) is { } entry) entry.Volume = (float)clamped;
    }

    partial void OnMicMonitorEnabledChanged(bool value)
    {
        if (_adoptingMicSelection) return;

        var id = SelectedMicDeviceId;
        if (string.IsNullOrEmpty(id)) return;

        _transport?.Engine?.SetMicMonitor(id, value);
        if (MicSettingsFor(id) is { } entry) entry.Monitor = value;
    }

    partial void OnMicMutedChanged(bool value)
    {
        if (_adoptingMicSelection) return;

        var id = SelectedMicDeviceId;
        if (string.IsNullOrEmpty(id)) return;

        _transport?.Engine?.SetMicMuted(id, value);
        if (MicSettingsFor(id) is { } entry) entry.Muted = value;
        UpdateMicStatus();
    }

    /// <summary>Open (or close) the capture endpoints for the saved device list.</summary>
    private void ApplyMicRoute()
    {
        if (_transport?.Engine == null) return;
        _transport.ApplyMicSettings(_settings);
        UpdateMicStatus();
    }

    /// <summary>Poll one meter: the selected strip's peak, never the loudest device's.</summary>
    internal void PollMic()
    {
        if (_transport?.Engine == null) return;

        // Fast attack, ~250 ms release: a talking head reads as a moving bar
        // instead of a jittering one.
        int index = SelectedMicIndex();
        double peak = index < 0 ? 0 : Math.Clamp(_transport.Engine.MicPeakAt(index), 0, 1);
        MicPeak = Math.Max(peak, MicPeak * 0.80);
        UpdateMicStatus();
        RefreshMicDeviceCount();
    }

    private void UpdateMicStatus()
    {
        if (_transport?.Engine == null)
        {
            MicStatusText = "off";
            return;
        }

        var id = SelectedMicDeviceId;
        if (string.IsNullOrEmpty(id))
        {
            MicStatusText = "off";
            return;
        }

        int index = SelectedMicIndex();
        if (index < 0)
        {
            // "Not enabled" and "unplugged" both land here on purpose: the
            // selection is kept, the controls stay bound, and the strip says why
            // the meter reads nothing instead of silently moving to another device.
            MicStatusText = "missing: " + id;
            return;
        }

        if (MicMuted)
        {
            MicStatusText = "muted";
            return;
        }

        var strip = _transport.Engine.Mics.Strips[index];
        MicStatusText = strip.IsOpen ? strip.DeviceName : "lost: " + strip.DeviceName;
    }
}
