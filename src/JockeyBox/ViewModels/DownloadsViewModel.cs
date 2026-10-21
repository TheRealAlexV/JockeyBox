using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Downloads;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// The download queue window: live progress, per-item stage, failures with
/// retry, and the batch options (bitrate, layout, auto-karaoke).
/// </summary>
public partial class DownloadsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DownloadQueue _queue;
    private readonly DispatcherTimer _timer;

    // The queue owns the real cache; this instance only measures/clears the same
    // folder so the window does not need the playback service handed to it.
    private readonly Playback.StreamCache _cache;

    /// <summary>A command result ("Re-queued 2 item(s)") hides the live line until this passes.</summary>
    private DateTime _statusHoldUntil = DateTime.MinValue;
    private long _ticks;
    private bool _refreshQueued;

    public DownloadsViewModel(AppSettings settings, DownloadQueue queue)
    {
        _settings = settings;
        _queue = queue;
        _cache = new Playback.StreamCache(settings.StreamCacheLimitMb);

        View = CollectionViewSource.GetDefaultView(_queue.Items);
        View.Filter = FilterItem;

        // Counts and the overall bar are cheap to recompute, but doing it on a
        // timer avoids re-entrancy from worker-thread item notifications. The
        // timer is owned by this view model but driven by the window lifetime
        // (StartPolling/StopPolling) so a closed window stops ticking.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Refresh();

        // The counts also drive the badge on the main window, which outlives
        // this window, so they cannot depend on the poll timer. State changes
        // are few (a handful per item) and push the counts immediately; the
        // timer above only smooths the live status line and cache size while
        // the window is open.
        _queue.Items.CollectionChanged += OnItemsChanged;
        foreach (var item in _queue.Items) item.PropertyChanged += OnItemPropertyChanged;

        // Tool versions are read from disk/processes, so they cannot be filled
        // in synchronously here. Without this the strip claims "not installed"
        // on a machine where both tools resolve fine.
        _ = RefreshToolVersionsAsync();

        Refresh();
    }

    public ObservableCollection<DownloadItem> Items => _queue.Items;
    public ICollectionView View { get; }

    public string[] Filters { get; } = { "Active", "Failed", "All" };

    [ObservableProperty] private string _selectedFilter = "Active";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _statusLine = "Queue is empty.";
    [ObservableProperty] private string _cacheStats = "cache: \u2014";
    [ObservableProperty] private bool _isInstallingTools;
    [ObservableProperty] private string _toolsStatus = "";

    public string ToolVersions
    {
        get
        {
            var yt = _settings.YtDlpVersion;
            var ff = _settings.FfmpegVersion;
            if (string.IsNullOrWhiteSpace(yt) && string.IsNullOrWhiteSpace(ff))
                return "yt-dlp: not installed \u00b7 ffmpeg: not installed";
            if (string.IsNullOrWhiteSpace(yt)) return $"yt-dlp: not installed \u00b7 ffmpeg {ff}";
            if (string.IsNullOrWhiteSpace(ff)) return $"yt-dlp {yt} \u00b7 ffmpeg: not installed";
            return $"yt-dlp {yt} \u00b7 ffmpeg {ff}";
        }
    }

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>"Retry failed (3)" so the batch action says how much work it implies.</summary>
    public string RetryFailedLabel => FailedCount == 0 ? "Retry failed" : $"Retry failed ({FailedCount})";

    /// <summary>
    /// Placeholder for the (empty) list area: distinguishes "queue is empty"
    /// from "this filter has nothing in it" so a Done-only queue does not look
    /// broken under the default Active filter.
    /// </summary>
    public string EmptyHint
    {
        get
        {
            if (TotalCount == 0)
                return "Nothing queued. Download a Spotify playlist or a YouTube result and it will show up here.";
            if (SelectedFilter == "All" || !View.IsEmpty) return "";
            return SelectedFilter == "Failed"
                ? "No failed downloads. Good."
                : "Nothing active right now \u2014 switch to All to see finished downloads.";
        }
    }

    partial void OnSelectedFilterChanged(string value)
    {
        View.Refresh();
        OnPropertyChanged(nameof(EmptyHint));
    }

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

    partial void OnFailedCountChanged(int value) => OnPropertyChanged(nameof(RetryFailedLabel));

    partial void OnIsInstallingToolsChanged(bool value)
    {
        UpdateYtDlpCommand.NotifyCanExecuteChanged();
        UpdateFfmpegCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Begin the 400 ms refresh tick. The window calls this from its Loaded handler.</summary>
    public void StartPolling()
    {
        RefreshCacheStats();
        Refresh();
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Stop the refresh tick when the queue window closes.</summary>
    public void StopPolling() => _timer.Stop();

    /// <summary>Re-read the stream cache size. Cheap enough for open + after Clear.</summary>
    public void RefreshCacheStats()
    {
        try
        {
            // Keep the limit in step with Settings; this instance is only a
            // measurer for the same folder the playback cache uses.
            _cache.LimitBytes = _settings.StreamCacheLimitMb * 1024L * 1024L;
            CacheStats = $"cache: {_cache.StatsText()}";
        }
        catch (Exception ex)
        {
            CacheStats = "cache: unavailable";
            Log.Warn($"Could not read the stream cache stats: {ex.Message}");
        }
    }

    // ---- live counts ------------------------------------------------------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (DownloadItem item in e.OldItems) item.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null)
            foreach (DownloadItem item in e.NewItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                item.PropertyChanged += OnItemPropertyChanged;
            }
        RequestRefresh();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Percent moves constantly and is bound directly on the row; only the
        // state transitions change the counts and the command availability.
        if (e.PropertyName is nameof(DownloadItem.State) or nameof(DownloadItem.OutputPath))
            RequestRefresh();
    }

    /// <summary>
    /// Refresh on the UI thread: items change from queue workers and from
    /// collection notifications, and a view refresh is illegal inside the
    /// latter. Posting is coalesced so a burst of state changes costs one pass.
    /// </summary>
    private void RequestRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Refresh();
            return;
        }

        if (_refreshQueued) return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _refreshQueued = false;
            Refresh();
        }));
    }

    private bool FilterItem(object obj)
    {
        if (obj is not DownloadItem item) return false;
        return SelectedFilter switch
        {
            "Failed" => item.State == DownloadState.Failed,
            "All" => true,
            _ => !item.IsFinished,
        };
    }

    private void Refresh()
    {
        TotalCount = Items.Count;
        QueuedCount = Items.Count(i => i.State == DownloadState.Queued);
        RunningCount = Items.Count(i => i.IsActive);
        ActiveCount = Items.Count(i => !i.IsFinished);
        FailedCount = Items.Count(i => i.State == DownloadState.Failed);
        CompletedCount = Items.Count(i => i.State == DownloadState.Done);
        IsPaused = _queue.IsPaused;

        var relevant = Items.Where(i => !i.IsFinished).ToList();
        OverallProgress = relevant.Count == 0
            ? (CompletedCount > 0 ? 100 : 0)
            : relevant.Average(i => i.Percent);

        if (!IsInstallingTools && DateTime.UtcNow >= _statusHoldUntil)
            StatusLine = BuildLiveStatus();

        View.Refresh();
        OnPropertyChanged(nameof(EmptyHint));
        TogglePauseCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged();
        RetryItemCommand.NotifyCanExecuteChanged();
        CancelItemCommand.NotifyCanExecuteChanged();
        OpenFileCommand.NotifyCanExecuteChanged();

        // Measuring the cache walks the cache folder; once every ~10 s is plenty.
        if (++_ticks % 25 == 0) RefreshCacheStats();
    }

    /// <summary>
    /// The always-on line: counts on the left, the current track + percent on
    /// the right, so the window is readable when the main window covers it.
    /// </summary>
    private string BuildLiveStatus()
    {
        if (TotalCount == 0) return "Queue is empty.";

        var parts = new List<string>
        {
            $"{QueuedCount} queued",
            $"{RunningCount} active",
            $"{CompletedCount} done",
            $"{FailedCount} failed",
        };

        if (IsPaused) parts.Insert(0, "paused");

        var current = Items.FirstOrDefault(i => i.IsActive);
        if (current != null)
        {
            var progress = current.State == DownloadState.Downloading
                ? $"{current.Percent:0}%"
                : current.StageText;
            parts.Add($"{current.Title} {progress}");
        }
        else
        {
            var next = Items.FirstOrDefault(i => i.State == DownloadState.Queued);
            if (next != null) parts.Add($"next: {next.Title}");
        }

        return string.Join(" \u00b7 ", parts);
    }

    /// <summary>Show a command result for a while, then let the live line take over again.</summary>
    private void SetStatus(string text, double holdSeconds = 4)
    {
        StatusLine = text;
        _statusHoldUntil = holdSeconds <= 0
            ? DateTime.MaxValue
            : DateTime.UtcNow.AddSeconds(holdSeconds);
    }

    // ---- commands ---------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanTogglePause))]
    private void TogglePause()
    {
        if (_queue.IsPaused) _queue.Resume();
        else _queue.Pause();
        IsPaused = _queue.IsPaused;
        Refresh();
    }

    private bool CanTogglePause() => ActiveCount > 0;

    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        _queue.CancelAll();
        SetStatus("Cancelled the queue.");
        Refresh();
    }

    private bool CanCancelAll() => ActiveCount > 0;

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private void RetryFailed()
    {
        var count = _queue.RetryFailed();
        SetStatus(count == 0 ? "Nothing to retry." : $"Re-queued {count} item(s).");
        Refresh();
    }

    private bool CanRetryFailed() => FailedCount > 0;

    [RelayCommand(CanExecute = nameof(CanClearCompleted))]
    private void ClearCompleted()
    {
        _queue.ClearCompleted();
        Refresh();
    }

    private bool CanClearCompleted() => Items.Any(i => i.IsFinished);

    /// <summary>Row action: re-run one failed item.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryItem))]
    private void RetryItem(DownloadItem? item)
    {
        if (item is null) return;
        _queue.Retry(item);
        SetStatus($"Retrying {item.Title}.");
        Refresh();
    }

    private static bool CanRetryItem(DownloadItem? item) => item?.State == DownloadState.Failed;

    /// <summary>
    /// Row action: drop a still-queued item. The worker skips cancelled items,
    /// so flipping the state here is the same thing CancelAll does for the queue.
    /// (An item already inside ProcessAsync can only be stopped by the queue's
    /// own cancellation token, which "Cancel all" owns.)
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelItem))]
    private void CancelItem(DownloadItem? item)
    {
        if (item is null || item.State != DownloadState.Queued) return;
        item.State = DownloadState.Cancelled;
        item.Error = null;
        item.Track.State = SourceTrackState.New;
        item.Track.Progress = 0;
        item.Completion?.TrySetResult(null);
        SetStatus($"Removed {item.Title} from the queue.");
        Refresh();
    }

    private static bool CanCancelItem(DownloadItem? item) => item?.State == DownloadState.Queued;

    /// <summary>Row action: reveal a finished file in Explorer with the file selected.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile(DownloadItem? item)
    {
        var path = item?.OutputPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            else
                SetStatus("That file is no longer on disk.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not show the downloaded file: {ex.Message}");
            SetStatus($"Could not open the file: {ex.Message}");
        }
    }

    private static bool CanOpenFile(DownloadItem? item) =>
        item?.State == DownloadState.Done && !string.IsNullOrWhiteSpace(item.OutputPath);

    [RelayCommand(CanExecute = nameof(CanUpdateTools))]
    private async Task InstallToolsAsync()
    {
        if (IsInstallingTools) return;
        IsInstallingTools = true;
        SetStatus("Checking yt-dlp and ffmpeg\u2026", 0);
        try
        {
            var progress = new Progress<string>(message => ToolsStatus = message);
            var ok = await ToolBootstrap.EnsureAllAsync(_settings, progress, CancellationToken.None);
            await RefreshToolVersionsAsync();
            SetStatus(ok
                ? "yt-dlp and ffmpeg are ready."
                : "yt-dlp is not available \u2014 see the log for the download error.", 5);
        }
        catch (Exception ex)
        {
            Log.Error("Tool bootstrap failed", ex);
            SetStatus($"Tool setup failed: {ex.Message}", 6);
        }
        finally
        {
            IsInstallingTools = false;
            ToolsStatus = "";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateTools))]
    private async Task UpdateYtDlpAsync()
    {
        if (IsInstallingTools) return;
        IsInstallingTools = true;
        SetStatus("Updating yt-dlp\u2026", 0);
        try
        {
            var progress = new Progress<string>(message => ToolsStatus = message);
            var ok = await ToolBootstrap.UpdateYtDlpAsync(_settings, progress, CancellationToken.None);
            await RefreshToolVersionsAsync();
            SetStatus(ok ? "yt-dlp updated." : "yt-dlp update failed \u2014 see the log.", 5);
        }
        catch (Exception ex)
        {
            Log.Error("yt-dlp update failed", ex);
            SetStatus($"yt-dlp update failed: {ex.Message}", 6);
        }
        finally
        {
            IsInstallingTools = false;
            ToolsStatus = "";
        }
    }

    /// <summary>Re-download the bundled ffmpeg (it has no self-updater on Windows).</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateTools))]
    private async Task UpdateFfmpegAsync()
    {
        if (IsInstallingTools) return;
        IsInstallingTools = true;
        SetStatus("Updating ffmpeg\u2026", 0);
        try
        {
            var progress = new Progress<string>(message => ToolsStatus = message);
            var ok = await ToolBootstrap.UpdateFfmpegAsync(_settings, progress, CancellationToken.None);
            await RefreshToolVersionsAsync();
            SetStatus(ok ? "ffmpeg updated." : "ffmpeg update failed \u2014 see the log.", 5);
        }
        catch (Exception ex)
        {
            Log.Error("ffmpeg update failed", ex);
            SetStatus($"ffmpeg update failed: {ex.Message}", 6);
        }
        finally
        {
            IsInstallingTools = false;
            ToolsStatus = "";
        }
    }

    private bool CanUpdateTools() => !IsInstallingTools;

    /// <summary>Read both tool versions and persist them for the Settings/queue display.</summary>
    public async Task RefreshToolVersionsAsync()
    {
        try
        {
            var ytPath = ToolBootstrap.ResolveYtDlp(_settings);
            var ffPath = ToolBootstrap.ResolveFfmpeg(_settings);
            _settings.YtDlpVersion = ytPath == null
                ? "" : await ToolBootstrap.GetVersionAsync(ytPath, CancellationToken.None) ?? "found";
            // A wrapped ffmpeg (e.g. a package-manager shim) can refuse to answer
            // --version while still converting audio for yt-dlp, so "found" is a
            // better answer than claiming the tool is missing.
            _settings.FfmpegVersion = ffPath == null
                ? "" : await ToolBootstrap.GetVersionAsync(ffPath, CancellationToken.None) ?? "found";
            _settings.Save();
            OnPropertyChanged(nameof(ToolVersions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read tool versions: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Tools.Log.DirectoryPath);
            Process.Start(new ProcessStartInfo(Tools.Log.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the log folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenMusicFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.MusicDirectory)) return;
            Process.Start(new ProcessStartInfo(_settings.MusicDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the music folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            _cache.Clear();
            RefreshCacheStats();
            SetStatus("Stream cache cleared.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not clear the cache: {ex.Message}");
        }
    }
}
