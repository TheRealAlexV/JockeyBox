using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JockeyBox.Activity;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Metadata;
using JockeyBox.Tools;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace JockeyBox.Lyrics;

/// <summary>Request payload passed to <see cref="LyricAlignmentService.Enqueue"/>.</summary>
public sealed record LyricAlignmentRequest(
    TrackEntry Track,
    LyricsDocument UnalignedLyrics,
    TaskCompletionSource<LyricsDocument?>? Completion = null);

/// <summary>
/// Background worker that coordinates offline audio lyric alignment via Python sidecar (stable-ts).
/// Follows the same bounded Channel + background worker pattern as BpmAnalysisService.
/// </summary>
public sealed class LyricAlignmentService : IDisposable
{
    public const string AlignedSourceId = "aligned-stable-ts";
    public const string AlignedSourceName = "Aligned (stable-ts)";

    private readonly Channel<LyricAlignmentRequest> _queue;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly TrackMetadataStore _trackMeta;
    private readonly LyricsService _lyricsService;
    private readonly string _scriptPath;
    private readonly string? _configuredPython;

    private string? _pythonPath;
    private int _probed;

    public LyricAlignmentService(
        TrackMetadataStore trackMeta,
        LyricsService lyricsService,
        string? scriptPath = null,
        string? configuredPython = null,
        int queueCapacity = 64)
    {
        _trackMeta = trackMeta;
        _lyricsService = lyricsService;
        _configuredPython = configuredPython;
        // Under PublishSingleFile, AppContext.BaseDirectory is the extraction
        // directory, but AnalysisScripts stays loose beside the real exe, so it has
        // to be resolved from the process's own directory (PreviewStreamPlayer does
        // the same for libvlc).
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _scriptPath = scriptPath ?? Path.Combine(exeDir, "AnalysisScripts", "analysis", "lyric_align.py");

        _queue = Channel.CreateBounded<LyricAlignmentRequest>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public static string? AudioPathOf(TrackEntry? track)
    {
        if (track == null) return null;
        if (!string.IsNullOrWhiteSpace(track.MusicPath)) return track.MusicPath;
        if (!string.IsNullOrWhiteSpace(track.KaraokePath)) return track.KaraokePath;
        return null;
    }

    /// <summary>Queue track for lyric alignment.</summary>
    public bool Enqueue(TrackEntry track, LyricsDocument unalignedLyrics, TaskCompletionSource<LyricsDocument?>? tcs = null)
    {
        var audioPath = AudioPathOf(track);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            tcs?.TrySetResult(null);
            return false;
        }

        var key = TrackMetadataStore.NormalizeKey(track.RelativeKey ?? audioPath);
        if (!_inFlight.TryAdd(key, 0))
        {
            // Already queued / running
            tcs?.TrySetResult(null);
            return false;
        }

        var req = new LyricAlignmentRequest(track, unalignedLyrics, tcs);
        if (!_queue.Writer.TryWrite(req))
        {
            _inFlight.TryRemove(key, out _);
            tcs?.TrySetResult(null);
            return false;
        }

        return true;
    }

    public async Task<LyricsDocument?> AlignAsync(TrackEntry track, LyricsDocument unalignedLyrics, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        if (!Enqueue(track, unalignedLyrics, tcs))
        {
            return null;
        }

        return await tcs.Task;
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            LyricAlignmentRequest req;
            try
            {
                req = await _queue.Reader.ReadAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var audioPath = AudioPathOf(req.Track);
            var key = TrackMetadataStore.NormalizeKey(req.Track.RelativeKey ?? audioPath ?? "");
            ActivityRow? activity = null;
            try
            {
                activity = ActivityFeed.Shared.Start(
                    ActivityKind.Scan,
                    $"align-{key}",
                    $"Align lyrics: {req.Track.Artist} - {req.Track.Title}");
                ActivityFeed.Shared.Update(activity, DownloadState.Converting, 10, "Aligning lyrics with stable-ts");

                var doc = await ExecuteAlignmentAsync(req.Track, req.UnalignedLyrics, _cts.Token);

                if (doc != null)
                {
                    // Cache to disk via LyricsService
                    _lyricsService.CacheDocument(req.Track, doc);

                    // Pin in TrackMetadataStore
                    _trackMeta.SetLyricsSource(key, AlignedSourceId);

                    ActivityFeed.Shared.Finish(activity, DownloadState.Done, "Aligned");
                    req.Completion?.TrySetResult(doc);
                }
                else
                {
                    ActivityFeed.Shared.Finish(activity, DownloadState.Failed, "Alignment failed", "No output or python error");
                    req.Completion?.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                if (activity != null)
                {
                    ActivityFeed.Shared.Finish(activity, DownloadState.Failed, ex.Message, ex.ToString());
                }
                req.Completion?.TrySetException(ex);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }
    }

    public async Task<LyricsDocument?> ExecuteAlignmentAsync(TrackEntry track, LyricsDocument unalignedLyrics, CancellationToken ct)
    {
        var python = ResolvePython();
        var audioPath = AudioPathOf(track);
        if (python == null || !File.Exists(_scriptPath) || string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "JockeyBox_LyricAlign_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputJsonPath = Path.Combine(tempDir, "input.json");
        var outputJsonPath = Path.Combine(tempDir, "output.json");

        try
        {
            var inputPayload = new
            {
                lines = unalignedLyrics.Lines.Select(l => new
                {
                    text = l.Text,
                    time_ms = (int)l.Time.TotalMilliseconds
                }).ToList()
            };

            await File.WriteAllTextAsync(inputJsonPath, JsonSerializer.Serialize(inputPayload), Encoding.UTF8, ct);

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{_scriptPath}\" \"{audioPath}\" \"{inputJsonPath}\" \"{outputJsonPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // The whisper model is ~150 MB and huggingface_hub would otherwise drop it
            // in the user profile; keep it under the app home so a sandboxed or
            // portable install carries its own model cache.
            var hfHome = Path.Combine(AppPaths.Home, "huggingface");
            psi.Environment["HF_HOME"] = hfHome;
            psi.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(hfHome, "hub");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), ct);
            var processTask = proc.WaitForExitAsync(ct);

            var completed = await Task.WhenAny(processTask, timeoutTask);
            if (completed == timeoutTask)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            if (!File.Exists(outputJsonPath))
            {
                return null;
            }

            var outputJson = await File.ReadAllTextAsync(outputJsonPath, Encoding.UTF8, ct);
            return ParseAlignmentOutput(outputJson);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static LyricsDocument? ParseAlignmentOutput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var statusElem) && statusElem.GetString() == "error")
            {
                return null;
            }

            if (!root.TryGetProperty("lines", out var linesElem) || linesElem.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var lines = new List<LyricLine>();
            foreach (var lineElem in linesElem.EnumerateArray())
            {
                var timeMs = lineElem.GetProperty("time_ms").GetInt32();
                var text = lineElem.GetProperty("text").GetString() ?? "";

                List<LyricWord>? words = null;
                if (lineElem.TryGetProperty("words", out var wordsElem) && wordsElem.ValueKind == JsonValueKind.Array)
                {
                    words = new List<LyricWord>();
                    foreach (var w in wordsElem.EnumerateArray())
                    {
                        var startMs = w.GetProperty("start_ms").GetInt32();
                        var endMs = w.GetProperty("end_ms").GetInt32();
                        var wText = w.GetProperty("text").GetString() ?? "";
                        words.Add(new LyricWord(TimeSpan.FromMilliseconds(startMs), TimeSpan.FromMilliseconds(endMs), wText, IsSynthetic: false));
                    }
                }

                lines.Add(new LyricLine(TimeSpan.FromMilliseconds(timeMs), text)
                {
                    Words = words != null && words.Count > 0 ? words : null,
                    IsSynthetic = false,
                });
            }

            return new LyricsDocument
            {
                Synced = true,
                Lines = lines,
                Source = AlignedSourceName,
                SourceId = AlignedSourceId,
            };
        }
        catch
        {
            return null;
        }
    }

    private string? ResolvePython()
    {
        if (Interlocked.Exchange(ref _probed, 1) == 1) return _pythonPath;

        foreach (var candidate in PythonCandidates())
        {
            var resolved = Probe(candidate);
            if (resolved == null) continue;
            _pythonPath = resolved;
            return resolved;
        }

        return null;
    }

    private IEnumerable<string[]> PythonCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_configuredPython)) yield return new[] { _configuredPython! };

        yield return new[] { Path.Combine(AppPaths.Home, "uvr-venv", "Scripts", "python.exe") };

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        yield return new[] { Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe") };
        yield return new[] { Path.Combine(localAppData, "Programs", "Python", "Python310", "python.exe") };
        yield return new[] { Path.Combine(programFiles, "Python311", "python.exe") };
        yield return new[] { Path.Combine(programFiles, "Python310", "python.exe") };

        yield return new[] { "python" };
        yield return new[] { "python3" };
    }

    private static string? Probe(string[] command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command[0],
                Arguments = "-c \"import sys; print(sys.executable)\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            for (var i = 1; i < command.Length; i++)
            {
                psi.ArgumentList.Add(command[i]);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var outText = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(3000) || proc.ExitCode != 0) return null;
            return string.IsNullOrEmpty(outText) ? command[0] : outText;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _cts.Dispose();
    }
}
