using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;

namespace JockeyBox.ViewModels;

/// <summary>
/// The talk-over microphone strip on the main window: an app-domain level, a
/// voice-only monitor send and a mute.
///
/// <para>Kept additive and in its own file like the 2.0 audio additions: the mic
/// is a third input to the same graph, not a second player, so the only state it
/// owns is a gain, a switch and a peak meter.</para>
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- state

    /// <summary>Mic gain, 0..2 (0..200%). Applied inside our mix; the Windows recording level is untouched.</summary>
    [ObservableProperty] private double _micLevel = 1.0;

    /// <summary>Send the voice to the monitor output too. Off by default (feedback).</summary>
    [ObservableProperty] private bool _micMonitorEnabled;

    /// <summary>Voice mute. The capture device stays open, so unmuting is instant.</summary>
    [ObservableProperty] private bool _micMuted;

    /// <summary>Meter fill, 0..1, decayed on the UI timer.</summary>
    [ObservableProperty] private double _micPeak;

    /// <summary>One-line mic state for the strip's tooltip: "off", "muted", "lost: ..." or the device name.</summary>
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

        // Assign through the properties so the generated change handlers run.
        MicLevel = _settings.MicLevel;
        MicMonitorEnabled = _settings.MicMonitorEnabled;

        ApplyMicRoute();

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
        MicLevel = _settings.MicLevel;
        MicMonitorEnabled = _settings.MicMonitorEnabled;
        ApplyMicRoute();
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

        // App gain only: no IAudioEndpointVolume, no device property writes.
        _transport.Engine.MicVolume = (float)clamped;
        _settings.MicLevel = (float)clamped;
    }

    partial void OnMicMonitorEnabledChanged(bool value)
    {
        if (_transport?.Engine != null) _transport.Engine.MicMonitor = value;
        _settings.MicMonitorEnabled = value;
    }

    partial void OnMicMutedChanged(bool value)
    {
        if (_transport?.Engine != null) _transport.Engine.MicMuted = value;
    }

    /// <summary>Open (or close) the capture endpoint for the saved device id.</summary>
    private void ApplyMicRoute()
    {
        if (_transport?.Engine == null) return;
        _transport.ApplyMicSettings(_settings);
        UpdateMicStatus();
    }

    private void PollMic()
    {
        if (_transport?.Engine == null) return;

        // Fast attack, ~250 ms release: a talking head reads as a moving bar
        // instead of a jittering one.
        double peak = Math.Clamp(_transport.Engine.MicPeak, 0, 1);
        MicPeak = Math.Max(peak, MicPeak * 0.80);
        UpdateMicStatus();
    }

    private void UpdateMicStatus()
    {
        if (_transport?.Engine == null)
        {
            MicStatusText = "off";
            return;
        }

        if (MicMuted)
        {
            MicStatusText = "muted";
            return;
        }

        var capture = _transport.Mic;
        if (capture == null)
        {
            // "None" and "saved-but-unplugged" both land here on purpose: the
            // engine stays silent for the voice and never re-routes itself.
            MicStatusText = string.IsNullOrEmpty(_settings.MicDeviceId)
                ? "off"
                : "missing: " + _settings.MicDeviceId;
            return;
        }

        MicStatusText = capture.IsCapturing
            ? capture.DeviceName
            : "lost: " + capture.DeviceName;
    }
}
