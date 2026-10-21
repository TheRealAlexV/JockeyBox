using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Downloads;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;
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

    private CancellationTokenSource? _searchCts;

    public YouTubeViewModel(AppSettings settings, PlaylistStore store, DownloadQueue queue,
                            LibraryLedger ledger, Func<SourceTrack, Task> play)
    {
        _settings = settings;
        _store = store;
        _queue = queue;
        _ledger = ledger;
        _play = play;

        PlaylistTracksView = CollectionViewSource.GetDefaultView(PlaylistTracks);
        PlaylistTracksView.Filter = FilterPlaylistTrack;

        _queue.ItemFinished += _ => Dispatcher(UpdateCounts);

        SelectedPlaylist = _store.Playlists.FirstOrDefault();
        UpdateCounts();
        StatusText = "Search YouTube, or paste a video / playlist link.";
    }

    // ---- search -----------------------------------------------------------

    public ObservableCollection<YtEntry> Results { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private YtEntry? _selectedResult;
    [ObservableProperty] private string _searchStatus = "";

    // ---- playlists --------------------------------------------------------

    public ObservableCollection<UserPlaylist> Playlists => _store.Playlists;
    public ObservableCollection<SourceTrack> PlaylistTracks { get; } = new();
    public ICollectionView PlaylistTracksView { get; }

    [ObservableProperty] private UserPlaylist? _selectedPlaylist;
    [ObservableProperty] private SourceTrack? _selectedPlaylistTrack;
    [ObservableProperty] private string _newPlaylistName = "";
    [ObservableProperty] private string _playlistFilter = "";
    [ObservableProperty] private string _countsText = "";

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _inLibraryCount;
    [ObservableProperty] private int _queuedCount;

    // ---- import -----------------------------------------------------------

    [ObservableProperty] private string _importUrl = "";
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    partial void OnSelectedPlaylistChanged(UserPlaylist? value)
    {
        PlaylistTracks.Clear();
        if (value != null)
            foreach (var track in value.Tracks) PlaylistTracks.Add(track);
        ReconcileStates();
        UpdateCounts();
        Log.Debug($"youtube playlist '{value?.Name ?? "(none)"}': {PlaylistTracks.Count} row(s), view {PlaylistTracksView.Cast<object>().Count()}");
    }

    partial void OnPlaylistFilterChanged(string value) => PlaylistTracksView.Refresh();

    // ---- commands: search -------------------------------------------------

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0) return;

        // A pasted URL is an import, not a search.
        if (YouTubeApi.TryParsePlaylistId(query, out _) || YouTubeApi.TryParseVideoId(query, out _))
        {
            ImportUrl = query;
            await ImportAsync();
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
        await _play(track);
        StatusText = $"Playing {track.DisplayName}.";
    }

    [RelayCommand]
    private async Task DownloadResultAsync()
    {
        if (SelectedResult == null) return;
        var track = ToTrack(SelectedResult);
        if (SelectedPlaylist != null) track.PlaylistName = SelectedPlaylist.Name;
        _queue.Enqueue(track);
        StatusText = $"Queued {track.DisplayName}.";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddResultToPlaylistAsync()
    {
        if (SelectedResult == null) return;

        var playlist = SelectedPlaylist ?? CreatePlaylistInternal("YouTube Picks");
        var added = _store.AddTracks(playlist, new[] { ToTrack(SelectedResult) });
        SelectedPlaylist = playlist;
        OnSelectedPlaylistChanged(playlist);
        StatusText = added > 0
            ? $"Added \"{SelectedResult.Title}\" to {playlist.Name}."
            : "That video is already in the playlist.";
        await Task.CompletedTask;
    }

    // ---- commands: import -------------------------------------------------

    [RelayCommand]
    private async Task ImportAsync()
    {
        var input = ImportUrl.Trim();
        if (input.Length == 0) return;

        try
        {
            IsImporting = true;
            StatusText = "Reading YouTube\u2026";

            if (YouTubeApi.TryParsePlaylistId(input, out var listId))
            {
                var isChannel = YouTubeApi.IsChannelUrl(input);
                var entries = await YtDlp.GetPlaylistAsync(
                    input, _settings, CancellationToken.None,
                    maxEntries: isChannel ? ChannelImportLimit : 0);
                if (entries.Count == 0)
                {
                    StatusText = "That playlist has no playable entries.";
                    return;
                }

                var derived = YouTubeApi.DeriveImportName(input);
                var name = derived.Length > 0
                    ? derived
                    : $"YouTube playlist {DateTime.Now:yyyy-MM-dd HH:mm}";
                var tracks = YouTubeApi.ToSourceTracks(entries, name, derived);
                var playlist = CreatePlaylistInternal(name, MediaSource.YouTube);
                playlist.SourceId = input;
                _store.AddTracks(playlist, tracks);
                _store.Save();

                SelectedPlaylist = playlist;
                OnSelectedPlaylistChanged(playlist);
                StatusText = $"Imported {tracks.Count} track(s) into {name}.";
            }
            else if (YouTubeApi.TryParseVideoId(input, out var videoId))
            {
                var entry = await YtDlp.GetVideoAsync(videoId, _settings, CancellationToken.None);
                if (entry == null)
                {
                    StatusText = "Could not read that video.";
                    return;
                }

                var playlist = SelectedPlaylist ?? CreatePlaylistInternal("YouTube Picks");
                var track = ToTrack(entry);
                track.PlaylistName = playlist.Name;
                var added = _store.AddTracks(playlist, new[] { track });
                SelectedPlaylist = playlist;
                OnSelectedPlaylistChanged(playlist);
                StatusText = added > 0
                    ? $"Added \"{entry.Title}\" to {playlist.Name}."
                    : "That video is already in the playlist.";
            }
            else
            {
                StatusText = "That does not look like a YouTube video or playlist link.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    // ---- commands: playlists ---------------------------------------------

    [RelayCommand]
    private void CreatePlaylist()
    {
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) name = $"Playlist {_store.Playlists.Count + 1}";
        var playlist = CreatePlaylistInternal(name);
        NewPlaylistName = "";
        SelectedPlaylist = playlist;
        StatusText = $"Created {name}.";
    }

    [RelayCommand]
    private void RenamePlaylist()
    {
        if (SelectedPlaylist == null) return;
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) return;
        SelectedPlaylist.Name = name;
        foreach (var track in SelectedPlaylist.Tracks) track.PlaylistName = name;
        _store.Save();
        NewPlaylistName = "";
        StatusText = $"Renamed to {name}.";
    }

    [RelayCommand]
    private void RemovePlaylist()
    {
        if (SelectedPlaylist == null) return;
        var name = SelectedPlaylist.Name;
        _store.Remove(SelectedPlaylist);
        SelectedPlaylist = _store.Playlists.FirstOrDefault();
        StatusText = $"Deleted {name}.";
    }

    [RelayCommand]
    private void RemoveTrack()
    {
        if (SelectedPlaylist == null || SelectedPlaylistTrack == null) return;
        SelectedPlaylist.Tracks.Remove(SelectedPlaylistTrack);
        PlaylistTracks.Remove(SelectedPlaylistTrack);
        SelectedPlaylist.NotifyTrackCountChanged();
        _store.Save();
        UpdateCounts();
    }

    [RelayCommand]
    private void MoveTrackUp() => MoveTrack(-1);

    [RelayCommand]
    private void MoveTrackDown() => MoveTrack(1);

    private void MoveTrack(int delta)
    {
        if (SelectedPlaylist == null || SelectedPlaylistTrack == null) return;
        var tracks = SelectedPlaylist.Tracks;
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
        if (SelectedPlaylist == null) return;
        var pending = SelectedPlaylist.Tracks
            .Where(t => t.State is SourceTrackState.New or SourceTrackState.Failed)
            .ToList();
        if (pending.Count == 0)
        {
            StatusText = "Nothing left to download in this playlist.";
            return;
        }

        var queued = _queue.EnqueueMany(pending);
        UpdateCounts();
        StatusText = $"Queued {queued} track(s) from {SelectedPlaylist.Name}.";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DownloadPlaylistTrackAsync()
    {
        if (SelectedPlaylistTrack == null) return;
        if (SelectedPlaylist != null) SelectedPlaylistTrack.PlaylistName = SelectedPlaylist.Name;
        _queue.Enqueue(SelectedPlaylistTrack);
        StatusText = $"Queued {SelectedPlaylistTrack.DisplayName}.";
        await Task.CompletedTask;
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
            .Where(p => p.Source == MediaSource.YouTube && !string.IsNullOrWhiteSpace(p.SourceId))
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
                : $"Queued {added} new track(s) from YouTube.";
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
        await _play(SelectedPlaylistTrack);
        StatusText = $"Playing {SelectedPlaylistTrack.DisplayName}.";
    }

    // ---- helpers ----------------------------------------------------------

    private UserPlaylist CreatePlaylistInternal(string name, MediaSource source = MediaSource.Manual)
    {
        var playlist = _store.Create(name, source);
        SelectedPlaylist = playlist;
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

    private void ReconcileStates()
    {
        foreach (var track in PlaylistTracks)
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
    }

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
            : $"{InLibraryCount}/{TotalCount} in library" + (QueuedCount > 0 ? $" \u00b7 {QueuedCount} queued" : "");
    }

    private static void Dispatcher(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(action);
        else action();
    }
}
