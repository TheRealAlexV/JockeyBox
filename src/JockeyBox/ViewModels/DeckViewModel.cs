using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;

namespace JockeyBox.ViewModels;

/// <summary>
/// Presentation model for one deck card: track info, device routing dropdown,
/// delay spinner, volume. Pushes changes into the AudioDeck and asks the
/// transport to resync when routing/delay change mid-playback.
/// </summary>
public partial class DeckViewModel : ObservableObject
{
    private readonly AudioDeck _deck;
    private readonly SyncTransport _transport;
    private readonly Action _persistSettings;
    private readonly Func<string?, double> _getDeviceDelay; // lookup per-device offset
    private readonly Action<string?, double> _setDeviceDelay;
    private readonly DispatcherTimer _resyncDebounce;
    private bool _suppressApply; // true while restoring saved settings

    [ObservableProperty] private string _trackTitle = "No track loaded";
    [ObservableProperty] private string _trackDetail = "";
    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private double _delayMs;
    [ObservableProperty] private float _volume = 1.0f;
    [ObservableProperty] private string _timeReadout = "0:00 / 0:00";
    [ObservableProperty] private bool _isLoaded;

    public DeckViewModel(string title, AudioDeck deck, SyncTransport transport,
                          Action persistSettings,
                          Func<string?, double> getDeviceDelay, Action<string?, double> setDeviceDelay)
    {
        Title = title;
        _deck = deck;
        _transport = transport;
        _persistSettings = persistSettings;
        _getDeviceDelay = getDeviceDelay;
        _setDeviceDelay = setDeviceDelay;

        // Debounce resync/persist so held +/- buttons or rapid edits produce a
        // single clean restart instead of a restart per step.
        _resyncDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _resyncDebounce.Tick += (_, _) =>
        {
            _resyncDebounce.Stop();
            _transport.ApplyDeckSettingsChange();
            _persistSettings();
        };
    }

    public string Title { get; }
    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    /// <summary>Restore persisted device/delay/volume without triggering resyncs.</summary>
    public void RestoreSettings(string? deviceId, double delayMs, float volume)
    {
        _suppressApply = true;
        try
        {
            _deck.DeviceId = deviceId;
            DelayMs = delayMs;
            Volume = volume;
        }
        finally { _suppressApply = false; }
    }

    /// <summary>Repopulate the device dropdown, keeping the current selection.</summary>
    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        var targetId = _deck.DeviceId;
        _suppressApply = true;
        try
        {
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == targetId) ?? Devices.FirstOrDefault();
        }
        finally { _suppressApply = false; }
    }

    public void SetTrack(string? filePath, string title, string artist)
    {
        if (filePath == null)
        {
            _deck.Unload();
            TrackTitle = "No track loaded";
            TrackDetail = "";
            IsLoaded = false;
            return;
        }
        _deck.Load(filePath);
        TrackTitle = title;
        TrackDetail = string.IsNullOrWhiteSpace(artist)
            ? System.IO.Path.GetFileName(filePath)
            : artist;
        IsLoaded = true;
    }

    /// <summary>Update the deck-local time readout from the master position.</summary>
    public void UpdateReadout(TimeSpan masterPosition)
    {
        if (!IsLoaded)
        {
            TimeReadout = "0:00 / 0:00";
            return;
        }
        var local = masterPosition - TimeSpan.FromMilliseconds(DelayMs);
        if (local < TimeSpan.Zero) local = TimeSpan.Zero;
        if (local > _deck.Duration) local = _deck.Duration;
        TimeReadout = $"{Format(local)} / {Format(_deck.Duration)}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Nudge the delay from the +/- repeat buttons (step in ms).</summary>
    [RelayCommand]
    private void NudgeDelay(string step)
    {
        if (double.TryParse(step, out var delta))
            DelayMs = Math.Clamp(DelayMs + delta, -5000, 5000);
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (value == null) return;
        _deck.DeviceId = value.Id;
        if (_suppressApply) return;

        var deviceDelay = _getDeviceDelay(value.Id);
        if (deviceDelay != 0)
            DelayMs = deviceDelay;

        _transport.ApplyDeckSettingsChange();
        _persistSettings();
    }

    partial void OnDelayMsChanged(double value)
    {
        var clamped = Math.Clamp(value, -5000, 5000);
        if (clamped != value)
        {
            DelayMs = clamped;
            return;
        }
        _deck.DelayMs = clamped;
        if (_suppressApply) return;

        // Persist per-device delay
        if (_deck.DeviceId != null)
            _setDeviceDelay(_deck.DeviceId, clamped);

        _resyncDebounce.Stop();
        _resyncDebounce.Start();
    }

    partial void OnVolumeChanged(float value)
    {
        _deck.Volume = value; // applies live, no resync needed
        if (!_suppressApply) _persistSettings();
    }
}
