using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Audio.Beat;
using JockeyBox.Audio.Graph;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Separation;

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
public partial class DeckViewModel : ObservableObject, IDisposable
{
    private readonly DeckChannel _channel;
    private readonly DualDeckTransport _transport;
    private readonly Action _persistSettings;
    private readonly Func<string?, double> _getDeviceDelay; // lookup per-device offset
    private readonly Action<string?, double> _setDeviceDelay;
    /// <summary>The route field this deck's output maps to (Main for A, Karaoke aid for B).</summary>
    private readonly Func<string?> _getRouteDeviceId;
    private readonly DispatcherTimer _resyncDebounce;
    private bool _suppressApply;   // true while restoring saved settings
    private bool _isUserSeeking;   // view is dragging a seek bar
    private int _waveformGeneration;
    private string? _residentKaraokePath; // the instrumental file the channel holds
    private StemKind? _pendingStem;      // a stem pinned before the deck finished loading
    private bool _startWhenLoaded;       // a play asked for before the channel began loading
    private bool _loadRequested;         // a load asked for, not yet settled by the channel

    /// <summary>
    /// A load has been asked for and the channel has not settled yet. The karaoke
    /// pair's release guard asks this: a UI tick that lands between the two loads
    /// must not declare the pair over before either deck has been announced, which
    /// is what used to reset the pair's titles and drop the lock mid-load.
    /// </summary>
    internal bool HasTrackInFlight =>
        _loadRequested && !_channel.IsLoaded && _channel.Error == null;

    [ObservableProperty] private string _trackTitle = "No track loaded";
    [ObservableProperty] private string _trackDetail = "";
    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;
    [ObservableProperty] private double _delayMs;

    /// <summary>
    /// This deck's output delay in ms: how far its audio lags the deck, applied by
    /// the engine after the read. Separate from <see cref="DelayMs"/>, which trims
    /// the karaoke stem inside this deck.
    /// </summary>
    [ObservableProperty] private double _outputDelayMs;
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
                          Func<string?, double> getDeviceDelay, Action<string?, double> setDeviceDelay,
                          Func<string?> getRouteDeviceId)
    {        Title = title;
        _baseTitle = title;
        DeckTarget = deckTarget;
        _channel = channel;
        _transport = transport;
        _persistSettings = persistSettings;
        _getDeviceDelay = getDeviceDelay;
        _setDeviceDelay = setDeviceDelay;
        _getRouteDeviceId = getRouteDeviceId;

        // The channel loads the track on a worker, so the flags sampled in
        // SetTrack are stale by construction. Subscribe once, here, and let the
        // channel say when the deck has actually reached a loaded state: a
        // per-load subscribe would double up on every re-load, and this channel
        // outlives every track that passes through the deck.
        _channel.StateChanged += OnChannelStateChanged;

        // Debounce resync/persist so held +/- buttons or rapid edits produce a
        // single clean restart instead of a restart per step.
        _resyncDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _resyncDebounce.Tick += (_, _) =>
        {
            _resyncDebounce.Stop();
            _transport.ApplyDeckSettingsChange(DeckTarget);
            _persistSettings();
        };

        // The pad bank reads the media clock directly: a cue captured from the
        // audible clock would land late by the output delay.
        Cues = new CueBankViewModel(
            MainViewModel.CueDeckOf(deckTarget),
            () => _channel.MediaPositionSeconds,
            seconds => _channel.Seek(TimeSpan.FromSeconds(seconds)),
            () => MainViewModel.HotCueStore);
    }

    /// <summary>Eight saved pads plus the quick cue, bound in the card's pad bank.</summary>
    public CueBankViewModel Cues { get; }

    /// <summary>Raw media clock of this deck's channel (the pair bank captures from it).</summary>
    internal double MediaSeconds => _channel.MediaPositionSeconds;

    /// <summary>
    /// Seek this deck's own channel. Used by the pair bank, which must move both
    /// channels without going through the transport's sync mirror.
    /// </summary>
    internal void SeekMedia(double seconds) => _channel.Seek(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Diagnostic for the deck's asynchronous load, on when
    /// <c>JOCKEYBOX_DECK_TRACE</c> is set: the state sampled at load time next to
    /// the state the channel reports when it is actually ready. stderr, so a
    /// capture's stdout parsing is unaffected.
    /// </summary>
    internal static void DeckTrace(string message)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOCKEYBOX_DECK_TRACE"))) return;
        Console.Error.WriteLine($"decktrace: {message}");
        Console.Error.Flush();
    }

    /// <summary>
    /// The takes this track has on disk, as a submenu: only what
    /// <c>KaraokeNaming.DiscoverVariants</c> reports, built when the track loads.
    /// Switching one calls <c>SetStemMode</c>, which crossfades over readers that
    /// are already resident, so the change is seamless.
    /// </summary>
    public ObservableCollection<DeckVersion> Versions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLabel))]
    private DeckVersion? _selectedVersion;

    /// <summary>The version menu's own label: what the deck plays right now.</summary>
    public string VersionLabel => SelectedVersion?.Label ?? "VERSION";

    /// <summary>False when the track has nothing to switch to, so the menu cannot open.</summary>
    public bool CanSwitchVersion => Versions.Count(v => v.IsAvailable) > 1;

    /// <summary>Why the version menu is not usable, shown as its tooltip.</summary>
    public string VersionHint => CanSwitchVersion
        ? "Which take this deck plays"
        : "This track has only one version on disk";

    [RelayCommand]
    private async Task SelectVersionAsync(DeckVersion? version)
    {
        if (version is not { IsAvailable: true }) return;

        // The channel holds one instrumental reader. Choosing the other render
        // means re-opening the pair around that file first; the attach carries
        // the playhead over, so the switch stays a crossfade in time instead of
        // a reload that would jump the deck back to 0:00.
        if (version.Kind is StemKind.Karaoke && version.Path is { } path
            && !string.Equals(path, _residentKaraokePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!await _channel.AttachKaraokeStemAsync(path)) return;
            _residentKaraokePath = path;
        }

        // 25 ms: short enough to feel instant, long enough that the switch is a
        // crossfade over the resident readers instead of a click.
        _channel.SetStemMode(version.Kind, 25);

        foreach (var option in Versions) option.IsSelected = ReferenceEquals(option, version);
        SelectedVersion = version;
    }

    /// <summary>
    /// Build the version submenu from what is on disk, in the stable order
    /// Original, Karaoke, Keep backing, Vocals. Availability is never inferred
    /// from the partner that happened to load: a track with both instrumental
    /// renders offers both, and the deck attaches the one it does not hold when
    /// the operator picks it.
    /// </summary>
    private void SetVersions(string? musicPath)
    {
        Versions.Clear();
        SelectedVersion = null;

        foreach (var option in BuildVersions(musicPath))
            Versions.Add(option);

        var current = Versions.FirstOrDefault(v => v.IsAvailable);
        if (current != null) _ = SelectVersionAsync(current);
        else Versions.Clear();

        foreach (var option in Versions) option.SelectCommand = SelectVersionCommand;

        OnPropertyChanged(nameof(CanSwitchVersion));
        OnPropertyChanged(nameof(VersionHint));
    }

    /// <summary>
    /// The menu a track's files support. Split out from the deck so the mapping
    /// (file on disk to menu entry) can be checked without a window or a channel.
    /// A track with only an original yields that one entry, unavailable, which the
    /// menu shows disabled with its reason rather than as an empty popup.
    ///
    /// <para>Every entry owns its own file, resolved through
    /// <see cref="KaraokeNaming.VariantPathFor"/>. Validating the entries against
    /// one shared partner path is what used to hide the second instrumental: with
    /// both renders on disk only one of them could ever match, so the other was
    /// dropped and the menu collapsed to a single entry.</para>
    /// </summary>
    internal static List<DeckVersion> BuildVersions(string? musicPath)
    {
        var options = new List<DeckVersion>();
        if (string.IsNullOrWhiteSpace(musicPath)) return options;

        foreach (var variant in KaraokeNaming.DiscoverVariants(musicPath))
        {
            var path = KaraokeNaming.VariantPathFor(musicPath, variant);
            if (path == null) continue;

            var (label, kind) = variant switch
            {
                AudioVariant.Karaoke => ("Karaoke", StemKind.Karaoke),
                AudioVariant.Backing => ("Keep backing", StemKind.Karaoke),
                AudioVariant.Vocals => ("Vocals", StemKind.Vocals),
                _ => ("Original", StemKind.Original),
            };
            options.Add(new DeckVersion(label, kind, true, null, variant, path));
        }

        if (options.Count == 0)
            options.Add(new DeckVersion("Original", StemKind.Original, false,
                                        "No version of this track was found on disk",
                                        AudioVariant.Original, null));

        return options;
    }

    /// <summary>
    /// Card title. Normally the ctor's name ("DECK A"); a karaoke pair renames the
    /// deck for as long as the pair holds (see <see cref="SetKaraokeTitle"/>).
    /// </summary>
    [ObservableProperty] private string _title = "";

    private readonly string _baseTitle;

    public PlaybackDeck DeckTarget { get; }

    /// <summary>
    /// This deck's identity in the header: "Deck A" / "Deck B". The take that
    /// follows it is the caller's, so the label is prefix + this deck's own name,
    /// never a hardcoded "Karaoke".
    /// </summary>
    public string DeckPrefix => DeckTarget == PlaybackDeck.B ? "Deck B" : "Deck A";

    /// <summary>
    /// Rename this deck for a karaoke pair: identity first, the take's own name
    /// after it. Idempotent, and a caller that passes its own take name keeps it.
    /// </summary>
    public void SetKaraokeTitle(string takeTitle) => Title = $"{DeckPrefix}: {takeTitle}";

    /// <summary>Back to the ctor's name. Idempotent, so a double release is harmless.</summary>
    public void RestoreTitle() => Title = _baseTitle;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    /// <summary>Generation counter: lets a late waveform render notice it is stale.</summary>
    public int WaveformGeneration => _waveformGeneration;

    /// <summary>Restore persisted device/delay/volume without triggering resyncs.</summary>
    public void RestoreSettings(string? deviceId, double delayMs, float volume, double outputDelayMs = 0)
    {
        _suppressApply = true;
        try
        {
            _channel.DeviceId = deviceId;
            DelayMs = delayMs;
            Volume = volume;
            OutputDelayMs = outputDelayMs;
        }
        finally { _suppressApply = false; }
    }

    /// <summary>
    /// Repopulate the device dropdown from the route field this deck maps to, so a
    /// deck selector and the matching Settings box are one list and one value.
    /// </summary>
    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        var targetId = _getRouteDeviceId();
        _suppressApply = true;
        try
        {
            Devices.Clear();
            foreach (var choice in OutputDeviceChoices.Build(devices, targetId))
                Devices.Add(choice);
            SelectedDevice = OutputDeviceChoices.Select(Devices, targetId);
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
            _loadRequested = false;
            _transport.LoadOnDeck(DeckTarget, null, null);
            TrackTitle = "No track loaded";
            TrackDetail = "";
            IsLoaded = false;
            HasKaraoke = false;
            IsPlaying = false;
            PlayGlyph = "\u25B6";
            SetVersions(null);
            Cues.SetTrack(null);
            return;
        }

        // The scan pairs ".karaoke"; a track whose only render is the keep-backing
        // one would otherwise reach the deck with no partner at all, so the deck
        // fills the partner in from what is actually on disk. The vocals take is
        // resident as the third stem, so the version menu can reach it without
        // re-pointing a reader.
        karaokePath ??= VariantPath(musicPath, AudioVariant.Karaoke)
                        ?? VariantPath(musicPath, AudioVariant.Backing);
        string? vocalsPath = VariantPath(musicPath, AudioVariant.Vocals);

        _residentKaraokePath = karaokePath;
        _loadRequested = true;
        _transport.LoadOnDeck(DeckTarget, musicPath, karaokePath, vocalsPath);

        TrackTitle = title;
        TrackDetail = string.IsNullOrWhiteSpace(artist)
            ? System.IO.Path.GetFileName(musicPath)
            : artist;
        IsLoaded = _channel.IsLoaded;
        HasKaraoke = _channel.HasKaraoke;
        DeckTrace($"SetTrack sample deck={DeckTarget} track={System.IO.Path.GetFileName(musicPath)} " +
                  $"karaoke={System.IO.Path.GetFileName(karaokePath ?? "")} karaokeOnDisk={System.IO.File.Exists(karaokePath ?? "")} " +
                  $"IsLoaded={IsLoaded} HasKaraoke={HasKaraoke} duration={_channel.Duration.TotalSeconds:0.000}s");

        SetVersions(musicPath);
        Cues.SetTrack(musicPath);
    }

    /// <summary>The file a variant lives in beside the source, or null when none does.</summary>
    /// <summary>
    /// Absolute path of one variant of a track, or null when it is not on disk.
    /// Thin alias over <see cref="KaraokeNaming.VariantPathFor"/> so the deck, the
    /// track model and the menus all resolve a variant through one rule.
    /// </summary>
    internal static string? VariantPath(string? primaryPath, AudioVariant variant) =>
        KaraokeNaming.VariantPathFor(primaryPath, variant);

    /// <summary>
    /// Pin this deck to one stem now, or as soon as its load lands. A deck load
    /// discovers the sibling render beside its file as a partner and then plays
    /// the default Mix, which sums both: a caller that loaded this deck for one
    /// file (the karaoke pair's original deck) says so here.
    /// </summary>
    internal void HoldStem(StemKind kind, string? takePath = null,
                           AudioVariant variant = AudioVariant.Original)
    {
        _pendingStem = kind;
        if (_channel.IsLoaded) ApplyPendingStem();
        SelectHeldVersion(kind, takePath, variant);
    }

    /// <summary>
    /// Point the version menu at the stem the pin just chose, so the label reads
    /// what the deck is playing: a karaoke pair pins deck A to its take, and the
    /// menu must not keep saying "Original". A kind with no entry on disk leaves
    /// the menu alone.
    /// </summary>
    private void SelectHeldVersion(StemKind kind, string? takePath, AudioVariant variant)
    {
        // The variant first: Karaoke and Keep backing are two files on one reader,
        // so matching the kind alone would label a backing take "Karaoke".
        var version = Versions.FirstOrDefault(v => v.IsAvailable && v.Variant == variant)
                      ?? Versions.FirstOrDefault(v => v.IsAvailable && v.Kind == kind);

        // The menu is discovered from the naming rule beside the primary file, and
        // a take the scan paired can live outside it (a karaoke-root copy). The pin
        // still names the take it is playing, so that take is added rather than
        // letting the label fall back to whatever the menu happened to find.
        if (version == null && takePath is { Length: > 0 })
        {
            version = new DeckVersion(KaraokeNaming.DisplayName(variant), kind, true,
                                      null, variant, takePath)
            {
                SelectCommand = SelectVersionCommand,
            };
            Versions.Add(version);
        }

        if (version == null) return;

        foreach (var option in Versions) option.IsSelected = ReferenceEquals(option, version);
        SelectedVersion = version;
    }

    private void ApplyPendingStem()
    {
        if (_pendingStem is not { } kind) return;
        _pendingStem = null;
        _channel.SetStemMode(kind, 0);
    }

    /// <summary>
    /// Start this deck as soon as its load lands. <see cref="Audio.DeckChannel.Play"/>
    /// is dropped outright when the channel is neither loaded nor loading yet, and
    /// that is exactly the window a caller that loads and plays in one gesture
    /// sits in, so the start is armed on the loaded transition here instead. One
    /// signal, no polling, and a deck that is already loaded starts at once.
    /// </summary>
    internal void StartWhenLoaded()
    {
        if (!_channel.IsLoaded)
        {
            _startWhenLoaded = true;
            return;
        }

        _channel.Play();
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

    /// <summary>
    /// The channel says what the deck now holds. <see cref="SetTrack"/> samples
    /// <see cref="IsLoaded"/> and <see cref="HasKaraoke"/> the instant it asks for
    /// the load, but the channel opens the readers on a worker, so on a real
    /// load both read false and the KARAOKE row never appears. This is the other
    /// half of that load: by the time the channel announces, the flags are true,
    /// and the card re-reads them instead of keeping the sample it took at 0 ms.
    /// </summary>
    private void OnChannelStateChanged(object? sender, EventArgs e)
    {
        bool loaded = _channel.IsLoaded;
        bool karaoke = _channel.HasKaraoke;
        DeckTrace($"StateChanged deck={DeckTarget} state={_channel.State} IsLoaded={loaded} " +
                  $"HasKaraoke={karaoke} duration={_channel.Duration.TotalSeconds:0.000}s");

        // A stem pinned before the load was in flight only takes effect now.
        if (loaded) ApplyPendingStem();

        // The deck has settled (loaded or failed), so the load is no longer in
        // flight for the pair guard.
        if (loaded || _channel.Error != null) _loadRequested = false;

        // A start that was asked for before the channel could accept it.
        if (loaded && _startWhenLoaded)
        {
            _startWhenLoaded = false;
            _channel.Play();
        }

        if (IsLoaded == loaded && HasKaraoke == karaoke) return;

        IsLoaded = loaded;
        HasKaraoke = karaoke;
        // The readout's own gate is IsLoaded: without this it keeps the
        // "0:00 / 0:00" it printed while the deck was still opening.
        UpdateReadout();
    }

    /// <summary>Detach from the channel this deck listens to.</summary>
    public void Dispose()
    {
        _resyncDebounce.Stop();
        _channel.StateChanged -= OnChannelStateChanged;
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

    /// <summary>Nudge this deck's output delay from the +/- repeat buttons (step in ms).</summary>
    [RelayCommand]
    private void NudgeOutputDelay(string step)
    {
        if (double.TryParse(step, out var delta))
            OutputDelayMs = Math.Clamp(OutputDelayMs + delta, 0, DeckChannel.MaxOutputDelayMs);
    }

    partial void OnOutputDelayMsChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, DeckChannel.MaxOutputDelayMs);
        if (clamped != value)
        {
            OutputDelayMs = clamped;
            return;
        }
        _channel.OutputDelayMs = clamped;
        if (_suppressApply) return;
        _persistSettings();
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (value == null) return;
        var deviceId = string.IsNullOrWhiteSpace(value.Id) ? null : value.Id;
        _channel.DeviceId = deviceId;
        if (_suppressApply) return;

        if (deviceId != null)
        {
            var deviceDelay = _getDeviceDelay(deviceId);
            if (deviceDelay != 0)
                DelayMs = deviceDelay;
        }

        // Persist first: the transport re-reads the settings file and only then
        // builds the route, so the route field has to be on disk before it looks.
        _persistSettings();
        _transport.ApplyDeckSettingsChange(DeckTarget);
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

/// <summary>
/// One entry in a deck's version submenu. <see cref="MenuText"/> carries the tick
/// because the app's MenuItem template draws only a header, never a check box, so
/// the selected take has to say so in its own text.
/// </summary>
public partial class DeckVersion : ObservableObject
{
    public DeckVersion(string label, StemKind kind, bool isAvailable, string? reason = null,
                       AudioVariant variant = AudioVariant.Original, string? path = null)
    {
        Label = label;
        Kind = kind;
        IsAvailable = isAvailable;
        Reason = reason ?? label;
        Variant = variant;
        Path = path;
    }

    public string Label { get; }

    /// <summary>Which resident stem this entry crossfades to.</summary>
    public StemKind Kind { get; }

    /// <summary>The take this entry is, so menus and rows can key an icon on it.</summary>
    public AudioVariant Variant { get; }

    /// <summary>The file this entry plays; null only on the unavailable placeholder.</summary>
    public string? Path { get; }

    public bool IsAvailable { get; }

    /// <summary>What the entry means, for the tooltip.</summary>
    public string Reason { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MenuText))]
    private bool _isSelected;

    /// <summary>
    /// The deck's select command, carried on the entry itself: a menu popup sits
    /// outside the card's visual tree, so a RelativeSource binding to the deck
    /// would not resolve from inside it.
    /// </summary>
    public IRelayCommand? SelectCommand { get; set; }

    /// <summary>The header text, with a trailing tick on the selected entry.</summary>
    public string MenuText => IsSelected ? Label + "   \u2713" : Label;
}

/// <summary>
/// The one list of output choices both routing surfaces show: the deck combos and
/// the Settings routing boxes. "None" first, then the live endpoints, then - only
/// when the stored id is not in the live list - a "Saved device (not connected)"
/// row, so saving either surface cannot quietly change a route to None just
/// because the endpoint is unplugged.
/// </summary>
public static class OutputDeviceChoices
{
    public const string NoneName = "None";
    public const string NotConnectedName = "Saved device (not connected)";

    /// <summary>None + the live endpoints + the saved id when it is not live.</summary>
    public static List<AudioDeviceInfo> Build(IEnumerable<AudioDeviceInfo> devices, string? savedId)
    {
        var rows = new List<AudioDeviceInfo> { new("", NoneName) };
        rows.AddRange(devices);

        if (!string.IsNullOrWhiteSpace(savedId) &&
            !rows.Any(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase)))
            rows.Add(new AudioDeviceInfo(savedId!, NotConnectedName));

        return rows;
    }

    /// <summary>
    /// The row for <paramref name="savedId"/>: the matching endpoint, the saved
    /// row, or None. A null/blank id is None - never the first live device, which
    /// would show a route the bus is not using.
    /// </summary>
    public static AudioDeviceInfo Select(IReadOnlyList<AudioDeviceInfo> rows, string? savedId) =>
        (string.IsNullOrWhiteSpace(savedId)
            ? null
            : rows.FirstOrDefault(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase)))
        ?? rows[0];
}
