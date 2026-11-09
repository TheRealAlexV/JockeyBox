using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JockeyBox.Library;
using JockeyBox.Sources;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.YouTube;

namespace JockeyBox.Downloads;

/// <summary>
/// Background download pump shared by every source: Spotify playlist imports,
/// YouTube videos and one-off "play this" streaming.
///
/// One queue, N workers (settings.DownloadConcurrency). Each item walks the
/// stages search -> download -> convert -> tag -> ledger, reports progress back
/// to its <see cref="DownloadItem"/> row, and survives a restart: the queue is
/// persisted to %APPDATA%\JockeyBox\downloads\queue.json on every state change
/// and rebuilt by <see cref="RestorePersisted"/> after the host constructs it.
///
/// Nothing here touches the UI thread; consumers marshal via ObservableCollection
/// updates from the dispatcher (the WPF binding engine handles that itself).
///
/// Pausing is deliberately "between items" only: <see cref="Pause"/> stops the
/// workers picking up new work, it does not suspend a transfer that is already
/// running (that would need yt-dlp process-level suspend and is out of scope).
/// </summary>
public sealed class DownloadQueue : IDisposable
{
    private const int MaxAttempts = 3;

    /// <summary>Progress/state changes are coalesced into one write per this many ms.</summary>
    private const int SaveCoalesceMs = 250;

    /// <summary>Progress is pushed to the source track at most this often (~4 Hz).</summary>
    private const int ProgressPushIntervalMs = 250;

    /// <summary>How much of yt-dlp's stderr line ends up in <see cref="DownloadItem.Error"/>.</summary>
    private const int StderrLineMax = 200;

    private const string QueueFileName = "queue.json";
    private const string DownloadsDirectoryName = "downloads";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppSettings _settings;
    private readonly LibraryLedger _ledger;
    private readonly StreamCache _cache;
    private readonly LibraryIndex _library;

    private readonly Channel<DownloadItem> _channel =
        Channel.CreateUnbounded<DownloadItem>(new UnboundedChannelOptions { SingleReader = false });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = new();
    private readonly object _gate = new();

    /// <summary>Guards the debounced queue.json writer.</summary>
    private readonly object _saveGate = new();

    /// <summary>One CTS per in-flight item, so CancelAll can stop all of them.</summary>
    private readonly HashSet<CancellationTokenSource> _liveItemCts = new();

    private TaskCompletionSource? _drainSignal;
    private Timer? _saveTimer;
    private bool _savePending;
    private bool _restored;

    private volatile bool _paused;
    private volatile bool _disposed;

    /// <summary>A shortlist candidate must score at least this to count as the track.</summary>
    private const double MinConfidentScore = 0.55;

    /// <summary>How many ranked matches are kept as fallbacks.</summary>
    private const int MaxCandidates = 5;

    /// <summary>Guards the one-time yt-dlp/ffmpeg bootstrap across workers.</summary>
    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private bool _toolsReady;

    public DownloadQueue(
        AppSettings settings,
        LibraryLedger ledger,
        StreamCache cache,
        LibraryIndex? library = null)
    {
        _settings = settings;
        _ledger = ledger;
        _cache = cache;
        _library = library ?? LibraryIndex.Empty;
    }

    /// <summary>%APPDATA%\JockeyBox\downloads\queue.json</summary>
    public static string FilePath { get; } = Path.Combine(AppPaths.Home, DownloadsDirectoryName, QueueFileName);

    /// <summary>Every queued/completed item, newest last. Bound by the status console's Downloads tab.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = new();

    /// <summary>Raised (on a worker thread) after an item reaches any terminal state.</summary>
    public event Action<DownloadItem>? ItemFinished;

    /// <summary>Raised when a permanent library file was added, so the browser can rescan.</summary>
    public event Action? LibraryChanged;

    /// <summary>Raised (on a background thread) after a downloaded file was deleted from disk.</summary>
    public event Action<DownloadItem>? FileDeleted;

    /// <summary>
    /// Raised when the queue becomes idle while someone is awaiting
    /// <see cref="WhenDrainedAsync"/>. It is not a general "queue empty" event:
    /// with no waiter there is nothing to wake, so nothing is raised.
    /// </summary>
    public event Action? Drained;

    public bool IsPaused => _paused;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return Items.Any(i => i.State is DownloadState.Queued
                    || i.State is DownloadState.Searching or DownloadState.Downloading
                       or DownloadState.Converting or DownloadState.Tagging);
        }
    }

    // ---- enqueue -----------------------------------------------------------

    /// <summary>
    /// Queue a track. Returns the item so a caller can await it (streaming) or
    /// watch its progress (imports).
    /// </summary>
    /// <param name="track">Track to fetch.</param>
    /// <param name="kind">Library copy or throwaway stream cache file.</param>
    /// <param name="indexInPlaylist">
    /// 0-based position of the track inside its playlist; drives the "NN Title"
    /// layout token. Callers that pass a filtered list should pass the original
    /// playlist position (or at least a running start index).
    /// </param>
    /// <param name="layoutOverride">Per-download layout preset; null/empty uses the global setting.</param>
    public DownloadItem Enqueue(
        SourceTrack track,
        DownloadKind kind = DownloadKind.Library,
        int indexInPlaylist = 0,
        string? layoutOverride = null)
    {
        var item = new DownloadItem
        {
            Key = $"{kind}:{track.Source}:{track.Id}:{Environment.TickCount64}",
            Track = track,
            Kind = kind,
            IndexInPlaylist = Math.Max(0, indexInPlaylist),
            LayoutOverride = layoutOverride,
            State = DownloadState.Queued,
            // Remember where the row was, so a preview (which is not a library
            // download) or a cancel can put it back instead of leaving it at
            // Downloading.
            PriorState = track.State is SourceTrackState.InLibrary or SourceTrackState.Failed
                ? track.State
                : SourceTrackState.New,
        };

        if (kind == DownloadKind.Stream)
            item.Completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Report into the shared activity feed straight away, so a queued item is
        // visible in the status console before a worker picks it up.
        item.PublishActivity();

        // Already on disk? Skip the network entirely, before the item ever reaches a worker.
        if (kind == DownloadKind.Library && _settings.DownloadSkipExisting)
        {
            var existing = ResolveInLibrary(track);
            if (!string.IsNullOrEmpty(existing))
            {
                MarkInLibrary(item, existing);
                lock (_gate) Items.Add(item);
                Log.Debug($"already in library: {track.DisplayName} -> {existing}");
                RequestSave();
                return item;
            }
        }

        lock (_gate) Items.Add(item);

        track.State = SourceTrackState.Queued;
        track.Progress = 0;
        track.Error = null;
        _channel.Writer.TryWrite(item);
        EnsureWorkers();
        RequestSave();
        Log.Debug($"queued {kind} {track.DisplayName} ({track.Id})");
        return item;
    }

    /// <summary>
    /// Queue many tracks; returns how many were newly queued. The sequence
    /// position becomes the item's <see cref="DownloadItem.IndexInPlaylist"/>
    /// (offset by <paramref name="startIndex"/>), so a playlist import numbers its
    /// files 01, 02, ... instead of "01" for everything.
    /// </summary>
    public int EnqueueMany(
        IEnumerable<SourceTrack> tracks,
        DownloadKind kind = DownloadKind.Library,
        int startIndex = 0,
        string? layoutOverride = null)
    {
        var count = 0;
        var index = startIndex;
        foreach (var track in tracks)
        {
            if (track.State is SourceTrackState.Queued or SourceTrackState.Downloading)
            {
                index++;
                continue;
            }

            Enqueue(track, kind, index, layoutOverride);
            count++;
            index++;
        }
        return count;
    }

    /// <summary>Re-run a failed/cancelled item in place.</summary>
    public void Retry(DownloadItem item)
    {
        if (!item.IsFinished) return;
        item.Error = null;
        item.Detail = null;
        item.Percent = 0;
        item.Attempts = 0;
        item.CandidateIndex = 0;
        item.State = DownloadState.Queued;
        item.Track.State = SourceTrackState.Queued;
        item.Track.Progress = 0;
        item.Track.Error = null;
        _channel.Writer.TryWrite(item);
        EnsureWorkers();
        RequestSave();
    }

    /// <summary>Retry every failed item. Returns how many were re-queued.</summary>
    public int RetryFailed()
    {
        List<DownloadItem> failed;
        lock (_gate) failed = Items.Where(i => i.State == DownloadState.Failed).ToList();
        foreach (var item in failed) Retry(item);
        return failed.Count;
    }

    public void ClearCompleted()
    {
        lock (_gate)
        {
            foreach (var item in Items.Where(i => i.IsFinished).ToList())
                Items.Remove(item);
        }
        RequestSave();
    }

    /// <summary>
    /// Delete the file behind a finished download, then forget it in the ledger
    /// so the app stops believing the track is downloaded.
    ///
    /// Safe by construction: the path must exist and must live under the
    /// configured Music/Karaoke folders (path-traversal guard), the file goes to
    /// the Recycle Bin so a mistaken click is undoable, and every failure is
    /// reported through <see cref="DownloadItem.Error"/> instead of throwing
    /// into the UI thread.
    /// </summary>
    /// <param name="item">Finished download to delete.</param>
    /// <param name="alsoRemoveLedger">Drop the matching ledger entry (default true).</param>
    /// <returns>True when the file is gone and the bookkeeping was updated.</returns>
    public Task<bool> DeleteDownloadedFileAsync(DownloadItem item, bool alsoRemoveLedger = true)
    {
        if (item is null) return Task.FromResult(false);

        // File probes and the shell's recycle call can block on a slow or
        // sleeping volume; keep them off the dispatcher that owns the command.
        return Task.Run(() => DeleteDownloadedFile(item, alsoRemoveLedger));
    }

    private bool DeleteDownloadedFile(DownloadItem item, bool alsoRemoveLedger)
    {
        var path = item.OutputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            item.Error = "There is no file recorded for this download.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Refusing to delete '{path}': unusable path", ex);
            item.Error = $"Not a usable path: {path}";
            return false;
        }

        if (!IsInsideLibrary(full))
        {
            // Never delete outside the folders the user configured, whatever the
            // queue item (or a hand-edited queue.json) claims.
            Log.Warn($"Refusing to delete '{full}': outside the music/karaoke folders");
            item.Error = "Refused: that file is outside the music/karaoke folders.";
            return false;
        }

        if (!File.Exists(full))
        {
            // Already gone: drop the stale ledger entry so the tick disappears.
            if (alsoRemoveLedger) _ledger.RemoveByPath(full);
            item.Error = "That file is no longer on disk.";
            return false;
        }

        try
        {
            MoveToRecycleBin(full);
            Log.Info($"deleted {full} (Recycle Bin)");
        }
        catch (Exception ex)
        {
            // Recycle Bin is a nicety; if the shell refuses, deleting for real
            // still beats leaving a file the user asked to remove.
            Log.Error($"Recycle Bin delete failed for '{full}'; falling back to File.Delete", ex);
            try
            {
                File.Delete(full);
            }
            catch (Exception inner)
            {
                Log.Error($"Could not delete '{full}'", inner);
                item.Error = $"Could not delete the file: {inner.Message}";
                return false;
            }
        }

        if (alsoRemoveLedger) _ledger.RemoveByPath(full);

        item.OutputPath = null;
        item.Error = null;
        item.Detail = null;

        var track = item.Track;
        if (PathsEqual(track.LibraryPath, full))
        {
            track.LibraryPath = null;
            track.State = SourceTrackState.New;
            track.Progress = 0;
        }
        if (PathsEqual(track.PreviewPath, full)) track.PreviewPath = null;
        track.Error = null;

        try
        {
            FileDeleted?.Invoke(item);
            // The library index is now stale; the host rescans on this event.
            LibraryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("A file-deleted handler threw", ex);
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> sits under the configured Music or
    /// Karaoke root. The separator matters: "C:\Music2" must not match "C:\Music".
    /// </summary>
    private bool IsInsideLibrary(string fullPath)
    {
        foreach (var root in new[] { _settings.MusicDirectory, _settings.KaraokeDirectory })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            string normalised;
            try
            {
                normalised = Path.GetFullPath(root);
            }
            catch (Exception ex)
            {
                Log.Warn($"Ignoring unusable library root '{root}': {ex.Message}");
                continue;
            }

            var prefix = normalised.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (prefix.Length == 0) continue;
            prefix += Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Send one file to the Recycle Bin so a mistaken delete is undoable.</summary>
    private static void MoveToRecycleBin(string fullPath) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            fullPath,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    /// <summary>
    /// Cancel everything in flight and everything queued. Each worker owns its
    /// item's CTS, so this cancels all of them - not just the newest one - and
    /// marks the not-yet-started items Cancelled so the workers skip them.
    /// </summary>
    public void CancelAll()
    {
        CancellationTokenSource[] live;
        List<DownloadItem> victims;

        lock (_gate)
        {
            live = _liveItemCts.ToArray();
            victims = Items
                .Where(i => i.State is DownloadState.Queued
                    or DownloadState.Searching or DownloadState.Downloading
                    or DownloadState.Converting or DownloadState.Tagging)
                .ToList();
        }

        // Item state is observable and bound by the Downloads tab of the status
        // console, so flip it outside the queue lock.
        foreach (var item in victims)
        {
            item.State = DownloadState.Cancelled;
            // Back to where the row was, not to a hard-coded New: a cancelled
            // library download that was already InLibrary must keep its tick.
            item.Track.State = item.PriorState;
            item.Track.Progress = 0;
            item.Completion?.TrySetResult(null);
        }

        foreach (var cts in live)
        {
            try { cts.Cancel(); } catch { /* already disposed or cancelled */ }
        }

        // A cancellation is a terminal state change for the whole queue.
        PersistNow();
    }

    /// <summary>
    /// Completes when the queue drains, i.e. when no item is queued or running.
    /// Returns a completed task when the queue is already idle. Only one waiter
    /// is tracked at a time (<see cref="_drainSignal"/>); the <see cref="Drained"/>
    /// event is raised alongside it, and only while such a waiter exists.
    /// </summary>
    public Task WhenDrainedAsync()
    {
        lock (_gate)
        {
            if (!IsRunning) return Task.CompletedTask;
            _drainSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drainSignal.Task;
        }
    }

    // ---- persistence -------------------------------------------------------

    /// <summary>
    /// Rebuild the queue from queue.json. Call once after construction (before
    /// the first enqueue). Items that were Queued/Searching/Downloading/
    /// Converting/Tagging are re-enqueued as Queued; Failed items are restored as
    /// Failed so the existing Retry action can pick them up. Done/Cancelled items
    /// are not persisted and therefore never come back.
    /// </summary>
    public void RestorePersisted()
    {
        lock (_gate)
        {
            if (_restored) return;
            _restored = true;
        }

        List<QueueRecord> records;
        try
        {
            records = LoadPersisted();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the download queue from '{FilePath}'", ex);
            return;
        }

        if (records.Count == 0) return;

        var requeued = 0;
        var failed = 0;

        foreach (var record in records)
        {
            if (record is null) continue;
            if (record.State is DownloadState.Done or DownloadState.Cancelled) continue;
            if (string.IsNullOrWhiteSpace(record.Id) && string.IsNullOrWhiteSpace(record.Title)) continue;

            var track = new SourceTrack
            {
                Id = record.Id,
                Source = record.Source,
                Title = record.Title,
                Artist = record.Artist,
                Album = record.Album,
                Isrc = record.Isrc,
                CoverUrl = record.CoverUrl,
                DurationMs = record.DurationMs,
                VideoId = string.IsNullOrWhiteSpace(record.VideoId) ? null : record.VideoId,
                PlaylistName = record.PlaylistName,
            };

            var isFailed = record.State == DownloadState.Failed;

            var item = new DownloadItem
            {
                Key = string.IsNullOrWhiteSpace(record.Key)
                    ? $"restore:{record.Kind}:{record.Source}:{record.Id}:{Guid.NewGuid():N}"
                    : record.Key,
                Track = track,
                Kind = record.Kind,
                IndexInPlaylist = Math.Max(0, record.IndexInPlaylist),
                LayoutOverride = record.LayoutOverride,
                State = isFailed ? DownloadState.Failed : DownloadState.Queued,
                PriorState = isFailed ? SourceTrackState.Failed : SourceTrackState.New,
                // A restarted process gets a fresh retry budget; keeping a spent
                // one would fail the item again on the first hiccup.
                Attempts = isFailed ? record.Attempts : 0,
                OutputPath = record.OutputPath,
                Error = isFailed ? record.Error : null,
                Detail = isFailed ? record.Detail : null,
            };

            item.CandidateIds.AddRange((record.CandidateIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)));
            item.CandidateIndex = Math.Clamp(
                record.CandidateIndex, 0, Math.Max(0, item.CandidateIds.Count - 1));

            track.State = isFailed ? SourceTrackState.Failed : SourceTrackState.Queued;
            if (isFailed) track.Error = record.Error;

            var duplicate = false;
            lock (_gate)
            {
                duplicate = Items.Any(existing =>
                    string.Equals(existing.Key, item.Key, StringComparison.Ordinal) ||
                    (existing.Kind == item.Kind
                     && !string.IsNullOrEmpty(track.Id)
                     && existing.Track.Id == track.Id));
                if (!duplicate) Items.Add(item);
            }
            if (duplicate) continue;

            if (isFailed)
            {
                failed++;
                continue;
            }

            _channel.Writer.TryWrite(item);
            requeued++;
        }

        if (requeued > 0) EnsureWorkers();

        // Normalise the file: a Downloading/Tagging item is Queued now.
        RequestSave();
        Log.Info($"restored {requeued} queued and {failed} failed download(s) from '{FilePath}'");
    }

    /// <summary>Coalesce a queue.json write; at most four per second.</summary>
    private void RequestSave()
    {
        lock (_saveGate)
        {
            if (_disposed) return;
            if (_savePending) return;

            _savePending = true;
            _saveTimer ??= new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveCoalesceMs, Timeout.Infinite);
        }
    }

    private void FlushSave()
    {
        lock (_saveGate) _savePending = false;
        PersistNow();
    }

    /// <summary>
    /// Write queue.json atomically ("queue.json.tmp" then move over the real
    /// file). Nothing but Done/Cancelled items left means the file is deleted, so
    /// a clean queue leaves no stale state behind. Failures are logged and
    /// swallowed - a lost queue only costs a re-queue, never a crash.
    /// </summary>
    private void PersistNow()
    {
        List<QueueRecord> records;
        lock (_gate)
        {
            records = Items
                .Where(i => i.State is not (DownloadState.Done or DownloadState.Cancelled))
                .Select(ToRecord)
                .ToList();
        }

        try
        {
            if (records.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(
                new QueueFile { SavedUtc = DateTime.UtcNow, Items = records }, JsonOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save the download queue to '{FilePath}'", ex);
        }
    }

    private static List<QueueRecord> LoadPersisted()
    {
        if (!File.Exists(FilePath)) return new List<QueueRecord>();

        var json = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(json)) return new List<QueueRecord>();

        return JsonSerializer.Deserialize<QueueFile>(json, JsonOptions)?.Items
               ?? new List<QueueRecord>();
    }

    /// <summary>
    /// Playlist provenance that queue.json still remembers: rows that were queued
    /// but not yet finished when the app last stopped. Exact by remote id - used
    /// once by <see cref="LibraryLedger"/> to name entries written before the
    /// ledger recorded provenance. Never throws: an unreadable queue yields
    /// nothing, because a missing hint is better than a wrong one.
    /// </summary>
    public static IReadOnlyDictionary<string, (string PlaylistName, MediaSource Source)> ReadPersistedProvenance()
    {
        var map = new Dictionary<string, (string, MediaSource)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var record in LoadPersisted())
            {
                var key = string.IsNullOrWhiteSpace(record.Id) ? record.Key : record.Id;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(record.PlaylistName)) continue;
                if (record.Kind != DownloadKind.Library) continue;

                // Only an imported source can name a playlist in the rail; a
                // Manual row's name is an app-owned list, where membership does
                // not live in the ledger.
                if (record.Source is not (MediaSource.Spotify or MediaSource.YouTube)) continue;

                map[key.Trim()] = (record.PlaylistName!.Trim(), record.Source);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read playlist provenance from queue.json: {ex.Message}");
        }

        return map;
    }

    private static QueueRecord ToRecord(DownloadItem item)
    {
        var track = item.Track;
        return new QueueRecord
        {
            Key = item.Key,
            Id = track.Id,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            Isrc = track.Isrc,
            DurationMs = track.DurationMs,
            VideoId = track.VideoId,
            CoverUrl = track.CoverUrl,
            Source = track.Source,
            State = item.State,
            TrackState = track.State,
            Kind = item.Kind,
            PlaylistName = track.PlaylistName,
            LayoutOverride = item.LayoutOverride,
            IndexInPlaylist = item.IndexInPlaylist,
            Attempts = item.Attempts,
            CandidateIds = item.CandidateIds.ToList(),
            CandidateIndex = item.CandidateIndex,
            OutputPath = item.OutputPath,
            Error = item.Error,
            Detail = item.Detail,
        };
    }

    // ---- workers -----------------------------------------------------------

    private void EnsureWorkers()
    {
        lock (_gate)
        {
            if (_disposed || _workers.Count > 0) return;
            var count = Math.Clamp(_settings.DownloadConcurrency, 1, 6);
            for (var i = 0; i < count; i++)
                _workers.Add(Task.Run(() => WorkerAsync(_shutdown.Token)));
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    if (ct.IsCancellationRequested) return;

                    while (_paused && !ct.IsCancellationRequested)
                        await Task.Delay(300, ct).ConfigureAwait(false);

                    if (item.State is DownloadState.Cancelled or DownloadState.Done) continue;
                    await ProcessAsync(item, ct).ConfigureAwait(false);
                    NotifyDrainedIfIdle();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            Log.Error("Download worker crashed", ex);
        }
    }

    private void NotifyDrainedIfIdle()
    {
        TaskCompletionSource? signal = null;
        lock (_gate)
        {
            if (!IsRunning && _drainSignal != null)
            {
                signal = _drainSignal;
                _drainSignal = null;
            }
        }
        signal?.TrySetResult();
        if (signal != null)
        {
            PersistNow();
            Drained?.Invoke();
        }
    }

    // ---- one item ----------------------------------------------------------

    private async Task ProcessAsync(DownloadItem item, CancellationToken ct)
    {
        // Every worker owns its item's CTS, registered under the gate so CancelAll
        // either sees it or has already marked the item Cancelled.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _liveItemCts.Add(cts);
            if (item.State == DownloadState.Cancelled) cts.Cancel();
        }

        var token = cts.Token;

        var track = item.Track;
        try
        {
            await EnsureToolsAsync(token).ConfigureAwait(false);

            item.Attempts++;
            item.Error = null;
            item.State = DownloadState.Searching;
            item.Detail = null;
            track.Error = null;
            track.Progress = 0;
            RequestSave();

            // 1. Already in the library? Skip the network entirely.
            if (item.Kind == DownloadKind.Library && _settings.DownloadSkipExisting)
            {
                var existing = ResolveInLibrary(track);
                if (!string.IsNullOrEmpty(existing))
                {
                    MarkInLibrary(item, existing);
                    // Done items are not persisted, so this save is what removes
                    // the item's stale "Searching" row from queue.json.
                    RequestSave();
                    Finish(item);
                    return;
                }
            }

            // 2. Build the candidate list on first sight of this item; later
            //    attempts reuse it so a retry never repeats the search.
            if (item.CandidateIds.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(track.VideoId))
                {
                    item.CandidateIds.Add(track.VideoId!);
                }
                else
                {
                    var results = await YouTubeApi.SearchAsync(track.SearchQuery, 6, _settings, token)
                        .ConfigureAwait(false);
                    var ranked = results
                        .Select(r => (Entry: r, Score: SourceMatcher.ScoreCandidate(
                            track, r.Title, r.Uploader, r.Duration)))
                        .OrderByDescending(x => x.Score.Score)
                        .ToList();

                    if (ranked.Count == 0 || ranked[0].Score.Score < MinConfidentScore)
                    {
                        throw new InvalidOperationException(
                            $"No confident YouTube match for \"{track.SearchQuery}\"" +
                            (ranked.Count > 0 ? $" (best: {ranked[0].Entry.Title}, {ranked[0].Score.Score:0.00})" : ""));
                    }

                    // The best match must clear the confidence bar, but the rest
                    // of the shortlist rides along as fallbacks: uploads vanish,
                    // get region-locked or turn members-only all the time, and a
                    // rough match still beats a failed download.
                    foreach (var candidate in ranked.Take(MaxCandidates))
                    {
                        item.CandidateIds.Add(candidate.Entry.VideoId);
                        Log.Info($"candidate {candidate.Score.Score:0.00} {candidate.Entry.VideoId} " +
                                 $"[{candidate.Entry.Uploader}] {candidate.Entry.Title}");
                    }

                    item.Detail = $"{ranked[0].Entry.Title} \u00b7 {ranked[0].Entry.Uploader}";
                    Log.Info($"matched \"{track.DisplayName}\" -> {ranked[0].Entry.VideoId} ({ranked[0].Score.Score:0.00})");
                }
            }

            // 3./4. Download, walking to the next candidate when a video turns
            //        out to be unusable (removed, private, region-locked, bot-walled).
            if (item.Kind == DownloadKind.Library) CleanYouTubeMetadata(track);

            var mp3 = _settings.DownloadBitrateKbps > 0 && ToolBootstrap.HasFfmpeg(_settings);
            var produced = "";
            while (true)
            {
                item.CandidateIndex = Math.Clamp(item.CandidateIndex, 0, Math.Max(0, item.CandidateIds.Count - 1));
                var videoId = item.CandidateIds[item.CandidateIndex];
                track.VideoId = videoId;

                var targetBase = ResolveTargetBase(item, track, videoId);

                item.State = DownloadState.Downloading;
                track.State = SourceTrackState.Downloading;
                RequestSave();

                var lastPush = 0L;
                var progress = new Progress<YtProgress>(p =>
                {
                    item.Percent = p.Percent;
                    item.Speed = p.Speed;
                    item.Eta = p.Eta;

                    // ~4 Hz is plenty for a table cell; every yt-dlp line would
                    // flood the UI thread and the property-changed plumbing.
                    var now = Environment.TickCount64;
                    var previous = Interlocked.Read(ref lastPush);
                    if (now - previous < ProgressPushIntervalMs) return;
                    if (Interlocked.CompareExchange(ref lastPush, now, previous) != previous) return;

                    // SourceTrack.Progress is 0-100 (see its doc comment), same
                    // scale as item.Percent.
                    track.Progress = p.Percent;
                });

                try
                {
                    produced = await YtDlp.DownloadBestAudioAsync(
                        videoId, targetBase, mp3, _settings.DownloadBitrateKbps, _settings, progress, token)
                        .ConfigureAwait(false);
                    break;
                }
                catch (YtDlpException ex) when (IsUnusableVideo(ex) &&
                                                item.CandidateIndex + 1 < item.CandidateIds.Count)
                {
                    item.CandidateIndex++;
                    item.Percent = 0;
                    item.Speed = "";
                    item.Eta = "";
                    item.Detail = $"alternate match {item.CandidateIndex + 1}/{item.CandidateIds.Count}";
                    track.Progress = 0;
                    Log.Warn($"video {videoId} unusable ({LastStderrLine(ex.Stderr)}); trying " +
                             $"{item.CandidateIds[item.CandidateIndex]}");
                }
            }

            if (!File.Exists(produced))
                throw new IOException($"yt-dlp reported success but {produced} is missing");

            // 5. Tag + ledger for library items.
            if (item.Kind == DownloadKind.Library)
            {
                item.State = DownloadState.Tagging;
                RequestSave();
                var cover = await DownloadTagger.FetchCoverAsync(track.CoverUrl, token).ConfigureAwait(false);
                await DownloadTagger.ApplyAsync(produced, track, cover, token).ConfigureAwait(false);

                // Provenance travels with the file: "this download came from
                // playlist X" survives the queue row being forgotten on completion.
                _ledger.Put(track.Id, produced, track.Title, track.Artist,
                    string.IsNullOrWhiteSpace(track.PlaylistName) ? null : track.PlaylistName,
                    track.Source is MediaSource.Spotify or MediaSource.YouTube ? track.Source : null);
                track.State = SourceTrackState.InLibrary;
                track.LibraryPath = produced;
                LibraryChanged?.Invoke();
                Log.Info($"downloaded {track.DisplayName} -> {produced}");
            }
            else
            {
                // A preview is NOT a library download: the file lives in the
                // evictable stream cache, so it gets PreviewPath ("▶") and never
                // LibraryPath ("✓"). The row also returns to the state it had
                // before the queue touched it - leaving it at Downloading is
                // exactly what produced the permanent "⋯ 0%" row.
                track.PreviewPath = produced;
                track.State = item.PriorState;
                _cache.Evict();
            }

            item.OutputPath = produced;
            item.Percent = 100;
            item.State = DownloadState.Done;
            track.Progress = 0;
            track.Error = null;
            RequestSave();
            Finish(item);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            item.State = DownloadState.Cancelled;
            item.Error = null;
            item.Percent = 0;
            // Restore the pre-download state (New, or Failed on a retry) instead
            // of leaving the row spinning at Downloading.
            track.State = item.PriorState;
            track.Progress = 0;
            track.Error = null;
            item.Completion?.TrySetResult(null);
            RequestSave();
            ItemFinished?.Invoke(item);
        }
        catch (Exception ex)
        {
            if (ex is YtDlpException yt) Log.Error($"yt-dlp stderr for {track.DisplayName}:\n{yt.Stderr}");
            Log.Error($"download failed for {track.DisplayName}", ex);

            if (ShouldRetry(item, ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, item.Attempts));
                var reason = DescribeFailure(ex);
                item.State = DownloadState.Queued;
                track.State = SourceTrackState.Queued;
                track.Progress = 0;
                item.Detail = $"retrying in {delay.TotalSeconds:0}s ({reason})";
                RequestSave();
                Log.Warn($"retry {item.Attempts}/{MaxAttempts} for {track.DisplayName}");

                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled while backing off: the item never reaches a worker again.
                    item.State = DownloadState.Cancelled;
                    item.Detail = null;
                    item.Percent = 0;
                    track.State = item.PriorState;
                    track.Progress = 0;
                    item.Completion?.TrySetResult(null);
                    RequestSave();
                    ItemFinished?.Invoke(item);
                    return;
                }

                _channel.Writer.TryWrite(item);
                return;
            }

            item.State = DownloadState.Failed;
            item.Error = DescribeFailure(ex);
            item.Detail = null;
            // Show the failure, but never overwrite a tick we still have: a track
            // that was already in the library keeps it, and in no case may the
            // row stay at Downloading.
            track.State = item.PriorState == SourceTrackState.InLibrary
                ? SourceTrackState.InLibrary
                : SourceTrackState.Failed;
            track.Progress = 0;
            track.Error = item.Error;
            item.Completion?.TrySetResult(null);
            RequestSave();
            ItemFinished?.Invoke(item);
        }
        finally
        {
            lock (_gate) _liveItemCts.Remove(cts);
        }
    }

    /// <summary>
    /// Fetch yt-dlp/ffmpeg into %APPDATA%\JockeyBox\tools on first use, so a
    /// fresh install can download without the user finding tools themselves.
    /// </summary>
    private async Task EnsureToolsAsync(CancellationToken ct)
    {
        if (_toolsReady) return;

        await _toolGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_toolsReady) return;

            var log = new Progress<string>(message => Log.Info($"tools: {message}"));
            if (!await ToolBootstrap.EnsureYtDlpAsync(_settings, log, ct).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "yt-dlp is not available. Open the download queue and press \"Install tools\".");

            // ffmpeg is optional (downloads fall back to m4a) so a failure here
            // is logged and ignored.
            await ToolBootstrap.EnsureFfmpegAsync(_settings, log, ct).ConfigureAwait(false);
            _toolsReady = true;
        }
        finally
        {
            _toolGate.Release();
        }
    }

    /// <summary>The file stem yt-dlp should write to, per download kind.</summary>
    private string ResolveTargetBase(DownloadItem item, SourceTrack track, string videoId)
    {
        string absolute;
        if (item.Kind == DownloadKind.Library)
        {
            var ctx = new DownloadPaths.LayoutContext(
                track, track.PlaylistName, item.IndexInPlaylist, item.LayoutOverride ?? "");
            var relative = DownloadPaths.BuildRelativePath(ctx, _settings);
            absolute = DownloadPaths.EnsureUnique(DownloadPaths.Absolute(_settings, relative));
        }
        else
        {
            // Cache files are named after the YouTube id, so a second play of
            // the same resolved video is a guaranteed hit.
            absolute = _cache.PathFor(videoId);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        // Strip the media extension only. Path.GetFileNameWithoutExtension would
        // cut at the last dot anywhere in the name, so a title containing "ft."
        // lost everything after it.
        var fileName = Path.GetFileName(absolute);
        var stem = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(Path.GetDirectoryName(absolute)!, stem);
    }

    /// <summary>
    /// Replace the clickbait video title with a clean song title before it is
    /// written to disk and tagged. Spotify metadata is already clean, so only
    /// YouTube items are touched.
    /// </summary>
    private static void CleanYouTubeMetadata(SourceTrack track)
    {
        if (track.Source != MediaSource.YouTube) return;

        var (title, artist) = MediaTitle.Clean(track.Title, track.Artist);
        if (title.Length > 0) track.Title = title;
        if (string.IsNullOrWhiteSpace(track.Artist) && artist.Length > 0) track.Artist = artist;

        Log.Debug($"title cleaned: \"{title}\" by \"{track.Artist}\"");
    }

    /// <summary>
    /// Path of an existing library copy for <paramref name="track"/>: the scanned
    /// library index first, the download ledger as a second fallback.
    /// </summary>
    private string? ResolveInLibrary(SourceTrack track)
    {
        try
        {
            var path = _library.Resolve(track);
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (Exception ex)
        {
            Log.Error($"Library lookup failed for {track.DisplayName}", ex);
        }

        return _ledger.FindPathByLibraryKey(track.Artist, track.Title);
    }

    /// <summary>Mark an item as satisfied by an existing library file.</summary>
    private static void MarkInLibrary(DownloadItem item, string path)
    {
        item.State = DownloadState.Done;
        item.OutputPath = path;
        item.Detail = "already in library";
        item.Error = null;
        item.Percent = 100;
        item.Track.State = SourceTrackState.InLibrary;
        item.Track.LibraryPath = path;
        item.Track.Progress = 0;
        item.Track.Error = null;
    }

    /// <summary>
    /// True when yt-dlp failed for a reason tied to that specific video rather
    /// than to the network or the tool, i.e. another match may still work.
    /// </summary>
    private static bool IsUnusableVideo(YtDlpException ex)
    {
        var text = ex.Stderr ?? "";
        string[] markers =
        {
            "not available", "unavailable", "Private video", "members-only",
            "Video unavailable", "has been removed", "removed by the uploader",
            "Sign in to confirm", "age-restricted", "age restricted",
            "This video is not available", "confirm your age",
            "Video is not playable", "does not exist", "copyright",
        };
        return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The last non-empty stderr line, trimmed to something a table cell can
    /// show. yt-dlp puts the actual reason ("ERROR: Sign in to confirm ...") on
    /// the last line; the rest is usually warnings and progress noise.
    /// </summary>
    private static string LastStderrLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            return line.Length <= StderrLineMax ? line : line[..StderrLineMax] + "\u2026";
        }
        return "";
    }

    /// <summary>
    /// What the user should see in <see cref="DownloadItem.Error"/>. For yt-dlp
    /// that includes its own last stderr line, because "exit 1" alone never
    /// explains a failure.
    /// </summary>
    private static string DescribeFailure(Exception ex)
    {
        if (ex is YtDlpException yt)
        {
            var line = LastStderrLine(yt.Stderr);
            return Trim(line.Length == 0
                ? $"yt-dlp exit {yt.ExitCode}: {yt.Message}"
                : $"yt-dlp exit {yt.ExitCode}: {line}");
        }

        return Trim(ex.Message);
    }

    private void Finish(DownloadItem item)
    {
        item.Completion?.TrySetResult(item.OutputPath);
        ItemFinished?.Invoke(item);
    }

    private static bool ShouldRetry(DownloadItem item, Exception ex)
    {
        if (item.Attempts >= MaxAttempts) return false;
        // A missing search match never improves on retry; network/tool failures do.
        if (ex is InvalidOperationException) return false;
        if (ex is YtDlpException yt && (yt.Stderr ?? "").Contains("Video unavailable", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string Trim(string message) =>
        message.Length <= 400 ? message : message[..400] + "\u2026";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Shutdown is the one save that must not be lost to the coalescing timer.
        PersistNow();

        try
        {
            _channel.Writer.TryComplete();
            _shutdown.Cancel();
        }
        catch { /* shutting down */ }

        try
        {
            lock (_saveGate)
            {
                _savePending = false;
                _saveTimer?.Dispose();
                _saveTimer = null;
            }
        }
        catch { /* shutting down */ }
    }

    // ---- queue.json shape --------------------------------------------------

    /// <summary>Root of queue.json: a version stamp so a future shape can be detected.</summary>
    private sealed class QueueFile
    {
        public int Version { get; set; } = 1;
        public DateTime SavedUtc { get; set; }
        public List<QueueRecord> Items { get; set; } = new();
    }

    /// <summary>
    /// One persisted queue row: everything needed to rebuild the
    /// <see cref="SourceTrack"/> and resume the download, but nothing that only
    /// matters inside a running process (completion sources, timestamps).
    /// Done/Cancelled items are never written.
    /// </summary>
    private sealed class QueueRecord
    {
        public string Key { get; set; } = "";
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Isrc { get; set; } = "";
        public double DurationMs { get; set; }
        public string? VideoId { get; set; }
        public string CoverUrl { get; set; } = "";
        public MediaSource Source { get; set; }
        public DownloadState State { get; set; }
        public SourceTrackState TrackState { get; set; }
        public DownloadKind Kind { get; set; }
        public string? PlaylistName { get; set; }
        public string? LayoutOverride { get; set; }
        public int IndexInPlaylist { get; set; }
        public int Attempts { get; set; }
        public List<string> CandidateIds { get; set; } = new();
        public int CandidateIndex { get; set; }
        public string? OutputPath { get; set; }
        public string? Error { get; set; }
        public string? Detail { get; set; }
    }
}
