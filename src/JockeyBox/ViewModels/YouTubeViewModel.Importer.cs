using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Downloads;
using JockeyBox.Sources;
using JockeyBox.Tools;
using JockeyBox.YouTube;

namespace JockeyBox.ViewModels;

/// <summary>Where one importer row stands, in the tab's own words.</summary>
public enum YouTubeImportRowState
{
    NotDownloaded,
    Queued,
    Downloading,
    Downloaded,
    Failed,
}

/// <summary>
/// One row of a pasted link's listing: a remote video, the <see cref="SourceTrack"/>
/// the queue and the row menus already understand, and its download state.
///
/// <para>The row never invents state: it is either bound to the live
/// <see cref="DownloadItem"/> the queue created (so progress and cancel come from
/// the queue itself) or it reports what the store says is on disk.</para>
/// </summary>
public sealed partial class YouTubeImportRow : ObservableObject
{
    private DownloadItem? _item;

    public YouTubeImportRow(YtEntry entry, SourceTrack track, int index)
    {
        Entry = entry;
        Track = track;
        Index = index;
    }

    public YtEntry Entry { get; }

    public SourceTrack Track { get; }

    /// <summary>0-based position in the playlist, used as the queue's layout index.</summary>
    public int Index { get; }

    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string VideoId => Track.VideoId ?? Track.Id;

    public string DurationText
    {
        get
        {
            if (Track.DurationMs <= 0) return "";
            var duration = TimeSpan.FromMilliseconds(Track.DurationMs);
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : duration.ToString(@"m\:ss");
        }
    }

    /// <summary>Live queue item, when this row has been queued in this session.</summary>
    public DownloadItem? Item => _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsDownloaded), nameof(IsActive), nameof(CanQueue))]
    private YouTubeImportRowState _state = YouTubeImportRowState.NotDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private double _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalFile))]
    private string? _outputPath;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasLocalFile => !string.IsNullOrWhiteSpace(OutputPath);
    public bool IsDownloaded => State == YouTubeImportRowState.Downloaded;
    public bool IsActive => State is YouTubeImportRowState.Queued or YouTubeImportRowState.Downloading;
    public bool CanQueue => !IsDownloaded && !IsActive;

    /// <summary>The state cell: "not downloaded" / "downloading 42%" / "downloaded".</summary>
    public string StateText => State switch
    {
        YouTubeImportRowState.Queued => "queued",
        YouTubeImportRowState.Downloading => Percent > 0 ? $"downloading {Percent:0}%" : "downloading",
        YouTubeImportRowState.Downloaded => "downloaded",
        YouTubeImportRowState.Failed => "failed",
        _ => "not downloaded",
    };

    /// <summary>What a screen reader announces for the row (list items default to the type name).</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";

    /// <summary>The store says this file is already on disk.</summary>
    public void MarkDownloaded(string? path)
    {
        OutputPath = path;
        State = YouTubeImportRowState.Downloaded;
    }

    /// <summary>
    /// Bind the queue item this row created. Progress, cancel and the final path
    /// then come from <see cref="DownloadItem"/> - the row keeps no copy of queue
    /// state, so it can never disagree with the status console's Downloads tab.
    /// </summary>
    public void Bind(DownloadItem item)
    {
        if (ReferenceEquals(_item, item))
        {
            SyncFromItem();
            return;
        }

        if (_item != null) _item.PropertyChanged -= OnItemPropertyChanged;
        _item = item;
        _item.PropertyChanged += OnItemPropertyChanged;
        SyncFromItem();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.State)
            or nameof(DownloadItem.Percent)
            or nameof(DownloadItem.OutputPath))
            SyncFromItem();
    }

    private void SyncFromItem()
    {
        if (_item is null) return;

        Percent = _item.Percent;
        if (!string.IsNullOrWhiteSpace(_item.OutputPath)) OutputPath = _item.OutputPath;
        State = _item.State switch
        {
            DownloadState.Queued => YouTubeImportRowState.Queued,
            DownloadState.Searching or DownloadState.Downloading
                or DownloadState.Converting or DownloadState.Tagging => YouTubeImportRowState.Downloading,
            DownloadState.Done => YouTubeImportRowState.Downloaded,
            DownloadState.Failed => YouTubeImportRowState.Failed,
            _ => YouTubeImportRowState.NotDownloaded,
        };
    }
}

/// <summary>
/// The YouTube tab's importer: paste a video or playlist link, list what it
/// contains, queue the rows through <see cref="DownloadQueue"/>, and browse the
/// files that already came from YouTube.
///
/// <para>Kept as a partial of <see cref="YouTubeViewModel"/> so the tab keeps one
/// status line, one queue and one "Add to playlist" menu. Everything here is
/// additive to the existing search/playlist behaviour.</para>
/// </summary>
public partial class YouTubeViewModel
{
    private CancellationTokenSource? _importCts;
    private YouTubeImportTarget? _importTarget;

    // ---- listing ----------------------------------------------------------

    /// <summary>Rows of the last successful listing, in playlist order.</summary>
    public ObservableCollection<YouTubeImportRow> ImporterRows { get; } = new();

    /// <summary>True once a listing (even an empty one) is loaded; the search list steps aside.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftPaneTitle), nameof(HasNoListing),
        nameof(ShowImporter), nameof(ShowSearchResults))]
    private bool _hasImportListing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadImporterCommand))]
    private bool _isImportLoading;

    /// <summary>Inline error under the paste box - never a dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportError))]
    private string _importError = "";

    /// <summary>True when the last failure could be retried (network), false for a bad link.</summary>
    [ObservableProperty]
    private bool _canRetryImport;

    /// <summary>The listing's own title: the playlist's name, or the video's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportMeta), nameof(LeftPaneTitle))]
    private string _importTitle = "";

    /// <summary>"PLAYLIST" or "VIDEO" - the small label above the title.</summary>
    [ObservableProperty]
    private string _importHeader = "";

    /// <summary>Entry count, skipped count and the playlist's name when yt-dlp gave one.</summary>
    [ObservableProperty]
    private string _importMeta = "";

    public bool HasImportError => !string.IsNullOrEmpty(ImportError);
    public bool HasNoListing => !HasImportListing;

    /// <summary>True when the listing is a single video: the toolbar says "Download video".</summary>
    public bool IsImportSingle => _importTarget?.IsVideo == true;

    public string DownloadSelectedText => IsImportSingle ? "Download video" : "Download selected";

    /// <summary>"Download all" is meaningless for one row, so it is hidden there.</summary>
    public bool ShowDownloadAll => IsImportSingle == false;

    /// <summary>Header over the list pane: the importer, the downloaded view, or search results.</summary>
    public string LeftPaneTitle => IsDownloadedView
        ? "DOWNLOADED FROM YOUTUBE"
        : HasImportListing ? "YOUTUBE IMPORT" : "SEARCH RESULTS";

    /// <summary>The importer listing replaces the search results until it is closed.</summary>
    public bool ShowImporter => HasImportListing && !IsDownloadedView;

    public bool ShowSearchResults => !HasImportListing && !IsDownloadedView;

    /// <summary>Empty-state hint shown when a playlist resolved to no rows.</summary>
    public string ImportEmptyText =>
        _importTarget?.IsPlaylist == true
            ? "That playlist has no playable entries."
            : "That video could not be read.";

    [RelayCommand(CanExecute = nameof(CanLoadImporter))]
    private async Task LoadImporterAsync()
    {
        if (!YouTubeImportParser.TryParse(ImportUrl, out var target, out var error) || target is null)
        {
            ShowImportError(error, canRetry: false);
            return;
        }

        await LoadImporterTargetAsync(target);
    }

    private bool CanLoadImporter() => !IsImportLoading;

    /// <summary>Load a target that is already parsed (the path unit tests and links use).</summary>
    public async Task LoadImporterTargetAsync(YouTubeImportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        _importCts?.Cancel();
        _importCts?.Dispose();
        var cts = new CancellationTokenSource();
        _importCts = cts;

        IsImportLoading = true;
        ImportError = "";
        CanRetryImport = false;
        StatusText = target.IsPlaylist ? "Loading playlist\u2026" : "Loading video\u2026";

        try
        {
            // The listing uses the same bootstrapped yt-dlp the queue uses, so a
            // first paste on a fresh install downloads the tool instead of
            // telling the operator to go and install it somewhere.
            var toolLog = new Progress<string>(message => Log.Debug($"tools: {message}"));
            if (!await ToolBootstrap.EnsureYtDlpAsync(_settings, toolLog, cts.Token))
                throw new YtDlpException(
                    "yt-dlp is not available and could not be downloaded. Check the connection and retry.", -1, "");

            var listing = await YtDlp.GetListingAsync(target, _settings, cts.Token);
            if (cts.IsCancellationRequested) return;

            ShowListing(target, listing);
        }
        catch (OperationCanceledException)
        {
            // A newer paste superseded this one; the newer call owns the UI.
        }
        catch (YtDlpException ex)
        {
            ShowImportError(ex.Message, canRetry: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube importer failed for {target.Url}: {ex.Message}");
            ShowImportError(ex.Message, canRetry: true);
        }
        finally
        {
            if (ReferenceEquals(_importCts, cts))
            {
                _importCts = null;
                IsImportLoading = false;
                LoadImporterCommand.NotifyCanExecuteChanged();
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task RetryImportAsync()
    {
        if (_importTarget is { } target) await LoadImporterTargetAsync(target);
        else await LoadImporterAsync();
    }

    /// <summary>Leave the listing and go back to search results.</summary>
    [RelayCommand]
    private void CloseImporter()
    {
        _importCts?.Cancel();
        _importTarget = null;
        ImporterRows.Clear();
        HasImportListing = false;
        ImportError = "";
        CanRetryImport = false;
        OnPropertyChanged(nameof(IsImportSingle));
        OnPropertyChanged(nameof(DownloadSelectedText));
        OnPropertyChanged(nameof(ShowDownloadAll));
    }

    private void ShowListing(YouTubeImportTarget target, YtListing listing)
    {
        _importTarget = target;
        IsDownloadedView = false;
        ImporterRows.Clear();

        // One code path for one video and for a playlist: yt-dlp already returns
        // a single entry for a video, and the same title cleaning applies.
        var tracks = YouTubeApi.ToSourceTracks(listing.Entries, playlistName: null);
        for (var i = 0; i < tracks.Count && i < listing.Entries.Count; i++)
        {
            var row = new YouTubeImportRow(listing.Entries[i], tracks[i], i);
            AdoptRowState(row);
            ImporterRows.Add(row);
        }

        ImportHeader = target.IsPlaylist ? "PLAYLIST" : "VIDEO";
        ImportTitle = string.IsNullOrWhiteSpace(listing.Title)
            ? (target.IsPlaylist ? "YouTube playlist" : "YouTube video")
            : listing.Title.Trim();

        var meta = new List<string>();
        if (target.IsPlaylist)
        {
            meta.Add(ImporterRows.Count == 1 ? "1 entry" : $"{ImporterRows.Count} entries");
        }
        else if (ImporterRows.FirstOrDefault() is { } single)
        {
            if (!string.IsNullOrWhiteSpace(single.Artist)) meta.Add(single.Artist);
            if (!string.IsNullOrWhiteSpace(single.DurationText)) meta.Add(single.DurationText);
        }

        if (listing.HasSkipped) meta.Add($"{listing.SkippedCount} skipped (private or deleted)");
        ImportMeta = string.Join(" \u00b7 ", meta);

        HasImportListing = true;
        ImportError = "";
        CanRetryImport = false;

        OnPropertyChanged(nameof(IsImportSingle));
        OnPropertyChanged(nameof(DownloadSelectedText));
        OnPropertyChanged(nameof(ShowDownloadAll));
        OnPropertyChanged(nameof(ImportEmptyText));

        StatusText = ImporterRows.Count == 0
            ? ImportEmptyText
            : $"Loaded {ImportMeta}.";
    }

    /// <summary>
    /// Paint a fresh row from what the app already knows: the ledger, an imported
    /// playlist row, or a live queue item. Only evidence, never a guess.
    /// </summary>
    private void AdoptRowState(YouTubeImportRow row)
    {
        var id = row.VideoId;
        if (string.IsNullOrWhiteSpace(id)) return;

        var recorded = _ledger.TryGet(id);
        if (recorded is not null && File.Exists(recorded.Path))
        {
            row.MarkDownloaded(recorded.Path);
            return;
        }

        var playlistRow = _store.Playlists
            .Where(p => p.Source == MediaSource.YouTube)
            .SelectMany(p => p.Tracks)
            .FirstOrDefault(t => t.Id == id && !string.IsNullOrWhiteSpace(t.LibraryPath));
        if (playlistRow?.LibraryPath is { } playlistPath && File.Exists(playlistPath))
        {
            row.MarkDownloaded(playlistPath);
            return;
        }

        var finished = _queue.Items.FirstOrDefault(item =>
            item.Track.Id == id && item.State == DownloadState.Done
            && !string.IsNullOrWhiteSpace(item.OutputPath));
        if (finished?.OutputPath is { } donePath && File.Exists(donePath))
        {
            row.MarkDownloaded(donePath);
            return;
        }

        var active = _queue.Items.FirstOrDefault(item => item.Track.Id == id && !item.IsFinished);
        if (active is not null) row.Bind(active);
    }

    private void ShowImportError(string message, bool canRetry)
    {
        ImportError = message;
        CanRetryImport = canRetry;
        ImporterRows.Clear();
        HasImportListing = false;
        _importTarget = null;
        StatusText = message;
        OnPropertyChanged(nameof(IsImportSingle));
        OnPropertyChanged(nameof(DownloadSelectedText));
        OnPropertyChanged(nameof(ShowDownloadAll));
    }

    partial void OnImportUrlChanged(string value)
    {
        // Typing over a rejection clears it; a network failure keeps its Retry
        // button until the next Load.
        if (HasImportError && !CanRetryImport) ImportError = "";
    }

    // ---- queue ------------------------------------------------------------

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var rows = ImporterRows
            .Where(row => row.IsSelected && row.CanQueue)
            .ToList();
        await QueueImporterRowsAsync(rows);
    }

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        var rows = ImporterRows.Where(row => row.CanQueue).ToList();
        await QueueImporterRowsAsync(rows);
    }

    private async Task QueueImporterRowsAsync(IReadOnlyList<YouTubeImportRow> rows)
    {
        if (rows.Count == 0)
        {
            StatusText = ImporterRows.Count == 0
                ? ImportEmptyText
                : "Nothing to download \u2014 those rows are already queued or on disk.";
            await Task.CompletedTask;
            return;
        }

        var playlistName = _importTarget?.IsPlaylist == true ? ImportTitle : null;
        var queued = 0;
        foreach (var row in rows)
        {
            var track = row.Track;
            if (!string.IsNullOrWhiteSpace(playlistName)) track.PlaylistName = playlistName;

            // IndexInPlaylist keeps the playlist's order in the layout token, and
            // Enqueue copies the row's prior state so a cancel can restore it.
            var item = _queue.Enqueue(track, DownloadKind.Library, row.Index);
            row.Bind(item);
            queued++;
        }

        OnPropertyChanged(nameof(LeftPaneTitle));
        StatusText = queued == 1
            ? $"Download of {rows[0].Title} added to the download queue."
            : $"Downloading {queued} tracks from {ImportTitle}.";
        await Task.CompletedTask;
    }

    // ---- downloaded view --------------------------------------------------

    /// <summary>Files on disk that came from YouTube, resolved from the store.</summary>
    public ObservableCollection<YouTubeLibraryFile> DownloadedFiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftPaneTitle), nameof(IsSearchVisible),
        nameof(ShowImporter), nameof(ShowSearchResults))]
    private bool _isDownloadedView;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayDownloadedCommand), nameof(RevealDownloadedCommand))]
    private YouTubeLibraryFile? _selectedDownloadedFile;

    /// <summary>How the list resolved, in one line (counts, derivations, ledger share).</summary>
    [ObservableProperty]
    private string _downloadedSummary = "";

    public bool HasDownloadedFiles => DownloadedFiles.Count > 0;

    /// <summary>Search results and the importer hide while the downloaded view is on.</summary>
    public bool IsSearchVisible => !IsDownloadedView;

    [RelayCommand]
    private void ShowDownloaded() => IsDownloadedView = true;

    [RelayCommand]
    private void ShowSearch() => IsDownloadedView = false;

    /// <summary>The toggle and the command both land here: fresh evidence on every open.</summary>
    partial void OnIsDownloadedViewChanged(bool value)
    {
        if (value) RefreshDownloadedLibrary();
    }

    /// <summary>Re-read the ledger, the playlists and the disk.</summary>
    [RelayCommand]
    private void RefreshDownloaded() => RefreshDownloadedLibrary();

    private void RefreshDownloadedLibrary()
    {
        try
        {
            var result = YouTubeLibrary.Resolve(_ledger.Entries, _store.Playlists, File.Exists);
            DownloadedFiles.Clear();
            foreach (var file in result.Files) DownloadedFiles.Add(file);
            SelectedDownloadedFile = DownloadedFiles.FirstOrDefault();
            DownloadedSummary = result.Summary;
            OnPropertyChanged(nameof(HasDownloadedFiles));
            StatusText = result.Summary;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve the YouTube downloads: {ex.Message}");
            DownloadedSummary = "Could not read the download store.";
        }
    }

    private bool CanPlayDownloaded() => SelectedDownloadedFile is not null;

    [RelayCommand(CanExecute = nameof(CanPlayDownloaded))]
    private Task PlayDownloadedAsync() => PlayDownloadedFileAsync(SelectedDownloadedFile);

    /// <summary>Load the file on a deck - the same <see cref="_play"/> path the library uses.</summary>
    [RelayCommand]
    private async Task PlayDownloadedFileAsync(YouTubeLibraryFile? file)
    {
        if (file is null) return;

        StatusText = $"Loading {file.Title} on a deck\u2026";
        try
        {
            await _play(file.ToSourceTrack());
            StatusText = $"Playing {file.Title}.";
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not play {file.Path}: {ex.Message}");
            StatusText = "Could not load that file: " + ex.Message;
        }
    }

    private bool CanRevealDownloaded() => SelectedDownloadedFile is not null;

    [RelayCommand(CanExecute = nameof(CanRevealDownloaded))]
    private void RevealDownloaded() => RevealDownloadedFile(SelectedDownloadedFile);

    [RelayCommand]
    private void RevealDownloadedFile(YouTubeLibraryFile? file)
    {
        if (file is null) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not reveal {file.Path}: {ex.Message}");
            StatusText = "Could not open Explorer: " + ex.Message;
        }
    }
}
