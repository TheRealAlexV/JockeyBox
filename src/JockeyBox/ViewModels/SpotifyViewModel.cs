using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Downloads;
using JockeyBox.Lyrics;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Spotify;
using JockeyBox.Tools;
using JockeyBox.Views;
using JockeyBox.YouTube;

namespace JockeyBox.ViewModels;

/// <summary>
/// The Spotify tab: browse the account's playlists, show which tracks are
/// already in the library, and push the rest through the download queue.
///
/// Audio itself never comes from Spotify (its Web Playback SDK is browser-only
/// and the Web API exposes 30-second previews), so auditioning streams the
/// chosen YouTube upload straight into ffplay (<see cref="PreviewStreamPlayer"/>)
/// and deck playback resolves it through the download pipeline - see
/// StreamResolver.
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
    private readonly Action<SourceTrack>? _addToQueue;

    /// <summary>Streams auditions through yt-dlp | ffplay; owns no deck state.</summary>
    private readonly StreamPreviewTransport _stream;

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
                            Func<SourceTrack, Task> play,
                            IPreviewTransport? previewTransport = null,
                            Action<SourceTrack>? addToQueue = null,
                            StreamPreviewTransport? streamPreview = null)
    {
        _settings = settings;
        _store = store;
        _queue = queue;
        _ledger = ledger;
        _oauth = oauth;
        _api = new SpotifyApi(settings, oauth);
        _play = play;
        _addToQueue = addToQueue;

        // Preview = a real stream (yt-dlp piped into ffplay, nothing written to
        // disk). The deck transport can no longer be the preview transport:
        // that path resolved the whole file into the stream cache first.
        var incoming = previewTransport as StreamPreviewTransport;
        _stream = streamPreview ?? incoming ?? StreamPreviewTransport.CreateDefault();
        Preview = new PreviewBarViewModel(_stream);
        _stream.StatusChanged += (_, message) => Dispatcher(() =>
        {
            if (!string.IsNullOrWhiteSpace(message)) StatusText = message;
        });

        TracksView = CollectionViewSource.GetDefaultView(Tracks);
        TracksView.Filter = FilterTrack;

        _queue.ItemFinished += item => Dispatcher(() =>
        {
            if (item.Kind == DownloadKind.Library) ReconcileTrack(item.Track);
            UpdateCounts();
            RestoreKaraokeChoiceWhenIdle();
        });
        // Idle pass: un-wedges rows left mid-transfer by a cancelled/failed item.
        _queue.Drained += () => Dispatcher(() =>
        {
            Reconcile(Tracks);
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

    /// <summary>Preview transport strip under the track table (position/seek/play-pause).</summary>
    public PreviewBarViewModel Preview { get; }

    public ObservableCollection<SpotifyPlaylistInfo> Playlists { get; } = new();
    public ObservableCollection<SourceTrack> Tracks { get; } = new();
    public ICollectionView TracksView { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromSpotifyPlaylistCommand))]
    private bool _isLoggedIn;

    [ObservableProperty] private bool _hasPlaylistScopes;

    /// <summary>
    /// Mirror of the OAuth grant for the write commands. A read-only grant (made
    /// before playlist editing existed) leaves this false and the tab offers
    /// <see cref="ShowReauthorize"/> instead.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromSpotifyPlaylistCommand))]
    private bool _hasPlaylistWriteScopes;

    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromSpotifyPlaylistCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromSpotifyPlaylistCommand))]
    private SpotifyPlaylistInfo? _selectedPlaylist;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromSpotifyPlaylistCommand))]
    private SourceTrack? _selectedTrack;

    [ObservableProperty] private string _filterText = "";

    /// <summary>Name typed into the "+ New" box on the Spotify tab.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSpotifyPlaylistCommand))]
    private string _newPlaylistName = "";

    /// <summary>
    /// True when no Spotify app Client ID is configured. The built-in client
    /// cannot be granted the playlist scopes, so playlist import is impossible
    /// until the user supplies their own app id.
    /// </summary>
    [ObservableProperty] private bool _needsClientId;

    /// <summary>
    /// Set when Spotify rejected a write with 401/403 even though the stored grant
    /// claims the write scopes - the grant is then no longer trustworthy, so the
    /// tab offers Re-authorize and holds the write commands until the next login
    /// or refresh clears this.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedToSpotifyPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedFromSpotifyPlaylistCommand))]
    private bool _writeScopeRejected;

    /// <summary>Mirror of the OAuth grant that the playlist screens care about.</summary>
    public bool HasPlaylistAccess => HasPlaylistScopes;

    /// <summary>
    /// Show the Re-authorize button when signed in without playlist editing
    /// (the only case where re-consent buys something). Hidden when signed out -
    /// plain Connect is the right button then.
    /// </summary>
    public bool ShowReauthorize =>
        IsLoggedIn && !NeedsClientId && (!HasPlaylistWriteScopes || WriteScopeRejected);

    /// <summary>Tooltip on the write controls, explaining a disabled command.</summary>
    public string WriteTooltip => !IsLoggedIn
        ? "Connect Spotify first."
        : WriteScopeRejected
            ? "Spotify refused the last edit - press Re-authorize to grant playlist editing."
            : !HasPlaylistWriteScopes
                ? "Re-authorize Spotify to grant playlist editing."
                : "Add or remove the selected track on the selected playlist.";

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
        : $"{InLibraryCount}/{TotalCount} in library \u00b7 {QueuedCount} downloading" +
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
                $"Download {pending.Count} tracks from \"{SelectedPlaylist.Name}\" into the library?",
                "JockeyBox", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        foreach (var track in pending) StopPreviewIfPlaying(track);

        RememberPlaylist(SelectedPlaylist, Tracks);
        ApplyKaraokeChoiceToBatch();
        var queued = _queue.EnqueueMany(pending);
        UpdateCounts();
        StatusText = $"Downloading {queued} track(s) into the library.";
        Log.Info($"Spotify: queued {queued} of {pending.Count} from {SelectedPlaylist.Name}");
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (SelectedTrack == null) return;
        EnqueueForDownload(SelectedTrack);
        StatusText = $"Download of {SelectedTrack.DisplayName} added to the download queue.";
        await Task.CompletedTask;
    }

    /// <summary>Download the selection and start its preview stream in the deck right away.</summary>
    [RelayCommand]
    private async Task DownloadAndPlaySelectedAsync()
    {
        if (SelectedTrack == null) return;
        var track = SelectedTrack;
        StopPreviewIfPlaying(track);
        EnqueueForDownload(track);
        StatusText = $"Downloading {track.DisplayName} \u2014 playing the stream now\u2026";
        await PlayInDeckAsync(track);
        StatusText = $"Downloading {track.DisplayName} \u00b7 playing the preview stream.";
    }

    /// <summary>One place for "this track goes to the library now": persistence, karaoke, counters.</summary>
    private void EnqueueForDownload(SourceTrack track)
    {
        // Preview and download of the same upload must not overlap; the stream
        // is a live pipe, so it would keep playing under the download.
        StopPreviewIfPlaying(track);
        if (SelectedPlaylist != null && _tracksOwnerId == SelectedPlaylist.Id)
            RememberPlaylist(SelectedPlaylist, Tracks);
        ApplyKaraokeChoiceToBatch();
        _queue.Enqueue(track);
        UpdateCounts();
    }

    [RelayCommand]
    private void AddToQueue(SourceTrack? track)
    {
        track ??= SelectedTrack;
        if (track == null || _addToQueue == null) return;

        _addToQueue(track);
        StatusText = $"Added {track.DisplayName} to the playback queue.";
    }

    [RelayCommand]
    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack == null) return;
        StatusText = $"Resolving {SelectedTrack.DisplayName}\u2026";
        await PlayInDeckAsync(SelectedTrack);
        StatusText = $"Playing {SelectedTrack.DisplayName}.";
    }

    // ---- versions (pick a YouTube upload before downloading) --------------

    /// <summary>How many YouTube hits a version search asks for; the panel keeps at most 50.</summary>
    private const int VersionSearchResults = 30;

    /// <summary>Hard cap on candidates shown, so a wide search cannot flood the panel.</summary>
    private const int MaxVersionCandidates = 50;

    /// <summary>YouTube uploads that could stand in for <see cref="VersionsTarget"/>.</summary>
    public ObservableCollection<VersionCandidate> Versions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionsVisible))]
    private SourceTrack? _versionsTarget;

    [ObservableProperty] private bool _isFindingVersions;
    [ObservableProperty] private string _versionsHeader = "";

    public bool IsVersionsVisible => VersionsTarget != null;

    /// <summary>
    /// Search YouTube for "{Artist} {Title}" and list the uploads, closest
    /// running length first. This is the "which version?" step: Spotify only
    /// tells us the song exists, not which upload to fetch.
    /// </summary>
    [RelayCommand]
    private async Task FindVersionsAsync(SourceTrack? track)
    {
        track ??= SelectedTrack;
        if (track == null) return;

        VersionsTarget = track;
        Versions.Clear();
        IsFindingVersions = true;

        var query = track.SearchQuery;
        VersionsHeader = $"Looking for \"{query}\" on YouTube\u2026";
        StatusText = VersionsHeader;

        try
        {
            var results = await YouTubeApi.SearchAsync(query, VersionSearchResults, _settings, CancellationToken.None)
                .ConfigureAwait(true);
            if (!ReferenceEquals(VersionsTarget, track)) return;   // superseded by another row

            var targetSeconds = Math.Max(0, track.Duration.TotalSeconds);
            var ranked = results
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.VideoId))
                .OrderBy(e => targetSeconds > 0 ? Math.Abs(e.DurationSeconds - targetSeconds) : 0)
                .ThenByDescending(e => e.ViewCount)
                .Take(MaxVersionCandidates)
                .Select(e => new VersionCandidate(e))
                .ToList();

            foreach (var candidate in ranked) Versions.Add(candidate);
            VersionsHeader = ranked.Count == 0
                ? $"No YouTube versions found for \"{query}\"."
                : $"{ranked.Count} version(s) of {track.DisplayName} \u2014 closest length first.";
            StatusText = VersionsHeader;
        }
        catch (Exception ex)
        {
            Log.Error($"Finding versions for {track.DisplayName} failed", ex);
            VersionsHeader = $"Could not search YouTube: {ex.Message}";
            StatusText = VersionsHeader;
        }
        finally
        {
            if (ReferenceEquals(VersionsTarget, track)) IsFindingVersions = false;
        }
    }

    [RelayCommand]
    private void CloseVersions()
    {
        Versions.Clear();
        VersionsTarget = null;
        VersionsHeader = "";
    }

    /// <summary>
    /// Download the picked upload as the Spotify track. The row keeps the
    /// Spotify identity (Id/Artist/Title/Album/Duration) and only VideoId - the
    /// upload to fetch - comes from YouTube, so the file lands in the library as
    /// "Artist - Title" and Reconcile still matches it to this row.
    /// </summary>
    [RelayCommand]
    private void DownloadVersion(VersionCandidate? candidate)
    {
        var target = VersionsTarget;
        if (candidate == null || target == null) return;

        var entry = candidate.Entry;
        target.VideoId = entry.VideoId;
        target.State = SourceTrackState.New;
        target.Error = null;
        Log.Info($"Spotify: {target.DisplayName} -> YouTube {entry.VideoId} \"{entry.Title}\" ({entry.Uploader})");

        EnqueueForDownload(target);
        StatusText = $"Download of {target.DisplayName} added to the download queue \u2014 \"{entry.Title}\" ({entry.Uploader}).";
        // Clearing the list from inside a row's own command would pull that row
        // out from under the click, so let the click finish first.
        var app = Application.Current;
        if (app != null) app.Dispatcher.BeginInvoke(new Action(CloseVersions));
        else CloseVersions();
    }

    /// <summary>Audition one candidate as a live stream; nothing is written to the library or to disk.</summary>
    [RelayCommand]
    private void PreviewVersion(VersionCandidate? candidate)
    {
        var target = VersionsTarget;
        if (candidate == null || target == null) return;

        var entry = candidate.Entry;
        if (string.IsNullOrWhiteSpace(entry.VideoId))
        {
            StatusText = "That upload has no video id, so it cannot be previewed.";
            return;
        }

        if (!_stream.CanStream)
        {
            // Never fall back to downloading the file just to hear it.
            StatusText = _stream.UnavailableText;
            return;
        }

        _stream.Play(PreviewUrl(entry.VideoId), $"{target.DisplayName} \u2014 {entry.Title}");
        StatusText = $"Previewing \"{entry.Title}\" ({entry.Uploader}) \u2014 streaming, nothing is saved.";
    }

    /// <summary>Audition a playlist row that already has a YouTube upload picked.</summary>
    [RelayCommand]
    private void PreviewTrack(SourceTrack? track)
    {
        track ??= SelectedTrack;
        if (track == null) return;

        if (string.IsNullOrWhiteSpace(track.VideoId))
        {
            StatusText = "No YouTube version chosen for that track yet \u2014 find versions first.";
            return;
        }

        if (!_stream.CanStream)
        {
            StatusText = _stream.UnavailableText;
            return;
        }

        _stream.Play(PreviewUrl(track.VideoId), track.DisplayName);
        StatusText = $"Previewing {track.DisplayName} \u2014 streaming, nothing is saved.";
    }

    /// <summary>Stops the stream, e.g. when the tab goes away.</summary>
    public void StopPreview() => _stream.Stop();

    /// <summary>Anything that plays on the deck silences the preview first: one audio source, always.</summary>
    private async Task PlayInDeckAsync(SourceTrack track)
    {
        _stream.Stop();
        await _play(track);
    }

    /// <summary>A download of the track being previewed stops the stream, so the two cannot overlap.</summary>
    private void StopPreviewIfPlaying(SourceTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.VideoId)) _stream.StopIfUrl(PreviewUrl(track.VideoId));
    }

    private static string PreviewUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    [RelayCommand]
    private void OpenVersionOnYouTube(VersionCandidate? candidate) => OpenOnYouTube(candidate?.Entry.VideoId);

    [RelayCommand]
    private void OpenSelectedOnYouTube() =>
        OpenOnYouTube(SelectedTrack?.VideoId ?? VersionsTarget?.VideoId);

    private void OpenOnYouTube(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            StatusText = "No YouTube version chosen for that track yet \u2014 find versions first.";
            return;
        }

        var url = $"https://www.youtube.com/watch?v={videoId}";
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            StatusText = $"Opened {url} in the browser.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the browser: {ex.Message}";
        }
    }

    /// <summary>
    /// The track handed to the queue when the user picks a specific upload.
    /// Identity: <see cref="SourceTrack.Id"/> and <see cref="SourceTrack.VideoId"/>
    /// are the *video id* of the chosen upload (so the queue fetches exactly that
    /// file and never re-runs its own YouTube search), while every metadata field
    /// the library key is built from - Title, Artist, Album, Duration, CoverUrl,
    /// PlaylistName and Source - comes from the Spotify row. The file therefore
    /// lands and tags under the Spotify artist/title, and a later reconcile finds
    /// it by (artist, title) and ticks the original row.
    /// </summary>
    private static SourceTrack BuildVersionTrack(SourceTrack target, YtEntry entry) => new()
    {
        Id = entry.VideoId,
        Source = target.Source,
        VideoId = entry.VideoId,
        Title = target.Title,
        Artist = target.Artist,
        Album = target.Album,
        DurationMs = target.DurationMs,
        CoverUrl = target.CoverUrl,
        PlaylistName = target.PlaylistName,
        State = SourceTrackState.New,
    };

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
            StatusText = added == 0 ? "Playlists already up to date." : $"Downloading {added} new track(s).";
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

    // ---- playlist editing -------------------------------------------------
    //
    // Writes need playlist-modify-private/-public. Everything here is guarded on
    // HasPlaylistWriteScopes and reports a 401/403 through ReportWriteAuthProblem,
    // which re-reads the grant so the Re-authorize button appears.

    private bool CanCreateSpotifyPlaylist() =>
        IsLoggedIn && HasPlaylistWriteScopes && !WriteScopeRejected && !IsBusy
        && !string.IsNullOrWhiteSpace(NewPlaylistName);

    private bool CanEditSelectedPlaylist() =>
        IsLoggedIn && HasPlaylistWriteScopes && !WriteScopeRejected && !IsBusy
        && SelectedTrack != null
        && SelectedPlaylist is { IsLikedSongs: false };

    /// <summary>Create a private playlist on the signed-in account, then show it.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateSpotifyPlaylist))]
    private async Task CreateSpotifyPlaylist()
    {
        var name = NewPlaylistName.Trim();
        if (!CanCreateSpotifyPlaylist()) return;

        IsBusy = true;
        try
        {
            StatusText = $"Creating playlist \"{name}\"\u2026";
            var created = await _api.CreatePlaylistAsync(
                name, description: null, isPublic: false, CancellationToken.None).ConfigureAwait(true);

            NewPlaylistName = "";
            await ReloadPlaylistsAsync(created.Id).ConfigureAwait(true);
            StatusText = $"Created playlist \"{created.Name}\".";
        }
        catch (SpotifyApiException ex) when (ex.NeedsReauthorization)
        {
            ReportWriteAuthProblem(ex);
        }
        catch (Exception ex)
        {
            Log.Error("Creating a Spotify playlist failed", ex);
            StatusText = $"Could not create the playlist: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Add the selected track to the selected playlist (idempotent as Spotify allows duplicates).</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedPlaylist))]
    private async Task AddSelectedToSpotifyPlaylist()
    {
        if (SelectedPlaylist is not { } playlist || SelectedTrack is not { } track) return;

        IsBusy = true;
        try
        {
            StatusText = $"Adding {track.DisplayName} to {playlist.Name}\u2026";
            var added = await _api.AddTracksToPlaylistAsync(
                playlist.Id, new[] { track.Id }, CancellationToken.None).ConfigureAwait(true);

            await ReloadPlaylistsAsync(playlist.Id).ConfigureAwait(true);
            StatusText = added == 0
                ? $"Nothing to add to {playlist.Name}."
                : $"Added {track.DisplayName} to {playlist.Name}.";
        }
        catch (SpotifyApiException ex) when (ex.NeedsReauthorization)
        {
            ReportWriteAuthProblem(ex);
        }
        catch (Exception ex)
        {
            Log.Error($"Adding a track to Spotify playlist {playlist.Name} failed", ex);
            StatusText = $"Could not add the track: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Remove the selected track from the selected playlist.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedPlaylist))]
    private async Task RemoveSelectedFromSpotifyPlaylist()
    {
        if (SelectedPlaylist is not { } playlist || SelectedTrack is not { } track) return;

        IsBusy = true;
        try
        {
            StatusText = $"Removing {track.DisplayName} from {playlist.Name}\u2026";
            var removed = await _api.RemoveTracksFromPlaylistAsync(
                playlist.Id, new[] { track.Id }, CancellationToken.None).ConfigureAwait(true);

            await ReloadPlaylistsAsync(playlist.Id).ConfigureAwait(true);
            StatusText = removed == 0
                ? $"Nothing to remove from {playlist.Name}."
                : $"Removed {track.DisplayName} from {playlist.Name}.";
        }
        catch (SpotifyApiException ex) when (ex.NeedsReauthorization)
        {
            ReportWriteAuthProblem(ex);
        }
        catch (Exception ex)
        {
            Log.Error($"Removing a track from Spotify playlist {playlist.Name} failed", ex);
            StatusText = $"Could not remove the track: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Re-run the consent screen with show_dialog=true so Spotify re-asks for the
    /// permissions - including playlist editing - even for an already-approved app.
    /// </summary>
    [RelayCommand]
    private async Task ReauthorizeSpotifyAsync()
    {
        if (NeedsClientId)
        {
            ShowSetupDialog();
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Opening Spotify to grant playlist editing\u2026";
            await _oauth.LoginAsync(includePlaylists: true, CancellationToken.None, forceConsent: true)
                .ConfigureAwait(true);

            RefreshConnectionState();
            await InitializeAsync(forceReload: true).ConfigureAwait(true);

            StatusText = HasPlaylistWriteScopes
                ? "Connected - playlist editing granted."
                : "Spotify did not grant playlist editing. Press Re-authorize and allow the playlist modify permissions.";
        }
        catch (Exception ex)
        {
            Log.Error("Spotify re-authorization failed", ex);
            StatusText = $"Spotify re-authorization failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// After a write: drop that playlist's cached tracks, reload the rail (the new
    /// playlist, or the changed track count), re-select <paramref name="selectId"/>
    /// and re-fetch its rows so the table matches Spotify.
    /// </summary>
    private async Task ReloadPlaylistsAsync(string? selectId = null)
    {
        var targetId = selectId ?? SelectedPlaylist?.Id;
        if (targetId != null) _trackCache.Remove(targetId);

        await InitializeAsync(forceReload: true).ConfigureAwait(true);

        if (selectId != null && Playlists.FirstOrDefault(p => p.Id == selectId) is { } match)
            SelectedPlaylist = match;

        // InitializeAsync re-selects the previous playlist and fires an async load;
        // awaiting one here supersedes it (LoadTracksAsync cancels the older fetch)
        // so the caller's status line is set after the rows are fresh.
        if (SelectedPlaylist is { } current && current.Id == targetId)
            await LoadTracksAsync(current).ConfigureAwait(true);
    }

    /// <summary>
    /// A write was rejected for auth reasons (401/403): re-read the grant so the
    /// Re-authorize button shows, and say what to press rather than echo Spotify.
    /// </summary>
    private void ReportWriteAuthProblem(SpotifyApiException ex)
    {
        RefreshConnectionState();
        WriteScopeRejected = true;
        StatusText = ex.StatusCode == 403
            ? "Spotify refused the edit - press Re-authorize to grant playlist editing (and check that the playlist is yours)."
            : "Spotify rejected the sign-in - press Re-authorize to grant playlist editing.";
        Log.Warn($"Spotify write rejected ({ex.StatusCode}): {ex.Message}");
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
        foreach (var track in tracks) ReconcileTrack(track);
        return tracks.ToList();
    }

    /// <summary>
    /// Re-derive every visible row after the library changed underneath it (a
    /// download deleted from the Downloads window). These are this tab's own
    /// track objects - the playlist store's sanitize pass cannot reach them -
    /// and the delete arrives on a worker thread, hence the dispatcher hop.
    /// </summary>
    public void ReconcileFromLibrary()
    {
        Dispatcher(() =>
        {
            foreach (var track in Tracks) ReconcileTrack(track);
            UpdateCounts();
        });
    }

    /// <summary>
    /// Self-heal one row against the disk. Only a row the download queue is
    /// genuinely transferring (progress flowing) is left alone; a Queued /
    /// Downloading row that no worker owns is stale and gets re-derived here, so
    /// a cancelled or failed download cannot wedge the row forever.
    /// </summary>
    private void ReconcileTrack(SourceTrack track)
    {
        if (track.IsTransferring) return;

        // Queued but not started: let the queue pick it up instead of flickering.
        // QueueOwns counts a waiting item, so only a row the queue really lost
        // falls through to the disk re-derivation below.
        if (track.IsBusy && QueueOwns(track)) return;

        var existing = _ledger.FindPathByLibraryKey(track.Artist, track.Title);
        if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing))
        {
            track.State = SourceTrackState.InLibrary;
            track.LibraryPath = existing;
        }
        else if (!string.IsNullOrWhiteSpace(track.LibraryPath) && File.Exists(track.LibraryPath))
        {
            track.State = SourceTrackState.InLibrary;
        }
        else
        {
            track.LibraryPath = null;
            if (track.State is SourceTrackState.Downloading or SourceTrackState.Queued)
                track.State = string.IsNullOrWhiteSpace(track.Error)
                    ? SourceTrackState.New
                    : SourceTrackState.Failed;
            else if (track.State == SourceTrackState.InLibrary)
                track.State = SourceTrackState.New;
        }

        if (!string.IsNullOrWhiteSpace(track.PreviewPath) && !File.Exists(track.PreviewPath))
            track.PreviewPath = null;
    }

    /// <summary>
    /// True when the download queue still holds this row: either a worker is on
    /// it, or the item is waiting its turn. A row the queue no longer has is
    /// stale and must be re-derived from disk.
    /// </summary>
    private bool QueueOwns(SourceTrack track) =>
        _queue.Items.Any(item => item.Kind == DownloadKind.Library
                                 && (item.IsActive || item.State == DownloadState.Queued)
                                 && string.Equals(item.Track.Id, track.Id, StringComparison.Ordinal));

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
        WriteScopeRejected = false;
        NeedsClientId = string.IsNullOrWhiteSpace(_settings.SpotifyClientIdOverride);
        IsLoggedIn = _oauth.IsLoggedIn;
        HasPlaylistScopes = _oauth.HasPlaylistScopes;
        HasPlaylistWriteScopes = _oauth.HasPlaylistWriteScopes;
        OnPropertyChanged(nameof(HasPlaylistAccess));
        OnPropertyChanged(nameof(ConnectLabel));
        OnPropertyChanged(nameof(ShowReauthorize));
        OnPropertyChanged(nameof(WriteTooltip));

        ConnectionStateText = !IsLoggedIn
            ? "Not connected. Press Connect Spotify."
            : NeedsClientId
                ? "Connected - playlist import needs your own Spotify app."
                : HasPlaylistAccess
                    ? HasPlaylistWriteScopes
                        ? "Connected - playlist access and editing granted."
                        : "Connected - playlist access granted. Press Re-authorize to allow playlist editing."
                    : "Connected - playlist access not granted. Press Grant playlist access and allow the playlist permissions.";
    }

    partial void OnHasPlaylistScopesChanged(bool value) => OnPropertyChanged(nameof(HasPlaylistAccess));

    partial void OnHasPlaylistWriteScopesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReauthorize));
        OnPropertyChanged(nameof(WriteTooltip));
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReauthorize));
        OnPropertyChanged(nameof(WriteTooltip));
        OnPropertyChanged(nameof(ConnectLabel));
    }

    partial void OnNeedsClientIdChanged(bool value) => OnPropertyChanged(nameof(ShowReauthorize));

    partial void OnWriteScopeRejectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReauthorize));
        OnPropertyChanged(nameof(WriteTooltip));
    }

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

/// <summary>
/// One YouTube upload offered as a stand-in for a Spotify track, with the bits
/// the picker shows (title, channel, length, views) pre-formatted for XAML.
/// </summary>
public sealed class VersionCandidate
{
    public VersionCandidate(YtEntry entry) => Entry = entry;

    public YtEntry Entry { get; }

    public string Title => Entry.Title;
    public string Uploader => Entry.Uploader;
    public string DurationText => Format(Entry.DurationSeconds);
    public string ViewsText => FormatViews(Entry.ViewCount);

    /// <summary>"Channel · 3:42 · 1.2M views" - one line under the title.</summary>
    public string DetailText => $"{Uploader} \u00b7 {DurationText} \u00b7 {ViewsText}";

    private static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private static string FormatViews(long views) => views switch
    {
        >= 1_000_000_000 => (views / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (views / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (views / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => views.ToString(CultureInfo.InvariantCulture),
    };
}
