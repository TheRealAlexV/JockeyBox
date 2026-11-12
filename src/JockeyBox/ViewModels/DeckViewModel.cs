using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Audio.Beat;
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

    // --- 2.0 tempo / BPM (plan item 5) -------------------------------------
    /// <summary>Tempo slider position in percent, negative = slower. Centre (0) = original.</summary>
    [ObservableProperty] private double _tempoPercent;
    /// <summary>Effective playback ratio shown next to the slider (1.000 = untouched).</summary>
    [ObservableProperty] private double _tempoRatio = 1.0;
    /// <summary>Analysed BPM of the loaded track, or 0 when unknown.</summary>
    [ObservableProperty] private double _bpm;
    /// <summary>BPM the deck is actually producing (BPM x ratio); this is the number a DJ mixes with.</summary>
    [ObservableProperty] private double _effectiveBpm;
    /// <summary>"128.0 BPM" / "BPM —" readout text.</summary>
    [ObservableProperty] private string _bpmText = "BPM \u2014";
    /// <summary>True when the tempo slider is away from centre (lights the reset button).</summary>
    [ObservableProperty] private bool _isTempoModified;
    /// <summary>Beat positions across the track as 0..1 fractions, for the waveform's beat ticks.</summary>
    [ObservableProperty] private List<double> _beatMarkers = new();

    public DeckViewModel(string title, PlaybackDeck deckTarget, DeckChannel channel,
                          DualDeckTransport transport,
                          Action persistSettings,
                          Func<string?, double> getDeviceDelay, Action<string?, double> setDeviceDelay)
    {        Title = title;
        _baseTitle = title;
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

    /// <summary>
    /// Card title. Normally the ctor's name ("DECK A"); a karaoke pair renames the
    /// deck for as long as the pair holds (see <see cref="SetKaraokeTitle"/>).
    /// </summary>
    [ObservableProperty] private string _title = "";

    private readonly string _baseTitle;

    public PlaybackDeck DeckTarget { get; }

    /// <summary>Rename this deck for a karaoke pair. Idempotent.</summary>
    public void SetKaraokeTitle(string karaokeTitle) => Title = karaokeTitle;

    /// <summary>Back to the ctor's name. Idempotent, so a double release is harmless.</summary>
    public void RestoreTitle() => Title = _baseTitle;

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

        // The previous track's grid (and its beat ticks) must not survive onto
        // this one; the caller attaches the right grid right after loading.
        SetBeatGrid(null);

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
        if (Math.Abs(DurationSeconds - duration.TotalSeconds) > 0.05)
        {
            DurationSeconds = duration.TotalSeconds;
            RebuildBeatMarkers(); // beat ticks are relative to the track length
        }

        if (!_isUserSeeking)
            PositionSeconds = pos.TotalSeconds;

        WaveformProgress = duration.TotalSeconds > 0
            ? Math.Clamp(pos.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;
    }

    /// <summary>Path of the track loaded on this deck, or null when empty.</summary>
    public string? TrackPath => _channel.PrimaryPath;

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

    [RelayCommand]
    private void SkipBack() => _transport.SkipBackDeck(DeckTarget);

    [RelayCommand]
    private void SkipForward() => _transport.SkipForwardDeck(DeckTarget);

    [RelayCommand]
    private void Stop() => _transport.StopDeck(DeckTarget);

    /// <summary>Park this deck at the start of its track (not a track change).</summary>
    [RelayCommand]
    private void Restart() => _transport.SeekDeck(DeckTarget, TimeSpan.Zero);

    /// <summary>Make this deck the one the shared transport surface follows.</summary>
    [RelayCommand]
    private void Activate() => _transport.ActivateDeck(DeckTarget);

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Half-range of the tempo slider in percent (settings: ManualTempoRangePercent).</summary>
    public double TempoRangePercent { get; private set; } = 16.0;

    /// <summary>Set the slider range; re-clamps the current value.</summary>
    public void SetTempoRange(double percent)
    {
        TempoRangePercent = Math.Clamp(percent, 1, 50);
        if (Math.Abs(TempoPercent) > TempoRangePercent) TempoPercent = TempoRangePercent;
    }

    /// <summary>Beat grid of the loaded track, for beat markers and the mix automation.</summary>
    public BeatGrid? BeatGrid { get; private set; }

    /// <summary>Set (or clear) the analysed beat grid; rebuilds the waveform beat ticks.</summary>
    public void SetBeatGrid(BeatGrid? grid)
    {
        BeatGrid = grid;
        Bpm = grid?.Bpm ?? 0;
        UpdateBpmText();
        RebuildBeatMarkers();
    }

    /// <summary>Return the tempo to centre (the auto-centre button).</summary>
    [RelayCommand]
    private void CenterTempo() => TempoPercent = 0;

    [RelayCommand]
    private void NudgeTempo(string step)
    {
        if (double.TryParse(step, out var delta))
            TempoPercent = Math.Clamp(TempoPercent + delta, -TempoRangePercent, TempoRangePercent);
    }

    private void UpdateBpmText()
    {
        EffectiveBpm = Bpm > 0 ? Bpm * TempoRatio : 0;
        BpmText = Bpm <= 0
            ? "BPM \u2014"
            : $"{EffectiveBpm:0.0} BPM";
    }

    private void RebuildBeatMarkers() =>
        BeatMarkers = JockeyBox.Audio.Beat.BeatMarkers.Build(BeatGrid, DurationSeconds);

    partial void OnTempoPercentChanged(double value)
    {
        double range = Math.Max(1, TempoRangePercent);
        double clamped = Math.Clamp(value, -range, range);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            TempoPercent = clamped;
            return;
        }

        TempoRatio = 1.0 + clamped / 100.0;
        IsTempoModified = Math.Abs(clamped) > 0.01;
        _channel.TempoRatio = TempoRatio;
        UpdateBpmText();
    }

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
