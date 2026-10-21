using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Playback;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// Root view model: owns the audio engine, library, lyrics pipeline and the
/// unified transport state that the whole window binds to.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly DeviceService _deviceService = new();
    private readonly SyncTransport _transport;
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
    private readonly StreamCache _streamCache;
    private readonly DownloadQueue _downloadQueue;
    private readonly StreamResolver _streamResolver;
    private readonly DispatcherTimer _rescanDebounce;
    private readonly DispatcherTimer _autoSyncTimer;
    private readonly HashSet<string> _pendingAutoKaraoke = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _lyricsCts;
    private bool _isUserSeeking;
    private int _currentLyricIndex = -1;
    private bool _lyricsSynced;

    public MainViewModel()
    {
        _settings = AppSettings.Load();

        var deckA = new AudioDeck("Deck A", _deviceService);
        var deckB = new AudioDeck("Deck B", _deviceService);
        _transport = new SyncTransport(deckA, deckB);
        _transport.StateChanged += (_, _) => OnTransportStateChanged();

        DeckA = new DeckViewModel("DECK A \u00b7 KARAOKE", deckA, _transport, PersistDeckSettings,
            id => id != null && _settings.DeviceDelays.TryGetValue(id, out var d) ? d : 0,
            (id, delay) => { if (id != null) _settings.DeviceDelays[id] = delay; });
        DeckB = new DeckViewModel("DECK B \u00b7 MUSIC", deckB, _transport, PersistDeckSettings,
            id => id != null && _settings.DeviceDelays.TryGetValue(id, out var d) ? d : 0,
            (id, delay) => { if (id != null) _settings.DeviceDelays[id] = delay; });
        DeckA.RestoreSettings(_settings.DeckADeviceId, _settings.DeckADelayMs, _settings.DeckAVolume);
        DeckB.RestoreSettings(_settings.DeckBDeviceId, _settings.DeckBDelayMs, _settings.DeckBVolume);
        RefreshDevices();

        _scanner = new LibraryScanner(_tagCache);
        _lyricsService = new LyricsService(_settings);
        _separation = new SeparationService(_settings,
            System.Windows.Application.Current.Dispatcher);
        _separation.StatusChanged += message => StatusText = message;

        TracksView = CollectionViewSource.GetDefaultView(Tracks);
        TracksView.Filter = FilterTrack;

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

        _downloadQueue.LibraryChanged += () => System.Windows.Application.Current?.Dispatcher
            .BeginInvoke(ScheduleRescan);
        _downloadQueue.ItemFinished += OnDownloadFinished;

        // A batch of downloads should trigger one rescan, not one per track.
        _rescanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _rescanDebounce.Tick += (_, _) =>
        {
            _rescanDebounce.Stop();
            _ = RescanAsync();
        };

        Spotify = new SpotifyViewModel(_settings, _playlistStore, _downloadQueue, _ledger,
            new SpotifyOAuthService(_settings), PlaySourceTrackAsync);
        YouTube = new YouTubeViewModel(_settings, _playlistStore, _downloadQueue, _ledger,
            PlaySourceTrackAsync);
        Downloads = new DownloadsViewModel(_settings, _downloadQueue);

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

    /// <summary>Waveform amplitude data for Deck A (karaoke) and Deck B (music).</summary>
    [ObservableProperty] private List<float> _waveformAData = new();
    [ObservableProperty] private List<float> _waveformBData = new();
    [ObservableProperty] private double _waveformProgress; // 0-1 from master clock

    /// <summary>Raised when the highlighted lyric changes (view scrolls to it).</summary>
    public event Action<LyricLineViewModel>? CurrentLyricChanged;

    [ObservableProperty] private string _musicDirectory = "";
    [ObservableProperty] private string _karaokeDirectory = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Set your Music and Karaoke folders in Settings.";
    [ObservableProperty] private TrackEntry? _selectedTrack;
    [ObservableProperty] private BrowserNode? _selectedTreeNode;
    [ObservableProperty] private TrackEntry? _loadedTrack;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _loadedTrackTitle = "JockeyBox";
    [ObservableProperty] private string _lyricsStatus = "Load a track to fetch lyrics.";

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

    private async Task RescanAsync()
    {
        if (string.IsNullOrWhiteSpace(MusicDirectory) && string.IsNullOrWhiteSpace(KaraokeDirectory))
        {
            Tracks.Clear();
            _watcher.StopWatching();
            StatusText = "Set your Music and Karaoke folders in Settings.";
            return;
        }

        _scanCts?.Cancel();
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

            _separation.RebindEntries(list);
            Tracks.Clear();
            foreach (var t in list) Tracks.Add(t);
            BuildTree(list);

            // Let downloads and playback see files that were already on disk,
            // not only the ones this app produced.
            _libraryIndex.Rebuild(list);

            // A queue restored before the library was known would re-download
            // tracks that are already here, so resume only once the index is warm.
            _downloadQueue.RestorePersisted();

            var missing = list.Count(t => t.NeedsKaraoke);
            StatusText = $"{list.Count} tracks \u00b7 {missing} need karaoke versions";
            _watcher.Watch(music, karaoke);
            ApplyPendingAutoKaraoke();
        }
        catch (OperationCanceledException) { /* superseded by a newer scan */ }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
    }

    partial void OnSearchTextChanged(string value) => TracksView.Refresh();

    private bool FilterTrack(object obj)
    {
        if (obj is not TrackEntry t) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return t.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || t.Artist.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || t.RelativePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Build a folder tree from the flat track list for the browser TreeView.</summary>
    private void BuildTree(List<TrackEntry> tracks)
    {
        BrowserRoots.Clear();
        var root = new Dictionary<string, BrowserNode>(StringComparer.OrdinalIgnoreCase);

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
                if (!root.TryGetValue(current, out var folder))
                {
                    folder = new BrowserNode(seg);
                    root[current] = folder;
                    (parent?.Children ?? BrowserRoots).Add(folder);
                }
                parent = folder;
            }

            // Leaf node for the file itself.
            var leaf = BrowserNode.FromTrack(track);
            (parent?.Children ?? BrowserRoots).Add(leaf);
        }
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

    private void LoadTrack(TrackEntry track)
    {
        _transport.OnTracksChanged(); // stop cleanly before swapping readers

        try
        {
            DeckA.SetTrack(track.KaraokePath, track.Title, track.Artist);
            DeckB.SetTrack(track.MusicPath, track.Title, track.Artist);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load track: {ex.Message}";
            return;
        }

        LoadedTrack = track;
        LoadedTrackTitle = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title : $"{track.Artist} \u2014 {track.Title}";
        RefreshDurations();
        _ = FetchLyricsAsync(track);
        _ = GenerateWaveformsAsync(track);
    }

    private async Task GenerateWaveformsAsync(TrackEntry track)
    {
        var aTask = Task.Run(() => WaveformGenerator.Generate(track.KaraokePath));
        var bTask = Task.Run(() => WaveformGenerator.Generate(track.MusicPath));
        await Task.WhenAll(aTask, bTask);
        WaveformAData = aTask.Result ?? new List<float>();
        WaveformBData = bTask.Result ?? new List<float>();
    }

    // ---- remote playback (Spotify / YouTube) --------------------------------

    /// <summary>
    /// Play a remote track: resolve it to a local file (library copy, ledger
    /// entry, warm cache, or a fresh fetch into the cache), then load it into
    /// Deck B exactly like a library file so delay/routing/lyrics apply.
    /// </summary>
    public async Task PlaySourceTrackAsync(SourceTrack source)
    {
        try
        {
            StatusText = $"Resolving {source.DisplayName}\u2026";
            var path = await _streamResolver.ResolveAsync(source, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = $"Could not play {source.DisplayName}.";
                return;
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

            LoadTrack(entry);
            if (!IsPlaying) _transport.Play();
            StatusText = $"Playing {source.DisplayName}";
        }
        catch (Exception ex)
        {
            Log.Error($"Remote playback failed for {source.DisplayName}", ex);
            StatusText = $"Playback failed: {ex.Message}";
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
            if (_separation.Enqueue(track, null)) queued++;
        }

        if (queued > 0)
            StatusText = $"Queued karaoke generation for {queued} downloaded track(s).";
    }

    // ---- karaoke generation (vocal removal) --------------------------------

    /// <summary>
    /// Right-click "Make Karaoke Track" with a specific model from the submenu.
    /// Called directly from the view's code-behind (not via ICommand).
    /// </summary>
    public void MakeKaraokeWithModel(TrackEntry track, string model)
    {
        EnqueueKaraoke(track, model);
    }

    [RelayCommand]
    private void MakeKaraoke(TrackEntry? track)
    {
        track ??= SelectedTrack;
        EnqueueKaraoke(track, modelOverride: null);
    }

    private void EnqueueKaraoke(TrackEntry? track, string? modelOverride)
    {
        if (track?.MusicPath == null || !track.NeedsKaraoke) return;

        if (string.IsNullOrWhiteSpace(_settings.KaraokeDirectory))
        {
            StatusText = "Set a Karaoke folder in Settings before generating karaoke tracks.";
            SettingsRequested?.Invoke();
            return;
        }

        if (!SeparationEngine.IsInstalled)
        {
            EngineInstallRequested?.Invoke(track);
            return;
        }

        if (_separation.Enqueue(track, modelOverride))
        {
            var title = string.IsNullOrWhiteSpace(track.Title) ? track.RelativePath : track.Title;
            var modelNote = modelOverride is { Length: > 0 }
                ? $" (via {System.IO.Path.GetFileNameWithoutExtension(modelOverride)})" : "";
            StatusText = $"Queued karaoke generation: {title}{modelNote}";
        }
    }

    // ---- lyrics ------------------------------------------------------------

    private async Task FetchLyricsAsync(TrackEntry track)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var ct = _lyricsCts.Token;

        LyricLines.Clear();
        _currentLyricIndex = -1;
        _lyricsSynced = false;
        LyricsStatus = "Fetching lyrics\u2026";

        try
        {
            var doc = await _lyricsService.GetLyricsAsync(track, ct);
            if (ct.IsCancellationRequested) return;

            if (doc == null)
            {
                LyricsStatus = "No lyrics found for this track.";
                return;
            }

            foreach (var line in doc.Lines)
                LyricLines.Add(new LyricLineViewModel(line.Time, line.Text));
            _lyricsSynced = doc.Synced;
            LyricsStatus = doc.Synced
                ? $"Synced lyrics \u00b7 {doc.Source}"
                : $"Lyrics (not time-synced) \u00b7 {doc.Source}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LyricsStatus = $"Lyrics error: {ex.Message}";
        }
    }

    // ---- UI clock ------------------------------------------------------------

    private void OnUiTick()
    {
        var pos = _transport.Position;

        if (!_isUserSeeking)
        {
            PositionSeconds = pos.TotalSeconds;
            PositionText = Format(pos);
        }

        DeckA.UpdateReadout(pos);
        DeckB.UpdateReadout(pos);
        UpdateLyricHighlight(pos);
        RefreshDurations(); // duration depends on deck delays; setters no-op when unchanged

        WaveformProgress = DurationSeconds > 0 ? pos.TotalSeconds / DurationSeconds : 0;

        // Auto-stop once every deck (including its delay tail) has finished.
        if (_transport.ReachedEnd) _transport.Stop();
    }

    private void OnTransportStateChanged()
    {
        IsPlaying = _transport.State == TransportState.Playing;
        RefreshDurations();
    }

    private void RefreshDurations()
    {
        DurationSeconds = Math.Max(0.001, _transport.Duration.TotalSeconds);
        DurationText = Format(_transport.Duration);
    }

    private void UpdateLyricHighlight(TimeSpan position)
    {
        // Unsynced lyrics are static scrollable text - no highlight tracking.
        if (!_lyricsSynced || LyricLines.Count == 0) return;

        var effective = position + TimeSpan.FromMilliseconds(_settings.LyricsOffsetMs);

        // Last line whose time has passed (cheap linear scan; lyric documents
        // are small enough that this is negligible at 30 Hz).
        int index = -1;
        for (int i = 0; i < LyricLines.Count; i++)
        {
            if (LyricLines[i].Time <= effective) index = i;
            else break;
        }

        if (index == _currentLyricIndex) return;

        if (_currentLyricIndex >= 0 && _currentLyricIndex < LyricLines.Count)
            LyricLines[_currentLyricIndex].IsCurrent = false;
        _currentLyricIndex = index;
        if (index >= 0)
        {
            LyricLines[index].IsCurrent = true;
            CurrentLyricChanged?.Invoke(LyricLines[index]);
        }
    }

    private void RefreshDevices()
    {
        var devices = _deviceService.GetOutputDevices();
        DeckA.RefreshDevices(devices);
        DeckB.RefreshDevices(devices);
    }

    [RelayCommand]
    private void ReloadDevices() => RefreshDevices();

    private void PersistDeckSettings()
    {
        _settings.DeckADeviceId = DeckA.SelectedDevice?.Id;
        _settings.DeckBDeviceId = DeckB.SelectedDevice?.Id;
        _settings.DeckADelayMs = DeckA.DelayMs;
        _settings.DeckBDelayMs = DeckB.DelayMs;
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

    public void Dispose()
    {
        _uiTimer.Stop();
        _rescanDebounce.Stop();
        _autoSyncTimer.Stop();
        _downloadQueue.Dispose();
        _scanCts?.Cancel();
        _lyricsCts?.Cancel();
        _separation.Dispose();
        _watcher.Dispose();
        _transport.Dispose();
        _tagCache.Save();
    }
}
