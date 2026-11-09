using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Downloads;
using JockeyBox.Data;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;
using JockeyBox.Views;
using JockeyBox.YouTube;

namespace JockeyBox.ViewModels;

/// <summary>
/// The YouTube tab: search, play, download, and build playlists out of results
/// or a pasted video/playlist URL.
///
/// Everything plays as audio through the deck engine - the video itself is
/// never rendered, so device routing, per-deck delay, waveform and lyrics keep
/// working on YouTube material exactly as they do on local files.
/// </summary>
public partial class YouTubeViewModel : ObservableObject
{
    /// <summary>How many uploads a channel import pulls in; big channels are unbounded otherwise.</summary>
    private const int ChannelImportLimit = 500;

    private readonly AppSettings _settings;
    private readonly PlaylistStore _store;
    private readonly DownloadQueue _queue;
    private readonly LibraryLedger _ledger;
    private readonly Func<SourceTrack, Task> _play;
    private readonly Action<SourceTrack>? _addToQueue;

    /// <summary>
    /// Library-side verbs for a remote row (own playlists, karaoke facts, reveal,
    /// separate). Owned by MainViewModel; null only in design-time/unit contexts.
    /// </summary>
    private readonly RemoteTrackHost? _remote;

    /// <summary>Streams auditions through yt-dlp | ffplay; owns no deck state.</summary>
    private readonly StreamPreviewTransport _stream;

    /// <summary>Google OAuth is optional to inject so tests and previews can supply their own file.</summary>
    private readonly GoogleOAuthService _auth;
    private readonly YouTubeDataApi _api;

    private CancellationTokenSource? _searchCts;

    /// <summary>Cancels the in-flight load of the selected playlist's tracks.</summary>
    private CancellationTokenSource? _railLoadCts;

    /// <summary>Re-entrancy guard for <see cref="SyncRail"/> (the rail feeds the picker).</summary>
    private bool _syncingRail;

    /// <summary>Selection -> first painted row, for the switch timing line.</summary>
    private System.Diagnostics.Stopwatch? _switchWatch;

    /// <summary>Playlist id the watch belongs to (a later selection wins).</summary>
    private string? _switchingTo;

    /// <summary>Where the rows painted last came from (<c>db</c>/<c>json</c>/cache).</summary>
    private string _rowsSource = "db";

    /// <summary>Milliseconds spent reading the rows themselves (query or graph copy).</summary>
    private double _rowsMs;

    /// <summary>Milliseconds spent re-deriving each row's state from disk/library facts.</summary>
    private double _reconcileMs;

    /// <summary>Generation token of the newest "my playlists" fetch; a new one cancels the old.</summary>
    private CancellationTokenSource? _remoteListCts;

    /// <summary>
    /// The remote playlist list and its items, with TTLs, plus the on-disk copy
    /// the rail paints from before the first request comes back.
    /// </summary>
    private readonly YouTubePlaylistCache _cache = YouTubePlaylistCache.Load();

    /// <summary>The channel the cached state belongs to; a reconnect to a different one drops it.</summary>
    private string _loadedChannelId = "";

    /// <summary>Re-renders the stale timestamp in the strip without a data change.</summary>
    private DispatcherTimer? _stampTimer;

    public YouTubeViewModel(AppSettings settings, PlaylistStore store, DownloadQueue queue,
                            LibraryLedger ledger, Func<SourceTrack, Task> play,
                            IPreviewTransport? previewTransport = null,
                            Action<SourceTrack>? addToQueue = null,
                            GoogleOAuthService? youtubeAuth = null,
                            StreamPreviewTransport? streamPreview = null,
                            RemoteTrackHost? remoteHost = null)
    {
        _settings = settings;
        _store = store;
        _queue = queue;
        _ledger = ledger;
        _play = play;
        _addToQueue = addToQueue;
        _remote = remoteHost;

        // "Add to playlist ▸" on the playlist row menu: the user's own internal
        // playlists plus create-and-add. A YouTube row need not be downloaded.
        if (_remote is not null)
            LibraryPlaylists = new RemotePlaylistTargets(store, _remote.AddToPlaylist);
        _auth = youtubeAuth ?? new GoogleOAuthService();
        _api = new YouTubeDataApi(_auth);
        // Token refreshes happen on a worker thread; every visual consequence
        // has to come back to the dispatcher.
        _auth.Changed += () => Dispatcher(RefreshConnectionState);

        // Preview auditions are a stream, not a download: this player owns its
        // own yt-dlp | ffplay pair and never touches the decks. When the caller
        // passes the deck transport instead, the strip still works, but the
        // audio would come from the deck - the streaming player wins.
        var incoming = previewTransport as StreamPreviewTransport;
        _stream = streamPreview ?? incoming ?? StreamPreviewTransport.CreateDefault();
        Preview = new PreviewBarViewModel(_stream);
        _stream.StatusChanged += (_, message) => Dispatcher(() =>
        {
            if (!string.IsNullOrWhiteSpace(message)) StatusText = message;
        });

        PlaylistTracksView = CollectionViewSource.GetDefaultView(PlaylistTracks);
        PlaylistTracksView.Filter = FilterPlaylistTrack;

        RailView = CollectionViewSource.GetDefaultView(Rail);
        RailView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(YouTubeRailItem.Group)));

        _queue.ItemFinished += item => Dispatcher(() =>
        {
            if (item.Kind == DownloadKind.Library) ReconcileTrack(item.Track);
            UpdateCounts();
        });
        // When the queue goes idle, re-derive every visible row: this is the pass
        // that un-wedges rows a cancelled or failed download left mid-transfer.
        _queue.Drained += () => Dispatcher(() =>
        {
            ReconcileStates();
            UpdateCounts();
        });

        // The rail is this tab's own view of the store (see SyncRail), seeded
        // before anything can select a row.
        _store.Playlists.CollectionChanged += (_, _) => Dispatcher(SyncRail);
        // UserPlaylist.Source is a plain property - nothing raises a change for
        // it - so a re-sourced playlist is only observable once the store saves.
        _store.Changed += () => Dispatcher(SyncRail);
        // Rows written while a channel page could still be stored as a playlist
        // SourceId ("@kavinsky") are unlistable and unsyncable; clear them before
        // the rail is built so the phantom never renders again.
        RepairBrokenYouTubeSources();
        SyncRail();

        UpdateCounts();
        StatusText = "Search YouTube, or paste a video / playlist link.";
        RefreshConnectionState();

        // The cache's age has to stay honest while the tab is open, and it is
        // what tells the user whether the rail's numbers are still current.
        _stampTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _stampTimer.Tick += (_, _) => UpdateRemoteStamp();
        _stampTimer.Start();
    }

    // ---- search -----------------------------------------------------------

    /// <summary>Preview transport strip under the results list (position/seek/play-pause).</summary>
    public PreviewBarViewModel Preview { get; }

    public ObservableCollection<YtEntry> Results { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private YtEntry? _selectedResult;
    [ObservableProperty] private string _searchStatus = "";

    // ---- playlists --------------------------------------------------------

    /// <summary>
    /// The playlist rail for this tab, one row per playlist the user should see:
    /// when the account is connected this is the channel's own playlists
    /// (<c>playlists.list?mine=true</c>, in YouTube's order) followed by any local
    /// import or hand-built list the channel does not have, under
    /// <see cref="YouTubeRailItem.LocalGroup"/>. Disconnected, it is just the
    /// local imports, as before.
    ///
    /// The rows are <see cref="YouTubeRailItem"/>s rather than store rows because
    /// a remote playlist may never have been opened here (and so has no store
    /// row yet); <see cref="YouTubeRailItem.Playlist"/> is filled in lazily.
    /// </summary>
    public ObservableCollection<YouTubeRailItem> Rail { get; } = new();

    /// <summary>
    /// <see cref="Rail"/>, grouped by <see cref="YouTubeRailItem.Group"/> so the
    /// picker can draw "ON YOUTUBE" and "LOCAL ONLY" headers. Grouping keeps
    /// YouTube's order inside each group (first appearance wins).
    /// </summary>
    public ICollectionView RailView { get; }

    public BulkObservableCollection<SourceTrack> PlaylistTracks { get; } = new();
    public ICollectionView PlaylistTracksView { get; }

    /// <summary>The rail selection; <see cref="SelectedPlaylist"/> follows it.</summary>
    [ObservableProperty] private YouTubeRailItem? _selectedRail;

    [ObservableProperty] private UserPlaylist? _selectedPlaylist;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SeparateSelectedKaraokeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealSelectedFileCommand))]
    [NotifyPropertyChangedFor(nameof(HasLocalFileForSelected))]
    private SourceTrack? _selectedPlaylistTrack;
    [ObservableProperty] private string _newPlaylistName = "";
    [ObservableProperty] private string _playlistFilter = "";
    [ObservableProperty] private string _countsText = "";

    /// <summary>False when the rail is empty; drives the "no playlists yet" hint.</summary>
    [ObservableProperty] private bool _hasPlaylists;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _inLibraryCount;
    [ObservableProperty] private int _queuedCount;

    // ---- import -----------------------------------------------------------

    [ObservableProperty] private string _importUrl = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportMyPlaylistsCommand))]
    private bool _isBusy;

    // ---- youtube account / remote write -----------------------------------
    //
    // Credentials deliberately live outside AppSettings (see GoogleOAuthService):
    // %APPDATA%\JockeyBox\youtube-oauth.json, edited through YouTubeAuthDialog.

    /// <summary>True once a refresh token exists - i.e. writes can be attempted.</summary>
    [ObservableProperty] private bool _isYouTubeConnected;

    /// <summary>"YouTube: Channel name", or the not-connected wording.</summary>
    [ObservableProperty] private string _youTubeAccountText = "YouTube: not connected";

    /// <summary>Connect / Reconnect label for the strip's first button.</summary>
    [ObservableProperty] private string _youTubeConnectLabel = "Connect YouTube";

    /// <summary>True while a Data API write is running; disables the write buttons.</summary>
    [ObservableProperty] private bool _isYouTubeBusy;

    /// <summary>True when the selected rail playlist has an id that belongs to the signed-in channel.</summary>
    [ObservableProperty] private bool _isSelectedPlaylistRemote;

    /// <summary>
    /// How many channel playlists the user removed from this rail, so a "show
    /// removed" affordance can be offered (see <see cref="UnhideAllPlaylistsAsync"/>).
    /// </summary>
    [ObservableProperty] private int _removedPlaylistCount;

    [ObservableProperty] private string _remotePlaylistName = "";
    [ObservableProperty] private string _selectedRemotePrivacy = "private";

    [ObservableProperty] private string _addToYouTubeHint = "";
    [ObservableProperty] private string _removeFromYouTubeHint = "";
    [ObservableProperty] private string _createRemoteHint = "";
    [ObservableProperty] private string _importMyPlaylistsHint = "";

    /// <summary>True while a remote playlist's items are loading; shows the strip's spinner text.</summary>
    [ObservableProperty] private bool _isRemoteLoading;

    /// <summary>Dim "updated N min ago" label next to ↻ (see <see cref="UpdateRemoteStamp"/>).</summary>
    [ObservableProperty] private string _remoteStampText = "";

    /// <summary>True when the rail is showing data older than the list TTL.</summary>
    [ObservableProperty] private bool _isRemoteStampStale;

    /// <summary>The channel's own playlists as of the last refresh; drives write targeting.</summary>
    public ObservableCollection<YouTubePlaylist> RemotePlaylists { get; } = new();

    /// <summary>Ids from <see cref="RemotePlaylists"/>; membership means "we may write to it".</summary>
    public ObservableCollection<string> RemotePlaylistIds { get; } = new();

    public IReadOnlyList<string> PrivacyOptions { get; } = new[] { "private", "unlisted", "public" };

    partial void OnSelectedPlaylistTrackChanged(SourceTrack? value) => RefreshWriteHints();

    partial void OnIsYouTubeConnectedChanged(bool value) => RefreshWriteHints();

    partial void OnIsRemoteStampStaleChanged(bool value) => OnPropertyChanged(nameof(RemoteStampOpacity));

    /// <summary>Dim the stale timestamp instead of hiding it or blocking the rail on it.</summary>
    public double RemoteStampOpacity => IsRemoteStampStale ? 0.45 : 0.8;

    /// <summary>
    /// A rail row became the selection: point the pane at its store row, creating
    /// that row for a remote playlist the first time it is opened (a 200-playlist
    /// channel must not leave 200 empty rows in playlists.json), then let
    /// <see cref="OnSelectedPlaylistChanged"/> load the tracks.
    /// </summary>
    partial void OnSelectedRailChanged(YouTubeRailItem? value)
    {
        if (value == null)
        {
            SelectedPlaylist = null;
            return;
        }

        var playlist = value.Playlist
                       ?? (value.IsRemote ? EnsureRemotePlaylistRow(value) : null);
        if (playlist != null)
        {
            value.Playlist = playlist;
            if (!string.IsNullOrWhiteSpace(value.Title) && playlist.Name != value.Title
                && string.IsNullOrWhiteSpace(playlist.SourceId))
            {
                playlist.Name = value.Title;
            }
        }
        SelectedPlaylist = playlist;
    }

    /// <summary>The store row that mirrors a remote playlist; created on first open.</summary>
    private UserPlaylist EnsureRemotePlaylistRow(YouTubeRailItem item)
    {
        var remoteId = item.RemoteId ?? "";
        var playlist = _store.Create(
            string.IsNullOrWhiteSpace(item.Title) ? "YouTube playlist" : item.Title,
            MediaSource.YouTube, remoteId);
        // The yt-dlp "Sync playlists" pass is for URL imports: this row is fed
        // by the Data API, and auto-queueing downloads on sync would surprise.
        playlist.SyncEnabled = false;
        _store.Save();
        return playlist;
    }

    partial void OnSelectedPlaylistChanged(UserPlaylist? value)
    {
        // Cancel the previous playlist's load: a fast click-through must not
        // append one playlist's pages to another's rows.
        _railLoadCts?.Cancel();
        _railLoadCts?.Dispose();
        _railLoadCts = new CancellationTokenSource();

        // Times the whole switch: selection -> store/DB read -> one collection
        // reset -> rows bound. Logged by ReplaceRows.
        _switchWatch = System.Diagnostics.Stopwatch.StartNew();
        _switchingTo = value?.Id;

        if (value == null)
        {
            PlaylistTracks.Clear();
            UpdateCounts();
            RefreshWriteHints();
            return;
        }

        _ = LoadPlaylistRowsAsync(value, _railLoadCts.Token);
        Log.Debug($"youtube playlist '{value.Name}': {PlaylistTracks.Count} row(s), view {PlaylistTracksView.Cast<object>().Count()}");
        RefreshWriteHints();
    }

    partial void OnPlaylistFilterChanged(string value) => PlaylistTracksView.Refresh();

    // The write strip's buttons are CanExecute-driven, so every input they read
    // has to re-notify: the name box (enable state), the privacy picker and the
    // busy flag (both appear in tooltips), and the rail selection (elsewhere).
    partial void OnRemotePlaylistNameChanged(string value) => RefreshWriteHints();

    partial void OnSelectedRemotePrivacyChanged(string value) => RefreshWriteHints();

    partial void OnIsYouTubeBusyChanged(bool value) => RefreshWriteHints();

    /// <summary>A rail row is a YouTube import or a hand-built list - never a Spotify one.</summary>
    private static bool InRail(UserPlaylist playlist) =>
        playlist.Source is MediaSource.YouTube or MediaSource.Manual;

    /// <summary>
    /// Rebuild <see cref="Rail"/> from the last-known remote list
    /// (<see cref="RemotePlaylists"/>, itself cached: memory TTL then
    /// %APPDATA%\JockeyBox\youtube-playlists-cache.json) followed by the store's
    /// local rows the channel does not have. Rows are reused rather than
    /// recreated so the picker's selection survives a rebuild, and the selection
    /// is re-anchored by playlist identity afterwards.
    ///
    /// Nothing here touches the network: this runs from the store's own
    /// CollectionChanged and from every cache/remote update.
    /// </summary>
    private void SyncRail()
    {
        if (_syncingRail) return;
        _syncingRail = true;
        try
        {
            var desired = BuildRailItems();
            var existing = Rail.ToDictionary(r => r.Key, StringComparer.Ordinal);

            // Merge into the rows already on the rail, then place them in order.
            for (var i = 0; i < desired.Count; i++)
            {
                if (existing.TryGetValue(desired[i].Key, out var row)) desired[i] = row.MergeFrom(desired[i]);
            }

            for (var i = Rail.Count - 1; i >= 0; i--)
            {
                if (!desired.Any(d => d.Key == Rail[i].Key)) Rail.RemoveAt(i);
            }

            for (var i = 0; i < desired.Count; i++)
            {
                var at = IndexOfRail(desired[i].Key);
                if (at < 0) Rail.Insert(i, desired[i]);
                else if (at != i) Rail.Move(at, i);
            }

            HasPlaylists = Rail.Count > 0;
            RemovedPlaylistCount = _cache.HiddenIds.Count;

            // Re-anchor the selection by key: a rebuild keeps the same playlist
            // selected, but a playlist that was deleted (or that lost its remote
            // id) must not keep the pane pointed at it.
            var key = SelectedRail?.Key ?? (SelectedPlaylist != null ? "local:" + SelectedPlaylist.Id : null);
            var anchor = key != null ? Rail.FirstOrDefault(r => r.Key == key) : null;
            SelectedRail = anchor ?? Rail.FirstOrDefault();
        }
        finally
        {
            _syncingRail = false;
        }
    }

    private int IndexOfRail(string key)
    {
        for (var i = 0; i < Rail.Count; i++)
            if (Rail[i].Key == key) return i;
        return -1;
    }

    /// <summary>
    /// The rail's rows, in display order: remote playlists in YouTube's order,
    /// then local rows the channel does not have. A local row whose id is on the
    /// channel becomes the remote row's backing store, so its downloads and
    /// queue state survive the upgrade from "import" to "remote".
    /// </summary>
    private List<YouTubeRailItem> BuildRailItems()
    {
        var items = new List<YouTubeRailItem>(Rail.Count + 4);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var seenRemote = new HashSet<string>(StringComparer.Ordinal);

        if (IsYouTubeConnected)
        {
            foreach (var remote in RemotePlaylists)
            {
                if (string.IsNullOrWhiteSpace(remote.Id) || !seenRemote.Add(remote.Id)) continue;

                // The store row already mirroring this id, if any: matching it
                // keeps downloads and queue state attached to the same object
                // when an import is upgraded to a remote row.
                var local = _store.Playlists.FirstOrDefault(p =>
                    InRail(p) && !claimed.Contains(p.Id) && PlaylistIdOf(p) == remote.Id);
                if (local != null) claimed.Add(local.Id);

                items.Add(new YouTubeRailItem
                {
                    Key = "remote:" + remote.Id,
                    Title = string.IsNullOrWhiteSpace(remote.Title) ? remote.Id : remote.Title,
                    Group = YouTubeRailItem.RemoteGroup,
                    IsRemote = true,
                    Count = remote.ItemCount,
                    RemoteId = remote.Id,
                    Playlist = local,
                });
            }
        }

        // Everything else the tab has always shown, under LOCAL ONLY: imports by
        // URL and hand-built lists. A matched row is skipped (it is the remote
        // row above), never deleted or hidden.
        foreach (var playlist in _store.Playlists.Where(InRail).ToList())
        {
            if (claimed.Contains(playlist.Id)) continue;
            items.Add(new YouTubeRailItem
            {
                Key = "local:" + playlist.Id,
                Title = playlist.Name,
                Group = YouTubeRailItem.LocalGroup,
                IsRemote = false,
                Count = playlist.Tracks.Count,
                RemoteId = null,
                Playlist = playlist,
            });
        }

        return items;
    }

    /// <summary>
    /// The selected playlist, but only while the rail is actually showing it.
    /// Every command that acts on "the selected playlist" goes through this, so
    /// a stale selection can never mutate a playlist this tab does not list.
    /// </summary>
    private UserPlaylist? CurrentPlaylist =>
        SelectedPlaylist != null && Rail.Any(r => ReferenceEquals(r.Playlist, SelectedPlaylist))
            ? SelectedPlaylist
            : null;

    // ---- remote track loading ---------------------------------------------

    /// <summary>
    /// Fill the pane for the selected playlist. Local-only rows keep the old
    /// behaviour (paint the store, no network); a remote row paints the last-known
    /// items instantly - memory or the on-disk cache - and then, unless that
    /// cache is still fresh, fetches page by page from the Data API. Each page is
    /// applied as it lands, so a 300-item playlist fills in progressively and the
    /// rail stays responsive; the UI thread is never awaited on.
    /// </summary>
    private async Task LoadPlaylistRowsAsync(UserPlaylist playlist, CancellationToken ct)
    {
        var remoteId = IsYouTubeConnected && Rail.Any(r => ReferenceEquals(r.Playlist, playlist))
            ? RemoteIdFor(playlist)
            : null;

        // Worth preferring the API only when the playlist is on *our* channel;
        // an import of someone else's list still goes through yt-dlp.
        if (remoteId == null)
        {
            ReplaceRows(playlist, LoadStoreRows(playlist));
            return;
        }

        if (_cache.GetAnyItems(remoteId, out _) is { } cached)
        {
            ApplyItems(playlist, cached);
            if (_cache.IsItemsFresh(remoteId, out _))
            {
                // Fresh enough: the pane is already correct and the strip keeps
                // showing the list's age instead of a fake "loading".
                return;
            }
        }
        else if (playlist.Tracks.Count > 0)
        {
            ReplaceRows(playlist, LoadStoreRows(playlist));
        }

        await FetchRemoteItemsAsync(playlist, remoteId, ct, force: false);
    }

    /// <summary>
    /// The rows of a stored playlist. With a database in use this is one indexed
    /// query (<see cref="PlaylistStore.LoadPlaylistRows"/>) materialised into a
    /// list; the JSON path stays the fallback. Recorded in
    /// <see cref="_rowsSource"/> for the switch timing line.
    /// </summary>
    private IReadOnlyList<SourceTrack> LoadStoreRows(UserPlaylist playlist)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var fromDb = _store.LoadPlaylistRows(playlist.Id);
            if (fromDb is not null)
            {
                _rowsSource = "db";
                return fromDb;
            }

            _rowsSource = _store.LoadSource;
            return playlist.Tracks.ToList();
        }
        finally
        {
            _rowsMs = watch.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Forced re-read of the selected remote playlist (the strip's ↻).</summary>
    private async Task ReloadSelectedRemoteAsync(bool force)
    {
        if (SelectedRail?.Playlist is not { } playlist) return;
        if (RemoteIdFor(playlist) is not { } remoteId) return;

        _railLoadCts?.Cancel();
        _railLoadCts?.Dispose();
        _railLoadCts = new CancellationTokenSource();
        await FetchRemoteItemsAsync(playlist, remoteId, _railLoadCts.Token, force);
    }

    /// <summary>
    /// playlistItems.list, one page at a time (50 items per call, 1 quota unit
    /// each), each page painted as it arrives. On failure the pane keeps what it
    /// has; only when nothing at all was painted does this fall back to the
    /// yt-dlp import path, which costs no quota but is far slower.
    /// </summary>
    private async Task FetchRemoteItemsAsync(UserPlaylist playlist, string remoteId,
                                            CancellationToken ct, bool force)
    {
        var all = new List<YouTubePlaylistItem>(256);
        var paintedAny = false;
        try
        {
            IsRemoteLoading = true;
            RemoteStampText = $"loading {playlist.Name}\u2026";
            RemoteStampOpacityNotify();

            if (!force && _cache.GetItems(remoteId, out _) is { } fresh)
            {
                // Another tab of this VM (or a previous selection) just fetched
                // it: no request, no quota.
                ApplyItems(playlist, fresh);
                return;
            }

            await foreach (var page in _api.ListPlaylistItemsPagesAsync(remoteId, ct))
            {
                ct.ThrowIfCancellationRequested();
                all.AddRange(page);
                if (!paintedAny)
                {
                    ApplyItems(playlist, all);
                    paintedAny = true;
                }
                else
                {
                    AppendItems(playlist, page);
                }
            }

            await FillDurationsAsync(all, ct);
            ct.ThrowIfCancellationRequested();

            _cache.SetItems(remoteId, all, DateTime.UtcNow);
            _cache.Save();

            ApplyItems(playlist, all);
            _store.Save();
            UpdateRemoteStamp();
        }
        catch (OperationCanceledException)
        {
            // A newer selection owns the pane now; leave it alone.
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube: playlistItems.list failed for {remoteId}: {ex.Message}");
            if (!paintedAny && all.Count == 0 && playlist.Tracks.Count == 0 && !force)
                await FallbackToYtDlpAsync(playlist, remoteId, ct);

            StatusText = $"Could not read \"{playlist.Name}\" from YouTube ({ex.Message}) - showing what is on disk.";
        }
        finally
        {
            IsRemoteLoading = false;
            UpdateRemoteStamp();
            RefreshWriteHints();
        }
    }

    /// <summary>
    /// Make <paramref name="items"/> the pane's rows, in YouTube's order. Rows
    /// already in the store are reused by video id, so download state, library
    /// hits and the preview survive a refresh; rows that are on disk (or in the
    /// queue) but no longer in the playlist are kept after the remote ones
    /// rather than silently dropped.
    /// </summary>
    private void ApplyItems(UserPlaylist playlist, IReadOnlyList<YouTubePlaylistItem> items)
    {
        var rows = new List<SourceTrack>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var existing = playlist.Tracks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.VideoId)) continue;
            var id = item.VideoId;
            if (!seen.Add(id)) continue;

            if (existing.TryGetValue(id, out var row))
            {
                row.VideoId = id;
                if (row.DurationMs == 0 && item.DurationSeconds > 0) row.DurationMs = item.DurationSeconds * 1000;
                if (string.IsNullOrWhiteSpace(row.Artist)) row.Artist = item.ChannelTitle;
                if (string.IsNullOrWhiteSpace(row.CoverUrl)) row.CoverUrl = ThumbnailFor(id);
                rows.Add(row);
            }
            else
            {
                rows.Add(NewRow(playlist, item));
            }
        }

        foreach (var track in playlist.Tracks)
        {
            if (seen.Contains(track.Id)) continue;
            var keep = track.State is not SourceTrackState.New
                       || !string.IsNullOrWhiteSpace(track.LibraryPath);
            if (keep) rows.Add(track);
        }

        ReplaceRows(playlist, rows);
    }

    /// <summary>
    /// Append one page's rows without re-planning the pane: this is what keeps a
    /// long playlist from re-rendering on every page boundary.
    /// </summary>
    private void AppendItems(UserPlaylist playlist, IReadOnlyList<YouTubePlaylistItem> page)
    {
        Dispatcher(() =>
        {
            foreach (var item in page)
            {
                if (string.IsNullOrWhiteSpace(item.VideoId)) continue;
                if (playlist.Tracks.Any(t => t.Id == item.VideoId)) continue;

                var row = NewRow(playlist, item);
                playlist.Tracks.Add(row);
                PlaylistTracks.Add(row);
                ReconcileTrack(row);
            }

            ApplyLibraryFacts();
            UpdateCounts();
        });
    }

    private SourceTrack NewRow(UserPlaylist playlist, YouTubePlaylistItem item)
    {
        var (title, artist) = MediaTitle.Clean(item.Title, item.ChannelTitle);
        return new SourceTrack
        {
            Id = item.VideoId,
            Source = MediaSource.YouTube,
            Title = title.Length > 0 ? title : item.Title,
            Artist = artist.Length > 0 ? artist : item.ChannelTitle,
            VideoId = item.VideoId,
            CoverUrl = ThumbnailFor(item.VideoId),
            DurationMs = item.DurationSeconds > 0 ? item.DurationSeconds * 1000 : 0,
            PlaylistName = playlist.Name,
            State = SourceTrackState.New,
        };
    }

    /// <summary>
    /// Cover art for a remote row. playlistItems.snippet carries no thumbnails,
    /// and i.ytimg.com serves every video's still by id - the same URL yt-dlp
    /// reports, so a row looks identical before and after it is downloaded.
    /// </summary>
    private static string ThumbnailFor(string videoId) =>
        string.IsNullOrWhiteSpace(videoId) ? "" : $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    /// <summary>
    /// Replace the pane's rows in one pass, then re-derive their state from disk.
    /// Runs on the dispatcher; the selection is re-anchored by video id so the
    /// strip keeps pointing at the same track across a refresh.
    ///
    /// <para>The pane is filled with a single Reset
    /// (<see cref="BulkObservableCollection{T}.ReplaceAll"/>): a 5 000-row list
    /// costs one layout pass, not 5 000. The store graph is updated only when it
    /// differs, which it does not when the rows came from the store.</para>
    /// </summary>
    private void ReplaceRows(UserPlaylist? playlist, IReadOnlyList<SourceTrack> rows)
    {
        Dispatcher(() =>
        {
            var selectedVideoId = SelectedPlaylistTrack?.VideoId;

            PlaylistTracks.ReplaceAll(rows);

            var graph = System.Diagnostics.Stopwatch.StartNew();
            if (playlist != null)
            {
                if (!SameRows(playlist.Tracks, rows))
                {
                    playlist.Tracks.Clear();
                    foreach (var track in rows)
                    {
                        track.PlaylistName = playlist.Name;
                        playlist.Tracks.Add(track);
                    }
                }
                else
                {
                    foreach (var track in rows) track.PlaylistName = playlist.Name;
                }
            }

            graph.Stop();
            var reconcile = System.Diagnostics.Stopwatch.StartNew();
            ReconcileStates();
            reconcile.Stop();
            _reconcileMs = reconcile.Elapsed.TotalMilliseconds;
            // A playlist loaded after the scan finished must still get its karaoke
            // column: the scan's apply pass ran before these rows existed.
            ApplyLibraryFacts();
            SelectedPlaylistTrack = string.IsNullOrWhiteSpace(selectedVideoId)
                ? null
                : PlaylistTracks.FirstOrDefault(t => t.VideoId == selectedVideoId);
            UpdateCounts();
            PlaylistTracksView.Refresh();
            RefreshWriteHints();

            LogSwitchTiming(playlist, rows.Count, graph.Elapsed.TotalMilliseconds);
        });
    }

    /// <summary>Reference-equality check: the store already holds exactly these rows.</summary>
    private static bool SameRows(IReadOnlyList<SourceTrack> stored, IReadOnlyList<SourceTrack> rows)
    {
        if (stored.Count != rows.Count) return false;
        for (var i = 0; i < rows.Count; i++)
            if (!ReferenceEquals(stored[i], rows[i])) return false;
        return true;
    }

    /// <summary>
    /// <c>playlist switch '&lt;name&gt;': &lt;n&gt; rows in &lt;ms&gt; ms
    /// (source=db|json)</c> - the evidence line for requirement 12.
    /// </summary>
    private void LogSwitchTiming(UserPlaylist? playlist, int rowCount, double graphMs)
    {
        if (playlist is null || playlist.Id != _switchingTo) return;

        var watch = _switchWatch;
        _switchWatch = null;
        if (watch is null) return;

        Log.Info(
            $"playlist switch '{playlist.Name}': {rowCount} rows in " +
            $"{watch.Elapsed.TotalMilliseconds:F0} ms (source={_rowsSource} " +
            $"load={_rowsMs:F0}ms graph={graphMs:F0}ms reconcile={_reconcileMs:F0}ms)");
        _switchingTo = null;
    }

    /// <summary>
    /// playlistItems.snippet carries no duration and the pane has a duration
    /// column: one videos.list call per 50 ids fills it in (1 quota unit each).
    /// Purely cosmetic - a failure leaves the rows without a time.
    /// </summary>
    private async Task FillDurationsAsync(List<YouTubePlaylistItem> items, CancellationToken ct)
    {
        var missing = items
            .Where(i => i.DurationSeconds <= 0 && !string.IsNullOrWhiteSpace(i.VideoId))
            .Select(i => i.VideoId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missing.Count == 0) return;

        try
        {
            var durations = await _api.GetDurationsAsync(missing, ct);
            foreach (var item in items)
            {
                if (item.DurationSeconds <= 0 && durations.TryGetValue(item.VideoId, out var seconds))
                    item.DurationSeconds = seconds;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"YouTube: durations unavailable for {missing.Count} row(s): {ex.Message}");
        }
    }

    /// <summary>
    /// The pre-Data-API import path, used only when the API refused the list
    /// outright: no quota, no durations, slower. Errors stay in the log - the
    /// caller already told the user what happened.
    /// </summary>
    private async Task FallbackToYtDlpAsync(UserPlaylist playlist, string remoteId, CancellationToken ct)
    {
        try
        {
            RemoteStampText = "falling back to yt-dlp\u2026";
            var entries = await YtDlp.GetPlaylistAsync(
                $"https://www.youtube.com/playlist?list={remoteId}", _settings, ct, maxEntries: 0);
            if (entries.Count == 0 || ct.IsCancellationRequested) return;

            var items = entries.Select(e => new YouTubePlaylistItem
            {
                VideoId = e.VideoId,
                ItemId = e.VideoId,
                Title = e.Title,
                ChannelTitle = e.Uploader,
                DurationSeconds = (int)e.DurationSeconds,
            }).ToList();

            ApplyItems(playlist, items);
            // Not cached: the next selection should try the cheap API again.
            _store.Save();
            StatusText = $"Loaded \"{playlist.Name}\" through yt-dlp ({items.Count} item(s)).";
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube: yt-dlp fallback failed for {remoteId}: {ex.Message}");
        }
    }

    private void RemoteStampOpacityNotify() => OnPropertyChanged(nameof(RemoteStampOpacity));

    // ---- commands: search -------------------------------------------------

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0) return;

        // A pasted link or bare id is an import, not a search, wherever it is
        // typed: one listing, one place to choose what to download. A channel
        // link parses to the importer's one-line rejection instead of silently
        // importing every upload.
        if (YouTubeImportParser.TryParse(query, out _, out var pastedError)
            || pastedError == YouTubeImportParser.ChannelMessage)
        {
            ImportUrl = query;
            await LoadImporterAsync();
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            IsSearching = true;
            SearchStatus = "Searching\u2026";
            var results = await YouTubeApi.SearchAsync(query, _settings.YouTubeSearchResults, _settings, ct);
            if (ct.IsCancellationRequested) return;

            Results.Clear();
            foreach (var entry in results) Results.Add(entry);
            SearchStatus = results.Count == 0 ? "No results." : $"{results.Count} result(s).";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            SearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task PlayResultAsync()
    {
        if (SelectedResult == null) return;
        var track = ToTrack(SelectedResult);
        StatusText = $"Resolving {track.DisplayName}\u2026";
        await PlayInDeckAsync(track);
        StatusText = $"Playing {track.DisplayName}.";
    }

    /// <summary>
    /// Queue one playlist row (or the selected result, when called without a
    /// parameter). Playlist rows are already <see cref="SourceTrack"/> objects.
    /// </summary>
    [RelayCommand]
    private void AddToQueue(SourceTrack? track)
    {
        track ??= SelectedPlaylistTrack;
        if (track == null || _addToQueue == null) return;

        _addToQueue(track);
        StatusText = $"Added {track.DisplayName} to the playback queue.";
    }

    /// <summary>Queue the selected search result without downloading it first.</summary>
    [RelayCommand]
    private void AddResultToQueue()
    {
        if (SelectedResult == null) return;
        AddToQueue(ToTrack(SelectedResult));
    }

    /// <summary>
    /// Audition one search result (row or context menu) without downloading it:
    /// yt-dlp is piped straight into ffplay, so the sound starts in about a
    /// second and nothing is written to disk.
    /// </summary>
    [RelayCommand]
    private void PreviewResult()
    {
        if (SelectedResult == null) return;
        PreviewEntry(SelectedResult);
    }

    [RelayCommand]
    private void PreviewEntry(YtEntry? entry)
    {
        if (entry == null) return;
        PreviewTrack(ToTrack(entry));
    }

    /// <summary>Audition one playlist row: same pipeline, straight from its video id.</summary>
    [RelayCommand]
    private void PreviewPlaylistTrack(SourceTrack? track)
    {
        track ??= SelectedPlaylistTrack;
        if (track == null) return;
        PreviewTrack(track);
    }

    /// <summary>Stops whatever is streaming, e.g. when the tab goes away or a download starts.</summary>
    public void StopPreview() => _stream.Stop();

    private void PreviewTrack(SourceTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.VideoId))
        {
            StatusText = "This row has no YouTube upload picked yet \u2014 find versions first.";
            return;
        }

        if (!_stream.CanStream)
        {
            // Never fall back to downloading: say why the stream cannot start.
            StatusText = _stream.UnavailableText;
            return;
        }

        _stream.Play(PreviewUrl(track.VideoId), track.DisplayName);
        StatusText = $"Previewing {track.DisplayName} \u2014 streaming, nothing is saved.";
    }

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

    /// <summary>
    /// Download one specific search result into the library. The row buttons bind
    /// this with the row itself as parameter, so a download never depends on what
    /// happens to be selected in the toolbar.
    /// </summary>
    [RelayCommand]
    private void DownloadEntry(YtEntry? entry)
    {
        if (entry == null) return;
        var track = ToTrack(entry);
        if (CurrentPlaylist is { } playlist) track.PlaylistName = playlist.Name;
        StopPreviewIfPlaying(track);
        _queue.Enqueue(track);
        StatusText = $"Download of {track.DisplayName} added to the download queue.";
    }

    [RelayCommand]
    private void DownloadResult()
    {
        if (SelectedResult == null) return;
        DownloadEntry(SelectedResult);
    }

    /// <summary>Download it properly and start the cached stream in the deck now.</summary>
    [RelayCommand]
    private async Task DownloadAndPlayResultAsync()
    {
        if (SelectedResult == null) return;
        var track = ToTrack(SelectedResult);
        if (CurrentPlaylist is { } playlist) track.PlaylistName = playlist.Name;
        StopPreviewIfPlaying(track);
        _queue.Enqueue(track);
        StatusText = $"Downloading {track.DisplayName} \u2014 playing the stream now\u2026";
        await PlayInDeckAsync(track);
        StatusText = $"Downloading {track.DisplayName} \u00b7 playing the preview stream.";
    }

    /// <summary>
    /// Fallback for anything the in-app preview cannot do (e.g. watching the
    /// actual video): hand the watch URL to the shell.
    /// </summary>
    [RelayCommand]
    private void OpenResultOnYouTube() => OpenOnYouTube(SelectedResult);

    [RelayCommand]
    private void OpenEntryOnYouTube(YtEntry? entry) => OpenOnYouTube(entry);

    private void OpenOnYouTube(YtEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.VideoId))
        {
            StatusText = "That result has no video id to open.";
            return;
        }

        var url = $"https://www.youtube.com/watch?v={entry.VideoId}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusText = $"Opened {url} in the browser.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the browser: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddResultToPlaylistAsync()
    {
        if (SelectedResult == null) return;

        var playlist = CurrentPlaylist ?? CreatePlaylistInternal("YouTube Picks");
        var added = _store.AddTracks(playlist, new[] { ToTrack(SelectedResult) });
        SelectedPlaylist = playlist;
        OnSelectedPlaylistChanged(playlist);
        StatusText = added > 0
            ? $"Added \"{SelectedResult.Title}\" to {playlist.Name}."
            : "That video is already in the playlist.";
        await Task.CompletedTask;
    }

    // ---- commands: playlists ---------------------------------------------

    [RelayCommand]
    private void CreatePlaylist()
    {
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) name = $"Playlist {Rail.Count + 1}";
        var playlist = CreatePlaylistInternal(name);
        NewPlaylistName = "";
        SelectPlaylist(playlist);
        StatusText = $"Created {name} (local only - + Remote puts it on YouTube).";
    }

    [RelayCommand]
    private void RenamePlaylist()
    {
        if (CurrentPlaylist is not { } playlist) return;
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) return;
        playlist.Name = name;
        foreach (var track in playlist.Tracks) track.PlaylistName = name;
        // A remote playlist's title lives on YouTube too: rename the rail row
        // (and the cached list) so the two do not disagree until the next fetch.
        var remoteId = RemoteIdFor(playlist);
        if (remoteId != null)
        {
            foreach (var remote in RemotePlaylists.Where(p => p.Id == remoteId)) remote.Title = name;
            if (SelectedRail != null) SelectedRail.Title = name;
            if (_cache.List is { } cached)
            {
                foreach (var entry in cached.Playlists.Where(p => p.Id == remoteId)) entry.Title = name;
            }
            _cache.Save();
            StatusText = $"Renamed here to {name}. YouTube still shows its old title.";
        }
        else
        {
            SyncRail();
            StatusText = $"Renamed to {name}.";
        }
        _store.Save();
        NewPlaylistName = "";
    }

    /// <summary>
    /// Remove the selected rail row. Every kind of row goes: a local list (the
    /// store row is deleted), a channel playlist mirrored by a store row (the row
    /// is deleted, the playlist stays on YouTube), and a channel playlist that was
    /// never opened here - that one has no store row at all, so it is remembered as
    /// hidden in the cache, or the next <c>mine=true</c> would put it straight back.
    /// </summary>
    [RelayCommand]
    private void RemovePlaylist()
    {
        // A row that has left the rail (a refresh, a delete elsewhere) must not be
        // acted on: fall back to the playlist the pane actually shows.
        if (SelectedRail is not { } row || !Rail.Contains(row))
        {
            if (CurrentPlaylist is not { } current) return;
            var currentName = current.Name;
            if (!_store.Delete(current))
            {
                StatusText = $"{currentName} is not in this rail any more.";
                return;
            }

            SyncRail();
            SelectPlaylist(Rail.FirstOrDefault()?.Playlist);
            StatusText = $"Deleted {currentName}." + DatabaseWarning();
            return;
        }

        var name = string.IsNullOrWhiteSpace(row.Title) ? "playlist" : row.Title;
        var isRemote = row.RemoteId != null || RemoteIdFor(row.Playlist) != null;
        var removed = false;

        if (row.Playlist is { } playlist) removed = _store.Delete(playlist);

        if (row.IsRemote && !string.IsNullOrWhiteSpace(row.RemoteId))
        {
            _cache.Hide(row.RemoteId);
            RemotePlaylistIds.Remove(row.RemoteId);
            var cached = RemotePlaylists.FirstOrDefault(p => p.Id == row.RemoteId);
            if (cached != null) RemotePlaylists.Remove(cached);
            removed = true;   // hidden in the cache, so the rail drops it
        }

        if (!removed)
        {
            // No store row and not a remote row: there is nothing to delete, and
            // saying "Deleted" would be a lie.
            StatusText = $"{name} is not in this rail any more.";
            return;
        }

        // _store.Remove already raises the store's CollectionChanged, but call it
        // here too so the rail is correct even if that arrived off the dispatcher.
        SyncRail();
        if (Rail.FirstOrDefault() is { } next) SelectedRail = next;
        SelectPlaylist(Rail.FirstOrDefault()?.Playlist);

        StatusText = (isRemote
            ? $"Removed {name} from JockeyBox. It is still on your YouTube channel."
            : $"Deleted {name}.") + DatabaseWarning();
    }

    /// <summary>
    /// The sentence a delete appends when the SQLite mirror failed: the database wins
    /// on load, so the row can come back next start and the user should hear it.
    /// </summary>
    private string DatabaseWarning() =>
        _store.LastDatabaseWriteFailed
            ? " library.db could not be updated, so it may return on the next start."
            : "";

    /// <summary>
    /// Bring one removed channel playlist back to the rail. A removal is hidden in
    /// the cache rather than deleted on YouTube, so undoing it is a cache write plus
    /// a list refresh: <see cref="ApplyRemoteList"/> stops filtering the id.
    /// </summary>
    [RelayCommand]
    private async Task UnhidePlaylistAsync(string? remoteId)
    {
        if (string.IsNullOrWhiteSpace(remoteId) || !_cache.IsHidden(remoteId)) return;

        _cache.Unhide(remoteId);
        await RefreshRemotePlaylistsAsync(report: false);
        StatusText = "Playlist restored to the rail.";
    }

    /// <summary>Undo every removal on this rail (the "show removed" affordance).</summary>
    [RelayCommand]
    private async Task UnhideAllPlaylistsAsync()
    {
        if (_cache.HiddenIds.Count == 0) return;

        _cache.UnhideAll();
        await RefreshRemotePlaylistsAsync(report: false);
        StatusText = "Removed playlists are back on the rail.";
    }

    [RelayCommand]
    private void RemoveTrack()
    {
        if (CurrentPlaylist is not { } playlist || SelectedPlaylistTrack == null) return;
        playlist.Tracks.Remove(SelectedPlaylistTrack);
        PlaylistTracks.Remove(SelectedPlaylistTrack);
        playlist.NotifyTrackCountChanged();
        _store.Save();
        UpdateCounts();
    }

    [RelayCommand]
    private void MoveTrackUp() => MoveTrack(-1);

    [RelayCommand]
    private void MoveTrackDown() => MoveTrack(1);

    private void MoveTrack(int delta)
    {
        if (CurrentPlaylist is not { } playlist || SelectedPlaylistTrack == null) return;
        var tracks = playlist.Tracks;
        var index = tracks.IndexOf(SelectedPlaylistTrack);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= tracks.Count) return;
        tracks.Move(index, target);
        PlaylistTracks.Move(PlaylistTracks.IndexOf(SelectedPlaylistTrack), target);
        _store.Save();
    }

    [RelayCommand]
    private async Task DownloadPlaylistAsync()
    {
        if (CurrentPlaylist is not { } playlist) return;
        var pending = playlist.Tracks
            .Where(t => t.State is SourceTrackState.New or SourceTrackState.Failed)
            .ToList();
        if (pending.Count == 0)
        {
            StatusText = "Nothing left to download in this playlist.";
            return;
        }

        foreach (var track in pending) StopPreviewIfPlaying(track);

        var queued = _queue.EnqueueMany(pending);
        UpdateCounts();
        StatusText = $"Downloading {queued} track(s) from {playlist.Name}.";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DownloadPlaylistTrackAsync()
    {
        if (SelectedPlaylistTrack == null) return;
        if (CurrentPlaylist is { } playlist) SelectedPlaylistTrack.PlaylistName = playlist.Name;
        StopPreviewIfPlaying(SelectedPlaylistTrack);
        _queue.Enqueue(SelectedPlaylistTrack);
        StatusText = $"Download of {SelectedPlaylistTrack.DisplayName} added to the download queue.";
        await Task.CompletedTask;
    }

    /// <summary>Download the selected playlist row into the library while its stream plays.</summary>
    [RelayCommand]
    private async Task DownloadAndPlayPlaylistTrackAsync()
    {
        if (SelectedPlaylistTrack == null) return;
        var track = SelectedPlaylistTrack;
        if (CurrentPlaylist is { } playlist) track.PlaylistName = playlist.Name;
        StopPreviewIfPlaying(track);
        _queue.Enqueue(track);
        StatusText = $"Downloading {track.DisplayName} \u2014 playing the stream now\u2026";
        await PlayInDeckAsync(track);
        StatusText = $"Downloading {track.DisplayName} \u00b7 playing the preview stream.";
    }

    /// <summary>
    /// Re-fetch the source of every imported YouTube playlist that has sync
    /// enabled and queue only the entries that were added since the import.
    /// Deletions upstream are never applied - a downloaded file is not going
    /// anywhere - so a track removed from YouTube simply stays in the list.
    /// </summary>
    [RelayCommand]
    private async Task SyncPlaylistsAsync()
    {
        var targets = _store.Playlists
            .Where(p => p.Source == MediaSource.YouTube && YouTubeApi.IsPlaylistSource(p.SourceId))
            .ToList();
        if (targets.Count == 0)
        {
            StatusText = "Import a YouTube playlist first, then sync it here.";
            return;
        }

        IsBusy = true;
        var added = 0;
        try
        {
            foreach (var playlist in targets)
            {
                StatusText = $"Syncing {playlist.Name}\u2026";
                var entries = await YtDlp.GetPlaylistAsync(playlist.SourceId!, _settings,
                    CancellationToken.None, maxEntries: 0);
                if (entries.Count == 0) continue;

                var known = new HashSet<string>(playlist.Tracks.Select(t => t.Id),
                    StringComparer.OrdinalIgnoreCase);
                var fresh = YouTubeApi.ToSourceTracks(entries, playlist.Name, playlist.Name)
                    .Where(t => !known.Contains(t.Id))
                    .ToList();
                if (fresh.Count == 0) continue;

                _store.AddTracks(playlist, fresh);
                added += _queue.EnqueueMany(fresh);
                playlist.LastSyncedUtc = DateTime.UtcNow;
            }

            _store.Save();
            StatusText = added == 0
                ? "Playlists already up to date."
                : $"Downloading {added} new track(s) from YouTube.";
        }
        catch (Exception ex)
        {
            Log.Error("YouTube playlist sync failed", ex);
            StatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            UpdateCounts();
        }
    }

    [RelayCommand]
    private async Task PlayPlaylistTrackAsync()
    {
        if (SelectedPlaylistTrack == null) return;
        StatusText = $"Resolving {SelectedPlaylistTrack.DisplayName}\u2026";
        await PlayInDeckAsync(SelectedPlaylistTrack);
        StatusText = $"Playing {SelectedPlaylistTrack.DisplayName}.";
    }

    // ---- commands: youtube account + remote write -------------------------
    //
    // These are the only commands that write to Google. Each is disabled - with
    // a tooltip that says why - when the account, the playlist or the selected
    // row cannot support the call.

    /// <summary>Open the connect/disconnect dialog; refresh the rail's notion of "mine" after.</summary>
    [RelayCommand]
    private async Task ConnectYouTubeAsync()
    {
        var dialog = new YouTubeAuthDialog(_auth, _api)
        {
            Owner = Application.Current?.MainWindow,
        };
        dialog.ShowDialog();

        RefreshConnectionState();
        await RefreshRemotePlaylistsAsync(report: false);
        StatusText = _auth.IsConnected
            ? $"Connected to YouTube as {_auth.ChannelTitle}."
            : "Not connected to YouTube - playlist writes are off.";
    }

    [RelayCommand(CanExecute = nameof(CanCreateRemotePlaylist))]
    private async Task CreateRemotePlaylistAsync()
    {
        var name = RemotePlaylistName.Trim();
        if (name.Length == 0)
        {
            StatusText = "Type a name for the new YouTube playlist first.";
            return;
        }

        var privacy = SelectedRemotePrivacy;
        await RunWriteAsync($"Creating \"{name}\" on YouTube\u2026", async ct =>
        {
            var remote = await _api.CreatePlaylistAsync(name, "", privacy, ct);
            remote.ItemCount = 0;

            // A new playlist is 0 items: put it straight into the rail (and the
            // cached list) instead of spending 1 quota unit on a mine=true
            // refetch, and select it so the pane is already pointed at it.
            RemotePlaylists.Insert(0, remote);
            if (!RemotePlaylistIds.Contains(remote.Id)) RemotePlaylistIds.Add(remote.Id);
            _cache.InsertPlaylist(remote);
            _cache.SetItems(remote.Id, Array.Empty<YouTubePlaylistItem>(), DateTime.UtcNow);
            _cache.Save();

            SyncRail();
            var row = Rail.FirstOrDefault(r => r.Key == "remote:" + remote.Id);
            if (row != null) SelectedRail = row;

            RemotePlaylistName = "";
            StatusText = row != null
                ? $"Created \"{name}\" on YouTube ({privacy}) - select a track and press Add to YouTube to fill it."
                : $"Created \"{name}\" on YouTube ({privacy}).";
        });
    }

    [RelayCommand(CanExecute = nameof(CanAddSelectedToYouTube))]
    private async Task AddSelectedToYouTubeAsync()
    {
        if (CurrentPlaylist is not { } playlist || SelectedPlaylistTrack is not { } track) return;
        if (RemoteIdFor(playlist) is not { } remoteId) return;
        if (string.IsNullOrWhiteSpace(track.VideoId)) return;

        var videoId = track.VideoId!;
        var title = track.Title;
        await RunWriteAsync($"Adding \"{title}\" to {playlist.Name} on YouTube\u2026", async ct =>
        {
            var itemId = await _api.InsertPlaylistItemAsync(remoteId, videoId, ct);

            // No refetch: append the row to the pane, and move the badge by one.
            if (!PlaylistTracks.Any(t => t.Id == track.Id))
            {
                playlist.Tracks.Add(track);
                PlaylistTracks.Add(track);
            }
            playlist.NotifyTrackCountChanged();
            var item = new YouTubePlaylistItem
            {
                ItemId = itemId,
                VideoId = videoId,
                Title = track.Title,
                ChannelTitle = track.Artist,
                DurationSeconds = (int)(track.DurationMs / 1000),
            };
            AppendToCachedItems(remoteId, item);
            BumpRemoteItemCount(remoteId, +1);
            _store.Save();
            UpdateCounts();
            StatusText = $"Added \"{title}\" to {playlist.Name} on YouTube.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFromYouTube))]
    private async Task RemoveFromYouTubeAsync()
    {
        if (CurrentPlaylist is not { } playlist || SelectedPlaylistTrack is not { } track) return;
        if (RemoteIdFor(playlist) is not { } remoteId) return;
        if (string.IsNullOrWhiteSpace(track.VideoId)) return;

        var videoId = track.VideoId!;
        var title = track.Title;
        await RunWriteAsync($"Removing \"{title}\" from {playlist.Name} on YouTube\u2026", async ct =>
        {
            // playlistItems.delete takes the playlistItem id, not the video id.
            var itemId = await _api.FindPlaylistItemIdAsync(remoteId, videoId, ct);
            if (itemId == null)
            {
                StatusText = $"\"{title}\" is not in {playlist.Name} on YouTube any more.";
                return;
            }

            await _api.DeletePlaylistItemAsync(itemId, ct);

            // The row was a mirror of the remote entry: drop it here too so the
            // rail does not show something the channel no longer has.
            if (SelectedPlaylistTrack == track) SelectedPlaylistTrack = null;
            playlist.Tracks.Remove(track);
            PlaylistTracks.Remove(track);
            playlist.NotifyTrackCountChanged();
            RemoveFromCachedItems(remoteId, videoId);
            BumpRemoteItemCount(remoteId, -1);
            _store.Save();
            UpdateCounts();
            StatusText = $"Removed \"{title}\" from {playlist.Name} on YouTube.";
        });
    }

    /// <summary>
    /// The strip's "Import my playlists": fetch <c>mine=true</c> with the same
    /// cache as the rail and report what the rail now shows. Local imports that
    /// match a channel playlist are adopted by that playlist's remote row rather
    /// than duplicated - the rows, their downloads and their queue state are
    /// shared (see <see cref="BuildRailItems"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportMyPlaylists))]
    private async Task ImportMyPlaylistsAsync()
    {
        // Not connected: the account's own playlists cannot be listed at all
        // (playlists?mine=true needs OAuth - an API key can never see a private
        // account), so offer the static list instead of an empty rail.
        if (!IsYouTubeConnected)
        {
            await AddFallbackPlaylistsAsync();
            return;
        }

        await RefreshRemotePlaylistsAsync(report: false, force: true);

        var matched = _store.Playlists.Count(p => InRail(p) && PlaylistIdOf(p) is { Length: > 0 } id
                                                  && RemotePlaylistIds.Contains(id));
        StatusText = RemotePlaylists.Count == 0
            ? "Your channel has no playlists yet - + Remote creates the first one, " +
              "or press the button again for the built-in lists."
            : $"{RemotePlaylists.Count} playlist(s) on your channel" +
              (matched > 0 ? $", {matched} local import(s) matched to it." : ".");
    }

    /// <summary>
    /// The static fallback the user asked for: when account playlists cannot be
    /// listed, add a fixed set of lists that still load. Each becomes an ordinary
    /// local playlist filled by one YouTube search, so it plays, downloads and
    /// deletes exactly like every other row, and survives a restart because it is
    /// a normal store row. Re-running it reuses a list already there.
    /// </summary>
    private async Task AddFallbackPlaylistsAsync()
    {
        IsBusy = true;
        var added = 0;
        try
        {
            foreach (var entry in YouTubeFallbackPlaylists.Entries)
            {
                // Skip a list that is already filled, and never adopt a row that is
                // bound to a real source: adding search results to it would pollute
                // a channel playlist that merely shares the built-in name.
                if (_store.FindByName(entry.Title) is { } existing
                    && (existing.Tracks.Count > 0 || !string.IsNullOrWhiteSpace(existing.SourceId)))
                    continue;

                StatusText = $"Loading {entry.Title}\u2026";
                try
                {
                    var entries = await YtDlp.SearchAsync(
                        entry.Query, YouTubeFallbackPlaylists.TracksPerEntry, _settings, CancellationToken.None);
                    var tracks = YouTubeApi.ToSourceTracks(entries, entry.Title, entry.Title);

                    // Nothing found is not a list: creating the row anyway leaves an
                    // Imported (read-only) row with no SourceId that can never load,
                    // be renamed or be deleted - and an empty search must never
                    // detach a binding that a real row already has.
                    if (tracks.Count == 0)
                    {
                        Log.Warn($"YouTube: built-in list \"{entry.Title}\" came back empty; nothing added.");
                        continue;
                    }

                    var playlist = _store.FindByName(entry.Title);
                    if (playlist == null)
                    {
                        playlist = _store.Create(entry.Title, MediaSource.Manual);
                        playlist.SyncEnabled = false;
                    }

                    _store.AddTracks(playlist, tracks);
                    added++;
                }
                catch (Exception ex)
                {
                    // One entry failing must not cost the others their results.
                    Log.Warn($"YouTube: couldn't fill the built-in list \"{entry.Title}\": {ex.Message}");
                }
            }

            _store.Save();
            SyncRail();
            var first = Rail.FirstOrDefault();
            if (first != null) SelectedRail = first;

            StatusText = added > 0
                ? $"Added {added} built-in list(s) from YouTube search. Account playlists need " +
                  "YouTube sign-in - press Connect YouTube to list your own."
                : "Couldn't load the built-in lists - check that yt-dlp is working in Settings.";
        }
        catch (Exception ex)
        {
            Log.Error("YouTube: building the built-in playlist list failed", ex);
            StatusText = $"Couldn't add the built-in lists: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One write at a time: disables the strip, routes Google's own message to the
    /// status line, and re-derives every write hint afterwards.
    /// </summary>
    private async Task RunWriteAsync(string working, Func<CancellationToken, Task> work)
    {
        IsYouTubeBusy = true;
        StatusText = working;
        try
        {
            await work(CancellationToken.None);
        }
        catch (Exception ex) when (ex is YouTubeApiException or GoogleAuthException)
        {
            // Already user-facing (quota explanation, Google's own error text).
            Log.Warn($"YouTube: {ex.Message}");
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("YouTube write failed", ex);
            StatusText = $"YouTube write failed: {ex.Message}";
        }
        finally
        {
            IsYouTubeBusy = false;
            RefreshWriteHints();
        }
    }

    /// <summary>Mirror the auth service's state into the strip's bindings.</summary>
    private void RefreshConnectionState()
    {
        IsYouTubeConnected = _auth.IsConnected;

        var channel = _auth.ChannelTitle;
        YouTubeAccountText = !IsYouTubeConnected
            ? "YouTube: not connected"
            : string.IsNullOrWhiteSpace(channel) ? "YouTube: connected" : $"YouTube: {channel}";
        YouTubeConnectLabel = IsYouTubeConnected ? "Reconnect" : "Connect YouTube";

        if (IsYouTubeConnected)
        {
            // A different Google account than the one the rail is showing means
            // the old rows are not this channel's: drop them before the fetch.
            var channelId = _auth.ChannelId;
            if (channelId.Length > 0 && _loadedChannelId.Length > 0 && channelId != _loadedChannelId)
            {
                RemotePlaylists.Clear();
                RemotePlaylistIds.Clear();
                SyncRail();
            }

            // Naming the channel and knowing which playlists are writable must
            // not need an extra click, so this runs in the background - and it
            // paints from the on-disk cache first, so the rail is never empty
            // while the request is in flight.
            LoadCachedRemotePlaylists();
            _ = RefreshRemotePlaylistsAsync(report: false);
        }
        else
        {
            RemotePlaylists.Clear();
            RemotePlaylistIds.Clear();
            // Signing out drops the rail back to the local imports. The cache
            // file is left alone: reconnecting paints instantly from it.
            SyncRail();
            UpdateRemoteStamp();
            RefreshWriteHints();
        }
    }

    /// <summary>
    /// Refresh playlists.list?mine=true (the write allow-list and, when
    /// connected, the rail itself) and the channel name.
    ///
    /// Quota: 1 unit for the list, plus 1 for the channel name on the first call.
    /// This is the cache's front door - a list younger than
    /// <see cref="YouTubePlaylistCache.ListTtl"/> is served from memory and costs
    /// nothing, which is what keeps a tab switch from spending quota.
    /// </summary>
    private async Task RefreshRemotePlaylistsAsync(bool report, bool force = false)
    {
        if (!_auth.IsConnected) return;

        if (!force && _cache.IsListFresh && _cache.List is { } cached && IsCacheForThisChannel(cached))
        {
            ApplyRemoteList(cached.Playlists, report: false);
            return;
        }

        // A forced refresh (or a second auth Changed) supersedes the request in
        // flight: the older one is cancelled and returns at its next await, and a
        // generation check in its finally keeps it from clearing the newer one's
        // busy flags. Starting a new request is therefore always allowed.
        _remoteListCts?.Cancel();
        _remoteListCts?.Dispose();
        _remoteListCts = new CancellationTokenSource();
        var cts = _remoteListCts;
        var ct = cts.Token;

        IsRemoteLoading = true;
        UpdateRemoteStamp();

        try
        {
            if (string.IsNullOrWhiteSpace(_auth.ChannelTitle))
            {
                var channel = await _api.GetMyChannelAsync(ct);
                // Raising Changed here re-enters RefreshConnectionState, which now
                // supersedes this very request (cancelling its own ct); the guard
                // below therefore checks the token before the list call, and a
                // cancelled pass leaves the previous list painted.
                _auth.SetChannel(channel.Id, channel.Title);
                ct.ThrowIfCancellationRequested();
            }

            var playlists = await _api.ListMyPlaylistsAsync(ct);
            _cache.SetList(playlists, _auth.ChannelId, _auth.ChannelTitle, DateTime.UtcNow);
            _cache.Save();
            _loadedChannelId = _auth.ChannelId;

            ApplyRemoteList(playlists, report);

            if (report)
                StatusText = $"{playlists.Count} playlist(s) on your YouTube channel.";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh: it owns the status line and the stamp.
        }
        catch (Exception ex)
        {
            // Background refresh: log it; an explicit action surfaces it in the status line.
            Log.Warn($"YouTube: could not list your playlists: {ex.Message}");

            // Say what actually happened. An expired refresh token is the common
            // case - an unverified OAuth app is capped at 7 days - and it must not
            // read as "you have no playlists": the rail showing nothing while the
            // account has plenty is exactly the confusion this tab must not create.
            var signInNeeded = ex is YouTubeApiException { IsAuth: true }
                               || ex is GoogleAuthException;
            var message = signInNeeded
                ? "YouTube sign-in expired or was never granted - press Connect YouTube " +
                  "to list your account playlists again."
                : $"YouTube: {ex.Message}";

            if (signInNeeded) RemoteStampText = "sign-in needed";
            if (report) StatusText = message;
            // Keep whatever is painted (cache or the previous list) - a quota
            // failure must not empty the rail.
        }
        finally
        {
            // Only the newest request clears the busy state: a cancelled one
            // finishing late must not report the newer one as done.
            if (ReferenceEquals(_remoteListCts, cts))
            {
                IsRemoteLoading = false;
                UpdateRemoteStamp();
                RefreshWriteHints();
            }
        }
    }

    /// <summary>Paint the rail from the on-disk cache before any request returns.</summary>
    private void LoadCachedRemotePlaylists()
    {
        if (!IsYouTubeConnected) return;
        // Live rows always win over the file: a create or an add must not be
        // undone by a cache repaint from an auth refresh.
        if (RemotePlaylists.Count > 0) return;
        if (_cache.List is not { } cached) return;
        if (!IsCacheForThisChannel(cached)) return;

        // The cache carries the channel name, so the strip can say whose
        // playlists these are before GetMyChannelAsync has answered.
        if (string.IsNullOrWhiteSpace(_auth.ChannelTitle) && !string.IsNullOrWhiteSpace(cached.ChannelTitle))
        {
            _auth.SetChannel(cached.ChannelId, cached.ChannelTitle);
            YouTubeAccountText = $"YouTube: {cached.ChannelTitle}";
        }

        _loadedChannelId = cached.ChannelId;
        ApplyRemoteList(cached.Playlists, report: false);
    }

    /// <summary>
    /// A cache written for another Google account must not paint this one's rail:
    /// credentials outlive a sign-out, so the channel id is the guard.
    /// </summary>
    private bool IsCacheForThisChannel(YouTubePlaylistCache.CachedList cached)
    {
        var channelId = _auth.ChannelId;
        return string.IsNullOrWhiteSpace(channelId)
            || string.IsNullOrWhiteSpace(cached.ChannelId)
            || string.Equals(channelId, cached.ChannelId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Swap the remote list in and re-anchor the rail. Rows are merged by key, so
    /// the selected row (and its pane) survives a refresh that did not change it.
    /// </summary>
    private void ApplyRemoteList(IReadOnlyList<YouTubePlaylist> playlists, bool report)
    {
        RemotePlaylists.Clear();
        RemotePlaylistIds.Clear();
        foreach (var playlist in playlists)
        {
            // Removed here by the user: mine=true keeps returning it, so the
            // removal is remembered in the cache instead (see RemovePlaylist).
            if (_cache.IsHidden(playlist.Id)) continue;

            RemotePlaylists.Add(playlist);
            RemotePlaylistIds.Add(playlist.Id);
        }

        SyncRail();
        UpdateRemoteStamp();
        RefreshWriteHints();
    }

    /// <summary>
    /// The dim timestamp in the strip: "updated 2 min ago" / "cached 41 min ago".
    /// Age past the list TTL means the numbers on screen may be out of date, and
    /// <see cref="IsRemoteStampStale"/> dims the label rather than blocking.
    /// </summary>
    private void UpdateRemoteStamp()
    {
        if (!IsYouTubeConnected)
        {
            RemoteStampText = "";
            IsRemoteStampStale = false;
            return;
        }

        if (IsRemoteLoading)
        {
            RemoteStampText = "refreshing\u2026";
            IsRemoteStampStale = false;
            return;
        }

        if (_cache.ListFetchedUtc is not { } at)
        {
            RemoteStampText = "not loaded yet";
            IsRemoteStampStale = true;
            return;
        }

        var age = DateTime.UtcNow - at;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        RemoteStampText = age < TimeSpan.FromMinutes(1)
            ? "updated just now"
            : $"updated {FormatAge(age)} ago";
        IsRemoteStampStale = age > YouTubePlaylistCache.ListTtl;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }

    /// <summary>
    /// The strip's ↻: bypasses every TTL, re-lists the channel and re-reads the
    /// selected playlist's items (2 quota units, plus 1 if the channel name is
    /// still unknown).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportMyPlaylists))]
    private async Task RefreshRemoteAsync()
    {
        await RefreshRemotePlaylistsAsync(report: true, force: true);
        await ReloadSelectedRemoteAsync(force: true);
        if (IsYouTubeConnected && !IsYouTubeBusy)
            StatusText = $"Rail refreshed: {RemotePlaylists.Count} playlist(s) on your channel.";
    }


    // ---- write eligibility ------------------------------------------------

    /// <summary>Move the picker to the row backing <paramref name="playlist"/> (null: clear).</summary>
    private void SelectPlaylist(UserPlaylist? playlist)
    {
        var row = playlist == null
            ? null
            : Rail.FirstOrDefault(r => ReferenceEquals(r.Playlist, playlist));
        if (row != null) SelectedRail = row;
        else SelectedPlaylist = playlist;
    }

    /// <summary>
    /// Move a remote row's badge by <paramref name="delta"/> and keep the cached
    /// list in step, so an add/remove is visible immediately and survives a
    /// restart without spending quota on a refetch.
    /// </summary>
    private void BumpRemoteItemCount(string remoteId, int delta)
    {
        var remote = RemotePlaylists.FirstOrDefault(p => p.Id == remoteId);
        if (remote != null) remote.ItemCount = Math.Max(0, remote.ItemCount + delta);

        var row = Rail.FirstOrDefault(r => r.RemoteId == remoteId);
        if (row != null) row.Count = Math.Max(0, row.Count + delta);

        _cache.SetItemCount(remoteId, row?.Count ?? remote?.ItemCount ?? 0);
        _cache.Save();
        UpdateRemoteStamp();
    }

    private void AppendToCachedItems(string remoteId, YouTubePlaylistItem item)
    {
        var items = _cache.GetAnyItems(remoteId, out _)?.ToList() ?? new List<YouTubePlaylistItem>();
        if (items.Any(i => i.VideoId == item.VideoId)) return;
        items.Add(item);
        _cache.SetItems(remoteId, items, DateTime.UtcNow);
        _cache.Save();
    }

    private void RemoveFromCachedItems(string remoteId, string videoId)
    {
        if (_cache.GetAnyItems(remoteId, out _) is not { } cached) return;
        var items = cached.Where(i => i.VideoId != videoId).ToList();
        _cache.SetItems(remoteId, items, DateTime.UtcNow);
        _cache.Save();
    }

    private bool CanCreateRemotePlaylist =>
        IsYouTubeConnected && !IsYouTubeBusy && !string.IsNullOrWhiteSpace(RemotePlaylistName);

    // Enabled while signed out too: that is when the built-in list is the only
    // thing this button can offer, and a dead button would hide that option.
    // IsBusy too: the built-in-list import is not a YouTube request, so IsYouTubeBusy
    // never covers it, and a second click would run the searches twice.
    private bool CanImportMyPlaylists => !IsYouTubeBusy && !IsBusy;

    private bool CanAddSelectedToYouTube =>
        IsYouTubeConnected && !IsYouTubeBusy
        && RemoteIdFor(CurrentPlaylist) != null
        && !string.IsNullOrWhiteSpace(SelectedPlaylistTrack?.VideoId);

    private bool CanRemoveFromYouTube => CanAddSelectedToYouTube;

    /// <summary>
    /// The remote playlist id behind a rail row, but only when that id belongs to
    /// the signed-in channel. Imported-by-URL rows carry a URL in SourceId, so it
    /// goes through the same parser the import uses; anything not in
    /// <see cref="RemotePlaylistIds"/> is read-only and writes are refused.
    /// </summary>
    private string? RemoteIdFor(UserPlaylist? playlist)
    {
        if (playlist == null || !IsYouTubeConnected) return null;
        var id = PlaylistIdOf(playlist);
        return id.Length > 0 && RemotePlaylistIds.Contains(id) ? id : null;
    }

    /// <summary>
    /// Clears rows written while a channel page could still be stored as a
    /// playlist <c>SourceId</c>: <c>TryParsePlaylistId</c> returned true with an
    /// empty id for <c>/@handle</c>, <c>/channel/</c>, <c>/c/</c> and <c>/user/</c>
    /// pages, and the empty id fell back to the raw <c>SourceId</c> - so the
    /// handle became the playlist's id and, with a blank title, its rail name.
    ///
    /// A row with nothing in it is the phantom itself: dropped. A row that holds
    /// tracks keeps every track and becomes a plain local list - the unusable
    /// source id is cleared so it can never be synced as a playlist, and the row
    /// stays on the rail, fully removable.
    /// </summary>
    private void RepairBrokenYouTubeSources()
    {
        var broken = _store.Playlists
            .Where(p => p.Source is MediaSource.YouTube or MediaSource.Manual)
            .Where(p => IsBrokenYouTubeSource(p.SourceId))
            .ToList();
        if (broken.Count == 0) return;

        // Mutate every row first, then persist once: this runs on the UI thread in
        // the constructor, and one write per phantom row is a full JSON serialise
        // plus a transaction each - a multi-second startup stall on a large library.
        var phantoms = new List<UserPlaylist>();
        var detached = new List<string>();
        foreach (var playlist in broken)
        {
            if (playlist.Tracks.Count == 0)
            {
                Log.Warn($"YouTube: dropping phantom playlist \"{playlist.Name}\" - " +
                         $"SourceId \"{playlist.SourceId}\" is a channel page, not a playlist.");
                phantoms.Add(playlist);
                continue;
            }

            Log.Warn($"YouTube: detaching \"{playlist.Name}\" from channel source " +
                     $"\"{playlist.SourceId}\" - kept as a local list with " +
                     $"{playlist.Tracks.Count} track(s).");
            playlist.SourceId = null;
            playlist.SyncEnabled = false;
            detached.Add(playlist.Name);
        }

        // RemoveMany writes the whole graph once, detached rows included.
        if (phantoms.Count > 0) _store.RemoveMany(phantoms);
        else _store.Save();

        // A dropped sync binding is a user-visible change, not just a log line.
        if (detached.Count > 0)
            StatusText = $"Detached {detached.Count} playlist(s) from a channel link that is not a playlist: " +
                         string.Join(", ", detached.Take(3)) + (detached.Count > 3 ? ", …" : "") + ".";
    }

    /// <summary>
    /// The startup repair's only decision: true when a <see cref="UserPlaylist.SourceId"/>
    /// is a channel page (or a bare <c>@handle</c>) that was stored as a playlist id.
    /// Such a row is dropped when it has no tracks and detached when it has some.
    ///
    /// <para>Everything else is left alone. An id the importer accepts is a playlist
    /// even when <see cref="YouTubeApi.IsPlaylistSource"/> does not recognise its
    /// prefix (<c>WL…</c>, <c>TL…</c>, <c>PU…</c>, <c>SP…</c>, <c>EC…</c>, <c>VL…</c>),
    /// and a shape this method does not name is never repaired away.</para>
    /// </summary>
    public static bool IsBrokenYouTubeSource(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return false;

        var id = sourceId.Trim();
        if (YouTubeApi.IsPlaylistSource(id)) return false;
        if (YouTubeImportParser.TryParse(id, out var target, out _) && target is { IsPlaylist: true }) return false;

        return id.StartsWith("@", StringComparison.Ordinal) || YouTubeApi.IsChannelUrl(id);
    }

    private static string PlaylistIdOf(UserPlaylist playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.SourceId)) return "";
        // SourceId is a bare id for rows this tab created, a URL for imports.
        // Anything else - a channel handle, a /@name page - is not a playlist id
        // and must never be shown or synced as one.
        return YouTubeApi.TryParsePlaylistId(playlist.SourceId, out var id) ? id : "";
    }

    /// <summary>Recompute every write button's enabled state and explanatory tooltip.</summary>
    private void RefreshWriteHints()
    {
        var playlist = CurrentPlaylist;
        var track = SelectedPlaylistTrack;
        var remoteId = RemoteIdFor(playlist);
        IsSelectedPlaylistRemote = remoteId != null;

        CreateRemoteHint = !IsYouTubeConnected
            ? "Connect your YouTube account first."
            : IsYouTubeBusy
                ? "A YouTube request is running."
                : string.IsNullOrWhiteSpace(RemotePlaylistName)
                    ? "Type a name for the new YouTube playlist."
                    : $"Create \"{RemotePlaylistName.Trim()}\" on YouTube as {SelectedRemotePrivacy} " +
                      $"(playlist write: {YouTubeDataApi.PlaylistWriteCost} quota units).";

        ImportMyPlaylistsHint = !IsYouTubeConnected
            ? "Connect your YouTube account first."
            : IsYouTubeBusy
                ? "A YouTube request is running."
                : "Fetch the playlists on your channel and mirror them in this rail.";

        AddToYouTubeHint = DescribeWrite(track, playlist, remoteId, "Add the selected track to");
        RemoveFromYouTubeHint = DescribeWrite(track, playlist, remoteId, "Delete the selected track from");

        CreateRemotePlaylistCommand.NotifyCanExecuteChanged();
        ImportMyPlaylistsCommand.NotifyCanExecuteChanged();
        AddSelectedToYouTubeCommand.NotifyCanExecuteChanged();
        RemoveFromYouTubeCommand.NotifyCanExecuteChanged();
    }

    private string DescribeWrite(SourceTrack? track, UserPlaylist? playlist, string? remoteId, string verb)
    {
        if (!IsYouTubeConnected) return "Connect your YouTube account first (Connect YouTube).";
        if (IsYouTubeBusy) return "A YouTube request is running.";
        if (playlist == null) return "Select a playlist first.";
        if (remoteId == null)
            return $"\"{playlist.Name}\" exists only in JockeyBox. Create it on YouTube with + Remote, " +
                   "or press Import my playlists for one that is already on your channel.";
        if (track == null) return "Select a playlist track first.";
        if (string.IsNullOrWhiteSpace(track.VideoId))
            return $"\"{track.DisplayName}\" has no YouTube video id (library-only row), so there is " +
                   $"nothing to {verb.Split(' ')[0].ToLowerInvariant()} on YouTube.";
        return $"{verb} \"{playlist.Name}\" on YouTube " +
               $"({YouTubeDataApi.PlaylistWriteCost} quota units).";
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Create a playlist from this tab and select it. Defaults to
    /// <see cref="MediaSource.Manual"/> - the existing convention for lists built
    /// here by hand; imports pass YouTube explicitly. Both are rail rows.
    /// </summary>
    private UserPlaylist CreatePlaylistInternal(string name, MediaSource source = MediaSource.Manual)
    {
        var playlist = _store.Create(name, source);
        // _store.Create raises the store's CollectionChanged, which normally
        // reconciles the rail already; sync here as well so a caller that ran off
        // the dispatcher never selects a row the rail has not admitted yet.
        SyncRail();
        SelectPlaylist(playlist);
        return playlist;
    }

    /// <summary>
    /// Video title as a song: "Artist - Song (Official Audio) ft. X" becomes
    /// artist "Artist", title "Song". The results list keeps the raw title so
    /// the video is still recognisable, but anything that leaves this view -
    /// playback, downloads, playlists - carries the clean pair.
    /// </summary>
    private static SourceTrack ToTrack(YtEntry entry)
    {
        var (title, artist) = MediaTitle.Clean(entry.Title, entry.Uploader);
        return new SourceTrack
        {
            Id = entry.VideoId,
            Source = MediaSource.YouTube,
            Title = title.Length > 0 ? title : entry.Title,
            Artist = artist.Length > 0 ? artist : entry.Uploader,
            VideoId = entry.VideoId,
            CoverUrl = entry.ThumbnailUrl ?? "",
            DurationMs = entry.DurationSeconds * 1000,
            State = SourceTrackState.New,
        };
    }

    /// <summary>
    /// Self-heals every visible row against the disk. Only a row the download
    /// queue is genuinely working on (progress is flowing) is left alone; a row
    /// that merely says Queued/Downloading but is not in the queue any more is
    /// stale - that stale state is what used to wedge rows at "0%".
    /// </summary>
    private void ReconcileStates()
    {
        // Disk first: ReconcileTrack may adopt the ledger's path for a row that
        // never stored one, and the karaoke pass then matches on that path.
        foreach (var track in PlaylistTracks) ReconcileTrack(track);

        // Karaoke availability per row from the scan index (one dictionary hit
        // each), after the per-row state re-derivation above.
        _remote?.Karaoke.Apply(PlaylistTracks);
    }

    // ---- library verbs on a remote row -------------------------------------

    /// <summary>
    /// "Add to playlist ▸" data: the user's own playlists plus create-and-add.
    /// Null only when the host was not supplied (design-time, unit tests).
    /// </summary>
    public RemotePlaylistTargets? LibraryPlaylists { get; }

    /// <summary>True when the selected row's file is on disk, so reveal/separate work.</summary>
    public bool HasLocalFileForSelected => SelectedPlaylistTrack?.LocalEntry is not null;

    /// <summary>Re-read the internal playlists (another window/tab wrote one).</summary>
    public void RefreshLibraryPlaylists() => LibraryPlaylists?.Refresh();

    /// <summary>Repaint karaoke availability on every row after a library scan.</summary>
    public void ApplyLibraryFacts()
    {
        _remote?.Karaoke.Apply(PlaylistTracks);
        OnPropertyChanged(nameof(HasLocalFileForSelected));
    }

    /// <summary>
    /// Add the selected YouTube row to one of the user's own playlists. The row
    /// need not be downloaded: the playlist keeps its video id and picks up the
    /// library file once one exists.
    /// </summary>
    [RelayCommand]
    private void AddToInternalPlaylist(RemotePlaylistChoice? choice)
    {
        if (SelectedPlaylistTrack is not { } track || LibraryPlaylists is null || choice is null) return;

        LibraryPlaylists.Choose(choice, track);
        if (choice.Playlist is { } playlist)
            StatusText = $"Added \u201c{track.DisplayName}\u201d to {playlist.Name}.";
        else
            StatusText = $"Created a playlist with \u201c{track.DisplayName}\u201d.";
    }

    /// <summary>Separate karaoke for the selected row's local file (disabled until it exists).</summary>
    [RelayCommand(CanExecute = nameof(HasLocalFileForSelected))]
    private void SeparateSelectedKaraoke()
    {
        if (SelectedPlaylistTrack is not { } track) return;
        _remote?.MakeKaraoke(track);
    }

    /// <summary>Show the selected row's local file in Explorer (disabled until it exists).</summary>
    [RelayCommand(CanExecute = nameof(HasLocalFileForSelected))]
    private void RevealSelectedFile()
    {
        if (SelectedPlaylistTrack is not { } track) return;
        _remote?.RevealLocalFile(track);
    }

    /// <summary>Copy the selected row's YouTube link.</summary>
    [RelayCommand]
    private void CopySelectedLink()
    {
        var videoId = SelectedPlaylistTrack?.VideoId;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            StatusText = "That row has no video id.";
            return;
        }

        try
        {
            Clipboard.SetText($"https://www.youtube.com/watch?v={videoId}");
            StatusText = "Link copied.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not copy the link: {ex.Message}";
        }
    }

    /// <summary>Open the selected playlist row on YouTube (same rule as the result list).</summary>
    [RelayCommand]
    private void OpenPlaylistTrackOnYouTube()
    {
        var videoId = SelectedPlaylistTrack?.VideoId;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            StatusText = "That row has no video id to open.";
            return;
        }

        var url = $"https://www.youtube.com/watch?v={videoId}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusText = $"Opened {url} in the browser.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the browser: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-derive every visible row after the library changed underneath it (a
    /// download deleted from the status console's Downloads tab). The delete
    /// arrives on a worker thread and the rows are this tab's own objects, so
    /// this hops to the dispatcher and re-reads the disk.
    /// </summary>
    public void ReconcileFromLibrary()
    {
        Dispatcher(() =>
        {
            ReconcileStates();
            UpdateCounts();
        });
    }

    /// <summary>
    /// artist+title view of the download ledger, rebuilt only when the ledger's
    /// entry count changes. A per-row <c>FindPathByLibraryKey</c> is a full walk of
    /// the ledger (the ledger is keyed by remote id), so re-deriving 5 000 rows
    /// used to cost 5 000 walks; this makes each row one dictionary hit.
    /// </summary>
    private LibraryLedgerIndex LedgerIndex
    {
        get
        {
            var count = _ledger.Entries.Count;
            if (_ledgerIndex is null || _ledgerIndexCount != count)
            {
                _ledgerIndex = LibraryLedgerIndex.Build(_ledger);
                _ledgerIndexCount = count;
            }
            return _ledgerIndex;
        }
    }

    /// <summary>Index over <see cref="LedgerIndex"/>; invalidated by the entry count.</summary>
    private LibraryLedgerIndex? _ledgerIndex;

    /// <summary>Ledger entry count the cached index was built from.</summary>
    private int _ledgerIndexCount = -1;

    private void ReconcileTrack(SourceTrack track)
    {
        // Actually transferring: the queue owns the row until it reports back.
        if (track.IsTransferring) return;

        // Queued, but no worker has touched it yet. Leave it alone so the row
        // does not flicker back to New before the first progress tick. QueueOwns
        // counts a waiting item as an owner, so a row only lands here when the
        // queue really does still hold it; a Queued row the queue forgot about
        // falls through and is re-derived from disk instead of sitting forever.
        if (track.IsBusy && QueueOwns(track)) return;

        // Cheap, same lookups the method always did: ledger first, then the
        // file the row remembers. A ledger hit means the track is on disk under
        // its artist/title whatever this particular row claims.
        var existing = LedgerIndex.FindPath(track.Artist, track.Title);
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
            // Nothing permanent on disk: InLibrary/Downloading are both lies now.
            track.LibraryPath = null;
            if (track.State is SourceTrackState.Downloading or SourceTrackState.Queued)
                track.State = string.IsNullOrWhiteSpace(track.Error)
                    ? SourceTrackState.New
                    : SourceTrackState.Failed;
            else if (track.State == SourceTrackState.InLibrary)
                track.State = SourceTrackState.New;
        }

        // A cached preview that the LRU already evicted must not keep its glyph.
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

    private bool FilterPlaylistTrack(object obj)
    {
        if (obj is not SourceTrack track) return false;
        if (string.IsNullOrWhiteSpace(PlaylistFilter)) return true;
        return track.Title.Contains(PlaylistFilter, StringComparison.OrdinalIgnoreCase)
            || track.Artist.Contains(PlaylistFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCounts()
    {
        TotalCount = PlaylistTracks.Count;
        InLibraryCount = PlaylistTracks.Count(t => t.State == SourceTrackState.InLibrary);
        QueuedCount = PlaylistTracks.Count(t => t.State is SourceTrackState.Queued or SourceTrackState.Downloading);
        CountsText = TotalCount == 0
            ? ""
            : $"{InLibraryCount}/{TotalCount} in library" + (QueuedCount > 0 ? $" \u00b7 {QueuedCount} downloading" : "");
    }

    private static void Dispatcher(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(action);
        else action();
    }
}
