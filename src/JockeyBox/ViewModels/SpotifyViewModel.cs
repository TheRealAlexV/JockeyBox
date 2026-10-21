using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Downloads;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Spotify;
using JockeyBox.Tools;
using JockeyBox.Views;

namespace JockeyBox.ViewModels;

/// <summary>
/// The Spotify tab: browse the account's playlists, show which tracks are
/// already in the library, and push the rest through the download queue.
///
/// Audio itself never comes from Spotify (its Web Playback SDK is browser-only
/// and the Web API exposes 30-second previews), so playback resolves each track
/// through the download pipeline into the stream cache - see StreamResolver.
/// </summary>
public partial class SpotifyViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly PlaylistStore _store;
    private readonly DownloadQueue _queue;
    private readonly LibraryLedger _ledger;
    private readonly SpotifyOAuthService _oauth;
    private readonly SpotifyApi _api;
    private readonly Func<SourceTrack, Task> _play;

    /// <summary>Session cache of fetched playlists, so switching tabs is instant.</summary>
    private readonly Dictionary<string, List<SourceTrack>> _trackCache = new(StringComparer.Ordinal);

    /// <summary>Cancels the previous playlist's track fetch when the selection changes.</summary>
    private CancellationTokenSource? _loadCts;

    /// <summary>The playlist whose tracks are currently in <see cref="Tracks"/>; null while loading.</summary>
    private string? _tracksOwnerId;

    private bool _suppressSelection;

    // Per-batch karaoke: MainViewModel decides on vocal removal per finished
    // download by reading AppSettings.DownloadAutoKaraoke, so a batch sets that
    // flag for its duration and the last finished item puts the old value back.
    private bool _karaokeRestorePending;
    private bool _karaokeValueBeforeBatch;

    public SpotifyViewModel(AppSettings settings, PlaylistStore store, DownloadQueue queue,
                            LibraryLedger ledger, SpotifyOAuthService oauth,
                            Func<SourceTrack, Task> play)
    {
        _settings = settings;
        _store = store;
        _queue = queue;
        _ledger = ledger;
        _oauth = oauth;
        _api = new SpotifyApi(settings, oauth);
        _play = play;

        TracksView = CollectionViewSource.GetDefaultView(Tracks);
        TracksView.Filter = FilterTrack;

        _queue.ItemFinished += _ => Dispatcher(() =>
        {
            UpdateCounts();
            RestoreKaraokeChoiceWhenIdle();
        });

        GenerateKaraokeAfterBatch = _settings.DownloadAutoKaraoke;
        RefreshConnectionState();
        StatusText = ConnectionStateText;

        if (IsLoggedIn)
            _ = InitializeAsync();
    }

    // ---- bindable state ---------------------------------------------------

    public ObservableCollection<SpotifyPlaylistInfo> Playlists { get; } = new();
    public ObservableCollection<SourceTrack> Tracks { get; } = new();
    public ICollectionView TracksView { get; }

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _hasPlaylistScopes;
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private SpotifyPlaylistInfo? _selectedPlaylist;
    [ObservableProperty] private SourceTrack? _selectedTrack;
    [ObservableProperty] private string _filterText = "";

    /// <summary>
    /// True when no Spotify app Client ID is configured. The built-in client
    /// cannot be granted the playlist scopes, so playlist import is impossible
    /// until the user supplies their own app id.
    /// </summary>
    [ObservableProperty] private bool _needsClientId;

    /// <summary>Mirror of the OAuth grant that the playlist screens care about.</summary>
    public bool HasPlaylistAccess => HasPlaylistScopes;

    /// <summary>One-line connection state for the tab and the setup dialog caller.</summary>
    [ObservableProperty] private string _connectionStateText = "";

    /// <summary>
    /// Send every track from the next download batch through vocal removal once
    /// it lands. Defaults to the global Downloads setting.
    /// </summary>
    [ObservableProperty] private bool _generateKaraokeAfterBatch;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _inLibraryCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private double _playlistProgress;

    public string ConnectLabel => IsLoggedIn
        ? (HasPlaylistScopes ? "Reconnect" : "Grant playlist access")
        : "Connect Spotify";

    public string CountsText => TotalCount == 0
        ? ""
        : $"{InLibraryCount}/{TotalCount} in library \u00b7 {QueuedCount} queued" +
          (FailedCount > 0 ? $" \u00b7 {FailedCount} failed" : "");

    // ---- commands ---------------------------------------------------------

    [RelayCommand]
    private async Task ConnectAsync()
    {
        // No own app id yet: the built-in client would only return invalid_scope,
        // so walk the user through creating one instead of failing silently.
        if (NeedsClientId)
        {
            ShowSetupDialog();
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "Opening Spotify in your browser\u2026";
            await _oauth.LoginAsync(includePlaylists: true, CancellationToken.None);
            RefreshConnectionState();
            StatusText = HasPlaylistAccess
                ? "Connected. Loading playlists\u2026"
                : ConnectionStateText;
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Spotify login failed", ex);
            StatusText = $"Spotify login failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Open the Spotify developer dashboard in the browser.</summary>
    [RelayCommand]
    private void OpenSpotifyAppDashboard() => SpotifySetupDialog.OpenDeveloperDashboard();

    /// <summary>
    /// Show the three-step setup dialog and fold the result back into this
    /// view model. The dialog owns the client id box and runs the login itself.
    /// </summary>
    private void ShowSetupDialog()
    {
        var dialog = new SpotifySetupDialog(_settings, _oauth);
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;

        dialog.ShowDialog();

        RefreshConnectionState();
        StatusText = ConnectionStateText;

        if (IsLoggedIn && HasPlaylistAccess)
            _ = InitializeAsync();
    }

    [RelayCommand]
    private void Disconnect()
    {
        _oauth.Logout();
        Playlists.Clear();
        CancelTrackLoad();
        Tracks.Clear();
        _tracksOwnerId = null;
        _trackCache.Clear();
        AccountName = "";
        UpdateCounts();
        RefreshConnectionState();
        StatusText = ConnectionStateText;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await InitializeAsync(forceReload: true);

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (SelectedPlaylist == null || Tracks.Count == 0) return;

        // Only ever queue the table that belongs to the visible selection.
        if (_tracksOwnerId != SelectedPlaylist.Id)
        {
            StatusText = $"Still loading {SelectedPlaylist.Name} - try again in a moment.";
            return;
        }

        var pending = Tracks.Where(t => t.State is SourceTrackState.New or SourceTrackState.Failed).ToList();
        if (pending.Count == 0)
        {
            StatusText = "Everything in this playlist is already downloaded.";
            return;
        }

        if (pending.Count > 300)
        {
            var answer = MessageBox.Show(
                $"Queue {pending.Count} tracks from \"{SelectedPlaylist.Name}\" for download?",
                "JockeyBox", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        RememberPlaylist(SelectedPlaylist, Tracks);
        ApplyKaraokeChoiceToBatch();
        var queued = _queue.EnqueueMany(pending);
        UpdateCounts();
        StatusText = $"Queued {queued} track(s) for download.";
        Log.Info($"Spotify: queued {queued} of {pending.Count} from {SelectedPlaylist.Name}");
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (SelectedTrack == null) return;
        if (SelectedPlaylist != null && _tracksOwnerId == SelectedPlaylist.Id)
            RememberPlaylist(SelectedPlaylist, Tracks);
        ApplyKaraokeChoiceToBatch();
        _queue.Enqueue(SelectedTrack);
        UpdateCounts();
        StatusText = $"Queued {SelectedTrack.DisplayName}.";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack == null) return;
        StatusText = $"Resolving {SelectedTrack.DisplayName}\u2026";
        await _play(SelectedTrack);
        StatusText = $"Playing {SelectedTrack.DisplayName}.";
    }

    /// <summary>Refresh every playlist that was imported and has sync enabled.</summary>
    [RelayCommand]
    private async Task SyncPlaylistsAsync()
    {
        if (!IsLoggedIn || !HasPlaylistScopes) return;

        var targets = _store.Playlists
            .Where(p => p.Source == MediaSource.Spotify && p.SourceId != null && p.SyncEnabled)
            .ToList();
        if (targets.Count == 0)
        {
            StatusText = "No Spotify playlists are set to sync yet \u2014 download one first.";
            return;
        }

        IsBusy = true;
        var added = 0;
        try
        {
            foreach (var playlist in targets)
            {
                StatusText = $"Syncing {playlist.Name}\u2026";
                var fresh = await FetchTracksAsync(playlist.SourceId!, playlist.Name,
                    isLiked: playlist.SourceId == LikedSongsId, CancellationToken.None);
                var known = new HashSet<string>(playlist.Tracks.Select(t => t.Id), StringComparer.Ordinal);
                var newTracks = fresh.Where(t => !known.Contains(t.Id)).ToList();
                if (newTracks.Count == 0) continue;

                _store.AddTracks(playlist, newTracks);
                ApplyKaraokeChoiceToBatch();
                added += _queue.EnqueueMany(newTracks);
                playlist.LastSyncedUtc = DateTime.UtcNow;
            }
            _store.Save();
            StatusText = added == 0 ? "Playlists already up to date." : $"Queued {added} new track(s).";
        }
        catch (Exception ex)
        {
            Log.Error("Spotify playlist sync failed", ex);
            StatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- loading ----------------------------------------------------------

    private const string LikedSongsId = "liked";

    private async Task InitializeAsync(bool forceReload = false)
    {
        if (!IsLoggedIn) return;

        IsBusy = true;
        try
        {
            if (!HasPlaylistScopes)
            {
                RefreshConnectionState();
                StatusText = ConnectionStateText;
                return;
            }

            CancelTrackLoad();
            _tracksOwnerId = null;
            StatusText = "Loading account\u2026";
            var user = await _api.GetCurrentUserAsync(CancellationToken.None).ConfigureAwait(true);
            if (user != null)
                AccountName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Id : user.DisplayName;

            StatusText = "Loading playlists\u2026";
            var infos = await _api.GetPlaylistsAsync(CancellationToken.None).ConfigureAwait(true);
            var liked = await _api.GetLikedSongsInfoAsync(CancellationToken.None).ConfigureAwait(true);
            if (liked.Count > 0 && liked[0].TrackCount > 0) infos.InsertRange(0, liked);

            var previousId = SelectedPlaylist?.Id;
            _suppressSelection = true;
            Playlists.Clear();
            foreach (var info in infos) Playlists.Add(info);
            _suppressSelection = false;

            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == previousId) ?? Playlists.FirstOrDefault();
            StatusText = Playlists.Count == 0
                ? "No playlists on this account."
                : $"{Playlists.Count} playlist(s).";
        }
        catch (SpotifyApiException ex) when (ex.StatusCode is 401 or 403)
        {
            RefreshConnectionState();
            StatusText = "Spotify rejected the token - press Connect to log in again.";
            Log.Warn($"Spotify auth problem: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("Loading Spotify playlists failed", ex);
            StatusText = $"Could not load playlists: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedPlaylistChanged(SpotifyPlaylistInfo? value)
    {
        if (_suppressSelection) return;

        // A new selection invalidates the in-flight fetch: without this the slow
        // previous playlist (Liked Songs can be thousands of tracks) lands later
        // and silently replaces the table - and "Download all" then queues it.
        CancelTrackLoad();
        if (value == null) return;
        _ = LoadTracksAsync(value);
    }

    private void CancelTrackLoad()
    {
        try { _loadCts?.Cancel(); } catch { }
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private async Task LoadTracksAsync(SpotifyPlaylistInfo info)
    {
        CancelTrackLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _tracksOwnerId = null;
        IsBusy = true;
        try
        {
            StatusText = $"Loading {info.Name}\u2026";
            var list = await FetchTracksAsync(info.Id, info.Name, info.IsLikedSongs, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedPlaylist, info)) return;

            Tracks.Clear();
            foreach (var track in list) Tracks.Add(track);
            _tracksOwnerId = info.Id;
            UpdateCounts();
            StatusText = $"{info.Name} \u00b7 {list.Count} track(s).";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection - the newer load owns the status text.
        }
        catch (Exception ex)
        {
            Log.Error($"Loading Spotify playlist {info.Name} failed", ex);
            StatusText = $"Could not load {info.Name}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsBusy = false;
                _loadCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task<List<SourceTrack>> FetchTracksAsync(string playlistId, string playlistName,
                                                           bool isLiked, CancellationToken ct)
    {
        if (_trackCache.TryGetValue(playlistId, out var cached))
            return Reconcile(cached);

        var list = isLiked
            ? await _api.GetLikedSongsAsync(ct).ConfigureAwait(true)
            : await _api.GetPlaylistTracksAsync(playlistId, ct).ConfigureAwait(true);

        foreach (var track in list) track.PlaylistName = playlistName;
        _trackCache[playlistId] = list;
        return Reconcile(list);
    }

    /// <summary>Mark tracks the library already holds so the grid shows ticks immediately.</summary>
    private List<SourceTrack> Reconcile(IEnumerable<SourceTrack> tracks)
    {
        foreach (var track in tracks)
        {
            if (track.State is SourceTrackState.Queued or SourceTrackState.Downloading) continue;
            var existing = _ledger.FindPathByLibraryKey(track.Artist, track.Title);
            if (existing != null)
            {
                track.State = SourceTrackState.InLibrary;
                track.LibraryPath = existing;
            }
            else if (track.State == SourceTrackState.InLibrary)
            {
                track.State = SourceTrackState.New;
                track.LibraryPath = null;
            }
        }
        return tracks.ToList();
    }

    /// <summary>Create the persistent playlist record the first time it is downloaded.</summary>
    private void RememberPlaylist(SpotifyPlaylistInfo info, IEnumerable<SourceTrack> tracks)
    {
        var existing = _store.FindBySourceId(MediaSource.Spotify, info.Id);
        if (existing != null)
        {
            _store.AddTracks(existing, tracks.Where(t => t.State != SourceTrackState.InLibrary));
            return;
        }

        var playlist = _store.Create(info.Name, MediaSource.Spotify, info.Id);
        playlist.SyncEnabled = true;
        _store.AddTracks(playlist, tracks);
    }

    // ---- helpers ----------------------------------------------------------

    partial void OnFilterTextChanged(string value) => TracksView.Refresh();

    private bool FilterTrack(object obj)
    {
        if (obj is not SourceTrack track) return false;
        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        return track.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || track.Artist.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || track.Album.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-read the OAuth state into the bindable flags and the one-line state
    /// text. Called at construction and after every login, logout and dialog.
    /// </summary>
    private void RefreshConnectionState()
    {
        NeedsClientId = string.IsNullOrWhiteSpace(_settings.SpotifyClientIdOverride);
        IsLoggedIn = _oauth.IsLoggedIn;
        HasPlaylistScopes = _oauth.HasPlaylistScopes;
        OnPropertyChanged(nameof(HasPlaylistAccess));
        OnPropertyChanged(nameof(ConnectLabel));

        ConnectionStateText = !IsLoggedIn
            ? "Not connected. Press Connect Spotify."
            : NeedsClientId
                ? "Connected - playlist import needs your own Spotify app."
                : HasPlaylistAccess
                    ? "Connected - playlist access granted."
                    : "Connected - playlist access not granted. Press Grant playlist access and allow the playlist permissions.";
    }

    partial void OnHasPlaylistScopesChanged(bool value) => OnPropertyChanged(nameof(HasPlaylistAccess));

    /// <summary>
    /// Carry the per-batch karaoke choice into the one flag MainViewModel reads
    /// for each finished download, without adding a new event. The value that
    /// was in place before the batch is remembered and restored on drain, so a
    /// Spotify batch does not change the global Downloads setting for good.
    /// </summary>
    private void ApplyKaraokeChoiceToBatch()
    {
        if (!_karaokeRestorePending)
        {
            _karaokeValueBeforeBatch = _settings.DownloadAutoKaraoke;
            _karaokeRestorePending = true;
        }
        _settings.DownloadAutoKaraoke = GenerateKaraokeAfterBatch;
    }

    /// <summary>
    /// Put the global karaoke setting back once the queue is idle. MainViewModel's
    /// ItemFinished handler posts to the dispatcher and reads the setting there;
    /// it subscribed first, so its callback runs before this one and the last
    /// item is still decided with the batch value.
    /// </summary>
    private void RestoreKaraokeChoiceWhenIdle()
    {
        if (!_karaokeRestorePending || _queue.IsRunning) return;
        _karaokeRestorePending = false;
        _settings.DownloadAutoKaraoke = _karaokeValueBeforeBatch;
    }

    private void UpdateCounts()
    {
        TotalCount = Tracks.Count;
        InLibraryCount = Tracks.Count(t => t.State == SourceTrackState.InLibrary);
        QueuedCount = Tracks.Count(t => t.State is SourceTrackState.Queued or SourceTrackState.Downloading);
        FailedCount = Tracks.Count(t => t.State == SourceTrackState.Failed);
        PlaylistProgress = TotalCount == 0 ? 0 : InLibraryCount * 100.0 / TotalCount;
        OnPropertyChanged(nameof(CountsText));
    }

    private static void Dispatcher(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(action);
        else action();
    }
}
