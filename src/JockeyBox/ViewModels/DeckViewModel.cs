using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Playback;

namespace JockeyBox.ViewModels;

/// <summary>
/// Presentation model for one DJ deck card: track info, device routing, deck
/// fader, time readout and its own playhead/seek bar, plus - when the loaded
/// track has a karaoke partner - the karaoke sub-bar (play/pause, seek, gain
/// and delay offset).
///
/// Each deck follows its own <see cref="DeckChannel"/> playhead; the karaoke
/// sub-bar seeks the same deck position, so both stems stay locked together
/// while the two decks themselves run independently.
/// </summary>
public partial class DeckViewModel : ObservableObject
{
    private readonly DeckChannel _channel;
    private readonly DualDeckTransport _transport;
    private readonly Action _persistSettings;
    private readonly Func<string?, double> _getDeviceDelay; // lookup per-device offset
    private readonly Action<string?, double> _setDeviceDelay;
    private readonly DispatcherTimer _resyncDebounce;
    private bool _suppressApply;   // true while restoring saved settings
    private bool _isUserSeeking;   // view is dragging a seek bar
    private int _waveformGeneration;

    [ObservableProperty] private string _trackTitle = "No track loaded";
    [ObservableProperty] private string _trackDetail = "";
    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private double _delayMs;
    [ObservableProperty] private float _volume = 1.0f;
    [ObservableProperty] private double _karaokeGain = 0.5;
    [ObservableProperty] private string _timeReadout = "0:00 / 0:00";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _hasKaraoke;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _playGlyph = "\u25B6";
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _waveformProgress;
    [ObservableProperty] private List<float> _waveform = new();
    [ObservableProperty] private List<float> _karaokeWaveform = new();

    public DeckViewModel(string title, PlaybackDeck deckTarget, DeckChannel channel,
                          DualDeckTransport transport,
                          Action persistSettings,
                          Func<string?, double> getDeviceDelay, Action<string?, double> setDeviceDelay)
    {
        Title = title;
        DeckTarget = deckTarget;
        _channel = channel;
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
            _transport.ApplyDeckSettingsChange(DeckTarget);
            _persistSettings();
        };
    }

    public string Title { get; }
    public PlaybackDeck DeckTarget { get; }
    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    /// <summary>Generation counter: lets a late waveform render notice it is stale.</summary>
    public int WaveformGeneration => _waveformGeneration;

    /// <summary>Restore persisted device/delay/volume without triggering resyncs.</summary>
    public void RestoreSettings(string? deviceId, double delayMs, float volume)
    {
        _suppressApply = true;
        try
        {
            _channel.DeviceId = deviceId;
            DelayMs = delayMs;
            Volume = volume;
        }
        finally { _suppressApply = false; }
    }

    /// <summary>Repopulate the device dropdown, keeping the current selection.</summary>
    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        var targetId = _channel.DeviceId;
        _suppressApply = true;
        try
        {
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == targetId) ?? Devices.FirstOrDefault();
        }
        finally { _suppressApply = false; }
    }

    /// <summary>
    /// Load a whole track onto this deck: the original file as the primary stem
    /// and, when the track has one, the karaoke version as the sub-stem.
    /// </summary>
    public void SetTrack(string? musicPath, string? karaokePath, string title = "", string artist = "")
    {
        _waveformGeneration++;
        Waveform = new List<float>();
        KaraokeWaveform = new List<float>();
        WaveformProgress = 0;

        if (string.IsNullOrWhiteSpace(musicPath))
        {
            _transport.LoadOnDeck(DeckTarget, null, null);
            TrackTitle = "No track loaded";
            TrackDetail = "";
            IsLoaded = false;
            HasKaraoke = false;
            IsPlaying = false;
            PlayGlyph = "\u25B6";
            return;
        }

        _transport.LoadOnDeck(DeckTarget, musicPath, karaokePath);

        TrackTitle = title;
        TrackDetail = string.IsNullOrWhiteSpace(artist)
            ? System.IO.Path.GetFileName(musicPath)
            : artist;
        IsLoaded = _channel.IsLoaded;
        HasKaraoke = _channel.HasKaraoke;
    }

    /// <summary>Apply generated waveforms unless a newer track was loaded meanwhile.</summary>
    public void SetWaveforms(int generation, List<float>? primary, List<float>? karaoke)
    {
        if (generation != _waveformGeneration) return;
        Waveform = primary ?? new List<float>();
        KaraokeWaveform = karaoke ?? new List<float>();
    }

    /// <summary>Refresh readout, seek position and waveform progress from this deck's own playhead.</summary>
    public void UpdateReadout()
    {
        if (!IsLoaded)
        {
            TimeReadout = "0:00 / 0:00";
            IsPlaying = false;
            PlayGlyph = "\u25B6";
            DurationSeconds = 0;
            if (!_isUserSeeking) PositionSeconds = 0;
            WaveformProgress = 0;
            return;
        }

        var pos = _channel.DisplayPosition;
        var duration = _channel.Duration;
        var playing = _channel.State == TransportState.Playing;

        IsPlaying = playing;
        PlayGlyph = playing ? "\u23F8" : "\u25B6";
        TimeReadout = $"{Format(pos)} / {Format(duration)}";
        DurationSeconds = duration.TotalSeconds;

        if (!_isUserSeeking)
            PositionSeconds = pos.TotalSeconds;

        WaveformProgress = duration.TotalSeconds > 0
            ? Math.Clamp(pos.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;
    }

    /// <summary>Called by the view when a deck seek drag starts.</summary>
    public void BeginSeek() => _isUserSeeking = true;

    /// <summary>Called by the view when the drag ends; commits one seek on this deck.</summary>
    public void EndSeek(double seconds)
    {
        if (!_isUserSeeking) return;
        _isUserSeeking = false;
        if (!IsLoaded) return;
        _transport.SeekDeck(DeckTarget, TimeSpan.FromSeconds(seconds));
    }

    [RelayCommand]
    private void PlayPause() => _transport.TogglePlayPause(DeckTarget);

    /// <summary>Make this deck the one the shared transport surface follows.</summary>
    [RelayCommand]
    private void Activate() => _transport.ActivateDeck(DeckTarget);

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Nudge the karaoke delay from the +/- repeat buttons (step in ms).</summary>
    [RelayCommand]
    private void NudgeDelay(string step)
    {
        if (double.TryParse(step, out var delta))
            DelayMs = Math.Clamp(DelayMs + delta, DeckChannel.MinDelayMs, DeckChannel.MaxDelayMs);
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (value == null) return;
        _channel.DeviceId = value.Id;
        if (_suppressApply) return;

        var deviceDelay = _getDeviceDelay(value.Id);
        if (deviceDelay != 0)
            DelayMs = deviceDelay;

        _transport.ApplyDeckSettingsChange(DeckTarget);
        _persistSettings();
    }

    partial void OnDelayMsChanged(double value)
    {
        var clamped = Math.Clamp(value, DeckChannel.MinDelayMs, DeckChannel.MaxDelayMs);
        if (clamped != value)
        {
            DelayMs = clamped;
            return;
        }
        _channel.DelayMs = clamped;
        if (_suppressApply) return;

        // Persist per-device delay
        if (_channel.DeviceId != null)
            _setDeviceDelay(_channel.DeviceId, clamped);

        _resyncDebounce.Stop();
        _resyncDebounce.Start();
    }

    partial void OnVolumeChanged(float value)
    {
        _channel.Volume = value; // applies live, no resync needed
        if (!_suppressApply) _persistSettings();
    }

    partial void OnKaraokeGainChanged(double value)
    {
        _channel.KaraokeGain = value; // applies live, no resync needed
    }
}
