using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using JockeyBox.Library;
using JockeyBox.Settings;

namespace JockeyBox.Separation;

/// <summary>
/// Serial background queue for "Make Karaoke Track" jobs. One separation runs
/// at a time (they are CPU/GPU-saturating); progress is pushed onto the
/// owning TrackEntry so the browser row updates live, and survives library
/// rescans via RebindEntries.
/// </summary>
public class SeparationService : IDisposable
{
    private record Job(string Key, string MusicPath, string OutputPath)
    {
        public TrackEntry Entry { get; set; } = null!;
        public SeparationState State { get; set; } = SeparationState.Queued;
        public int Progress { get; set; }
        public string ModelOverride { get; init; } = "";
    }

    private readonly SeparationEngine _engine = new();
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    /// <summary>Footer status line, e.g. "Karaoke: Song - 42%". Raised on the UI thread.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised on the UI thread when a job finishes successfully.</summary>
    public event Action<TrackEntry, string>? Completed;

    public SeparationService(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public int PendingCount
    {
        get
        {
            lock (_lock) return _jobs.Values.Count(j =>
                j.State is SeparationState.Queued or SeparationState.Running);
        }
    }

    /// <summary>Queue vocal removal for a track. No-op if already queued/running.
    /// <paramref name="modelOverride"/> uses a specific model instead of the default.</summary>
    public bool Enqueue(TrackEntry track, string? modelOverride = null)
    {
        if (track.MusicPath == null || string.IsNullOrWhiteSpace(_settings.KaraokeDirectory))
            return false;

        var outputPath = Path.Combine(_settings.KaraokeDirectory,
            track.RelativeKey + "." + _settings.SeparationOutputFormat.ToLowerInvariant());

        Job job;
        lock (_lock)
        {
            if (_jobs.TryGetValue(track.RelativeKey, out var existing)
                && existing.State is SeparationState.Queued or SeparationState.Running)
                return false;

            job = new Job(track.RelativeKey, track.MusicPath, outputPath)
            {
                Entry = track,
                ModelOverride = modelOverride ?? "",
            };
            _jobs[track.RelativeKey] = job;
        }

        track.Separation = SeparationState.Queued;
        track.SeparationProgress = 0;
        return _queue.Writer.TryWrite(job);
    }

    /// <summary>
    /// After a library rescan replaced the TrackEntry instances, re-attach
    /// known job states to the new objects so queue status stays visible.
    /// </summary>
    public void RebindEntries(IEnumerable<TrackEntry> tracks)
    {
        lock (_lock)
        {
            foreach (var track in tracks)
            {
                if (!_jobs.TryGetValue(track.RelativeKey, out var job)) continue;
                job.Entry = track;
                track.Separation = job.State;
                track.SeparationProgress = job.Progress;
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        var ct = _shutdown.Token;
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(ct))
            {
                await ProcessJobAsync(job, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task ProcessJobAsync(Job job, CancellationToken ct)
    {
        UpdateJob(job, SeparationState.Running, 0);
        ReportStatus(job, "starting\u2026");

        var progress = new Progress<(int percent, string phase)>(p =>
        {
            // The engine reports from process-output threads; hop to the UI
            // dispatcher before touching the observable TrackEntry.
            _dispatcher.InvokeAsync(() =>
            {
                lock (_lock)
                {
                    if (job.State != SeparationState.Running) return;
                    job.Progress = p.percent;
                }
                job.Entry.SeparationProgress = p.percent;
                ReportStatus(job, p.phase == "downloading model"
                    ? $"downloading model\u2026 {p.percent}%"
                    : $"{p.percent}%");
            });
        });

        try
        {
            var model = job.ModelOverride.Length > 0 ? job.ModelOverride : _settings.SeparationModel;
            var request = new SeparationEngine.SeparationRequest(
                job.MusicPath, job.OutputPath,
                model, _settings.SeparationOutputFormat);

            var output = await _engine.SeparateAsync(request, progress, ct);

            await _dispatcher.InvokeAsync(() =>
            {
                lock (_lock) _jobs.Remove(job.Key);
                job.Entry.KaraokePath = output;
                job.Entry.Separation = SeparationState.None;
                job.Entry.SeparationProgress = 0;
                Completed?.Invoke(job.Entry, output);
                StatusChanged?.Invoke($"Karaoke created: {Path.GetFileName(output)}"
                    + (PendingCount > 0 ? $" \u00b7 {PendingCount} in queue" : ""));
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var message = Compact(ex.Message);
            UpdateJob(job, SeparationState.Failed, 0);
            await _dispatcher.InvokeAsync(() =>
                StatusChanged?.Invoke($"Karaoke failed for {Path.GetFileName(job.MusicPath)}: {message}"));
        }
    }

    private void UpdateJob(Job job, SeparationState state, int progressPct)
    {
        lock (_lock) { job.State = state; job.Progress = progressPct; }
        _dispatcher.InvokeAsync(() =>
        {
            job.Entry.Separation = state;
            job.Entry.SeparationProgress = progressPct;
        });
    }

    private void ReportStatus(Job job, string detail)
    {
        var title = string.IsNullOrWhiteSpace(job.Entry.Title)
            ? Path.GetFileNameWithoutExtension(job.MusicPath) : job.Entry.Title;
        var queued = PendingCount - 1;
        _dispatcher.InvokeAsync(() => StatusChanged?.Invoke(
            $"Making karaoke track: {title} \u2014 {detail}"
            + (queued > 0 ? $" \u00b7 {queued} more in queue" : "")));
    }

    /// <summary>Flatten a multi-line process error into one status-bar-sized line.</summary>
    private static string Compact(string s)
    {
        var flat = string.Join(" \u00b7 ",
            s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length > 240 ? flat[..240] : flat;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
