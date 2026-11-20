using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Audio.Beat;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using JockeyBox.Playback;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Selftest;
using JockeyBox.Sources;
using JockeyBox.Tools;
using JockeyBox.Views;

namespace JockeyBox.ViewModels;

/// <summary>
/// Root view model: owns the audio engine, library, lyrics pipeline and the
/// unified transport state that the whole window binds to.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly DeviceService _deviceService = new();
    private readonly DualDeckTransport _transport;
    private readonly DeckPreviewTransport _previewTransport;
    private readonly TagCache _tagCache = new();
    private readonly LibraryScanner _scanner;
    private readonly LibraryWatcher _watcher = new();
    private readonly LyricsService _lyricsService;
    private readonly SeparationService _separation;
    private readonly DispatcherTimer _uiTimer;

    // Source downloads: Spotify/YouTube imports, the shared queue, and the
    // cache that makes remote tracks playable through the deck engine.
    private readonly PlaylistStore _playlistStore;
    private readonly LibraryLedger _ledger;
    private readonly LibraryIndex _libraryIndex;
    private Dictionary<string, SourceTrack>? _sourceTracksById;
    private readonly StreamCache _streamCache;
    private readonly DownloadQueue _downloadQueue;
    private readonly StreamResolver _streamResolver;
    private readonly DispatcherTimer _rescanDebounce;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _playlistReloadDebounce;
    private readonly DispatcherTimer _autoSyncTimer;

    // Playback queue: what plays after this track, plus its on-disk mirror.
    private readonly PlaybackQueueStore _queueStore = new();
    private bool _restoringQueue;
    private bool _queueRestored;

    // Deck routing for the queue: the deck the loaded track went to, and the
    // deck handed to the last enqueued row (so a batch alternates A/B/A/B/A).
    private PlaybackDeck _activeDeck = PlaybackDeck.A;
    private PlaybackDeck? _lastAssignedDeck;
    private readonly HashSet<string> _pendingAutoKaraoke = new(StringComparer.OrdinalIgnoreCase);

    // A library rename/move reports its old/new path and key through the media
    // service's callbacks; these carry that pair into the persistence step.
    private string _mediaOpOldPath = "";
    private string _mediaOpNewPath = "";
    private string _mediaOpOldKey = "";

    private const string AutoLyricsSource = "Auto";

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _lyricsCts;
    private CancellationTokenSource? _lyricsCandidatesCts;
    private readonly DispatcherTimer _lyricsSettingsSave;

    /// <summary>
    /// The home <see cref="ScheduleLyricsSettingsSave"/> armed for, captured at
    /// schedule time. Null means no save is pending, which is also how a tick
    /// that arrives after teardown is recognised and dropped.
    /// </summary>
    private string? _lyricsSettingsHome;
    private bool _isUserSeeking;
    private bool _syncingCrossfader;
    private int _currentLyricIndex = -1;
    private bool _lyricsSynced;

    // Guards the two-way bindings of the lyrics header so programmatic syncs
    // (track load, picker changes) don't loop back into persistence.
    private bool _syncingLyricsOffset;
    private bool _syncingLyricsSource;
    private int _lyricsCandidatesGeneration;

    /// <summary>
    /// Query the current candidate list was built with (null = file tags). Applied
    /// to the track when a candidate is picked, so a cover keeps searching the
    /// original's tags after a restart.
    /// </summary>
    private LyricsService.LyricsQuery? _activeLyricsQuery;

    // ---- per-track lyric metadata ----------------------------------------
    // One store for the process, opened next to the settings and before the
    // first track can load. It owns the per-track offset, the pinned source
    // and the offset sections; AppSettings only keeps the global default and
    // is read (never written) for the legacy per-track values.
    private readonly TrackMetadataStore _trackMeta;

    /// <summary>The document behind <see cref="LyricLines"/>; null when none is loaded.</summary>
    private LyricsDocument? _lyricsDocument;

    /// <summary>
    /// The provider's document, before the operator's line edits are laid over it.
    /// Re-applying an edit starts from this one, so a second edit is not applied on
    /// top of the first. Null when no provider answered (the lines then come from
    /// the edits alone).
    /// </summary>
    private LyricsDocument? _lyricsBaseDocument;

    /// <summary>Why the last lookup missed, kept so a re-render after an edit says the same thing.</summary>
    private string? _lyricsMissStatus;

    /// <summary>True once a document (or an all-edits sheet) has been rendered for the loaded track.</summary>
    private bool _lyricsApplied;

    /// <summary>
    /// Sections of the loaded track, fetched once per track/metadata change:
    /// <c>GetSections</c> hands out a copy, and the 30 Hz tick must not copy.
    /// </summary>
    private IReadOnlyList<LyricOffsetSection> _trackSections = Array.Empty<LyricOffsetSection>();

    /// <summary>Section the header edits; -1 means the track/global value.</summary>
    private int _editSectionIndex = -1;

    /// <summary>Section the playhead was inside on the previous tick (change detection only).</summary>
    private int _playheadSectionIndex = -1;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        // The browser layout comes from settings; assign the generated backing
        // field so the change hook does not save settings during start-up.
        _layout = _settings.LibraryLayout;

        // Per-track lyric metadata lives here from now on: open the store before
        // any track can load, import the pre-store per-track settings exactly
        // once (the store keeps the fence, so this is safe on every launch) and
        // listen for writes made elsewhere. Opening is best-effort by design -
        // a corrupt/unwritable store must not stop the app from starting.
        _trackMeta = TrackMetadataStore.Open();
        _trackMeta.MigrateLegacyLyrics(_settings);
        _trackMeta.Changed += OnTrackMetadataChanged;
        // Restore the follow mode before the UI binds; writing the backing field
        // avoids the change hook saving settings during start-up.
        _isWordFollow = _settings.LyricsFollowMode == LyricsFollowMode.Word;

        var channelA = new DeckChannel("Deck A", _deviceService);
        var channelB = new DeckChannel("Deck B", _deviceService);
        _transport = new DualDeckTransport(channelA, channelB);
        _transport.StateChanged += (_, _) => OnTransportStateChanged();
        _transport.ActiveDeckChanged += (_, _) => SyncDeckActivation();

        DeckA = new DeckViewModel("DECK A", PlaybackDeck.A, channelA, _transport, PersistDeckSettings,
            id => id != null && _settings.DeviceDelays.TryGetValue(id, out var d) ? d : 0,
            (id, delay) => { if (id != null) _settings.DeviceDelays[id] = delay; });
        DeckB = new DeckViewModel("DECK B", PlaybackDeck.B, channelB, _transport, PersistDeckSettings,
            id => id != null && _settings.DeviceDelays.TryGetValue(id, out var d) ? d : 0,
            (id, delay) => { if (id != null) _settings.DeviceDelays[id] = delay; });
        DeckA.RestoreSettings(_settings.DeckADeviceId, _settings.DeckADelayMs, _settings.DeckAVolume,
            _settings.DeckAOutputDelayMs);
        DeckB.RestoreSettings(_settings.DeckBDeviceId, _settings.DeckBDelayMs, _settings.DeckBVolume,
            _settings.DeckBOutputDelayMs);
        SyncDeckActivation();
        // Endpoint enumeration deliberately does not happen here: it blocks on
        // MMDevAPI for seconds and would run on the UI thread during start-up.
        // MainWindow calls RefreshDevicesAsync once it is loaded.

        // 2.0: master volume, output delay, transition automation, the
        // soundboard, its hotkeys and the loopback API. Kept in
        // MainViewModel.Audio2.cs so this file stays about the 1.x surface.
        InitializeAudio2();

        _scanner = new LibraryScanner(_tagCache);
        _lyricsService = new LyricsService(_settings);
        _separation = new SeparationService(_settings,
            System.Windows.Application.Current.Dispatcher);
        _separation.StatusChanged += message => StatusText = message;

        // A finished render writes a file the library has never scanned: the
        // instrumental refreshes an existing row's KaraokePath, the vocals take
        // is a row of its own. BumpLibraryGeneration only invalidates cached
        // membership, it does not scan, so pair it with the same debounced
        // rescan a finished download uses - one per render, not one per row.
        _separation.Completed += (_, _) =>
        {
            BumpLibraryGeneration("karaoke render completed");
            ScheduleRescan();
        };

        // Lyrics header: the offset slider writes through to the metadata store
        // on every drag step (the store debounces its own save); the settings
        // debounce below is only for the global default.
        _lyricsSettingsSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _lyricsSettingsSave.Tick += (_, _) => SaveScheduledLyricsSettings();

        LyricsSourceOptions.Add(AutoLyricsSource);
        foreach (var name in _lyricsService.SourceNames)
            LyricsSourceOptions.Add(name);
        SyncLyricsOffsetEditor();

        TracksView = CollectionViewSource.GetDefaultView(Tracks);
        TracksView.Filter = FilterTrack;

        // A rescan replaces TrackEntry instances, but membership is a set of
        // library keys: only a change to the *key set* invalidates a cached
        // answer. A scan raises this once per added row, so the scan itself
        // suppresses the burst and decides once, at the end, whether the keys
        // really moved (see RescanAsync).
        Tracks.CollectionChanged += (_, _) =>
        {
            if (_suppressLibraryBump) return;
            BumpLibraryGeneration("library list changed");
        };

        // Typing filters the tree itself. Coalesce the rebuild so a burst of
        // keystrokes costs one tree build (and one collection view refresh).
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            TracksView.Refresh();
            RebuildBrowser();
        };

        _watcher.Changed += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = RescanAsync());

        MusicDirectory = _settings.MusicDirectory;
        KaraokeDirectory = _settings.KaraokeDirectory;

        // ---- source downloads (Spotify / YouTube) --------------------------
        _playlistStore = new PlaylistStore();
        _ledger = new LibraryLedger();
        _libraryIndex = new LibraryIndex();
        _streamCache = new StreamCache(_settings.StreamCacheLimitMb);
        _downloadQueue = new DownloadQueue(_settings, _ledger, _streamCache, _libraryIndex);
        _streamResolver = new StreamResolver(_downloadQueue, _streamCache, _ledger, _settings,
            _libraryIndex);

        // The store keeps only a remote id per row, so "is this on disk?" is
        // answered against the live download ledger plus the scanned library
        // instead of the store's read-only snapshot of ledger.json.
        // Build the id index *before* anything can ask a row "are you on disk?".
        // The first row's probe would otherwise pay for it (about 12 ms at
        // 11 000 rows) inside a load loop, where it reads as a stuck UI.
        EnsureSourceTrackIndex();
        _playlistStore.LibraryPathResolver = ResolveSourceLibraryPath;

        // Any write to playlists.json (this view model, Spotify, YouTube) rebuilds
        // the rail: the tabs create imported playlists too.
        _playlistStore.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _sourceTracksById = null;
            RefreshPlaylistRail();
            ApplyPlaylistFilter(_activePlaylist);
        });

        // One-time heal for ledgers written before provenance existed: names are
        // taken from queue.json and from imported playlists that still hold the
        // row, never guessed. Idempotent - it writes only when it names something.
        BackfillLedgerProvenance();
        RefreshPlaylistRail();

        // Cache invalidation is wired to the same signals the rescan uses: a
        // membership answer resolved against the old library must never survive.
        _downloadQueue.LibraryChanged += () => System.Windows.Application.Current?.Dispatcher
            .BeginInvoke(() =>
            {
                BumpLibraryGeneration("library changed");
                ScheduleRescan();
            });
        // A delete already raises LibraryChanged, which schedules the rescan;
        // what is missing is the source tabs, which hold their own row objects
        // and would otherwise keep their "in library" glyph.
        _downloadQueue.FileDeleted += _ =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                BumpLibraryGeneration("file deleted");
                RefreshSourceLibraryStates();
            });
        };
        _downloadQueue.ItemFinished += OnDownloadFinished;
        // A finished queue means the last file landed without another signal.
        _downloadQueue.Drained += () => System.Windows.Application.Current?.Dispatcher
            .BeginInvoke(() =>
            {
                BumpLibraryGeneration("downloads drained");
                ScheduleRescan();
            });

        // A batch of downloads should trigger one rescan, not one per track.
        _rescanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _rescanDebounce.Tick += (_, _) =>
        {
            _rescanDebounce.Stop();
            _ = RescanAsync();
        };

        // An open playlist is re-resolved after a library change, once per burst
        // (a scan bumps the generation once per added row).
        _playlistReloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _playlistReloadDebounce.Tick += (_, _) =>
        {
            _playlistReloadDebounce.Stop();
            // Counts the cache could not answer were dropped when the library
            // moved on; repaint them together with the open playlist.
            RefreshPlaylistRail();
            if (_activePlaylist is not null) ApplyPlaylistFilter(_activePlaylist);
        };

        // The tabs' preview strips drive the deck pair this window already owns;
        // the adapter reads the live transport and is ticked by OnUiTick.
        _previewTransport = new DeckPreviewTransport(_transport);

        // Remote tabs get the library's verbs (own playlists, karaoke, reveal)
        // through one host object built here; see MainViewModel.RemoteLists.cs.
        InitializeRemoteLists();

        Spotify = new SpotifyViewModel(_settings, _playlistStore, _downloadQueue, _ledger,
            new SpotifyOAuthService(_settings), s => PlaySourceTrackAsync(s),
            previewTransport: _previewTransport, addToQueue: AddSourceToQueue,
            remoteHost: _remoteHost);
        YouTube = new YouTubeViewModel(_settings, _playlistStore, _downloadQueue, _ledger,
            s => PlaySourceTrackAsync(s),
            previewTransport: _previewTransport, addToQueue: AddSourceToQueue,
            remoteHost: _remoteHost);
        Downloads = new DownloadsViewModel(_settings, _downloadQueue);

        // Re-derive the remote rows once now that the store and the library
        // index both exist: a file removed outside the app must not keep a
        // stale "in library" glyph on either tab.
        RefreshSourceLibraryStates();

        // ~30 Hz UI clock for seek bar, deck readouts and lyric highlighting.
        // Re-read imported playlists on the interval chosen in Settings.
        _autoSyncTimer = new DispatcherTimer();
        _autoSyncTimer.Tick += async (_, _) =>
        {
            _autoSyncTimer.Stop();
            try
            {
                if (_settings.YouTubeAutoSyncMinutes > 0 && !_downloadQueue.IsRunning)
                {
                    await Spotify.SyncPlaylistsCommand.ExecuteAsync(null);
                    await YouTube.SyncPlaylistsCommand.ExecuteAsync(null);
                }
            }
            catch (Exception ex)
            {
                Tools.Log.Error("Auto-sync failed", ex);
            }
            finally
            {
                ScheduleAutoSync();
            }
        };
        ScheduleAutoSync();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        if (!string.IsNullOrEmpty(MusicDirectory))
            _ = RescanAsync();
    }

    // ---- bindable state -------------------------------------------------

    public DeckViewModel DeckA { get; }
    public DeckViewModel DeckB { get; }

    public ObservableCollection<TrackEntry> Tracks { get; } = new();
    public ICollectionView TracksView { get; }
    public ObservableCollection<BrowserNode> BrowserRoots { get; } = new();
    public ObservableCollection<LyricLineViewModel> LyricLines { get; } = new();

    /// <summary>
    /// Crossfader position for the deck pair: 0 = deck A, 1 = deck B, 0.5 =
    /// equal-power centre. Programme changes (queue hand-off) ramp the transport
    /// fader; the 30 Hz tick mirrors it back here so the slider follows.
    /// </summary>
    [ObservableProperty] private double _crossfader = 0.5;

    /// <summary>
    /// Which deck the shared transport surface follows. This stays a plain pair
    /// (exactly one true) because the link still keeps a single master: the seek
    /// bar, position readout, lyric clock and deck sync all follow it even while
    /// commands fan out to both decks.
    /// </summary>
    public bool IsDeckAActive => _transport.ActiveDeck == PlaybackDeck.A;
    public bool IsDeckBActive => !IsDeckAActive;

    /// <summary>
    /// The one-of-three selection behind the A / B / A-B buttons: A only, B only
    /// or the link. Mutually exclusive by construction, so the row cannot show B
    /// selected and A-B ticked at the same time.
    /// </summary>
    public bool IsAOnlySelected => !_abMode && _transport.ActiveDeck == PlaybackDeck.A;
    public bool IsBOnlySelected => !_abMode && _transport.ActiveDeck == PlaybackDeck.B;

    /// <summary>Raised when the highlighted lyric changes (view scrolls to it).</summary>
    public event Action<LyricLineViewModel>? CurrentLyricChanged;

    /// <summary>
    /// Raised when the active word changes (or word-follow is switched on): the
    /// view must re-measure the line's word positions and push them back through
    /// <see cref="SetLyricWordGeometry"/>. The active word turns bold, which
    /// changes its width, so cached centres go stale on every word change.
    /// </summary>
    public event Action? CurrentWordChanged;

    [ObservableProperty] private string _musicDirectory = "";
    [ObservableProperty] private string _karaokeDirectory = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Set your Music and Karaoke folders in Settings.";
    [ObservableProperty] private TrackEntry? _selectedTrack;
    [ObservableProperty] private BrowserNode? _selectedTreeNode;
    [ObservableProperty] private TrackEntry? _loadedTrack;

    /// <summary>Last track selected in the tree; survives rebuilds and filtering so selection can be restored.</summary>
    private string _lastSelectedTrackKey = "";

    /// <summary>
    /// How the library tree groups tracks: tag-derived artist → album, or the
    /// on-disk folder path. Persisted on change.
    /// </summary>
    [ObservableProperty] private LibraryLayout _layout;

    public bool IsArtistAlbumLayout => Layout == LibraryLayout.ArtistAlbum;
    public bool IsPathLayout => Layout == LibraryLayout.Path;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _loadedTrackTitle = "JockeyBox";
    [ObservableProperty] private string _lyricsStatus = "Load a track to fetch lyrics.";

    // ---- lyrics header (offset + source) ---------------------------------

    /// <summary>"Auto" followed by one entry per lyrics provider (same names
    /// <see cref="LyricsService.SourceNames"/> uses).</summary>
    public ObservableCollection<string> LyricsSourceOptions { get; } = new();

    /// <summary>Documents offered by "try other sources" for the loaded track.</summary>
    public ObservableCollection<LyricsCandidateViewModel> LyricsCandidates { get; } = new();

    /// <summary>Signed offset in ms for the current track (or the global default):
    /// positive = lyrics later. Written straight through to AppSettings, so the
    /// 30 Hz highlighter picks it up on the next tick.</summary>
    [ObservableProperty] private double _lyricsOffsetMs;

    /// <summary>Formatted offset, e.g. "+120 ms".</summary>
    [ObservableProperty] private string _lyricsOffsetText = "0 ms";

    /// <summary>"TRACK" when a track is loaded, otherwise "GLOBAL".</summary>
    [ObservableProperty] private string _lyricsOffsetScope = "GLOBAL";

    /// <summary>Selected entry of <see cref="LyricsSourceOptions"/>.</summary>
    [ObservableProperty] private string _selectedLyricsSource = AutoLyricsSource;

    [ObservableProperty] private bool _isLyricsSourcePickerOpen;
    [ObservableProperty] private string _lyricsSourceHint = "";

    /// <summary>Editable search title in the source picker (prefilled from the file tags).</summary>
    [ObservableProperty] private string _lyricsQueryTitle = "";

    /// <summary>Editable search artist in the source picker (prefilled from the file tags).</summary>
    [ObservableProperty] private string _lyricsQueryArtist = "";

    /// <summary>
    /// True when the loaded track has a pinned query (title/artist differ from the
    /// file tags), so the panel can offer "use file tags" instead of hiding it.
    /// </summary>
    [ObservableProperty] private bool _hasLyricsQueryOverride;

    // ---- lyrics follow mode + bouncing ball ------------------------------

    /// <summary>
    /// Word-by-word follow with the bouncing ball. Persisted immediately through
    /// <see cref="AppSettings.LyricsFollowMode"/>; line mode is the default.
    /// </summary>
    [ObservableProperty] private bool _isWordFollow;

    /// <summary>Compact header toggle label.</summary>
    public string LyricsFollowLabel => IsWordFollow ? "WORD" : "LINE";

    [ObservableProperty] private bool _ballVisible;
    [ObservableProperty] private double _ballX;
    [ObservableProperty] private double _ballY;

    /// <summary>Ball diameter in device-independent pixels (the view's Ellipse matches).</summary>
    public const double BallSize = 10;

    /// <summary>Peak height of the hop above the word, in pixels.</summary>
    private const double BallHopHeight = 6;

    private int _currentWordIndex = -1;

    /// <summary>Line whose word centres the cache describes; null = no cache.</summary>
    private LyricLineViewModel? _ballLine;

    /// <summary>Y (canvas space) of the cached line's word tops; NaN when unknown.</summary>
    private double _ballWordTop = double.NaN;

    /// <summary>X centre of every word in <see cref="_ballLine"/>, index-aligned with its Words.</summary>
    private IReadOnlyList<double>? _ballCentres;

    /// <summary>The line the highlighter is on, or null. Binds and measurement both use it.</summary>
    public LyricLineViewModel? CurrentLyricLine =>
        _currentLyricIndex >= 0 && _currentLyricIndex < LyricLines.Count
            ? LyricLines[_currentLyricIndex]
            : null;

    /// <summary>
    /// Accept measured word geometry from the view (the only place that can read
    /// glyph positions). Ignored when it describes a line that is no longer
    /// current, which happens when a queued layout pass lands after a seek.
    /// </summary>
    public void SetLyricWordGeometry(LyricLineViewModel line, double wordTop, IReadOnlyList<double> centres)
    {
        if (!ReferenceEquals(line, CurrentLyricLine) || centres.Count != line.Words.Count)
        {
            ClearLyricWordGeometry();
            return;
        }

        _ballLine = line;
        _ballWordTop = wordTop;
        _ballCentres = centres;
    }

    /// <summary>Drop the cached geometry and hide the ball (measurement failed / no line).</summary>
    public void ClearLyricWordGeometry()
    {
        _ballLine = null;
        _ballCentres = null;
        _ballWordTop = double.NaN;
        BallVisible = false;
    }

    [RelayCommand]
    private void ToggleLyricsFollow() => IsWordFollow = !IsWordFollow;

    partial void OnIsWordFollowChanged(bool value)
    {
        _settings.LyricsFollowMode = value ? LyricsFollowMode.Word : LyricsFollowMode.Line;
        _settings.Save(); // explicit user action: no debounce needed
        OnPropertyChanged(nameof(LyricsFollowLabel));

        ClearAllWordHighlights();
        ClearLyricWordGeometry();

        // Ask the view to measure the line that is already on screen, so the
        // ball appears without waiting for the next line change.
        if (value) CurrentWordChanged?.Invoke();
    }


    public AppSettings SettingsRef => _settings;

    /// <summary>The live playlist store, so Settings' CSV import writes through it
    /// instead of a second instance that the tabs would later clobber.</summary>
    public PlaylistStore PlaylistsRef => _playlistStore;

    /// <summary>Spotify tab view model (playlists + batch download).</summary>
    public SpotifyViewModel Spotify { get; }

    /// <summary>YouTube tab view model (search, imports, playlists).</summary>
    public YouTubeViewModel YouTube { get; }

    /// <summary>Download queue view model (progress window).</summary>
    public DownloadsViewModel Downloads { get; }

    /// <summary>Raised when the user asks for the download queue window.</summary>
    public event Action? DownloadsRequested;

    /// <summary>Raised when an action needs the Settings dialog (e.g. folders unset).</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when Make Karaoke needs the engine installed first.</summary>
    public event Action<TrackEntry>? EngineInstallRequested;

    // ---- transport commands ----------------------------------------------

    [RelayCommand] private void PlayPause() => _transport.TogglePlayPause();
    [RelayCommand] private void Stop() => _transport.Stop();
    [RelayCommand] private void FastForward() => _transport.SkipForward(10);
    [RelayCommand] private void Rewind() => _transport.SkipBack(10);

    /// <summary>Make deck A / deck B the one the seek bar and lyrics clock follow.</summary>
    [RelayCommand] private void ActivateDeckA() => _transport.ActivateDeck(PlaybackDeck.A);
    [RelayCommand] private void ActivateDeckB() => _transport.ActivateDeck(PlaybackDeck.B);

    partial void OnCrossfaderChanged(double value)
    {
        if (_syncingCrossfader) return;
        _transport.SetCrossfader(value);
    }

    /// <summary>Snap the crossfader to a deck end or to the equal-power centre.</summary>
    [RelayCommand] private void SnapCrossfaderA() => SnapCrossfader(0.0);
    [RelayCommand] private void SnapCrossfaderCentre() => SnapCrossfader(0.5);
    [RelayCommand] private void SnapCrossfaderB() => SnapCrossfader(1.0);

    /// <summary>
    /// Move the fader the way a drag does: the bound property is written first,
    /// so <see cref="OnCrossfaderChanged"/> forwards it to the transport like any
    /// user move. The explicit transport call covers the case where the property
    /// already holds the target (the generated setter then no-ops): a snap is a
    /// hand on the fader, so it must still stop a running crossfade ramp.
    /// </summary>
    private void SnapCrossfader(double value)
    {
        Crossfader = value;
        _transport.SetCrossfader(value);
    }

    /// <summary>Called by the view while the user drags the seek thumb.</summary>
    public void BeginSeek() => _isUserSeeking = true;

    /// <summary>Called by the view when the drag ends; commits the seek once.</summary>
    public void EndSeek(double seconds)
    {
        if (!_isUserSeeking) return; // guard against double commit (mouse-up + drag-completed)
        _isUserSeeking = false;
        _transport.Seek(TimeSpan.FromSeconds(seconds));
    }

    // ---- library ---------------------------------------------------------

    /// <summary>Called by the view after the Settings dialog is saved.</summary>
    public void OnSettingsSaved()
    {
        MusicDirectory = _settings.MusicDirectory;
        KaraokeDirectory = _settings.KaraokeDirectory;

        // The Settings dialog can edit the global lyric offset directly.
        SyncLyricsOffsetEditor();

        // A new cache cap must take effect without a restart.
        if (Math.Abs(_streamCache.LimitBytes - _settings.StreamCacheLimitMb * 1024L * 1024L) > 1)
        {
            _streamCache.LimitBytes = _settings.StreamCacheLimitMb * 1024L * 1024L;
            _streamCache.Evict();
        }

        ScheduleAutoSync();
        _ = RescanAsync();
    }

    [RelayCommand]
    private Task Rescan() => RescanAsync();

    /// <summary>
    /// Every library-mutating entry point funnels through here (startup, the watcher,
    /// the download queue, a settings save, delete, rename, and the Rescan command),
    /// so this is the one place that raises <see cref="IsLibraryScanning"/>.
    /// </summary>
    private async Task RescanAsync()
    {
        try
        {
            // Both live inside the try: a PropertyChanged subscriber that throws
            // from the assignment would skip the finally, leaving the counter
            // above zero and the flag set for the rest of the process, after which
            // every capture waits out its full budget. Incrementing and
            // decrementing now share one protected region.
            Interlocked.Increment(ref _scansInFlight);
            IsLibraryScanning = true;
            await ScanLibraryAsync();
        }
        finally
        {
            // Only the last scan out clears the flag: a superseded scan is
            // cancelled but its finally still runs, and it can land after the
            // newer scan has started.
            if (Interlocked.Decrement(ref _scansInFlight) == 0)
                IsLibraryScanning = false;
        }
    }

    private async Task ScanLibraryAsync()
    {
        // Supersede any running scan before this call can return, not after the
        // directory guard. Blanking both folders while a scan runs lands here: the
        // guard clears the tree and stops the watcher, and if it returned without
        // cancelling, the older scan would finish later and repopulate Tracks from
        // the removed directories, then re-arm the watcher on them. At the top of
        // this method the cancel covers every early return below it.
        _scanCts?.Cancel();

        if (string.IsNullOrWhiteSpace(MusicDirectory) && string.IsNullOrWhiteSpace(KaraokeDirectory))
        {
            Tracks.Clear();
            _watcher.StopWatching();
            StatusText = "Set your Music and Karaoke folders in Settings.";
            return;
        }

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        // Progress<T> posts to the dispatcher, so the last progress tick can
        // land *after* the continuation below and overwrite the summary with
        // "Scanning library… 747/747". Once the scan is done, ignore ticks.
        var scanFinished = false;
        var progress = new Progress<(int done, int total)>(p =>
        {
            if (!scanFinished) StatusText = $"Scanning library\u2026 {p.done}/{p.total}";
        });

        try
        {
            var music = MusicDirectory;
            var karaoke = KaraokeDirectory;
            var list = await Task.Run(() => _scanner.Scan(music, karaoke, progress, ct), ct);
            if (ct.IsCancellationRequested) return;
            scanFinished = true;

            // One scan result, one prune policy, both stores - and only when the
            // scan could see its roots. An offline folder ("no files at all")
            // must not be read as "every file deleted".
            if (Data.LibraryPrune.RootsReachable(music, karaoke))
                Data.LibraryPrune.Apply(
                    list.SelectMany(t => new[] { t.MusicPath, t.KaraokePath }),
                    _tagCache,
                    Data.LibraryDatabase.Tracks);
            else
                Log.Warn($"scan: library folder unreachable ({music} / {karaoke}); " +
                         "keeping library.db and library.json as they are");

            // Analysis already on disk reaches both stores here rather than by
            // re-running the analyser: one pass, once per database (it records a
            // marker), at the point where the two stores are loaded and pruned.
            // The scan itself fills the cache per track; this covers the grids
            // that were measured before anything wrote them down.
            if (_bpmStore is { Count: > 0 } gridStore)
            {
                var db = Data.LibraryDatabase.Current;
                await Task.Run(() => AnalysisBackfill.Run(gridStore, _tagCache, db), ct);
            }

            // Breadcrumb: this is the synchronous half of the scan - thousands of
            // rows rebuilt on the UI thread. No await follows it, so the scope ends
            // with the method.
            using var crumb = UiBreadcrumb.Enter("library scan: apply the scan result");

            _separation.RebindEntries(list);

            // Suppress the per-row invalidation: 5,000 rows would bump the
            // generation 5,000 times and throw the persisted cache away even
            // when nothing changed. The key-set comparison below is the exact
            // test - a hash could collide and serve a stale membership.
            var newKeys = LibraryKeySet(list);
            var keysChanged = _libraryKeySignature is null || !_libraryKeySignature.SetEquals(newKeys);

            _suppressLibraryBump = true;
            try
            {
                Tracks.Clear();
                foreach (var t in list) Tracks.Add(t);
            }
            finally
            {
                _suppressLibraryBump = false;
            }

            _libraryKeySignature = newKeys;
            RebuildBrowser();

            // Let downloads and playback see files that were already on disk,
            // not only the ones this app produced.
            _libraryIndex.Rebuild(list);

            // Same pass feeds the remote tabs' "is it here / is there karaoke?"
            // columns: one dictionary build per scan, not a ledger walk per row.
            // The ledger is folded in as well - it remembers the remote id each
            // file was fetched for, which is how a downloaded YouTube row is
            // recognised even when the file's tags read differently.
            _remoteFacts.Rebuild(list);
            _remoteFacts.RebuildLedger(_ledger.Entries);
            RefreshRemoteLibraryFacts();

            // Cached membership survives a rescan that returned the same files;
            // TrackEntry instances are looked up per rebuild, so the new ones are
            // picked up without re-resolving a single row.
            if (keysChanged) BumpLibraryGeneration("library scan completed");
            RefreshPlaylistRail();
            ApplyPlaylistFilter(_activePlaylist);

            // The queue can only resolve library rows once this list exists.
            RestorePlaybackQueue();

            // A queue restored before the library was known would re-download
            // tracks that are already here, so resume only once the index is warm.
            _downloadQueue.RestorePersisted();

            var missing = list.Count(t => t.NeedsKaraoke);
            StatusText = $"{list.Count} tracks \u00b7 {missing} need karaoke versions";
            _watcher.Watch(music, karaoke);
            ApplyPendingAutoKaraoke();

            // Beat data for the whole library, once the scan has released the
            // disk. Cached tracks are skipped inside the service, so a rescan
            // costs a dictionary lookup per row, not a Python run.
            StartLibraryAnalysisPass();
        }
        catch (OperationCanceledException) { /* superseded by a newer scan */ }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        // The tree is rebuilt off a debounce; until then the old tree stays
        // visible, which keeps typing responsive.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnLayoutChanged(LibraryLayout value)
    {
        if (_settings.LibraryLayout != value)
        {
            _settings.LibraryLayout = value;
            _settings.Save();
        }
        OnPropertyChanged(nameof(IsArtistAlbumLayout));
        OnPropertyChanged(nameof(IsPathLayout));
        RebuildBrowser();
    }

    [RelayCommand]
    private void ShowArtistAlbumLayout() => Layout = LibraryLayout.ArtistAlbum;

    [RelayCommand]
    private void ShowPathLayout() => Layout = LibraryLayout.Path;

    private const string UnknownArtist = "Unknown Artist";

    private static string Norm(string? value) => (value ?? "").Trim();

    /// <summary>Case-insensitive grouping key: " ABBA " and "abba" share a node.</summary>
    private static string GroupKey(string? value) => Norm(value).ToLowerInvariant();

    private static string TrackKey(string relativeKey) => "track:" + relativeKey.ToLowerInvariant();

    private bool FilterTrack(object obj)
    {
        if (obj is not TrackEntry t) return false;

        // Playlist filter first: a playlist is a narrower view of the library,
        // not a search, so the two compose (AND) without either widening.
        //
        // _frozenKeys is only ever replaced as a whole set (a cache hit or one
        // finished background load), so a filter pass can never see a playlist
        // that is half resolved; while a load is in flight the tree is empty
        // rather than partial.
        if (_activePlaylist is not null)
        {
            if (!_hasFrozenKeys) return false;
            if (!_frozenKeys.Contains(SourceTrack.NormalizeLocalKey(t.RelativeKey))) return false;
        }

        var query = Norm(SearchText);
        if (query.Length == 0) return true;
        return t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
            || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase)
            || t.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // ---- playlist rail ---------------------------------------------------

    /// <summary>Every rail row: ALL TRACKS, then app-owned lists, then imports by source.</summary>
    public ObservableCollection<PlaylistRailItem> PlaylistRail { get; } = new();

    public PlaylistRailItem AllTracksRow { get; } = new();

    /// <summary>
    /// The playlist the browser is filtered to, or null for the whole library.
    /// Stored on the view model, not on a selected row, because the rail rows are
    /// rebuilt whenever the store changes.
    /// </summary>
    private UserPlaylist? _activePlaylist;

    /// <summary>
    /// Resolved membership per playlist: memory plus
    /// %APPDATA%\JockeyBox\playlist-cache.json, keyed by playlist id and stamped
    /// with the library generation and the playlist store identity. A hit is one
    /// dictionary lookup and costs no filesystem work.
    /// </summary>
    private readonly PlaylistMembershipCache _playlistCache = new();

    /// <summary>Library indexes built once per generation; read by the worker thread.</summary>
    private LibraryMembershipSnapshot? _membershipSnapshot;

    /// <summary>
    /// Bumped whenever <see cref="Tracks"/>/the library index is rebuilt. The
    /// single source of truth is <see cref="PlaylistMembershipCache.LibraryGeneration"/>:
    /// it is persisted, so a restart that has not rescanned yet still matches the
    /// entries written last run.
    /// </summary>
    private int _libraryGeneration => _playlistCache.LibraryGeneration;

    /// <summary>The membership the browser currently filters by, applied as one whole set.</summary>
    private HashSet<string> _frozenKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every leaf of the tree currently on screen. Kept as a flat list so the
    /// rebuild's detach pass is O(leaves) instead of a recursive walk of every
    /// folder, and so a future "which rows show progress?" query does not have
    /// to walk the tree either.
    /// </summary>
    private List<BrowserNode> _treeLeaves = new();
    private bool _hasFrozenKeys;

    /// <summary>
    /// Rows of the open playlist that have no local file yet: a remote row added
    /// from the Spotify / YouTube tab before it was downloaded. They live in the
    /// store and show in that tab's list, but the library browser can only render
    /// files, so the count is surfaced in the status line instead of silently
    /// dropping them ("Dirty · 40 tracks · 2 not downloaded yet").
    /// </summary>
    private int _pendingPlaylistRows;
    private string? _frozenPlaylistId;
    private int _frozenLibraryGeneration = -1;
    private int _frozenStoreRevision = -1;

    /// <summary>True while a scan repopulates Tracks: its rows must not bump the generation.</summary>
    private bool _suppressLibraryBump;

    /// <summary>
    /// Library keys of the last completed scan, or null before one. A rescan that
    /// produces the same set keeps every cached membership valid.
    /// </summary>
    private HashSet<string>? _libraryKeySignature;

    /// <summary>Generation+revision of the rail count pass in flight (-1: none), so a
    /// rebuild burst does not start several passes over the same library.</summary>
    private int _railCountsInFlightGen = -1;
    private int _railCountsInFlightRev = -1;
    private int _railCountsRetryBudget = 2;

    /// <summary>Generation counter for playlist loads; a newer load cancels the old one.</summary>
    private int _playlistLoadVersion;
    private CancellationTokenSource? _playlistLoadCts;

    /// <summary>Click -> painted rows, for the switch timing line.</summary>
    private System.Diagnostics.Stopwatch? _switchWatch;

    /// <summary>Playlist id the running <see cref="_switchWatch"/> belongs to.</summary>
    private string? _switchingTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotCreatingPlaylist))]
    private bool _isCreatingPlaylist;

    /// <summary>True while a playlist's membership is being resolved off the UI thread.</summary>
    [ObservableProperty]
    private bool _isPlaylistLoading;

    /// <summary>
    /// True while a library scan is populating <see cref="Tracks"/>. Raised by
    /// <see cref="RescanAsync"/> before the scan starts and cleared in its finally,
    /// so an exception or cancellation cannot leave it stuck on. A capture harness
    /// uses it to tell a populated tree from a half-built one.
    /// </summary>
    [ObservableProperty]
    private bool _isLibraryScanning;

    /// <summary>
    /// Scans that have entered <see cref="RescanAsync"/> and not yet left it. A
    /// superseded scan is cancelled but still runs its finally, so a bare boolean
    /// would be cleared while the newer scan was still filling the tree.
    /// <para><b>Dispatcher thread only.</b> <see cref="Interlocked"/> here keeps the
    /// count itself honest, it does not make the pairing thread-safe: the counter is
    /// written alongside the <see cref="IsLibraryScanning"/> property, which
    /// <see cref="ObservableObject"/> only allows to be raised from the UI thread.
    /// Called off that thread, the two writes could interleave so a finished scan
    /// clears the flag a newer one has just set, leaving a live scan reported as
    /// idle. Every caller is already on the dispatcher; keep it that way.</para>
    /// </summary>
    private int _scansInFlight;

    [ObservableProperty] private string _newPlaylistName = "";

    public bool IsNotCreatingPlaylist => !IsCreatingPlaylist;

    public bool HasInternalPlaylists => PlaylistRail.Any(r => r.IsInternal);

    /// <summary>Drives the rail's "no playlists yet" hint (there is no inverse converter in this view).</summary>
    public bool HasNoInternalPlaylists => !HasInternalPlaylists;

    /// <summary>
    /// Rebuild the rail from the store and recompute honest counts. Called on
    /// every store change and after a scan.
    /// </summary>
    public void RefreshPlaylistRail()
    {
        // A playlist removed by another tab (or by a failed load) must not keep
        // filtering the browser to an empty set.
        if (_activePlaylist is not null && !_playlistStore.Playlists.Contains(_activePlaylist))
            _activePlaylist = null;

        var keep = _activePlaylist;

        PlaylistRail.Clear();
        PlaylistRail.Add(AllTracksRow);

        // The LIBRARY rail is internal-only by design: these are the user's own
        // lists of local files. Imported Spotify / YouTube mirrors keep their
        // rows in their own tabs, so no group headers and no source rows here.
        foreach (var playlist in _playlistStore.Playlists.Where(p => p.IsInternal).OrderBy(p => p.SortOrder))
            PlaylistRail.Add(new PlaylistRailItem(playlist));

        // Selection is derived from the view model's active playlist, so a row
        // that survives a rebuild comes back highlighted.
        foreach (var row in PlaylistRail)
            row.IsSelected = keep != null && ReferenceEquals(row.Playlist, keep);
        AllTracksRow.IsSelected = keep is null;

        OnPropertyChanged(nameof(HasInternalPlaylists));
        OnPropertyChanged(nameof(HasNoInternalPlaylists));

        RefreshPlaylistCounts();
    }

    /// <summary>
    /// Paint every rail count from the cache. A row whose entry is current shows
    /// its number immediately; a row that would need resolution shows "\u2026" and
    /// is filled by one background pass, so a rebuild never walks the library on
    /// the UI thread. Counts are resolved library keys, so an imported playlist
    /// whose remote rows are gone but whose files remember it shows its real size
    /// instead of 0.
    /// </summary>
    public void RefreshPlaylistCounts()
    {
        AllTracksRow.TrackCount = Tracks.Count;
        AllTracksRow.IsCountPending = false;

        var missing = new List<UserPlaylist>();

        foreach (var row in PlaylistRail.Where(r => r.Playlist is not null))
        {
            var playlist = row.Playlist!;
            if (_playlistCache.TryGetCount(playlist.Id, _playlistStore.Revision,
                    _playlistStore.FileStamp, out var count))
            {
                row.IsCountPending = false;
                row.TrackCount = count;
            }
            else
            {
                row.IsCountPending = true;
                missing.Add(playlist);
            }
        }

        SyncInternalPlaylists();

        if (missing.Count > 0
            && (_railCountsInFlightGen != _libraryGeneration
                || _railCountsInFlightRev != _playlistStore.Revision))
        {
            _railCountsInFlightGen = _libraryGeneration;
            _railCountsInFlightRev = _playlistStore.Revision;
            _ = FillRailCountsAsync(missing);
        }
    }

    /// <summary>
    /// Resolve the counts the cache could not answer, off the UI thread, and
    /// store them so the next rebuild is a hit. One cache write, not one per
    /// playlist.
    /// </summary>
    private async Task FillRailCountsAsync(List<UserPlaylist> playlists)
    {
        var handedOver = false;
        var generation = _libraryGeneration;
        var revision = _playlistStore.Revision;
        try
        {
            // Built on the UI thread (it reads Tracks), read on the worker.
            var snapshot = MembershipSnapshot();
            var stamp = _playlistStore.FileStamp;
            var requests = playlists.Select(PlaylistMembershipRequest.Capture).ToList();

            var resolved = await Task.Run(() =>
            {
                var result = new List<(string Id, List<string> Keys)>(requests.Count);
                foreach (var request in requests)
                {
                    var keys = new List<string>(snapshot.Resolve(request));
                    result.Add((request.PlaylistId, keys));
                }

                // One file write for the whole pass.
                _playlistCache.PutRange(
                    result.Select(r => (r.Id, (IReadOnlyList<string>)r.Keys)),
                    revision, stamp, generation);

                return result;
            });

            // A rescan moved on while this ran: the numbers belong to the old
            // library and are already invalid, so they are dropped. Same for a
            // store write: the rows this pass read are no longer the rows on screen.
            if (generation != _libraryGeneration) return;
            if (revision != _playlistStore.Revision || stamp != _playlistStore.FileStamp) return;

            foreach (var (id, keys) in resolved)
            {
                var row = PlaylistRail.FirstOrDefault(r => r.Playlist?.Id == id);
                if (row is null) continue;
                row.TrackCount = keys.Count;
                row.IsCountPending = false;
            }

            // A row that appeared while the pass was running (a sync adding a
            // playlist) would otherwise keep its "…" until the next rebuild.
            if (PlaylistRail.Any(r => r.IsCountPending) && _railCountsRetryBudget > 0)
            {
                _railCountsRetryBudget--;
                // Clear the guard first: the retry must be allowed to start, and
                // this pass must not clear the guard it takes over.
                handedOver = true;
                _railCountsInFlightGen = -1;
                _railCountsInFlightRev = -1;
                RefreshPlaylistCounts();
            }
            else
            {
                _railCountsRetryBudget = 2;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist rail: background count pass failed", ex);
        }
        finally
        {
            // Only clear the guard if no newer pass took it over (a retry, or a
            // rebuild after a rescan).
            // Only clear the guard if it still belongs to this pass (a retry, or a
            // rebuild after a rescan, may have taken it over).
            if (!handedOver
                && _railCountsInFlightGen == generation
                && _railCountsInFlightRev == revision)
            {
                _railCountsInFlightGen = -1;
                _railCountsInFlightRev = -1;
            }
        }
    }

    [RelayCommand]
    private void SelectPlaylist(PlaylistRailItem? row)
    {
        if (row is null) return;

        // JSON stays authoritative for external edits: one stat here costs
        // nothing on the normal path and picks up a playlists.json edited by
        // another process (the store reloads it and rewrites the database).
        if (_playlistStore.RefreshFromDiskIfChanged())
        {
            RefreshPlaylistRail();
            row = PlaylistRail.FirstOrDefault(r => r.Playlist?.Id == row.Playlist?.Id);
            if (row is null) return;
        }

        // ALL TRACKS: its row carries no playlist, so selecting it means "no
        // filter" - the teardown below is the whole library again.
        if (row.Playlist is null)
        {
            _activePlaylist = null;
            foreach (var item in PlaylistRail) item.IsSelected = ReferenceEquals(item, row);
            ApplyPlaylistFilter(null);
            return;
        }

        _switchWatch = System.Diagnostics.Stopwatch.StartNew();
        _switchingTo = row.Playlist.Id;

        _activePlaylist = row.Playlist;
        foreach (var item in PlaylistRail) item.IsSelected = ReferenceEquals(item, row);
        ApplyPlaylistFilter(_activePlaylist);
    }

    /// <summary>
    /// <c>playlist switch '&lt;name&gt;': &lt;n&gt; rows in &lt;ms&gt; ms
    /// (source=db|json)</c> - measured from the click to the rows being on
    /// screen. One line per switch, requirement 12's evidence.
    /// </summary>
    private void LogSwitch(UserPlaylist playlist, int rows)
    {
        if (playlist.Id != _switchingTo) return;      // superseded by a newer click
        var watch = _switchWatch;
        _switchWatch = null;
        _switchingTo = null;
        if (watch is null) return;

        PerfTrace.Record("switch.vm-total", watch.Elapsed.TotalMilliseconds);
        Log.Info(
            $"playlist switch '{playlist.Name}': {rows} rows in " +
            $"{watch.Elapsed.TotalMilliseconds:F0} ms (source={_playlistStore.LoadSource})");
    }

    /// <summary>
    /// Point the browser at <paramref name="playlist"/> (null = the whole library).
    ///
    /// Cache hit: the frozen key set is swapped in and the tree is filtered in one
    /// refresh - no File.Exists, no ledger read, no tag lookup (the early return is
    /// <see cref="PlaylistMembershipCache.TryGet"/> above).
    /// Cache miss: nothing old is left on screen, a busy state is shown and the
    /// resolution runs on a worker; switching playlists cancels the previous load
    /// through <see cref="_playlistLoadVersion"/> plus its token.
    /// </summary>
    private void ApplyPlaylistFilter(UserPlaylist? playlist)
    {
        // A pending re-resolve after a library change is superseded by this call.
        _playlistReloadDebounce?.Stop();

        if (playlist is null)
        {
            CancelPlaylistLoad();
            UnfreezeMembership();
            IsPlaylistLoading = false;
            TracksView.Refresh();
            RebuildBrowser();
            StatusText = $"{Tracks.Count} tracks";
            return;
        }

        // Fast path: one dictionary lookup, then one tree rebuild; TracksView is
        // not consulted, so nothing here filters the whole library.
        if (_playlistCache.TryGet(playlist.Id, _playlistStore.Revision, _playlistStore.FileStamp,
                out var cached))
        {
            CancelPlaylistLoad();
            FreezeMembership(playlist, cached);
            // The cache keeps resolved keys only; the difference to the stored
            // rows is the remote-only tail (a remote row added before download).
            _pendingPlaylistRows = Math.Max(0, playlist.Tracks.Count - cached.Count);
            IsPlaylistLoading = false;
            RebuildBrowser();
            StatusText = PlaylistStatus(playlist);
            LogSwitch(playlist, playlist.Tracks.Count);
            return;
        }

        // Already showing this playlist at the current stamps (a finished load).
        if (HasFrozenMembership(playlist))
        {
            RebuildBrowser();
            StatusText = PlaylistStatus(playlist);
            LogSwitch(playlist, playlist.Tracks.Count);
            return;
        }

        // Miss: show a busy state rather than a half-built list.
        // Breadcrumb: the watchdog names this if the rebuild wedges the UI thread.
        using var crumb = UiBreadcrumb.Enter("playlist select: rebuild the browser list");
        UnfreezeMembership();
        IsPlaylistLoading = true;
        StatusText = $"Loading {playlist.Name}\u2026";
        RebuildBrowser();

        _ = LoadPlaylistViewAsync(playlist);
    }

    /// <summary>
    /// Resolve one playlist on a worker and apply the answer in one batch. A
    /// newer selection, a rescan or a store write supersedes it: the version
    /// check is what makes the result safe to drop.
    /// </summary>
    private async Task LoadPlaylistViewAsync(UserPlaylist playlist)
    {
        _playlistLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _playlistLoadCts = cts;

        var version = ++_playlistLoadVersion;
        var ct = cts.Token;

        try
        {
            var snapshot = MembershipSnapshot();
            var request = PlaylistMembershipRequest.Capture(playlist);
            var generation = _libraryGeneration;
            var revision = _playlistStore.Revision;
            var stamp = _playlistStore.FileStamp;

            var resolveWatch = Stopwatch.StartNew();
            var (keys, pending) = await Task.Run<ValueTuple<IReadOnlyList<string>, int>>(() =>
            {
                var resolved = snapshot.Resolve(request, ct);
                var list = new List<string>(resolved);
                // One cache write for this playlist; stamped with the generation it
                // was resolved against, so a rescan mid-flight discards it.
                _playlistCache.Put(playlist.Id, list, revision, stamp, generation);
                // Rows the library cannot render (no file yet) still belong to
                // this playlist - count them so the status line can say so.
                var missing = Math.Max(0, request.Rows.Count - list.Count);
                return (list, missing);
            }, ct);

            if (ct.IsCancellationRequested || version != _playlistLoadVersion) return;
            if (!ReferenceEquals(_activePlaylist, playlist)) return;
            if (generation != _libraryGeneration) return;
            // A rename/add/remove during the load changed the rows this set came
            // from: the debounced reload owns them now, and freezing here would
            // claim a membership the store no longer has.
            if (revision != _playlistStore.Revision || stamp != _playlistStore.FileStamp) return;

            PerfTrace.Record("load.worker", resolveWatch.Elapsed.TotalMilliseconds);
            FreezeMembership(playlist, keys);
            _pendingPlaylistRows = pending;
            IsPlaylistLoading = false;
            // One rebuild for the whole set: no per-row notifications, no flicker.
            using (PerfTrace.Measure("load.ui-rebuild"))
            {
                RebuildBrowser();
                StatusText = PlaylistStatus(playlist);
            }
            LogSwitch(playlist, playlist.Tracks.Count);
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        catch (Exception ex)
        {
            Log.Error($"Playlist '{playlist.Name}': resolving membership failed", ex);
            if (version == _playlistLoadVersion)
            {
                IsPlaylistLoading = false;
                StatusText = $"Could not open {playlist.Name}";
            }
        }
        finally
        {
            bool current = ReferenceEquals(_playlistLoadCts, cts);
            cts.Dispose();
            if (current)
            {
                _playlistLoadCts = null;
                // This load is still the one that owns the browser, and it just
                // ended - cancelled with no successor (CancelPlaylistLoad bumps the
                // version but never touches the flag) or failed on its own. Clear
                // the busy overlay here; when a newer load exists it owns the
                // overlay and this stale one must stay silent. Keyed on the CTS
                // reference rather than the version because the version moves on
                // every supersede, including ones whose load never starts.
                IsPlaylistLoading = false;
            }
        }
    }

    [RelayCommand]
    private void BeginCreatePlaylist()
    {
        NewPlaylistName = "";
        IsCreatingPlaylist = true;
    }

    [RelayCommand]
    private void CancelCreatePlaylist()
    {
        IsCreatingPlaylist = false;
        NewPlaylistName = "";
    }

    [RelayCommand]
    private void CommitCreatePlaylist()
    {
        var name = Norm(NewPlaylistName);
        if (name.Length == 0)
        {
            CancelCreatePlaylist();
            return;
        }

        var playlist = _playlistStore.CreateInternal(name);
        _activePlaylist = playlist;
        IsCreatingPlaylist = false;
        NewPlaylistName = "";
        RefreshPlaylistRail();
        ApplyPlaylistFilter(playlist);
        StatusText = $"created playlist {playlist.Name}";
    }

    [RelayCommand]
    private void BeginRenamePlaylist(PlaylistRailItem? row) => row?.BeginEdit();

    [RelayCommand]
    private void CommitRenamePlaylist(PlaylistRailItem? row)
    {
        if (row?.Playlist is null) return;

        if (!_playlistStore.Rename(row.Playlist, row.EditName))
        {
            row.CancelEdit();
            return;
        }

        row.CancelEdit();
        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);
        StatusText = $"renamed playlist to {row.Playlist.Name}";
    }

    [RelayCommand]
    private void CancelRenamePlaylist(PlaylistRailItem? row) => row?.CancelEdit();

    [RelayCommand]
    private void BeginDeletePlaylist(PlaylistRailItem? row) => row?.BeginDelete();

    [RelayCommand]
    private void CancelDeletePlaylist(PlaylistRailItem? row) => row?.CancelDelete();

    /// <summary>Delete an app-owned playlist after the rail's confirmation.</summary>
    [RelayCommand]
    private void CommitDeletePlaylist(PlaylistRailItem? row)
    {
        if (row?.Playlist is not { IsInternal: true } playlist) return;

        if (_activePlaylist == playlist) _activePlaylist = null;

        if (!_playlistStore.Delete(playlist))
        {
            row.CancelDelete();
            return;
        }

        // The store save already bumped the revision (which invalidates every
        // entry); dropping this one keeps the file from carrying dead ids.
        _playlistCache.Remove(playlist.Id);

        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);
        StatusText = $"deleted playlist {playlist.Name}";
    }

    /// <summary>
    /// Add the selected library track(s) to an app-owned playlist. The row's
    /// library key is what gets stored; re-downloads under a new name fall back
    /// to the ledger path and then to artist/title (see <see cref="LibraryMembershipSnapshot"/>).
    /// </summary>
    [RelayCommand]
    private void AddSelectedToPlaylist(UserPlaylist? playlist)
    {
        if (playlist is not { IsInternal: true }) return;

        var added = _playlistStore.AddLibraryTracks(playlist, SelectedLibraryTracks().ToList());
        if (added == 0)
        {
            StatusText = "nothing new for " + playlist.Name;
            return;
        }

        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);
        StatusText = $"added {added} track{(added == 1 ? "" : "s")} to {playlist.Name}";
    }

    [RelayCommand]
    private void RemoveSelectedFromPlaylist()
    {
        if (_activePlaylist is not { IsInternal: true } playlist) return;

        var removed = _playlistStore.RemoveLibraryTracks(playlist, SelectedLibraryTracks().ToList());
        if (removed == 0) return;

        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);
        StatusText = $"removed {removed} track{(removed == 1 ? "" : "s")} from {playlist.Name}";
    }

    /// <summary>Remove the right-clicked library row from every app-owned playlist holding it.</summary>
    [RelayCommand]
    private void RemoveSelectedFromAllPlaylists()
    {
        var rows = SelectedLibraryTracks().ToList();
        if (rows.Count == 0) return;

        var removed = 0;
        foreach (var playlist in _playlistStore.Playlists.Where(p => p.IsInternal).ToList())
            removed += _playlistStore.RemoveLibraryTracks(playlist, rows);

        if (removed == 0)
        {
            StatusText = "no playlist holds that track";
            return;
        }

        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);
        StatusText = $"removed {removed} row{(removed == 1 ? "" : "s")} from playlists";
    }

    // ---- library file operations (rename / move / delete / reveal) ---------

    /// <summary>
    /// The single track a file operation acts on. A folder row (or no row at
    /// all) yields null, which disables the commands instead of guessing a
    /// target. Falls back to the flat list selection when the tree has none.
    /// </summary>
    private TrackEntry? SelectedFileTrack()
    {
        if (SelectedTreeNode is { } node) return node.Track;
        return SelectedTrack;
    }

    private bool CanEditSelectedFileTrack() => AudioPathOf(SelectedFileTrack()) != null;

    /// <summary>Music file when it exists, otherwise the karaoke file (karaoke-only track).</summary>
    private static string? AudioPathOf(TrackEntry? track)
    {
        if (track == null) return null;
        if (!string.IsNullOrWhiteSpace(track.MusicPath)) return track.MusicPath;
        if (!string.IsNullOrWhiteSpace(track.KaraokePath)) return track.KaraokePath;
        return null;
    }

    /// <summary>Fresh service per operation so a stale pair of roots is never reused.</summary>
    private MediaFileOps? CreateMediaOps()
    {
        try
        {
            var ops = new MediaFileOps(MusicDirectory, KaraokeDirectory)
            {
                OnLibraryFileMoved = OnMediaFileMoved,
                OnRelativeKeyChanged = OnMediaRelativeKeyChanged,
                // Rescanning is owned by the command that called the service
                // (and by the watcher), so this callback stays silent.
            };
            return ops;
        }
        catch (ArgumentException ex)
        {
            StatusText = $"Cannot edit library files: {ex.Message}";
            return null;
        }
    }

    private void OnMediaFileMoved(string oldPath, string newPath)
    {
        _mediaOpOldPath = oldPath ?? "";
        _mediaOpNewPath = newPath ?? "";
        ApplyMediaPathChange(_mediaOpOldPath, _mediaOpNewPath, KeyForPath(oldPath), KeyForPath(newPath));
    }

    private void OnMediaRelativeKeyChanged(string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(_mediaOpNewPath)) return;

        // The service's keys are authoritative; the first pass used keys
        // derived from the paths, and this pass is a no-op unless they differ.
        ApplyMediaPathChange(_mediaOpOldPath, _mediaOpNewPath, oldKey ?? "", newKey ?? "");
        _mediaOpOldKey = oldKey ?? _mediaOpOldKey;
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedFileTrack))]
    private async Task RenameTrackAsync()
    {
        if (SelectedFileTrack() is not { } track || AudioPathOf(track) is not { } path) return;

        var owner = DialogOwner();
        var dialog = new RenameTrackDialog(path);
        if (owner != null) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        var ops = CreateMediaOps();
        if (ops == null) return;

        BeginMediaOp(track);
        var outcome = ops.Rename(path, dialog.NewBaseName);
        await HandleMediaOutcomeAsync(ops, outcome, path, "Rename");
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedFileTrack))]
    private async Task MoveTrackAsync()
    {
        if (SelectedFileTrack() is not { } track || AudioPathOf(track) is not { } path) return;

        var ops = CreateMediaOps();
        if (ops == null) return;

        var initialFolder = ops.SuggestFolder(path, track.Artist, track.Album);
        var owner = DialogOwner();
        var dialog = new MoveTrackDialog(DisplayRelativePath(track, path),
            track.Artist, track.Album, track.Title, initialFolder);
        if (owner != null) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        BeginMediaOp(track);
        var outcome = dialog.UsePreset
            ? ops.MoveToPreset(path, dialog.PresetArtist, dialog.PresetAlbum, dialog.PresetTitle)
            : ops.MoveTo(path, dialog.TargetFolder);
        await HandleMediaOutcomeAsync(ops, outcome, path, "Move");
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedFileTrack))]
    private async Task DeleteTrackAsync()
    {
        if (SelectedFileTrack() is not { } track || AudioPathOf(track) is not { } path) return;

        var fileName = Path.GetFileName(path);
        var karaokeOnly = string.IsNullOrWhiteSpace(track.MusicPath);
        var detail = karaokeOnly
            ? "This track has no music file, so only the karaoke file is deleted."
            : "The karaoke mirror, if there is one, is kept.";

        var question = $"Delete \u201c{fileName}\u201d?\n\n{path}\n\nThe file goes to the Recycle Bin. {detail}";
        if (ShowOwnerMessage(question, "Delete track", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var ops = CreateMediaOps();
        if (ops == null) return;

        var outcome = ops.Delete(path);

        if (outcome.Result != MediaOpResult.Ok)
        {
            StatusText = MessageOrFallback(outcome, "Could not delete the track.");
            return;
        }

        // The file is gone. A karaoke-only track keeps the same RelativeKey as
        // its deleted music file, so reselecting the old key finds the mirror
        // when it survives and clears the selection when it does not.
        var key = KeyForPath(path);
        await RescanAsync();
        ReselectLibraryKey(key);
        StatusText = string.IsNullOrWhiteSpace(outcome.Message) ? "Deleted the track file." : outcome.Message;
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedFileTrack))]
    private void RevealInExplorer()
    {
        if (SelectedFileTrack() is not { } track || AudioPathOf(track) is not { } path) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
            StatusText = $"Showing \u201c{Path.GetFileName(path)}\u201d in Explorer.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open Explorer: {ex.Message}";
        }
    }

    /// <summary>Reset the pending pair before an operation that may move a file.</summary>
    private void BeginMediaOp(TrackEntry track)
    {
        _mediaOpOldPath = AudioPathOf(track) ?? "";
        _mediaOpNewPath = "";
        _mediaOpOldKey = track.RelativeKey;
    }

    /// <summary>
    /// Act on a service outcome: report refusals verbatim, ask before adopting
    /// a collision suggestion, and finish a success with persistence + rescan.
    /// Every branch writes the status line, so no outcome is swallowed.
    /// </summary>
    private async Task HandleMediaOutcomeAsync(MediaFileOps ops, MediaOpOutcome outcome,
        string sourcePath, string verb, int depth = 0)
    {
        switch (outcome.Result)
        {
            case MediaOpResult.Ok:
                await CompleteMediaOperationAsync(outcome, sourcePath, verb);
                return;

            case MediaOpResult.Collision:
            {
                var suggestion = outcome.NewPath;
                var detail = string.IsNullOrWhiteSpace(outcome.Message)
                    ? $"A file already exists at the destination.\n\n{suggestion}"
                    : $"{outcome.Message}\n\nSuggested destination:\n{suggestion}";
                var question = $"{detail}\n\nMove to the suggested name instead?";

                if (depth >= 3 || string.IsNullOrWhiteSpace(suggestion))
                {
                    StatusText = MessageOrFallback(outcome, "A file already exists at the destination.");
                    return;
                }

                if (ShowOwnerMessage(question, "Destination already exists", MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    StatusText = MessageOrFallback(outcome, "Nothing was moved.");
                    return;
                }

                var folder = Path.GetDirectoryName(suggestion);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    StatusText = MessageOrFallback(outcome, "The suggested destination is not usable.");
                    return;
                }

                var retry = ops.MoveToNamed(sourcePath, folder!,
                    Path.GetFileNameWithoutExtension(suggestion));
                await HandleMediaOutcomeAsync(ops, retry, sourcePath, verb, depth + 1);
                return;
            }

            default:
                StatusText = MessageOrFallback(outcome,
                    outcome.Result == MediaOpResult.Blocked ? "The file is blocked." : "The operation failed.");
                return;
        }
    }

    private async Task CompleteMediaOperationAsync(MediaOpOutcome outcome, string sourcePath, string verb)
    {
        var newPath = outcome.NewPath ?? "";

        // Rename to the name the file already has reports success without moving.
        if (string.IsNullOrWhiteSpace(newPath)
            || string.Equals(newPath, sourcePath, StringComparison.Ordinal))
        {
            StatusText = MessageOrFallback(outcome, $"{verb}: nothing to change.");
            return;
        }

        // The service callbacks normally persisted already; this covers a
        // service implementation (or a future overload) that does not call them.
        if (!string.Equals(_mediaOpNewPath, newPath, StringComparison.OrdinalIgnoreCase))
            ApplyMediaPathChange(sourcePath, newPath, KeyForPath(sourcePath), KeyForPath(newPath));

        var oldKey = _mediaOpOldKey.Length > 0 ? _mediaOpOldKey : KeyForPath(sourcePath);
        var newKey = KeyForPath(newPath);

        // The rescan refreshes the tree, the index, playlist counts and the
        // source tabs' "in library" states.
        await RescanAsync();

        RepointLiveQueue(oldKey, newKey);
        if (LoadedTrack != null &&
            string.Equals(LoadedTrack.RelativeKey, oldKey, StringComparison.OrdinalIgnoreCase))
            LoadedTrack = ResolveLibraryTrack(newKey);

        ReselectLibraryKey(newKey);
        StatusText = MessageOrFallback(outcome, $"{verb}d \u201c{Path.GetFileName(newPath)}\u201d.");
    }

    private static string MessageOrFallback(MediaOpOutcome outcome, string fallback) =>
        string.IsNullOrWhiteSpace(outcome.Message) ? fallback : outcome.Message;

    private string DisplayRelativePath(TrackEntry track, string path)
    {
        var root = !string.IsNullOrWhiteSpace(track.MusicPath) ? MusicDirectory : KaraokeDirectory;
        try
        {
            if (!string.IsNullOrWhiteSpace(root)) return Path.GetRelativePath(root, path);
        }
        catch (ArgumentException)
        {
            // A path that does not share the root: show it as-is.
        }
        return path;
    }

    /// <summary>
    /// Persist a successful library move/rename across every store that holds
    /// the file by path or by library-relative key. Runs in the documented
    /// order, each step independently guarded so one failed write does not
    /// strand the others. Idempotent: a second call with the same pair finds
    /// nothing left to move and writes nothing. One summary log line is emitted
    /// only when at least one store changed.
    /// </summary>
    private void ApplyMediaPathChange(string oldPath, string newPath, string oldKey, string newKey)
    {
        var oldFull = (oldPath ?? "").Trim();
        var newFull = (newPath ?? "").Trim();
        if (oldFull.Length == 0 || newFull.Length == 0
            || string.Equals(oldFull, newFull, StringComparison.Ordinal))
            return;

        var ledgerMoved = 0;
        var tagsMoved = false;
        var playlistsMoved = 0;
        var queueMoved = 0;
        var lyricsMoved = 0;

        // 1. Download ledger: Path is what downloads resolve against.
        try
        {
            ledgerMoved = _ledger.RepointPath(oldFull, newFull);
        }
        catch (Exception ex)
        {
            Log.Error($"Ledger repoint failed for {oldFull}", ex);
        }

        // 2. Tag cache: library.json is keyed by absolute path. Re-key so the
        //    next scan keeps BPM/duration instead of re-tagging from scratch.
        try
        {
            tagsMoved = _tagCache.Repoint(oldFull, newFull);
        }
        catch (Exception ex)
        {
            Log.Error($"Tag cache repoint failed for {oldFull}", ex);
        }

        // 3. Playlists: internal rows key on LocalKey, imported rows keep their
        //    remote id but may have cached a library path.
        try
        {
            playlistsMoved = _playlistStore.RepointLibraryFile(oldKey, newKey, oldFull, newFull);
        }
        catch (Exception ex)
        {
            Log.Error($"Playlist repoint failed for {oldFull}", ex);
        }

        // 4. Playback queue mirror: library rows are keyed by RelativeKey.
        try
        {
            queueMoved = _queueStore.RepointLibraryKey(oldKey, newKey);
        }
        catch (Exception ex)
        {
            Log.Error($"Playback queue repoint failed for {oldFull}", ex);
        }

        // 5. Per-track lyric settings: the legacy offset/source keys in settings
        //    (read-only since the metadata store took over, kept in step so the
        //    one-time migration cannot resurrect stale rows) plus the store's own
        //    record, which now holds the offset, the pin and the sections.
        try
        {
            lyricsMoved = _settings.RepointLyricsKey(oldKey, newKey);
            if (lyricsMoved > 0) _settings.Save();
            if (MoveLyricMetadata(oldKey, newKey)) lyricsMoved++;
        }
        catch (Exception ex)
        {
            Log.Error($"Lyrics settings repoint failed for {oldFull}", ex);
        }

        if (ledgerMoved == 0 && !tagsMoved && playlistsMoved == 0 && queueMoved == 0 && lyricsMoved == 0)
            return;

        // A move/rename changes paths that membership resolution uses as fallback
        // keys, even when no playlist row itself was rewritten.
        BumpLibraryGeneration("file moved or renamed");
        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);

        Log.Info($"Repointed {Path.GetFileName(oldFull)} \u2192 {Path.GetFileName(newFull)}: " +
                 $"ledger={ledgerMoved}, tags={(tagsMoved ? 1 : 0)}, playlists={playlistsMoved}, " +
                 $"queue={queueMoved}, lyrics={lyricsMoved}");
    }

    /// <summary>
    /// Carry a track's saved lyric metadata (offset, pinned source, sections) to
    /// its new <c>RelativeKey</c> after a move/rename. Returns false when there
    /// was nothing to move. The store has no repoint of its own, so the record is
    /// re-applied under the new key through its public setters and then dropped.
    /// </summary>
    private bool MoveLyricMetadata(string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)
            || string.Equals(oldKey, newKey, StringComparison.Ordinal))
            return false;

        var record = _trackMeta.Get(oldKey);
        if (record == null) return false;

        _trackMeta.SetTrackInfo(newKey, record.Title, record.Artist);
        if (record.LyricsOffsetMs is { } offsetMs) _trackMeta.SetLyricsOffset(newKey, offsetMs);
        if (!string.IsNullOrWhiteSpace(record.LyricsSourceId))
            _trackMeta.SetLyricsSource(newKey, record.LyricsSourceId);
        if (record.LyricsFollowMode is { Length: > 0 } followMode)
            _trackMeta.SetLyricsFollowMode(newKey, followMode);
        foreach (var section in record.Sections)
            _trackMeta.AddSection(newKey, section.StartMs, section.EndMs, section.OffsetMs, section.Label);
        _trackMeta.Remove(oldKey);

        if (string.Equals(TrackMetadataStore.NormalizeKey(CurrentLyricsKey),
                TrackMetadataStore.NormalizeKey(oldKey), StringComparison.Ordinal))
        {
            RefreshTrackSections();
            SyncLyricsOffsetEditor();
        }
        return true;
    }

    /// <summary>Rewrite live queue rows after a rescan gave the moved key a new entry.</summary>
    private void RepointLiveQueue(string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)
            || string.Equals(oldKey, newKey, StringComparison.Ordinal))
            return;

        var track = ResolveLibraryTrack(newKey);
        foreach (var item in Queue)
        {
            if (!item.IsLibrary) continue;
            if (!string.Equals(item.Key, oldKey, StringComparison.OrdinalIgnoreCase)) continue;
            item.Repoint(newKey, track);
        }
    }

    /// <summary>Select the track row for <paramref name="key"/> after a rebuild; clears selection when gone.</summary>
    private void ReselectLibraryKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        _lastSelectedTrackKey = TrackKey(key);
        var node = FindByKey(BrowserRoots, _lastSelectedTrackKey);
        SelectedTreeNode = node;
        if (node != null) node.IsSelected = true;
    }

    /// <summary>Library key for an absolute file path, using the root it lives under.</summary>
    private string KeyForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in new[] { MusicDirectory, KaraokeDirectory })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                if (!IsInsideRoot(full, root)) continue;
                return LibraryScanner.PairKey(root, full);
            }
            return "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool IsInsideRoot(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void NotifyMediaCommandStates()
    {
        RenameTrackCommand.NotifyCanExecuteChanged();
        MoveTrackCommand.NotifyCanExecuteChanged();
        DeleteTrackCommand.NotifyCanExecuteChanged();
        RevealInExplorerCommand.NotifyCanExecuteChanged();
    }

    private static System.Windows.Window? DialogOwner()
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        return owner is { IsVisible: true } ? owner : null;
    }

    private static MessageBoxResult ShowOwnerMessage(string message, string caption,
        MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var owner = DialogOwner();
        return owner == null
            ? MessageBox.Show(message, caption, buttons, image, defaultResult)
            : MessageBox.Show(owner, message, caption, buttons, image, defaultResult);
    }

    /// <summary>
    /// Tracks the context menu acts on: the right-clicked row when it is a track,
    /// otherwise every track under that folder. Never more than the browser is
    /// showing, so "Add to playlist" cannot pull in filtered-out files.
    /// </summary>
    private IEnumerable<TrackEntry> SelectedLibraryTracks()
    {
        var node = SelectedTreeNode;
        if (node is null)
        {
            return SelectedTrack is { } single ? new[] { single } : Array.Empty<TrackEntry>();
        }

        var found = new List<TrackEntry>();
        CollectNodeTracks(node, found);
        return found;
    }

    private static void CollectNodeTracks(BrowserNode node, List<TrackEntry> found)
    {
        if (node.Track is { } track)
        {
            found.Add(track);
            return;
        }

        foreach (var child in node.Children) CollectNodeTracks(child, found);
    }

    /// <summary>
    /// The exact library-key set a scan produced. Exact on purpose: a hash of the
    /// keys could collide, and a missed change would serve a stale membership
    /// instead of re-resolving it.
    /// </summary>
    private static HashSet<string> LibraryKeySet(IEnumerable<TrackEntry> tracks)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (track is null) continue;
            var key = SourceTrack.NormalizeLocalKey(track.RelativeKey);
            if (key.Length > 0) keys.Add(key);
        }

        return keys;
    }

    // ---- playlist membership: cache + off-thread resolution --------------

    /// <summary>
    /// Resolve <see cref="Tracks"/> and the playlists into immutable indexes the
    /// worker can read. Rebuilt only when the library generation or the playlist
    /// store moved on, and always on the UI thread - it is the only reader of the
    /// mutable collections.
    /// </summary>
    private LibraryMembershipSnapshot MembershipSnapshot()
    {
        var cached = _membershipSnapshot;
        if (cached is not null
            && cached.LibraryGeneration == _libraryGeneration
            && cached.StoreRevision == _playlistStore.Revision
            && cached.StoreStamp == _playlistStore.FileStamp)
            return cached;

        cached = LibraryMembershipSnapshot.Build(
            Tracks.ToArray(),
            _playlistStore.Playlists.ToArray(),
            _ledger.Entries,
            _libraryIndex,
            _libraryGeneration,
            _playlistStore.Revision,
            _playlistStore.FileStamp);

        _membershipSnapshot = cached;
        return cached;
    }

    /// <summary>
    /// The library was rebuilt (scan, file operation, download): every resolved
    /// answer is stale. Deliberately cheap - a scan raises it once per added row -
    /// and the cache write it needs is coalesced in
    /// <see cref="PlaylistMembershipCache.BumpLibraryGeneration"/>.
    /// </summary>
    private void BumpLibraryGeneration(string reason)
    {
        // The cache owns the counter (it persists it): one source of truth, so the
        // generation a load resolves against is the generation the cache stamps.
        _playlistCache.BumpLibraryGeneration();
        _membershipSnapshot = null;
        UnfreezeMembership();
        CancelPlaylistLoad();

        // A scan raises this once per added row; only the meaningful hooks log.
        if (!string.Equals(reason, "library list changed", StringComparison.Ordinal))
            Log.Debug($"Playlist membership invalidated: {reason} (generation {_libraryGeneration})");

        // The open playlist's frozen set was just dropped. Show the busy state
        // (not an empty list, not the stale one) and resolve again once the burst
        // of bumps is over.
        if (_activePlaylist is not null)
        {
            // No rebuild here: a scan raises this once per added row, and a tree
            // rebuild per row would be quadratic. The old tree stays under the
            // busy overlay until the debounced reload replaces it in one batch.
            IsPlaylistLoading = true;
            _playlistReloadDebounce?.Stop();
            _playlistReloadDebounce?.Start();
        }
    }

    /// <summary>Supersede any in-flight playlist load; its result is dropped on return.</summary>
    private void CancelPlaylistLoad()
    {
        _playlistLoadVersion++;
        _playlistLoadCts?.Cancel();
    }

    private void UnfreezeMembership()
    {
        // No allocation: a scan unfreezes once per added row, and the stale set is
        // unreachable while _hasFrozenKeys is false.
        _hasFrozenKeys = false;
        _pendingPlaylistRows = 0;
        _frozenPlaylistId = null;
        _frozenLibraryGeneration = -1;
        _frozenStoreRevision = -1;
    }

    private void FreezeMembership(UserPlaylist playlist, IReadOnlyList<string> keys)
    {
        _frozenKeys = keys as HashSet<string>
                      ?? new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        _hasFrozenKeys = true;
        _frozenPlaylistId = playlist.Id;
        _frozenLibraryGeneration = _libraryGeneration;
        _frozenStoreRevision = _playlistStore.Revision;
    }

    /// <summary>The playlist status line, naming rows that are still remote-only.</summary>
    private string PlaylistStatus(UserPlaylist playlist)
    {
        var text = $"{playlist.Name} \u00b7 {_frozenKeys.Count} tracks";
        return _pendingPlaylistRows > 0 ? $"{text} \u00b7 {_pendingPlaylistRows} not downloaded yet" : text;
    }

    /// <summary>True when the frozen set is the current playlist's and still current.</summary>
    private bool HasFrozenMembership(UserPlaylist? playlist) =>
        _hasFrozenKeys
        && _frozenLibraryGeneration == _libraryGeneration
        && _frozenStoreRevision == _playlistStore.Revision
        && string.Equals(_frozenPlaylistId, playlist?.Id, StringComparison.Ordinal);


    /// <summary>Membership rows the rail offers in "Add to playlist ▸".</summary>
    public ObservableCollection<UserPlaylist> InternalPlaylists { get; } = new();

    private void SyncInternalPlaylists()
    {
        InternalPlaylists.Clear();
        foreach (var playlist in _playlistStore.Playlists.Where(p => p.IsInternal))
            InternalPlaylists.Add(playlist);
    }

    /// <summary>
    /// Heal pre-provenance ledgers once at start-up: entries written before the
    /// ledger recorded a playlist are named from queue.json (exact remote id) and
    /// from imported playlists that still hold the row. Nothing is guessed; a
    /// source that is not known stays null, and one save happens at most.
    ///
    /// Title/artist are not re-read from tags here: the ledger wrote them from the
    /// same tags at download time, and re-scanning every file at start-up would
    /// trade a start-up stall for data that is already correct.
    /// </summary>
    private void BackfillLedgerProvenance()
    {
        var hints = new List<(string SourceKey, string PlaylistName, MediaSource Source)>();
        foreach (var playlist in _playlistStore.Playlists.Where(p => p.IsImported))
        {
            foreach (var track in playlist.Tracks)
            {
                if (string.IsNullOrWhiteSpace(track.Id)) continue;
                hints.Add((track.Id, playlist.Name, playlist.Source));
            }
        }

        _ledger.BackfillProvenance(DownloadQueue.ReadPersistedProvenance(), hints);
    }

    /// <summary>Rebuild the browser from the tracks the current search matches.</summary>
    private void RebuildBrowser()
    {
        // A playlist with no search text is rebuilt straight from the frozen key
        // set: O(rows), not O(library). Everything else filters the library.
        if (TryBuildPlaylistTracks(out var rows))
        {
            BuildTree(rows, Layout);
            return;
        }

        List<TrackEntry> filtered;
        using (PerfTrace.Measure("tree.filter"))
            filtered = Tracks.Where(FilterTrack).ToList();
        BuildTree(filtered, Layout);
    }

    /// <summary>
    /// Rows of the active playlist in library order, without a filter pass over
    /// the whole library. False when the tree needs the general filter path (no
    /// playlist, nothing frozen yet, or a search that narrows within the playlist).
    /// </summary>
    private bool TryBuildPlaylistTracks(out List<TrackEntry> rows)
    {
        rows = null!;
        if (_activePlaylist is null || !_hasFrozenKeys) return false;
        if (Norm(SearchText).Length > 0) return false;

        using (PerfTrace.Measure("tree.rows-for-keys"))
            rows = MembershipSnapshot().TracksForKeys(_frozenKeys);
        return true;
    }

    /// <summary>
    /// Build the browser tree for <paramref name="layout"/>. The previously
    /// selected track and the expanded folder keys are carried over where the
    /// new tree still contains them; folders that no longer exist collapse.
    /// </summary>
    private void BuildTree(List<TrackEntry> tracks, LibraryLayout layout)
    {
        var selectedKey = _lastSelectedTrackKey;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (PerfTrace.Measure("tree.collect-expanded"))
        {
            foreach (var root in BrowserRoots)
                CollectExpanded(root, expanded);
        }

        // Old leaves hold live subscriptions into Tracks; release them before
        // dropping the tree (rebuilds happen on every search keystroke).
        using (PerfTrace.Measure("tree.detach"))
        {
            foreach (var leaf in _treeLeaves)
                leaf.Detach();
            _treeLeaves.Clear();
            BrowserRoots.Clear();
        }

        if (layout == LibraryLayout.Path)
            BuildPathTree(tracks);
        else
            BuildArtistAlbumTree(tracks);

        using (PerfTrace.Measure("tree.count"))
        {
            foreach (var root in BrowserRoots)
                _ = CountTracks(root);
        }

        using (PerfTrace.Measure("tree.restore-expanded"))
        {
            foreach (var root in BrowserRoots)
                RestoreExpanded(root, expanded);
        }

        var restored = selectedKey.Length == 0
            ? null
            : FindByKey(BrowserRoots, selectedKey);
        SelectedTreeNode = restored;
        if (restored != null)
            restored.IsSelected = true;

        // The playlist commands answer from the leaves/frozen state just replaced.
        QueueActivePlaylistCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Tag layout: Artist → Album → Track. An empty artist lands under
    /// "Unknown Artist"; an empty album puts its tracks directly under the
    /// artist (no invented "Unknown Album" level).
    /// </summary>
    private void BuildArtistAlbumTree(List<TrackEntry> tracks)
    {
        using var _scope = PerfTrace.Measure("tree.build-album");
        var artists = tracks
            .GroupBy(t => GroupKey(t.Artist), StringComparer.Ordinal)
            .Select(g => new
            {
                Key = g.Key,
                Name = Norm(g.First().Artist) is { Length: > 0 } name ? name : UnknownArtist,
                Tracks = g,
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);

        var roots = new List<BrowserNode>(tracks.Count > 0 ? 64 : 0);

        foreach (var artist in artists)
        {
            var artistNode = new BrowserNode(artist.Name)
            {
                Key = "artist:" + artist.Key,
                FolderArtist = artist.Name,
            };

            // Named albums first, alphabetical (and case-insensitive).
            var albums = artist.Tracks
                .GroupBy(t => GroupKey(t.Album), StringComparer.Ordinal)
                .Where(g => g.Key.Length > 0)
                .Select(g => new { Key = g.Key, Name = Norm(g.First().Album), Tracks = g })
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var album in albums)
            {
                var albumNode = new BrowserNode(album.Name)
                {
                    Key = artistNode.Key + "/" + album.Key,
                    FolderArtist = artist.Name,
                    FolderAlbum = album.Name,
                };
                foreach (var track in album.Tracks.OrderBy(t => t, TrackOrder))
                    albumNode.Children.Add(Leaf(track));
                artistNode.Children.Add(albumNode);
            }

            // Album-less tracks sit directly under the artist, after the albums.
            var loose = artist.Tracks
                .Where(t => GroupKey(t.Album).Length == 0)
                .OrderBy(t => t, TrackOrder);
            foreach (var track in loose)
                artistNode.Children.Add(Leaf(track));

            roots.Add(artistNode);
        }

        // One attach for the whole forest: the artist nodes were built detached,
        // so their album/track children never reached the TreeView one by one.
        foreach (var root in roots) BrowserRoots.Add(root);
    }

    /// <summary>
    /// The sort strip's leaf order. LINQ's OrderBy is stable, so equal keys keep
    /// the order the rows arrived in (scan order, or playlist order).
    /// </summary>
    private IComparer<TrackEntry> TrackOrder =>
        new TrackSortComparer(_librarySortField, _librarySortDescending);

    /// <summary>Path layout: folder tree straight from <see cref="TrackEntry.RelativePath"/> (unchanged behaviour).</summary>
    /// <summary>Create a leaf node and remember it for the next rebuild's detach pass.</summary>
    private BrowserNode Leaf(TrackEntry track)
    {
        var node = BrowserNode.FromTrack(track, TrackKey(track.RelativeKey));
        _treeLeaves.Add(node);
        return node;
    }

    private void BuildPathTree(List<TrackEntry> tracks)
    {
        using var _scope = PerfTrace.Measure("tree.build-path");
        var folders = new Dictionary<string, BrowserNode>(StringComparer.OrdinalIgnoreCase);

        // The tree is built *detached* and attached in one step at the end.
        // While a node is detached its Children collection has no WPF listener,
        // so 5 000 row adds cost a list append each instead of 5 000
        // CollectionChanged notifications the TreeView has to answer. Only the
        // roots (tens, not thousands) are announced to the bound collection.
        var roots = new List<BrowserNode>();

        foreach (var track in tracks)
        {
            var segments = track.RelativePath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Walk/create folder nodes for all segments except the last (which is the file).
            var current = "";
            BrowserNode? parent = null;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                current = current.Length == 0 ? seg : current + "/" + seg;
                if (!folders.TryGetValue(current, out var folder))
                {
                    folder = new BrowserNode(seg) { Key = "path:" + current.ToLowerInvariant() };
                    folders[current] = folder;
                    (parent is null ? roots : (IList<BrowserNode>)parent.Children).Add(folder);
                }
                parent = folder;
            }

            // Leaf node for the file itself.
            var leaf = Leaf(track);
            (parent is null ? roots : (IList<BrowserNode>)parent.Children).Add(leaf);
        }

        // Ordering happens while the forest is still detached: no TreeView answers
        // a single move, and the roots are announced once below as before.
        // The roots are siblings too, and SortPathForest only orders each node's
        // children, so they are ordered here with the same comparer (stable, so
        // equal keys keep scan order in both directions).
        var orderedRoots = roots.OrderBy(root => root, PathSiblingOrder).ToList();
        roots.Clear();
        roots.AddRange(orderedRoots);
        SortPathForest(roots);
        foreach (var root in roots) BrowserRoots.Add(root);
    }

    /// <summary>
    /// Explorer order at every level: folders before files, names in
    /// case-insensitive natural order ("Album 2" before "Album 10"), and files by
    /// the sort strip (by file name when that strip says "track name", because
    /// the file name is the name Explorer shows). The sort direction applies at
    /// each level, folders included; folders stay above files both ways.
    /// </summary>
    private void SortPathForest(IEnumerable<BrowserNode> nodes)
    {
        var order = PathSiblingOrder;
        foreach (var node in nodes)
        {
            if (!node.IsFolder) continue;
            if (node.Children.Count > 1)
            {
                var ordered = node.Children.OrderBy(child => child, order).ToList();
                node.Children.Clear();
                foreach (var child in ordered) node.Children.Add(child);
            }
            SortPathForest(node.Children);
        }
    }

    private IComparer<BrowserNode> PathSiblingOrder
    {
        get
        {
            // Leaf rows go through the shared comparer, which owns the direction:
            // negating here would lift missing values to the top on descending.
            var tracks = new TrackSortComparer(_librarySortField, _librarySortDescending);
            return Comparer<BrowserNode>.Create((a, b) =>
            {
                if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;

                if (!a.IsFolder && _librarySortField != TrackSortField.TrackName)
                    return tracks.Compare(a.Track!, b.Track!);

                var result = a.IsFolder
                    ? NaturalCompare(a.Name, b.Name)
                    : NaturalCompare(PathLeafName(a.Track), PathLeafName(b.Track));
                return _librarySortDescending ? -result : result;
            });
        }
    }

    /// <summary>Last segment of the relative path: the file name Explorer would show.</summary>
    private static string PathLeafName(TrackEntry? track)
    {
        if (track is null) return "";
        var path = track.RelativePath.Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// Case-insensitive natural order: a run of digits compares as a number, so
    /// "Album 2" sorts before "Album 10" the way Explorer does.
    /// </summary>
    private static int NaturalCompare(string? x, string? y)
    {
        x ??= "";
        y ??= "";

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var digitsX = x.AsSpan(startX, i - startX).TrimStart('0');
                var digitsY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (digitsX.Length != digitsY.Length) return digitsX.Length - digitsY.Length;
                var byDigits = digitsX.SequenceCompareTo(digitsY);
                if (byDigits != 0) return byDigits;
                continue;
            }

            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0) return byChar;
            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }

    /// <summary>Post-order leaf count so folder rows can show "12 tracks".</summary>
    private static int CountTracks(BrowserNode node)
    {
        if (!node.IsFolder) return 1;
        var count = 0;
        foreach (var child in node.Children)
            count += CountTracks(child);
        node.TrackCount = count;
        return count;
    }

    private static void CollectExpanded(BrowserNode node, HashSet<string> expanded)
    {
        if (!node.IsFolder) return;
        if (node.IsExpanded && node.Key.Length > 0)
            expanded.Add(node.Key);
        foreach (var child in node.Children)
            CollectExpanded(child, expanded);
    }

    private static void RestoreExpanded(BrowserNode node, HashSet<string> expanded)
    {
        if (!node.IsFolder) return;
        if (node.Key.Length > 0 && expanded.Contains(node.Key))
            node.IsExpanded = true;
        foreach (var child in node.Children)
            RestoreExpanded(child, expanded);
    }

    /// <summary>Find a node by key and expand the folders leading to it.</summary>
    private static BrowserNode? FindByKey(IEnumerable<BrowserNode> nodes, string key)
    {
        foreach (var node in nodes)
        {
            if (node.Key == key) return node;
            var hit = FindByKey(node.Children, key);
            if (hit == null) continue;
            if (node.IsFolder) node.IsExpanded = true;
            return hit;
        }
        return null;
    }

    // ---- smart loading -----------------------------------------------------

    [RelayCommand]
    private void LoadSelectedTrack()
    {
        if (SelectedTrack == null) return;
        LoadTrack(SelectedTrack);
    }

    /// <summary>Load a track from the tree view (right-click or double-click).</summary>
    [RelayCommand]
    private void LoadSelectedTreeTrack()
    {
        if (SelectedTreeNode?.Track is { } track)
            LoadTrack(track);
    }

    /// <summary>
    /// Load a whole track onto exactly one deck: the original music file as the
    /// deck's primary stem plus, when the track has a karaoke partner, the
    /// karaoke version as the sub-stem inside the same deck. The other deck's
    /// media is left alone - it may be the track being mixed out of.
    ///
    /// Without an explicit target the free deck is used while the active deck is
    /// playing (queueing the next track without cutting the current one).
    ///
    /// <paramref name="activate"/> is what hands ACTIVE (and with it the shared
    /// transport strip) to the target deck. It defaults to true for a load the
    /// user asked for, but the arming path passes false: pre-loading the deck for
    /// a planned transition must not take the controls away from the deck that is
    /// actually being listened to. There <see cref="OnTransitionCompleted"/>'s
    /// <c>ActivateDeck(arrived)</c> promotes the incoming deck at the fade.
    /// </summary>
    private void LoadTrack(TrackEntry track, bool fromQueue = false, PlaybackDeck? targetDeck = null,
                           bool activate = true)
    {
        // The karaoke pair's one release point: the pair path never comes through
        // here, so every ordinary load ends the pair by construction.
        ReleaseKaraokePair();

        if (!fromQueue)
        {
            // Played outside the queue: leave the queue's rows alone, but
            // nothing in it is "current" any more.
            SetCurrentQueueIndex(-1);
            MarkQueueState();
        }

        var deck = targetDeck
            ?? (_transport.IsDeckPlaying(_activeDeck) ? _activeDeck.Opposite() : _activeDeck);
        var target = deck == PlaybackDeck.A ? DeckA : DeckB;

        // The original file is always the deck's primary stem; the karaoke file
        // only exists as its sub-stem (a karaoke-only track has no sub-stem).
        var musicPath = track.MusicPath ?? track.KaraokePath;
        var karaokePath = track.MusicPath != null ? track.KaraokePath : null;

        try
        {
            target.SetTrack(musicPath, karaokePath, track.Title, track.Artist);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load track: {ex.Message}";
            return;
        }

        if (activate)
        {
            _activeDeck = deck;
            _transport.ActivateDeck(deck);
            SyncDeckActivation();
        }
        // !activate: pre-load for a planned transition. The deck is prepared but
        // the user is still listening to the other one, so ACTIVE stays where it
        // is (OnTransitionCompleted promotes this deck when the fade lands).

        LoadedTrack = track;
        LoadedTrackTitle = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title : $"{track.Artist} \u2014 {track.Title}";
        RefreshDurations();

        // The store owns this track's lyric metadata: record its identity (so a
        // moved file can be re-matched later) and swap in its sections before
        // the header reads the offset, so the scope is right from the first tick.
        _trackMeta.SetTrackInfo(track.RelativeKey, track.Title, track.Artist);
        RefreshTrackSections();
        _editSectionIndex = -1;
        // -2: "not resolved for this track yet", so the first tick reads the
        // playhead's section even if it is already mid-track (the transport may
        // still be settling, so it is not trusted here).
        _playheadSectionIndex = -2;

        // Point the lyrics header at the new track: its persisted offset and
        // its pinned source (Auto when the track never had one).
        var lyricsSource = StoredLyricsSource(track.RelativeKey);
        SyncLyricsOffsetEditor();
        SetLyricsSourceSelection(LyricsSourceNameForId(lyricsSource));
        IsLyricsSourcePickerOpen = false;
        LyricsCandidates.Clear();

        _ = FetchLyricsAsync(track, lyricsSource);
        _ = GenerateWaveformsAsync(target, target.WaveformGeneration, musicPath, karaokePath);

        // BPM/beat grid for what is now on this deck: cached grids appear at
        // once, unknown ones are queued and arrive through OnBeatAnalysed.
        AttachBeatGrid(target, musicPath);
    }

    private async Task GenerateWaveformsAsync(DeckViewModel deck, int generation,
                                               string? musicPath, string? karaokePath)
    {
        var musicTask = Task.Run(() => WaveformGenerator.Generate(musicPath));
        var karaokeTask = Task.Run(() => WaveformGenerator.Generate(karaokePath));
        await Task.WhenAll(musicTask, karaokeTask);
        deck.SetWaveforms(generation, musicTask.Result, karaokeTask.Result);
    }

    // ---- playback queue -----------------------------------------------------

    /// <summary>
    /// What plays after the current track, in order. Rows stay in the list once
    /// played (greyed in the UI); the queue is persisted across runs.
    /// </summary>
    public ObservableCollection<PlaybackQueueItem> Queue { get; } = new();

    /// <summary>Index of the queue row playing now; -1 = playing outside the queue.</summary>
    [ObservableProperty] private int _currentIndex = -1;

    [ObservableProperty] private int _queueCount;

    /// <summary>Rows not played yet, for the tab header and the empty hint.</summary>
    [ObservableProperty] private int _queueRemainingCount;

    [ObservableProperty] private bool _isQueueEmpty = true;

    [ObservableProperty] private PlaybackQueueItem? _selectedQueueItem;

    /// <summary>When on, the queue starts over instead of stopping at the end.</summary>
    [ObservableProperty] private bool _repeatQueue;

    public string QueueHeader => QueueCount > 0 ? $"QUEUED ({QueueCount})" : "QUEUED";

    public string QueueStatusText => QueueCount == 0
        ? "The queue is empty \u2014 right-click a track and choose \u201cAdd to queue\u201d."
        : QueueRemainingCount == 0
            ? "Every queued track has played."
            : $"{QueueRemainingCount} of {Queue.Count} still to play";

    partial void OnQueueCountChanged(int value)
    {
        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(QueueStatusText));
    }

    partial void OnQueueRemainingCountChanged(int value) => OnPropertyChanged(nameof(QueueStatusText));

    partial void OnSelectedTrackChanged(TrackEntry? value)
    {
        AddToQueueCommand.NotifyCanExecuteChanged();
        NotifyMediaCommandStates();
    }

    partial void OnSelectedTreeNodeChanged(BrowserNode? value)
    {
        AddToQueueCommand.NotifyCanExecuteChanged();
        PlayKaraokeSelectedCommand.NotifyCanExecuteChanged();
        QueueKaraokeSelectedCommand.NotifyCanExecuteChanged();
        NotifyMediaCommandStates();
        RefreshTrackMenuVariants();
        if (value == null) return; // tree rebuild / filtered out: keep the key for restore
        _lastSelectedTrackKey = value.Track is { } track
            ? TrackKey(track.RelativeKey)
            : ""; // a folder row carries no track to restore
    }

    /// <summary>Add the library selection (tree row or list row) to the end of the queue.</summary>
    [RelayCommand(CanExecute = nameof(CanAddToQueue))]
    private void AddToQueue()
    {
        var track = SelectedTreeNode?.Track ?? SelectedTrack;
        if (track != null)
        {
            Enqueue(PlaybackQueueItem.FromTrack(track, AssignDeckForNewRow()));
            return;
        }

        // A folder row (or the playlist itself) while a playlist is open: queue
        // what the playlist currently shows. The shared menu carries one "Add to
        // Queue" item, so this is the playlist half of the same gesture.
        if (CanQueueActivePlaylist())
        {
            QueueActivePlaylist();
            return;
        }

        StatusText = "Select a track to queue.";
    }

    /// <summary>
    /// Queue the active playlist exactly as the browser shows it: the frozen
    /// membership narrowed by the current search, in tree (sort) order. Reading
    /// the built leaves, rather than the playlist store, means a filter or a
    /// sort both apply and nothing hidden is queued.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanQueueActivePlaylist))]
    private void QueueActivePlaylist()
    {
        if (_activePlaylist is null)
        {
            StatusText = "Open a playlist to queue it.";
            return;
        }

        var rows = VisibleTracks();
        if (rows.Count == 0)
        {
            StatusText = $"No visible tracks in \u201c{_activePlaylist.Name}\u201d to queue.";
            return;
        }

        EnqueueMany(rows);
        StatusText = $"Queued {rows.Count} track{(rows.Count == 1 ? "" : "s")} from " +
                     $"\u201c{_activePlaylist.Name}\u201d.";
    }

    private bool CanQueueActivePlaylist() => _activePlaylist is not null && _treeLeaves.Count > 0;

    /// <summary>The leaves in tree order: exactly the rows the user can see.</summary>
    private List<TrackEntry> VisibleTracks()
    {
        var rows = new List<TrackEntry>(_treeLeaves.Count);
        Walk(BrowserRoots);
        return rows;

        void Walk(IEnumerable<BrowserNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Track is { } track) rows.Add(track);
                else Walk(node.Children);
            }
        }
    }

    /// <summary>
    /// Append a whole playlist in order as one queue operation: one state pass
    /// and one save, with the duplicate policy of <see cref="Enqueue"/> (an
    /// already-queued row moves to the end instead of appearing twice).
    /// </summary>
    private void EnqueueMany(IReadOnlyList<TrackEntry> tracks)
    {
        var queued = new Dictionary<string, PlaybackQueueItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Queue)
            queued.TryAdd(QueueRowKey(row.Kind, row.Key), row);

        foreach (var track in tracks)
        {
            var item = PlaybackQueueItem.FromTrack(track, AssignDeckForNewRow());
            if (!queued.TryGetValue(QueueRowKey(item.Kind, item.Key), out var existing))
            {
                Queue.Add(item);
                queued[QueueRowKey(item.Kind, item.Key)] = item;
                continue;
            }

            var from = Queue.IndexOf(existing);
            existing.IsPlayed = false;
            if (!existing.IsCurrent) existing.DeckTarget = item.DeckTarget;
            Queue.Move(from, Queue.Count - 1);
            if (CurrentIndex == from) SetCurrentQueueIndex(Queue.Count - 1);
            else if (CurrentIndex > from) SetCurrentQueueIndex(CurrentIndex - 1);
        }

        MarkQueueState();
        SaveQueue();
    }

    private static string QueueRowKey(string kind, string key) => kind + "\u0000" + key;

    /// <summary>
    /// Deck for a freshly queued row. Rotation reads the live transport, never a
    /// cached guess: while a deck is playing the new row goes to the other one,
    /// so the current track is never pulled out from under itself. With nothing
    /// playing the first row takes A and later rows alternate from the last
    /// pick (A, B, A, B, A), which keeps a batch spread over both decks.
    /// </summary>
    private PlaybackDeck AssignDeckForNewRow()
    {
        var deck = _transport.AnyPlaying
            ? _activeDeck.Opposite()
            : _lastAssignedDeck is { } last ? last.Opposite() : PlaybackDeck.A;

        _lastAssignedDeck = deck;
        return deck;
    }

    // ---- queue contract: enqueueing is not playback --------------------------
    //
    // Enqueue - and therefore AddToQueue and AddSourceToQueue - is additive and
    // silent: it never touches the transport, so queueing a track neither starts
    // it nor crossfades. A queued row is the "armed next" row purely by sitting
    // unplayed after the current one; it is brought in only when the track that
    // is playing now reaches its end:
    //
    //     OnUiTick -> ReachedEnd -> AdvanceQueueAfterEndAsync
    //              -> StartQueueItemAsync(autoAdvance: true) -> StartDeckAfterLoad
    //
    // Playback still starts on user intent only: the row's Play command
    // (PlayNowFromQueue), PlayNext / PlayPrevious, or that end-of-track advance.
    // The advance keeps the existing 2 s CrossfadeToDeck hand-off, so the mix
    // stays continuous until the real transition engine lands.
    //
    // There is deliberately no separate "armed" flag: CurrentIndex plus the rows'
    // IsPlayed already say what is playing and what comes next, which is what
    // PlayNext / PlayPrevious / SaveQueue / RestorePlaybackQueue round-trip.

    private void Enqueue(PlaybackQueueItem item)
    {
        var existing = Queue.FirstOrDefault(q =>
            string.Equals(q.Key, item.Key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(q.Kind, item.Kind, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // Already queued: move it to the end instead of duplicating the row,
            // and take the deck the rotation just handed out.
            var from = Queue.IndexOf(existing);
            existing.IsPlayed = false;
            // The running row's audio came from its deck; only a row that has not
            // started yet takes the deck the rotation just handed out.
            if (!existing.IsCurrent) existing.DeckTarget = item.DeckTarget;
            Queue.Move(from, Queue.Count - 1);
            if (CurrentIndex == from) SetCurrentQueueIndex(Queue.Count - 1);
            else if (CurrentIndex > from) SetCurrentQueueIndex(CurrentIndex - 1);
            MarkQueueState();
            StatusText = $"Moved \u201c{existing.Title}\u201d to the end of the queue " +
                         $"(deck {existing.DeckText}).";
            SaveQueue();
            return;
        }

        Queue.Add(item);
        MarkQueueState();
        // Queued, not started: the row plays when the current track ends (see the
        // queue contract above), so the status line says so instead of implying
        // it is already playing.
        StatusText = _transport.AnyPlaying && CurrentIndex >= 0
            ? $"Queued \u201c{item.Title}\u201d for deck {item.DeckText} \u2014 it starts when the current track ends."
            : $"Queued \u201c{item.Title}\u201d for deck {item.DeckText}.";
        SaveQueue();
    }

    /// <summary>Flip a row between deck A and deck B. The running row keeps its deck.</summary>
    [RelayCommand(CanExecute = nameof(CanRetargetQueueItem))]
    private void FlipQueueDeck(PlaybackQueueItem? item)
    {
        if (item == null) return;
        RetargetQueueItem(item, item.DeckTarget.Opposite());
    }

    [RelayCommand(CanExecute = nameof(CanRetargetQueueItem))]
    private void SendToDeckA(PlaybackQueueItem? item) => RetargetQueueItem(item, PlaybackDeck.A);

    [RelayCommand(CanExecute = nameof(CanRetargetQueueItem))]
    private void SendToDeckB(PlaybackQueueItem? item) => RetargetQueueItem(item, PlaybackDeck.B);

    /// <summary>
    /// Route a queued row to a deck and persist it. The row playing now is
    /// excluded (see <see cref="CanRetargetQueueItem"/>): its badge describes the
    /// audio that is already on a deck.
    /// </summary>
    private void RetargetQueueItem(PlaybackQueueItem? item, PlaybackDeck deck)
    {
        if (item == null || item.DeckTarget == deck) return;

        item.DeckTarget = deck;
        StatusText = $"\u201c{item.Title}\u201d will play on deck {item.DeckText}.";
        SaveQueue();
    }

    [RelayCommand(CanExecute = nameof(CanPlayNowFromQueue))]
    private async Task PlayNowFromQueue(PlaybackQueueItem? item)
    {
        if (item == null) return;

        var index = Queue.IndexOf(item);
        if (index < 0) return;

        if (item.IsLibrary && item.Track == null)
        {
            StatusText = $"\u201c{item.Title}\u201d is no longer in the library.";
            return;
        }

        item.IsPlayed = false;
        SetCurrentQueueIndex(index);
        MarkQueueState();
        await StartQueueItemAsync(item);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFromQueue))]
    private void RemoveFromQueue(PlaybackQueueItem? item)
    {
        if (item == null) return;

        var index = Queue.IndexOf(item);
        if (index < 0) return;

        Queue.RemoveAt(index);
        if (CurrentIndex == index) SetCurrentQueueIndex(-1);
        else if (CurrentIndex > index) SetCurrentQueueIndex(CurrentIndex - 1);
        MarkQueueState();
        SaveQueue();
    }

    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private void ClearQueue()
    {
        Queue.Clear();
        SetCurrentQueueIndex(-1);
        MarkQueueState();
        StatusText = "Queue cleared.";
        SaveQueue();
    }

    /// <summary>Drop finished rows; the row playing now is never played, so it stays.</summary>
    [RelayCommand(CanExecute = nameof(CanClearPlayed))]
    private void ClearPlayed()
    {
        for (var i = Queue.Count - 1; i >= 0; i--)
        {
            if (!Queue[i].IsPlayed || i == CurrentIndex) continue;
            if (i < CurrentIndex) CurrentIndex--;
            Queue.RemoveAt(i);
        }

        MarkQueueState();
        SaveQueue();
    }

    [RelayCommand(CanExecute = nameof(CanPlayNext))]
    private async Task PlayNext()
    {
        var next = FindNextUnplayed(CurrentIndex + 1);
        if (next == null)
        {
            StatusText = RepeatQueue
                ? "Nothing left to play; Repeat is on, so the next automatic advance starts over."
                : "No more tracks in the queue.";
            return;
        }

        await PlayNowFromQueue(next);
    }

    [RelayCommand(CanExecute = nameof(CanPlayPrevious))]
    private async Task PlayPrevious()
    {
        // Leaving a row backwards must not leave it greyed as finished.
        if (CurrentIndex >= 0 && CurrentIndex < Queue.Count)
            Queue[CurrentIndex].IsPlayed = false;

        var start = CurrentIndex < 0 ? Queue.Count - 1 : CurrentIndex - 1;
        PlaybackQueueItem? target = null;
        for (var i = start; i >= 0; i--)
        {
            if (Queue[i].IsPlayed) continue;
            target = Queue[i];
            break;
        }

        if (target == null && start >= 0)
        {
            // Everything before it already played: still let the user rewind one
            // row rather than dead-ending on a greyed list.
            target = Queue[start];
        }

        if (target != null)
        {
            await PlayNowFromQueue(target);
            return;
        }

        MarkQueueState();
        StatusText = "Nothing earlier in the queue.";
    }

    [RelayCommand]
    private void ToggleRepeatQueue() => RepeatQueue = !RepeatQueue;

    /// <summary>
    /// Queue a remote (Spotify / YouTube) track. The source tabs own their own
    /// selection, so they call this - the library tab uses <see cref="AddToQueue"/>.
    /// </summary>
    public void AddSourceToQueue(SourceTrack source)
    {
        if (source == null) return;
        if (string.IsNullOrWhiteSpace(source.Id) && string.IsNullOrWhiteSpace(source.Title)) return;

        Enqueue(PlaybackQueueItem.FromSource(source, AssignDeckForNewRow()));
    }

    /// <summary>
    /// Library file for a remote track id, for <see cref="PlaylistStore"/>.
    ///
    /// The store only hands over the id. The download ledger records exactly
    /// the ids this app fetched, so it is the first (and normal) answer; a file
    /// the app never downloaded is matched through the scanned library, which
    /// needs the row's metadata, hence the lookup in the store's own playlists.
    /// Only paths that still exist are returned.
    /// </summary>
    private string? ResolveSourceLibraryPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var key = id.Trim();

        var entry = _ledger.TryGet(key);
        if (entry is { Path: { Length: > 0 } ledgerPath })
        {
            // Warm path the library scan already proved: no stat. A 5 000-row
            // playlist hits this for most rows, which is the difference between
            // a load that probes the disk 5 000 times and one that does not.
            if (_libraryIndex.ContainsPath(ledgerPath)) return ledgerPath;

            // Miss: the file may be newer than the last scan (a fresh download
            // lands here), so only then pay for the filesystem answer.
            if (PathExistsNoThrow(ledgerPath)) return ledgerPath;
        }

        var track = FindSourceTrackById(key);
        return track == null ? null : _libraryIndex.Resolve(track);
    }

    /// <summary>File.Exists that swallows the "invalid path" case instead of throwing.</summary>
    private static bool PathExistsNoThrow(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private SourceTrack? FindSourceTrackById(string id) =>
        EnsureSourceTrackIndex().TryGetValue(id, out var match) ? match : null;

    /// <summary>
    /// The store's own rows keyed by id, built once per library change (the
    /// store's Changed handler clears it). Every per-row availability probe goes
    /// through this, so a playlist load is a dictionary lookup per row instead of
    /// a walk over every playlist.
    /// </summary>
    private Dictionary<string, SourceTrack> EnsureSourceTrackIndex()
    {
        if (_sourceTracksById is { } cached) return cached;

        var byId = new Dictionary<string, SourceTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in _playlistStore.Playlists)
        {
            foreach (var track in playlist.Tracks)
            {
                if (!string.IsNullOrWhiteSpace(track.Id)) byId.TryAdd(track.Id.Trim(), track);
            }
        }

        _sourceTracksById = byId;
        return byId;
    }

    /// <summary>
    /// Re-derive every remote row's state from what is actually on disk: the
    /// playlist store's rows (YouTube's playlist) and the source tabs' own row
    /// objects (Spotify keeps its fetched track objects). Run on the UI thread.
    /// </summary>
    private void RefreshSourceLibraryStates()
    {
        _sourceTracksById = null; // playlists may have changed since the last pass
        _playlistStore.SanitizeStates();
        Spotify.ReconcileFromLibrary();
        YouTube.ReconcileFromLibrary();
        // The karaoke column is the scan index's view of the same rows: one
        // dictionary hit per row, so this can ride along with every state pass.
        RefreshRemoteLibraryFacts();
    }

    private bool CanAddToQueue() =>
        SelectedTreeNode?.Track != null || SelectedTrack != null || CanQueueActivePlaylist();

    private bool CanPlayNext() => Queue.Count > 0;

    private bool CanPlayPrevious() => Queue.Count > 0;

    private bool CanClearQueue() => Queue.Count > 0;

    private bool CanClearPlayed() => Queue.Any(item => item.IsPlayed);

    private bool CanRemoveFromQueue(PlaybackQueueItem? item) => item != null && Queue.Contains(item);

    /// <summary>
    /// Deck changes are allowed for rows that have not started: the running
    /// row's badge describes audio already loaded on a deck.
    /// </summary>
    private bool CanRetargetQueueItem(PlaybackQueueItem? item) =>
        item != null && !item.IsCurrent && Queue.Contains(item);

    private bool CanPlayNowFromQueue(PlaybackQueueItem? item) => item != null && Queue.Contains(item);

    private PlaybackQueueItem? FindNextUnplayed(int start)
    {
        for (var i = Math.Max(0, start); i < Queue.Count; i++)
            if (!Queue[i].IsPlayed) return Queue[i];
        return null;
    }

    /// <summary>Refresh row numbers, the current flag and the header counts.</summary>
    private void MarkQueueState()
    {
        for (var i = 0; i < Queue.Count; i++)
        {
            Queue[i].Index = i + 1;
            Queue[i].IsCurrent = i == CurrentIndex;
        }

        QueueCount = Queue.Count;
        QueueRemainingCount = Queue.Count(item => !item.IsPlayed);
        IsQueueEmpty = Queue.Count == 0;

        PlayNextCommand.NotifyCanExecuteChanged();
        PlayPreviousCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
        ClearPlayedCommand.NotifyCanExecuteChanged();
        RemoveFromQueueCommand.NotifyCanExecuteChanged();
        PlayNowFromQueueCommand.NotifyCanExecuteChanged();
        FlipQueueDeckCommand.NotifyCanExecuteChanged();
        SendToDeckACommand.NotifyCanExecuteChanged();
        SendToDeckBCommand.NotifyCanExecuteChanged();
    }

    private void SetCurrentQueueIndex(int index)
    {
        CurrentIndex = index;
        for (var i = 0; i < Queue.Count; i++)
            Queue[i].IsCurrent = i == index;
    }

    /// <summary>
    /// Start a queue row: library rows load directly, remote rows resolve first.
    /// Auto-advance hands the track to the deck that is not currently audible
    /// (and rewrites the row's deck target, so the persisted queue stays honest);
    /// a manual Next/Prev/Play now keeps the row's own target.
    /// This is the only place a queue row starts playing, and it is reached from
    /// user intent (PlayNowFromQueue, Next/Prev) or from the end-of-track advance
    /// (AdvanceQueueAfterEndAsync) - never from the enqueue path.
    /// </summary>
    private async Task StartQueueItemAsync(PlaybackQueueItem item, bool autoAdvance = false)
    {
        // A pair's original row never starts on its own - it is the second deck of
        // the pair - so a request for it starts the pair instead.
        if (item.PairRole == KaraokePairRole.Original)
        {
            var lead = Queue.FirstOrDefault(q => q.PairRole == KaraokePairRole.Karaoke
                && string.Equals(q.PairKey, item.PairKey, StringComparison.OrdinalIgnoreCase));
            if (lead != null) item = lead;
        }

        if (item.PairRole == KaraokePairRole.Karaoke && item.Track is { } pairTrack)
        {
            // One unit: both rows are spent when the pair starts. The pair loads
            // through the deck path, not LoadTrack, so its lock survives and the
            // original row is never started separately.
            if (item.PairKey is { } key)
                foreach (var mate in Queue.Where(q =>
                             string.Equals(q.PairKey, key, StringComparison.OrdinalIgnoreCase)))
                    mate.IsPlayed = true;

            LoadKaraokePair(pairTrack);
            SetCurrentQueueIndex(Queue.IndexOf(item));
            MarkQueueState();
            SaveQueue();
            return;
        }

        var deck = item.DeckTarget;
        if (autoAdvance && _transport.IsDeckPlaying(deck))
        {
            deck = deck.Opposite();
            item.DeckTarget = deck;
            SaveQueue();
        }

        if (item.Track != null)
        {
            LoadTrack(item.Track, fromQueue: true, targetDeck: deck);
        }
        else if (item.Source != null)
        {
            if (!await PlaySourceTrackAsync(item.Source, fromQueue: true, targetDeck: deck,
                                            startPlayback: false))
                return;
        }
        else
        {
            return;
        }

        StartDeckAfterLoad(deck, fromQueue: true, item: item);
    }

    /// <summary>
    /// Start a freshly loaded deck and, when the other deck is still running,
    /// hand the mix over with the default 2 s equal-power crossfade instead of a cut.
    /// </summary>
    private void StartDeckAfterLoad(PlaybackDeck deck, bool fromQueue = false, PlaybackQueueItem? item = null)
    {
        // autoCue: false - the hand-off below owns the fader, so PlayDeck must
        // not snap it first.
        _transport.PlayDeck(deck, autoCue: false);
        _transport.ActivateDeck(deck);
        SyncDeckActivation();

        // Both decks can be live at once. If the other deck is still playing,
        // hand the mix over with the default 2 s equal-power crossfade instead
        // of a cut; if it is only parked/loaded there is nothing to fade out, so
        // snap the fader so the deck that just started is audible.
        var other = deck.Opposite();
        if (_transport.IsDeckPlaying(other))
        {
            _transport.CrossfadeToDeck(deck);
        }
        else
        {
            var side = deck == PlaybackDeck.A ? 0.0 : 1.0;
            if (Math.Abs(_transport.Crossfader - side) > 0.001)
                _transport.SetCrossfader(side);
        }

        SyncCrossfaderFromTransport();

        // Queue rows arm the next row on the idle deck so the fade ends with this
        // track instead of starting after it. Non-queue playback keeps the 1.x
        // behaviour (a 2 s fade when a deck is already running).
        if (fromQueue && item != null)
            _ = ArmNextQueueItemAsync(deck);
    }

    /// <summary>
    /// The transport finished a track that came from the queue: grey the row,
    /// then start the next unplayed one (or stop at the end). The successor
    /// loads on its own target deck, so the deck that just finished is the one
    /// that gets reused when the rotation comes back to it.
    /// </summary>
    private async Task AdvanceQueueAfterEndAsync()
    {
        try
        {
            if (CurrentIndex >= 0 && CurrentIndex < Queue.Count)
                Queue[CurrentIndex].IsPlayed = true;

            var next = FindNextUnplayed(CurrentIndex + 1);
            if (next == null && RepeatQueue && Queue.Count > 0)
            {
                foreach (var item in Queue) item.IsPlayed = false;
                next = FindNextUnplayed(0);
            }

            if (next == null)
            {
                SetCurrentQueueIndex(-1);
                MarkQueueState();
                StatusText = "End of queue.";
                SaveQueue();
                return;
            }

            SetCurrentQueueIndex(Queue.IndexOf(next));
            MarkQueueState();
            SaveQueue();
            await StartQueueItemAsync(next, autoAdvance: true);
        }
        catch (Exception ex)
        {
            Log.Error("Queue auto-advance failed", ex);
        }
    }

    // ---- queue persistence ---------------------------------------------------

    private void SaveQueue()
    {
        if (_restoringQueue) return;

        _queueStore.Save(Queue.Select(item => new PlaybackQueueRecord
        {
            Kind = item.Kind,
            Key = item.Key,
            Title = item.Title,
            Artist = item.Artist,
            Played = item.IsPlayed,
            Deck = item.DeckTarget.ToPersistedString(),
            Pair = item.PairKey,
            Role = PlaybackQueueItem.PersistPairRole(item.PairRole),
        }));
    }

    /// <summary>
    /// Restore the saved queue once the library index is warm. Only library rows
    /// whose file still resolves come back; anything else is dropped silently
    /// (a remote row would have to re-download to be playable).
    /// </summary>
    private void RestorePlaybackQueue()
    {
        if (_queueRestored) return;
        _queueRestored = true;

        var records = _queueStore.Load();
        if (records.Count == 0) return;

        _restoringQueue = true;
        try
        {
            foreach (var record in records)
            {
                if (!string.Equals(record.Kind, PlaybackQueueItem.LibraryKind,
                        StringComparison.OrdinalIgnoreCase))
                    continue; // remote rows are not restored (see summary)

                var track = ResolveLibraryTrack(record.Key);
                if (track == null) continue; // file gone - drop it

                Queue.Add(PlaybackQueueItem.FromRecord(record, track));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore the playback queue: {ex.Message}");
        }
        finally
        {
            _restoringQueue = false;
        }

        // A pair whose mate did not come back (its file is gone) is a normal row.
        foreach (var item in Queue.Where(i => i.PairKey != null).ToList())
        {
            if (!Queue.Any(other => !ReferenceEquals(other, item) &&
                    string.Equals(other.PairKey, item.PairKey, StringComparison.OrdinalIgnoreCase)))
                item.DropPair();
        }

        SetCurrentQueueIndex(-1);
        MarkQueueState();

        // Resume the rotation where the restored rows left off, so a new addition
        // after a restart alternates instead of colliding with the restored tail.
        _lastAssignedDeck = Queue.Count > 0 ? Queue[^1].DeckTarget : null;
    }

    private TrackEntry? ResolveLibraryTrack(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        foreach (var track in Tracks)
            if (string.Equals(track.RelativeKey, key, StringComparison.OrdinalIgnoreCase))
                return track;

        return null;
    }

    // ---- remote playback (Spotify / YouTube) --------------------------------

    /// <summary>
    /// Play a remote track: resolve it to a local file (library copy, ledger
    /// entry, warm cache, or a fresh fetch into the cache), then load it onto a
    /// deck exactly like a library file so delay/routing/lyrics apply.
    /// Returns false when nothing could be resolved.
    /// </summary>
    public async Task<bool> PlaySourceTrackAsync(SourceTrack source, bool fromQueue = false,
                                                  PlaybackDeck? targetDeck = null,
                                                  bool startPlayback = true, bool activate = true)
    {
        try
        {
            StatusText = $"Resolving {source.DisplayName}\u2026";
            var path = await _streamResolver.ResolveAsync(source, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = $"Could not play {source.DisplayName}.";
                return false;
            }

            var entry = new TrackEntry
            {
                RelativeKey = Path.GetFileNameWithoutExtension(path),
                RelativePath = DownloadPaths.RelativeTo(_settings, path),
                MusicPath = path,
                Title = string.IsNullOrWhiteSpace(source.Title)
                    ? Path.GetFileNameWithoutExtension(path)
                    : source.Title,
                Artist = source.Artist,
                Album = source.Album,
                Duration = source.Duration,
            };

            LoadTrack(entry, fromQueue, targetDeck, activate);
            if (startPlayback)
            {
                _transport.Play();
                if (_transport.IsDeckPlaying(_transport.ActiveDeck.Opposite()))
                    _transport.CrossfadeToDeck(_transport.ActiveDeck);
            }
            StatusText = $"Playing {source.DisplayName}";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Remote playback failed for {source.DisplayName}", ex);
            StatusText = $"Playback failed: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private void ShowDownloads() => DownloadsRequested?.Invoke();

    /// <summary>Coalesce several library-change notifications into one rescan.</summary>
    private void ScheduleRescan()
    {
        _rescanDebounce.Stop();
        _rescanDebounce.Start();
    }

    /// <summary>
    /// After a permanent download: remember it for the optional karaoke chain,
    /// and surface a failure in the status bar.
    /// </summary>
    private void OnDownloadFinished(DownloadItem item)
    {
        if (item.Kind != DownloadKind.Library) return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (item.State == DownloadState.Failed)
            {
                StatusText = $"Download failed: {item.Track.DisplayName} \u2014 {item.Error}";
                return;
            }

            if (item.State != DownloadState.Done || item.OutputPath == null) return;

            // The file just joined the library: membership resolved before it
            // existed is stale even if the scan has not finished yet.
            BumpLibraryGeneration("download completed");

            if (_settings.DownloadAutoKaraoke)
                _pendingAutoKaraoke.Add(item.OutputPath);
            else
                StatusText = $"Downloaded {item.Track.DisplayName}";
        });
    }

    /// <summary>Queue karaoke generation for any freshly downloaded tracks.</summary>
    private void ApplyPendingAutoKaraoke()
    {
        if (_pendingAutoKaraoke.Count == 0) return;

        var wanted = _pendingAutoKaraoke.ToList();
        _pendingAutoKaraoke.Clear();
        var queued = 0;
        foreach (var track in Tracks)
        {
            if (track.MusicPath == null || !wanted.Contains(track.MusicPath)) continue;
            if (!track.NeedsKaraoke || !SeparationEngine.IsInstalled) continue;
            if (_separation.Enqueue(track, KaraokeStemMode.Instrumental)) queued++;
        }

        Log.Info($"Auto-karaoke: queued {queued} of {wanted.Count} downloaded track(s)");
        if (queued > 0)
            StatusText = $"Queued karaoke generation for {queued} downloaded track(s).";
    }

    // ---- karaoke generation (vocal removal) --------------------------------

    // Each render mode is its own command, and each reads the surface's
    // selection. The shared TrackMenu sends no CommandParameter, and a binding
    // that quietly yields null is exactly how the old RelayCommand<object?>
    // wrapper here came to look enabled and do nothing. Selection is the
    // contract Play already follows: the right-click handler selects the row
    // before the menu opens, so a click always has a target.

    /// <summary>Render <paramref name="track"/> with an explicit stem mode.</summary>
    public void MakeKaraoke(TrackEntry track, KaraokeStemMode mode) => EnqueueKaraoke(track, mode);

    // The tree row wins, then the flat-list row: the same resolution "Add to
    // Queue" uses, because a library click arrives as a tree selection and
    // SelectedTrack alone is null there.
    private TrackEntry? KaraokeTarget => SelectedTreeNode?.Track ?? SelectedTrack;

    [RelayCommand]
    private void MakeKaraokeInstrumental() => EnqueueKaraoke(KaraokeTarget, KaraokeStemMode.Instrumental);

    [RelayCommand]
    private void MakeKaraokeKeepBacking() => EnqueueKaraoke(KaraokeTarget, KaraokeStemMode.InstrumentalKeepBacking);

    [RelayCommand]
    private void MakeKaraokeVocalsOnly() => EnqueueKaraoke(KaraokeTarget, KaraokeStemMode.VocalsOnly);

    private void EnqueueKaraoke(TrackEntry? track, KaraokeStemMode mode)
    {
        // Every refusal logs one line naming the track, so a click that does
        // nothing is still visible in the console's LOG list.
        switch (KaraokeGate.Check(track, _settings.KaraokeDirectory, SeparationEngine.IsInstalled, mode))
        {
            case KaraokeDenial.ModeAlreadyExists:
                StatusText = $"“{track!.Title}” already has that karaoke version.";
                return;
            case KaraokeDenial.NoKaraokeFolder:
                StatusText = "Set a Karaoke folder in Settings before generating karaoke tracks.";
                SettingsRequested?.Invoke();
                return;
            case KaraokeDenial.EngineNotInstalled:
                EngineInstallRequested?.Invoke(track!);
                return;
            case KaraokeDenial.Allowed:
                break;
            default:
                return;
        }

        if (_separation.Enqueue(track!, mode))
        {
            var title = string.IsNullOrWhiteSpace(track!.Title) ? track.RelativePath : track.Title;
            var modeNote = mode switch
            {
                KaraokeStemMode.InstrumentalKeepBacking => " (backing vocals kept)",
                KaraokeStemMode.VocalsOnly => " (vocals only)",
                _ => "",
            };
            StatusText = $"Queued karaoke generation: {title}{modeNote}";
        }
    }

    // ---- lyrics ------------------------------------------------------------

    /// <summary>
    /// The pinned search query for a track, or null when the file tags are the
    /// query. Kept in one place so the fetch path and the picker prefill can never
    /// disagree about what "the override" is.
    /// </summary>
    private LyricsService.LyricsQuery? LyricsQueryFor(TrackEntry? track)
    {
        var key = track?.RelativeKey;
        if (key == null) return null;

        var record = _trackMeta.Get(key);
        if (record == null) return null;
        if (string.IsNullOrWhiteSpace(record.LyricsTitle) && string.IsNullOrWhiteSpace(record.LyricsArtist))
            return null;

        return new LyricsService.LyricsQuery(record.LyricsTitle, record.LyricsArtist);
    }

    private async Task FetchLyricsAsync(TrackEntry track, string? preferredSourceId = null)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var ct = _lyricsCts.Token;

        LyricLines.Clear();
        _currentLyricIndex = -1;
        _lyricsSynced = false;
        _lyricsDocument = null;
        _lyricsBaseDocument = null;
        _lyricsMissStatus = null;
        _lyricsApplied = false;
        LyricsStatus = "Fetching lyrics\u2026";

        try
        {
            var lookup = await _lyricsService.GetLyricsAsync(track, ct, preferredSourceId, LyricsQueryFor(track));
            if (ct.IsCancellationRequested) return;

            ApplyLyricsDocument(lookup.Document, lookup.MissStatus);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LyricsStatus = $"Lyrics error: {ex.Message}";
        }
    }

    /// <summary>
    /// Rebuild the rendered lines from a document. Unsynced documents still
    /// render, just without a highlight (the tick skips them).
    /// <para>
    /// The provider's document is kept as-is in <see cref="_lyricsBaseDocument"/> and
    /// the operator's line edits are laid over a copy last, after the word spans the
    /// provider chain produced. Nothing here writes the lyrics cache: that write
    /// happens upstream in <see cref="LyricsService"/>, whose key is
    /// <c>artist|title|seconds|sourceId</c>, so edited text stored there would leak
    /// onto every other file with the same tags and duration.
    /// </para>
    /// </summary>
    private void ApplyLyricsDocument(LyricsDocument? doc, string? missStatus = null)
    {
        _lyricsBaseDocument = doc;
        _lyricsMissStatus = missStatus;
        _lyricsApplied = true;

        var edits = _trackMeta.GetLyricEdits(CurrentLyricsKey);

        LyricLines.Clear();
        _currentLyricIndex = -1;
        // The word spans are rebuilt from the edited text, so the old index points
        // into a word list that no longer exists: reset it here, before the rebuild,
        // or a different word stays lit.
        _currentWordIndex = -1;
        _lyricsSynced = false;
        ClearLyricWordGeometry();
        OnPropertyChanged(nameof(CurrentLyricLine));

        bool fromEditsOnly = false;
        if (doc == null)
        {
            // No provider answered, but the operator's edits still make a sheet.
            // Unsynced on purpose: there are no timestamps to follow.
            doc = LyricEdits.FromEdits(edits);
            fromEditsOnly = doc != null;
        }

        if (doc == null)
        {
            // The section command reads the document's line times, so keep the one
            // behind the rendered lines (cleared when the lines are cleared).
            _lyricsDocument = null;
            // Name the query that was actually searched: the usual reason for a
            // miss is that the file tags belong to a cover, so the text has to say
            // which title/artist went out.
            LyricsStatus = missStatus ?? "No lyrics found for this track.";
            return;
        }

        if (!fromEditsOnly)
        {
            // Instrumental stretches get a marker line before the word spans are
            // built: it becomes the pre-gap line's next line, which is what stops
            // that line's synthetic words stretching across the whole gap. New
            // document, so the provider's copy and the cache stay marker-free.
            doc = InstrumentalGap.Fill(doc);

            // Word-follow needs per-word spans. A document-wide HasWordTimings
            // test would be wrong here: the marker Fill just inserted carries its
            // own glyph words, so it would report true and leave every sung line
            // word-less. Synthesize is per-line instead: lines that already carry
            // real timings (A2 enhanced LRC, providers with word data) are left
            // as they are, markers are skipped, and line-only lines get an even
            // split bounded by the next line, which is the marker where there is
            // one. Unsynced documents stay line-only.
            if (doc.Synced)
                WordTimeline.Synthesize(doc, LoadedTrack?.Duration);

            // Operator edits last, on a copy: line times, offsets and sections stay
            // exactly as resolved, and the cache never sees the new text.
            doc = LyricEdits.Apply(doc, edits, LoadedTrack?.Duration) ?? doc;
        }

        _lyricsDocument = doc;
        foreach (var line in doc.Lines)
            LyricLines.Add(new LyricLineViewModel(line.Time, line.Text, line.Words, line.IsInstrumental));
        _lyricsSynced = doc.Synced;
        LyricsStatus = doc.Synced
            ? $"Synced lyrics \u00b7 {doc.Source}"
            : $"Lyrics (not time-synced) \u00b7 {doc.Source}";
        // A document kept despite overrunning the file (see LyricsService) says so
        // anywhere the operator reads the lyrics status.
        if (!string.IsNullOrWhiteSpace(missStatus)) LyricsStatus += $" \u00b7 {missStatus}";
    }

    // ---- lyrics header (offset + source) ---------------------------------

    /// <summary>Persistence key of the loaded track; null when nothing is loaded.</summary>
    private string? CurrentLyricsKey => LoadedTrack?.RelativeKey;

    partial void OnLyricsOffsetMsChanged(double value)
    {
        LyricsOffsetText = FormatLyricsOffset(value);
        if (_syncingLyricsOffset) return;

        // Inside a saved section the header edits that span only: the store
        // applies it as an override, so the track and global values stay put.
        if (EditSection is not null && CurrentLyricsKey is { } sectionKey)
        {
            _trackMeta.SetSectionOffset(sectionKey, _editSectionIndex, ToOffsetMs(value));
            RefreshTrackSections();
            return;
        }

        var key = CurrentLyricsKey;
        if (key == null)
        {
            // No track: the header edits the global default in settings.
            _settings.LyricsOffsetMs = Math.Clamp(value, -AppSettings.MaxLyricsOffsetMs,
                AppSettings.MaxLyricsOffsetMs);
            ScheduleLyricsSettingsSave();
            return;
        }

        // Outside any section this writes the track's own offset, which is what
        // the label promised; inside one the branch above edited only that span.
        _trackMeta.SetLyricsOffset(key, value);
        RefreshTrackSections();
        SyncLyricsOffsetEditor();
    }

    partial void OnSelectedLyricsSourceChanged(string value)
    {
        if (_syncingLyricsSource) return;

        if (string.IsNullOrEmpty(value))
        {
            // The ComboBox can transiently push null; fall back to the pinned
            // source instead of silently clearing it.
            SetLyricsSourceSelection(LyricsSourceNameForId(StoredLyricsSource(CurrentLyricsKey)));
            return;
        }

        _ = ApplyLyricsSourceAsync(value);
    }

    /// <summary>
    /// Mirror the offset the header edits into the slider and its readout: the
    /// section under the playhead when one is selected, otherwise the track's
    /// own value (which falls back to the global default). The scope label says
    /// which of the three the next nudge will write.
    /// </summary>
    private void SyncLyricsOffsetEditor()
    {
        var key = CurrentLyricsKey;

        double stored;
        string scope;
        if (EditSection is { } section)
        {
            stored = section.OffsetMs;
            scope = "SECTION";
        }
        else if (key != null && _trackMeta.HasOwnOffset(key))
        {
            stored = _trackMeta.GetBaseOffset(key, _settings.LyricsOffsetMs);
            scope = "TRACK";
        }
        else
        {
            stored = _trackMeta.GetBaseOffset(key, _settings.LyricsOffsetMs);
            scope = "GLOBAL";
        }

        _syncingLyricsOffset = true;
        try
        {
            LyricsOffsetScope = scope;
            // The slider covers the whole legal range (+/-MaxLyricsOffsetMs), so
            // the value it shows is the stored one: touching it writes back what
            // the operator actually set, not a clamped stand-in.
            LyricsOffsetMs = stored;
            LyricsOffsetText = FormatLyricsOffset(stored);
        }
        finally
        {
            _syncingLyricsOffset = false;
        }
    }

    private void ScheduleLyricsSettingsSave()
    {
        // Bind the target now: by the time the debounce fires, JOCKEYBOX_HOME
        // may name a different profile (another test, another launch), and the
        // save must land where this edit was made.
        _lyricsSettingsHome ??= AppPaths.Home;
        _lyricsSettingsSave.Stop();
        _lyricsSettingsSave.Start();
    }

    /// <summary>
    /// Run the pending debounce, once. Clears the bound home first, so a second
    /// tick (or one after <see cref="Dispose"/>) has nothing left to write.
    /// </summary>
    private void SaveScheduledLyricsSettings()
    {
        _lyricsSettingsSave.Stop();
        if (_lyricsSettingsHome is not { } home) return;
        _lyricsSettingsHome = null;
        _settings.Save(home);
    }

    private static string FormatLyricsOffset(double ms) =>
        ms.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// The store is written from other places too (importer, settings tooling),
    /// so re-read the loaded track's record when it changes. Marshalled to the
    /// UI thread because the write can come from a background save, and dropped
    /// when it is not the track that is loaded by the time it lands.
    /// </summary>
    private void OnTrackMetadataChanged(object? sender, TrackMetadataChangedEventArgs e)
    {
        if (e.Key.Length == 0) return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var key = CurrentLyricsKey;
            if (key == null) return;
            if (!string.Equals(TrackMetadataStore.NormalizeKey(key), e.Key, StringComparison.Ordinal))
                return;

            RefreshTrackSections();
            if (_editSectionIndex >= _trackSections.Count) _editSectionIndex = -1;
            OnPropertyChanged(nameof(HasSectionAtPlayhead));
            SyncLyricsOffsetEditor();
        }));
    }

    /// <summary>"Auto" for no pin, else the provider's display name.</summary>
    private string LyricsSourceNameForId(string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return AutoLyricsSource;
        var ids = _lyricsService.SourceIds;
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], sourceId, StringComparison.OrdinalIgnoreCase))
                return _lyricsService.SourceNames[i];
        }
        return AutoLyricsSource;
    }

    /// <summary>Provider id for a picker entry; null means "Auto".</summary>
    private string? LyricsSourceIdForName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == AutoLyricsSource) return null;
        var names = _lyricsService.SourceNames;
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return _lyricsService.SourceIds[i];
        }
        return null;
    }

    /// <summary>Move the picker without triggering a re-fetch.</summary>
    private void SetLyricsSourceSelection(string name)
    {
        _syncingLyricsSource = true;
        try
        {
            SelectedLyricsSource = name;
        }
        finally
        {
            _syncingLyricsSource = false;
        }
    }

    /// <summary>
    /// Pin the picked provider for the loaded track and re-fetch, or clear the
    /// pin for "Auto" (normal priority chain).
    /// </summary>
    private async Task ApplyLyricsSourceAsync(string? name)
    {
        var sourceId = LyricsSourceIdForName(name);
        var key = CurrentLyricsKey;

        // The pin lives in the track's metadata record: null/blank clears it
        // back to the default provider chain.
        _trackMeta.SetLyricsSource(key, sourceId);

        if (LoadedTrack is { } track)
            await FetchLyricsAsync(track, sourceId);
    }

    [RelayCommand]
    private void NudgeLyrics(string? deltaMs)
    {
        if (!double.TryParse(deltaMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
            return;

        LyricsOffsetMs = Math.Clamp(LyricsOffsetMs + delta, -AppSettings.MaxLyricsOffsetMs,
            AppSettings.MaxLyricsOffsetMs);
    }

    /// <summary>
    /// Back to 0 ms for whatever the header currently edits: the section under
    /// the playhead, or the track (which then inherits the global offset again).
    /// </summary>
    [RelayCommand]
    private void ResetLyrics()
    {
        var key = CurrentLyricsKey;

        if (EditSection is not null && key != null)
        {
            var name = SectionDisplayName(_editSectionIndex);
            _trackMeta.SetSectionOffset(key, _editSectionIndex, 0);
            RefreshTrackSections();
            SyncLyricsOffsetEditor();
            StatusText = $"Timing reset for {name} (0 ms)";
            return;
        }

        if (key != null)
        {
            // Drop the per-track value rather than pinning 0: the track follows
            // the global slider again, which is what "Reset" means here.
            _trackMeta.ClearLyricsOffset(key);
            RefreshTrackSections();
            SyncLyricsOffsetEditor();
            StatusText = "Track timing reset \u2014 following the global offset.";
            return;
        }

        LyricsOffsetMs = 0;
    }

    // ---- lyric timing sections --------------------------------------------
    // A section is a saved span of the track with its own offset (override
    // semantics: inside it the section's value replaces the track's). The
    // header edits whichever section the playhead is in - that is what makes
    // "fix timing for this part only" possible - and the cached list below is
    // what the 30 Hz tick reads instead of copying the store every frame.

    /// <summary>True when the playhead sits inside a saved section (drives the context-menu entry).</summary>
    public bool HasSectionAtPlayhead => CachedSectionIndexAt(PlayheadMs()) >= 0;

    /// <summary>The section the header edits; null means it edits the track/global value.</summary>
    private LyricOffsetSection? EditSection =>
        _editSectionIndex >= 0 && _editSectionIndex < _trackSections.Count
            ? _trackSections[_editSectionIndex]
            : null;

    /// <summary>Refresh the cached section list for the loaded track (one copy, on change only).</summary>
    private void RefreshTrackSections() => _trackSections = _trackMeta.GetSections(CurrentLyricsKey);

    /// <summary>Section index at a position, from the cached list - no store call, no allocation.</summary>
    private int CachedSectionIndexAt(int positionMs) =>
        _trackSections.Count == 0 ? -1 : LyricOffsetTimeline.SectionIndexAt(_trackSections, positionMs);

    /// <summary>The active deck's playhead, in the millisecond clock the store uses.</summary>
    private int PlayheadMs() => LyricOffsetTimeline.ToPositionMs(_transport.Position);

    /// <summary>
    /// Follow the playhead into and out of saved sections; the section the header
    /// edits always follows the one under the playhead. Cheap enough for the tick:
    /// it scans the cached list and only touches the UI at a boundary.
    /// </summary>
    private void UpdateLyricSectionScope(TimeSpan position)
    {
        var index = CachedSectionIndexAt(LyricOffsetTimeline.ToPositionMs(position));
        if (index == _playheadSectionIndex) return;

        _playheadSectionIndex = index;
        SetEditSection(index);
    }

    /// <summary>Point the header at a section, or back at the track/global value with -1.</summary>
    private void SetEditSection(int index)
    {
        if (index < 0 || index >= _trackSections.Count) index = -1;

        _editSectionIndex = index;
        OnPropertyChanged(nameof(HasSectionAtPlayhead));
        SyncLyricsOffsetEditor();
    }

    private string SectionDisplayName(int index)
    {
        if (index < 0 || index >= _trackSections.Count) return "section";
        var label = _trackSections[index].Label;
        return string.IsNullOrWhiteSpace(label) ? $"section {index + 1}" : label;
    }

    /// <summary>Round a slider value to the integer milliseconds the store keeps.</summary>
    private static int ToOffsetMs(double ms) =>
        (int)Math.Round(Math.Clamp(ms, -AppSettings.MaxLyricsOffsetMs, AppSettings.MaxLyricsOffsetMs),
            MidpointRounding.AwayFromZero);

    /// <summary>Pinned provider id for a track; null means the default chain.</summary>
    private string? StoredLyricsSource(string? key)
    {
        var id = _trackMeta.Get(key)?.LyricsSourceId;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// "Set timing for selected lines": save the offset the selected lines play
    /// at now as a section, so the next nudge moves only that span. The indexes
    /// come from the view (the ListBox owns the selection).
    /// </summary>
    /// <summary>
    /// Re-render the lyrics after an operator edit changed, without asking a
    /// provider again. Starts from the provider's document, so a second edit is laid
    /// over the provider's text rather than over the first edit's result.
    /// </summary>
    public void RefreshLyricEdits()
    {
        if (!_lyricsApplied) return;
        ApplyLyricsDocument(_lyricsBaseDocument, _lyricsMissStatus);
    }

    /// <summary>
    /// Set the text of the selected lyric lines of the loaded track, as one
    /// operator edit per line.
    /// <para>
    /// Each edit records the current document's source id and the line's start
    /// time, so it only ever applies to the same provider's timing. An existing
    /// edit for that line (same source, inside the match window) is replaced; edits
    /// for other lines and other sources are kept untouched.
    /// </para>
    /// </summary>
    public void SetLyricLineText(IReadOnlyList<int>? lineIndexes, string? text)
    {
        var key = CurrentLyricsKey;
        if (key == null)
        {
            StatusText = "Load a track before editing lyric text.";
            return;
        }

        if (LyricLines.Count == 0)
        {
            StatusText = "Load lyrics first \u2014 there is no line to edit.";
            return;
        }

        var replacement = text ?? "";
        // The document on screen decides which source these edits belong to, so an
        // all-edits sheet records the "edit" marker and stays self-consistent.
        var sourceId = _lyricsDocument?.SourceId is { Length: > 0 } current
            ? current
            : LyricEdits.EditSourceId;

        var merged = new List<LyricLineEdit>(_trackMeta.GetLyricEdits(key));
        int edited = 0;
        int markersSkipped = 0;

        if (lineIndexes != null)
        {
            foreach (var index in lineIndexes)
            {
                if (index < 0 || index >= LyricLines.Count) continue;

                // A marker is not sung text: an edit written at its time would be
                // an orphan, because LyricLineEdit.FindLine only matches real
                // lines and would never apply it.
                if (LyricLines[index].IsInstrumental)
                {
                    markersSkipped++;
                    continue;
                }

                var timeMs = (int)Math.Round(LyricLines[index].Time.TotalMilliseconds);
                merged.RemoveAll(e => string.Equals(e.SourceId, sourceId, StringComparison.Ordinal)
                                      && Math.Abs(e.TimeMs - timeMs) <= LyricEdits.MatchWindowMs);
                merged.Add(new LyricLineEdit { TimeMs = timeMs, Text = replacement, SourceId = sourceId });
                edited++;
            }
        }

        if (edited == 0)
        {
            StatusText = markersSkipped > 0
                ? "Instrumental lines carry no text to edit."
                : "Select one or more lyric lines first.";
            return;
        }

        _trackMeta.SetLyricEdits(key, merged);
        RefreshLyricEdits();
        StatusText = edited == 1 ? "Line text saved." : $"{edited} line texts saved.";
    }

    public void SetTimingForSelectedLines(IReadOnlyList<int>? lineIndexes)
    {
        var key = CurrentLyricsKey;
        if (key == null)
        {
            StatusText = "Load a track before saving lyric timing.";
            return;
        }

        if (_lyricsDocument is not { Synced: true } document)
        {
            StatusText = "Load synced lyrics first \u2014 timing needs line timestamps.";
            return;
        }

        int first = int.MaxValue, last = -1;
        if (lineIndexes != null)
        {
            foreach (var index in lineIndexes)
            {
                if (index < 0 || index >= LyricLines.Count) continue;
                if (index < first) first = index;
                if (index > last) last = index;
            }
        }

        if (last < 0)
        {
            StatusText = "Select one or more lyric lines first.";
            return;
        }

        // The section starts by reproducing the offset the user already hears,
        // so adding it changes nothing until they nudge it.
        var current = _trackMeta.GetEffectiveOffset(key, PlayheadMs(), _settings.LyricsOffsetMs);

        // Name the line the section actually starts on: a selection can start on
        // an instrumental marker, and the selector resolves past it to the first
        // real line. A selection with no real line is refused, as it is there.
        int startLine = LyricSectionSelector.ResolveStartLine(document, first);
        if (startLine < 0 || startLine > last)
        {
            StatusText = "Those lines have no usable timing.";
            return;
        }

        var label = startLine == last
            ? $"line {startLine + 1}"
            : $"lines {startLine + 1}\u2013{last + 1}";

        if (LyricSectionSelector.FromLines(document, first, last, ToOffsetMs(current), label) is not { } span)
        {
            StatusText = "Those lines have no usable timing.";
            return;
        }

        var result = _trackMeta.AddSection(key, span.StartMs, span.EndMs, span.OffsetMs, span.Label);
        if (!result.Added)
        {
            StatusText = $"Could not set timing: {result.RejectReason}";
            return;
        }

        // Make the new section the edit target right away: the slider shows its
        // offset now, without waiting for the playhead (or a reload) to get there.
        RefreshTrackSections();
        SetEditSection(result.Index);

        StatusText = result.ReplacedSections > 0
            ? $"Timing set for {label} ({FormatLyricsOffset(span.OffsetMs)}) \u00b7 " +
              $"{result.ReplacedSections} earlier section(s) displaced"
            : $"Timing set for {label} ({FormatLyricsOffset(span.OffsetMs)})";
    }

    /// <summary>Drop the section the playhead is in; the track offset applies there again.</summary>
    [RelayCommand]
    private void RemoveLyricTimingSection()
    {
        var key = CurrentLyricsKey;
        var index = CachedSectionIndexAt(PlayheadMs());
        if (key == null || index < 0)
        {
            StatusText = "The playhead is not inside a timed section.";
            return;
        }

        var name = SectionDisplayName(index);
        if (!_trackMeta.RemoveSection(key, index))
        {
            RefreshTrackSections();
            SetEditSection(-1);
            StatusText = "That section is already gone.";
            return;
        }

        RefreshTrackSections();
        SetEditSection(-1);
        StatusText = $"Timing removed for {name} \u2014 the track offset applies again";
    }

    /// <summary>
    /// Seed the picker's editable query from the track's pinned override when it
    /// has one, otherwise from the file tags. Called every time the popup opens so
    /// it never shows a stale search.
    /// </summary>
    private void PrefillLyricsQuery(TrackEntry track)
    {
        _activeLyricsQuery = LyricsQueryFor(track);
        LyricsQueryTitle = _activeLyricsQuery?.Title ?? track.Title;
        LyricsQueryArtist = _activeLyricsQuery?.Artist ?? track.Artist;
        HasLyricsQueryOverride = _activeLyricsQuery != null;
    }

    /// <summary>
    /// Search with the title/artist currently in the picker, not the file tags.
    /// Providers are keyed on artist+title, which is exactly why a cover misses;
    /// this is the manual correction path for that.
    /// </summary>
    [RelayCommand]
    private async Task SearchLyricsQueryAsync()
    {
        if (LoadedTrack is not { } loaded) return;

        var title = LyricsQueryTitle.Trim();
        var artist = LyricsQueryArtist.Trim();

        // Same identity as the track, only the query changes: providers take a
        // TrackEntry, so a synthetic one avoids an interface change.
        var query = new TrackEntry
        {
            RelativeKey = loaded.RelativeKey,
            RelativePath = loaded.RelativePath,
            Title = title,
            Artist = artist,
            Duration = TimeSpan.Zero,
        };

        _activeLyricsQuery = new LyricsService.LyricsQuery(title, artist);
        await RunCandidateSearchAsync(query, _activeLyricsQuery);
    }

    /// <summary>Drop the pinned query and re-fetch with the file tags.</summary>
    [RelayCommand]
    private async Task ClearLyricsQueryAsync()
    {
        if (LoadedTrack is not { } track) return;

        _trackMeta.SetLyricsQuery(track.RelativeKey, null, null);
        _activeLyricsQuery = null;
        HasLyricsQueryOverride = false;
        LyricsQueryTitle = track.Title;
        LyricsQueryArtist = track.Artist;
        await FetchLyricsAsync(track);
    }

    /// <summary>
    /// Fetch every provider for the loaded track and offer them in the header
    /// popup (source, line count, synced/unsynced). Picking one pins it for
    /// this track only.
    /// </summary>
    [RelayCommand]
    private async Task TryOtherSourcesAsync()
    {
        // Prefill from the first real track there is. With nothing loaded the
        // picker used to keep whatever was typed for a previous song.
        var track = LoadedTrack
            ?? SelectedTrack
            ?? SelectedTreeNode?.Track
            ?? Queue.FirstOrDefault(q => q.IsCurrent)?.Track;
        if (track == null)
        {
            LyricsCandidates.Clear();
            LyricsSourceHint = "Load a track first, then try its other lyric sources.";
            IsLyricsSourcePickerOpen = true;
            return;
        }

        PrefillLyricsQuery(track);
        IsLyricsSourcePickerOpen = true;
        await RunCandidateSearchAsync(track, _activeLyricsQuery);
    }

    /// <summary>
    /// Fill <see cref="LyricsCandidates"/> from one query. Shared by the tag-based
    /// and the pinned-query search so both report identically.
    /// </summary>
    private async Task RunCandidateSearchAsync(TrackEntry track, LyricsService.LyricsQuery? query)
    {
        var generation = ++_lyricsCandidatesGeneration;
        _lyricsCandidatesCts?.Cancel();
        _lyricsCandidatesCts = new CancellationTokenSource();
        var ct = _lyricsCandidatesCts.Token;

        LyricsCandidates.Clear();
        LyricsSourceHint = "Searching sources\u2026";

        try
        {
            var docs = await _lyricsService.GetCandidatesAsync(track, ct, query);
            if (ct.IsCancellationRequested || generation != _lyricsCandidatesGeneration) return;

            foreach (var doc in docs)
            {
                if (doc == null) continue;
                var detail = doc.Synced
                    ? $"synced \u00b7 {doc.Lines.Count} lines"
                    : $"unsynced \u00b7 {doc.Lines.Count} lines";
                LyricsCandidates.Add(new LyricsCandidateViewModel(doc, detail));
            }

            LyricsSourceHint = LyricsCandidates.Count switch
            {
                0 => $"No source returned lyrics for \"{LyricsQueryTitle}\" by {LyricsQueryArtist}.",
                1 => "One source had lyrics - pick it to pin it.",
                _ => $"{LyricsCandidates.Count} sources found - pick one for this track.",
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LyricsSourceHint = $"Could not search sources: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ApplyLyricsCandidate(LyricsCandidateViewModel? candidate)
    {
        if (candidate == null || LoadedTrack is not { } track) return;

        // Pin by stable id; a document without one (legacy cache entry) is
        // applied for this session only.
        if (!string.IsNullOrEmpty(candidate.SourceId))
        {
            _trackMeta.SetLyricsSource(track.RelativeKey, candidate.SourceId);
            SetLyricsSourceSelection(LyricsSourceNameForId(candidate.SourceId));
        }

        // The query is part of the binding: pinning the provider alone would
        // re-run the same wrong artist+title on the next load.
        _trackMeta.SetLyricsQuery(track.RelativeKey, _activeLyricsQuery?.Title, _activeLyricsQuery?.Artist);
        HasLyricsQueryOverride = _activeLyricsQuery != null;

        ApplyLyricsDocument(candidate.Document);

        IsLyricsSourcePickerOpen = false;
        LyricsCandidates.Clear();
    }

    [RelayCommand]
    private void CloseLyricsSourcePicker() => IsLyricsSourcePickerOpen = false;

    // ---- UI clock ------------------------------------------------------------

    private void OnUiTick()
    {
        // The UI watchdog's heartbeat: one timestamp store, ~20 ns. Every tick that
        // reaches here is what tells the watchdog the UI thread is still moving.
        UiBreadcrumb.NoteUiTick();

        // The active deck's playhead drives the shared seek bar and lyric clock;
        // each deck card reads its own playhead independently.
        DeckA.UpdateReadout();
        DeckB.UpdateReadout();

        var pos = _transport.Position;

        if (!_isUserSeeking)
        {
            PositionSeconds = pos.TotalSeconds;
            PositionText = Format(pos);
        }

        UpdateLyricHighlight(pos);

        // Entering or leaving a saved section changes what the header edits, so
        // follow the playhead across section boundaries. This reads the cached
        // section list and only touches the UI when the index actually changes.
        UpdateLyricSectionScope(pos);

        RefreshDurations(); // duration depends on deck delays; setters no-op when unchanged

        SyncCrossfaderFromTransport();

        // A deck that finishes while it is not the active one is simply stopped:
        // its media is over, but the queue hand-off belongs to the active deck.
        var otherDeck = _transport.ActiveDeck == PlaybackDeck.A ? PlaybackDeck.B : PlaybackDeck.A;
        if (_transport.Channel(otherDeck).ReachedEnd)
            _transport.StopDeck(otherDeck);

        // The source tabs' preview strips ride this clock (their own timers would
        // only fight it): one notification per frame keeps position, duration and
        // play state in step with the decks.
        _previewTransport.NotifyTick();

        // 2.0 surfaces: what the transition scheduler is doing, how much of the
        // library has a beat grid, and the state the Stream Deck keys mirror.
        RefreshTransitionStatus();
        RefreshAnalysisCounters();
        PublishApiState();

        // Auto-stop the active deck once it (including its karaoke tail) has
        // finished. A track that came from the queue hands over to the next row;
        // the other deck keeps playing underneath, which is the mix.
        if (_transport.ReachedEnd)
        {
            // A queued row is normally armed ahead of time: the transition
            // scheduler has already faded this deck out at its last sample and
            // OnTransitionCompleted promotes the new row.
            var fromQueue = CurrentIndex >= 0;
            bool armed = _armedItem != null && _transport.Transitions?.IsActive == true;

            if (armed)
            {
                // The armed fade *owns* the end of this deck - it is planned to
                // finish exactly here. Stopping or cancelling it is what froze the
                // fader mid-ramp and left the incoming deck silent; complete it
                // instead so the hand-off still lands (and the row is promoted).
                _transport.Transitions!.CompleteNow();
            }
            else
            {
                // Nothing armed means either a non-queue track (just stop) or a
                // refused/failed transition (advance the old way, with a hard start).
                _transport.Stop();
                if (fromQueue) _ = AdvanceQueueAfterEndAsync();
            }
        }

        // A deck reaching Playing raises no channel event of its own (the channel
        // only subscribes to failures), so this tick is where the app first learns
        // the pair actually started. Logged before the release guard below, so a
        // start is never missed by a pair that ends on the same tick.
        MaybeLogPairStart();

        // Stopped with nothing loaded: the pair is over. Every ordinary load
        // releases inside LoadTrack; this covers the operator who stops and
        // loads nothing at all.
        //
        // Asked of the channels, not of the deck cards: a card's IsLoaded is a
        // copy that only refreshes when the channel announces a state, so during
        // the load it still reads false. Asking the copy released a pair that was
        // one tick old, which took the deck titles and the lock with it and left
        // one deck playing.
        if (IsKaraokePairLoaded
            && !DeckA.HasTrackInFlight && !DeckB.HasTrackInFlight
            && !_transport.DeckA.IsLoaded && !_transport.DeckB.IsLoaded)
            ReleaseKaraokePair();
    }

    private void OnTransportStateChanged()
    {
        IsPlaying = _transport.State == TransportState.Playing;
        RefreshDurations();
    }

    /// <summary>Mirror the deck pair's active deck onto the two cards' highlight.</summary>
    private void SyncDeckActivation()
    {
        var active = _transport.ActiveDeck;
        _activeDeck = active;
        DeckA.IsActive = active == PlaybackDeck.A;
        DeckB.IsActive = active == PlaybackDeck.B;
        OnPropertyChanged(nameof(IsDeckAActive));
        OnPropertyChanged(nameof(IsDeckBActive));
        OnPropertyChanged(nameof(IsAOnlySelected));
        OnPropertyChanged(nameof(IsBOnlySelected));
    }

    /// <summary>
    /// A programme crossfade ramps the transport fader; copy it back so the
    /// slider thumb follows without feeding the value straight back in.
    /// </summary>
    private void SyncCrossfaderFromTransport()
    {
        if (Math.Abs(Crossfader - _transport.Crossfader) < 0.002) return;
        _syncingCrossfader = true;
        try { Crossfader = _transport.Crossfader; }
        finally { _syncingCrossfader = false; }
    }

    private void RefreshDurations()
    {
        DurationSeconds = Math.Max(0.001, _transport.Duration.TotalSeconds);
        DurationText = Format(_transport.Duration);
    }

    private void UpdateLyricHighlight(TimeSpan position)
    {
        // Unsynced lyrics are static scrollable text - no highlight tracking.
        if (!_lyricsSynced || LyricLines.Count == 0)
        {
            BallVisible = false;
            return;
        }

        // Positive settings mean "lyrics later": the store hands the offset back
        // already negated for the "position + offset" comparison, applying the
        // section override under the playhead instead of the track value. One
        // value drives the line highlight, the word highlight and the ball, so
        // they can never disagree.
        var offset = _trackMeta.GetEffectiveOffset(CurrentLyricsKey, position, _settings.LyricsOffsetMs);
        var effective = position + TimeSpan.FromMilliseconds(-offset);

        // Last line whose time has passed (cheap linear scan; lyric documents
        // are small enough that this is negligible at 30 Hz).
        int index = -1;
        for (int i = 0; i < LyricLines.Count; i++)
        {
            if (LyricLines[i].Time <= effective) index = i;
            else break;
        }

        if (index != _currentLyricIndex)
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < LyricLines.Count)
            {
                var previous = LyricLines[_currentLyricIndex];
                previous.IsCurrent = false;
                foreach (var word in previous.Words) word.IsCurrent = false;
            }

            _currentLyricIndex = index;
            _currentWordIndex = -1;
            // Cached X positions describe the old line; the view re-measures
            // when CurrentLyricChanged fires below.
            ClearLyricWordGeometry();
            OnPropertyChanged(nameof(CurrentLyricLine));

            if (index >= 0)
            {
                LyricLines[index].IsCurrent = true;
                CurrentLyricChanged?.Invoke(LyricLines[index]);
            }
        }

        UpdateWordHighlight(effective);
        UpdateBallMotion(effective);
    }

    /// <summary>
    /// Highlight exactly one word of the current line when word-follow is on,
    /// and none at all in line mode.
    /// </summary>
    private void UpdateWordHighlight(TimeSpan effective)
    {
        if (!IsWordFollow)
        {
            // Toggling the mode already cleared every flag; this covers the tick
            // that follows a switch back to line mode.
            if (_currentWordIndex >= 0) ClearAllWordHighlights();
            return;
        }

        if (_currentLyricIndex < 0 || _currentLyricIndex >= LyricLines.Count) return;

        var words = LyricLines[_currentLyricIndex].Words;
        // No word layout rendered (no words, or one unbreakable token): line
        // highlighting is all there is, so there is nothing to flag.
        if (!LyricLines[_currentLyricIndex].HasWords)
        {
            if (_currentWordIndex != -1) ClearAllWordHighlights();
            return;
        }

        // Last word that has started. Words are ordered and non-overlapping.
        int index = -1;
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Start <= effective) index = i;
            else break;
        }

        if (index == _currentWordIndex) return;

        if (_currentWordIndex >= 0 && _currentWordIndex < words.Count)
            words[_currentWordIndex].IsCurrent = false;
        _currentWordIndex = index;
        if (index >= 0) words[index].IsCurrent = true;

        // The active word turns bold, so its measured box changes. Ask the view
        // to re-measure; until it answers, the previous centres stay in use (a
        // tick of stale-by-a-pixel beats a flickering ball).
        CurrentWordChanged?.Invoke();
    }

    /// <summary>Clear every word flag (mode switch, line change, rebuild).</summary>
    private void ClearAllWordHighlights()
    {
        for (int i = 0; i < LyricLines.Count; i++)
        {
            foreach (var word in LyricLines[i].Words)
            {
                if (word.IsCurrent) word.IsCurrent = false;
            }
        }
        _currentWordIndex = -1;
    }

    /// <summary>
    /// Place the bouncing ball from the transport clock alone: no animation, so a
    /// seek or pause lands exactly where the current position says. The ball
    /// travels from the active word's centre to the next word's centre over the
    /// active word's span, with a sine hop.
    /// </summary>
    private void UpdateBallMotion(TimeSpan effective)
    {
        var line = CurrentLyricLine;
        var centres = _ballCentres;

        if (!IsWordFollow || line is null || !line.HasWords || centres is null
            || !ReferenceEquals(line, _ballLine)
            || double.IsNaN(_ballWordTop))
        {
            BallVisible = false;
            return;
        }

        var words = line.Words;
        int index = _currentWordIndex;
        if (index < 0 || index >= words.Count || index >= centres.Count)
        {
            BallVisible = false;
            return;
        }

        var word = words[index];
        double span = (word.End - word.Start).TotalMilliseconds;
        double t = span > 0
            ? Math.Clamp((effective - word.Start).TotalMilliseconds / span, 0, 1)
            : 1;

        double x0 = centres[index];
        double x1 = index + 1 < centres.Count ? centres[index + 1] : x0;

        BallX = x0 + (x1 - x0) * t - BallSize / 2;
        // Top of the line, minus the ball and a 2 px gap, minus the hop.
        BallY = _ballWordTop - BallSize - 2 - Math.Sin(Math.PI * t) * BallHopHeight;
        // Measurement is in canvas space; a negative origin means the line is
        // scrolled out of the panel, where a ball would be misleading.
        BallVisible = BallX + BallSize > 0 && BallY >= 0;
    }

    /// <summary>
    /// Enumerate active output endpoints and hand them to both decks.
    ///
    /// <para>Runs on a worker thread on purpose: resolving <c>FriendlyName</c>
    /// re-enters <c>IPropertyStore.GetCount</c> across COM per property read, which
    /// has been measured at seconds per device. Called from the window's
    /// <c>Loaded</c>, never from the constructor, so start-up never blocks on
    /// MMDevAPI.</para>
    /// </summary>
    public async Task RefreshDevicesAsync()
    {
        var devices = await Task.Run(_deviceService.GetOutputDevices);
        DeckA.RefreshDevices(devices);
        DeckB.RefreshDevices(devices);
    }

    [RelayCommand]
    private Task ReloadDevices() => RefreshDevicesAsync();

    private void PersistDeckSettings()
    {
        _settings.DeckADeviceId = DeckA.SelectedDevice?.Id;
        _settings.DeckBDeviceId = DeckB.SelectedDevice?.Id;
        _settings.DeckADelayMs = DeckA.DelayMs;
        _settings.DeckBDelayMs = DeckB.DelayMs;
        _settings.DeckAOutputDelayMs = DeckA.OutputDelayMs;
        _settings.DeckBOutputDelayMs = DeckB.OutputDelayMs;
        _settings.DeckAVolume = DeckA.Volume;
        _settings.DeckBVolume = DeckB.Volume;
        _settings.Save();
    }

    /// <summary>(Re)arm the playlist auto-sync timer from the current setting.</summary>
    private void ScheduleAutoSync()
    {
        _autoSyncTimer.Stop();
        var minutes = _settings.YouTubeAutoSyncMinutes;
        if (minutes <= 0) return;
        _autoSyncTimer.Interval = TimeSpan.FromMinutes(minutes);
        _autoSyncTimer.Start();
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>
    /// Selftest seam: <c>--linger-check --throw</c> fails the teardown here, where
    /// a store can fail in the field, so the close path's survival is falsified by
    /// a run rather than assumed. Null in every real run.
    /// </summary>
    internal static Action? TeardownFault;

    /// <summary>
    /// The last teardown group <see cref="Dispose"/> entered. Logged with a
    /// failure, so whoever reads the log knows how much of the exit path was
    /// skipped and not only that it broke.
    /// </summary>
    internal string TeardownReached { get; private set; } = "not started";

    private void TeardownStep(string name) => TeardownReached = name;

    public void Dispose()
    {
        TeardownStep("timers");
        _uiTimer.Stop();
        _rescanDebounce.Stop();
        _searchDebounce.Stop();
        _autoSyncTimer.Stop();
        _lyricsSettingsSave.Stop();
        // Flush a queue write that hadn't hit its debounce yet.
        TeardownStep("queue and download stores");
        _queueStore.Dispose();
        _downloadQueue.Dispose();
        _scanCts?.Cancel();
        _lyricsCts?.Cancel();
        _lyricsCandidatesCts?.Cancel();
        // Persist a pending lyric offset drag that hadn't hit its debounce yet.
        // The pending save carries the home it was scheduled for; clearing it
        // here is also what makes a tick arriving after teardown a no-op.
        TeardownStep("settings and lyric metadata");
        _settings.Save(_lyricsSettingsHome ?? AppPaths.Home);
        _lyricsSettingsHome = null;
        // Per-track lyric metadata (offset, pinned sources, sections) lives in
        // the store now: flush anything the debounce still holds, then stop
        // listening.
        _trackMeta.Changed -= OnTrackMetadataChanged;
        _trackMeta.Save();
        _trackMeta.Dispose();
        TeardownStep("audio, hotkeys and the local api");
        ShutdownAudio2();
        TeardownStep("soundboard");
        Soundboard?.Save();

        // The failure seam: after this point come the playlist drain, the
        // separation worker, the watcher, the transport, the tag cache and the
        // library.json re-mirror that marks a clean exit.
        TeardownFault?.Invoke();

        // The playlist store mirrors every save into library.db on a coalescing
        // writer; drain it and close it so the last add/rename of the session is
        // on disk before the process exits.
        TeardownStep("playlist drain, separation, watcher, transport");
        _playlistStore.Dispose();
        _separation.Dispose();
        _watcher.Dispose();
        DeckA.Dispose();
        DeckB.Dispose();
        _transport.Dispose();
        TeardownStep("tag cache and the library.json re-mirror");
        _tagCache.Save();
        // library.json was just rewritten by us. Mirror it into SQLite now and
        // record its stamp, so the tags land in the database and the next start
        // sees a matching file instead of reimporting what we wrote ourselves.
        Data.LibraryBoot.SyncFromDisk("library.json saved at shutdown");
        TeardownStep("complete");
    }
}
