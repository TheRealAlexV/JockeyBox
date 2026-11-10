using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JockeyBox.Activity;
using JockeyBox.Audio.Transition;
using JockeyBox.Downloads;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Beat;

/// <summary>Persistence for analysed beat grids (backed by the track database).</summary>
public interface IBeatGridStore
{
    /// <summary>Cached grid for a path, or null if never analysed.</summary>
    BeatGrid? Get(string path);

    /// <summary>Store a grid (idempotent by path).</summary>
    void Save(string path, BeatGrid grid);

    /// <summary>Paths already analysed, for queue de-duplication.</summary>
    bool Has(string path);

    /// <summary>True when a cached grid predates key estimation and the file has no key tag.</summary>
    bool NeedsKeyPass(string path);

    /// <summary>Remember that a file states its own key in a tag (scan-time hint).</summary>
    void MarkTagKey(string path);

    /// <summary>How many grids are cached (persisted, not just this run).</summary>
    int Count { get; }
}

/// <summary>
/// Offline BPM / beat-grid analysis (plan item 11).
///
/// <para><b>Runs the analyser the app already ships with.</b> Beat tracking needs
/// librosa; a .NET port would be a research project, and the app already builds a
/// Python venv for stem separation whose dependencies include librosa 0.11.
/// Measured on real library files: ~2 s per track warm, ~20 s for the very first
/// track (JIT + model import), so one long-lived worker processes a queue of jobs
/// instead of paying start-up per file.</para>
///
/// <para><b>Never blocks playback.</b> Analysis is a background lane with one
/// worker, lowest priority, and a job is skipped when the track is already cached.
/// A deck can start on a track whose grid is unknown — the transition then falls
/// back to a plain crossfade, which is the correct degradation.</para>
///
/// <para><b>Two queues.</b> A track the user just loaded is analysed next
/// (<see cref="Enqueue"/>); the first-run library pass (<see cref="EnqueueAll"/>)
/// fills the gaps behind it. One FIFO would put a freshly loaded deck behind 700
/// library tracks, which is exactly when the readout must not wait.</para>
///
/// <para><b>Degrades loudly, once.</b> The interpreter is probed for
/// <c>numpy</c>+<c>librosa</c> before the first job; when neither the separation
/// venv nor a PATH python can import them this logs one
/// <c>beat analysis disabled: …</c> line and stops consuming the queue. The rest
/// of the app (playback, waveforms, mixes without beat data) is unaffected.</para>
/// </summary>
public sealed class BpmAnalysisService : IBeatGridProvider, IDisposable
{
    private readonly IBeatGridStore _store;
    private readonly ConcurrentQueue<string> _onDemand = new();
    private readonly Channel<string> _bulk = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly BpmQueuePolicy _policy = new();
    private readonly Func<bool> _isPlaying;
    private readonly Task _worker;
    private readonly string? _configuredPython;
    private readonly string _scriptPath;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _pythonPath;
    private string? _disabledReason;
    private int _probed;
    private int _analysed;
    private int _failed;
    private int _queued;
    private int _disposed;

    /// <param name="audioPlaying">
    /// Whether the deck pair is playing right now, asked at each track boundary.
    /// The library pass stays out of the way while this is true; a track a deck is
    /// waiting for is analysed anyway.
    /// </param>
    public BpmAnalysisService(IBeatGridStore store, string? pythonPath = null, string? scriptPath = null,
                              Func<bool>? audioPlaying = null)
    {
        _store = store;
        _isPlaying = audioPlaying ?? (() => false);
        _configuredPython = pythonPath;
        _scriptPath = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "AnalysisScripts", "analysis", "bpm_scan.py");
        _worker = Task.Run(WorkerLoopAsync);
    }

    /// <summary>Raised (on the worker thread) when a track has just been analysed.</summary>
    public event Action<string, BeatGrid>? Analysed;

    /// <summary>Tracks analysed since start (diagnostics).</summary>
    public int AnalysedCount => Volatile.Read(ref _analysed);

    /// <summary>Tracks that failed analysis (unreadable/unsupported).</summary>
    public int FailedCount => Volatile.Read(ref _failed);

    /// <summary>Grids known to the store, including the ones from earlier runs.</summary>
    public int CachedCount
    {
        get
        {
            try { return _store.Count; }
            catch { return 0; }
        }
    }

    /// <summary>Jobs waiting (deck loads first, then the library pass).</summary>
    public int QueuedCount => Math.Max(0, Volatile.Read(ref _queued));

    /// <summary>Why analysis cannot run on this machine, or null while it can.</summary>
    public string? DisabledReason => Volatile.Read(ref _disabledReason);

    /// <summary>True when the analyser process is alive.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The interpreter analysis actually runs on (null until probed).</summary>
    public string? PythonPath => _pythonPath;

    /// <summary>Cached grid for a path; never blocks on analysis.</summary>
    public BeatGrid? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return _store.Get(path); }
        catch (Exception ex)
        {
            Log.Warn($"beat grid lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Queue a track the user is waiting for (jumps ahead of the library pass).</summary>
    public void Enqueue(string? path) => Queue(path, priority: true);

    /// <summary>Queue a whole library (the first-run pass).</summary>
    public void EnqueueAll(IEnumerable<string> paths)
    {
        if (paths == null) return;
        int added = 0;
        foreach (var path in paths)
            if (Queue(path, priority: false)) added++;
        if (added > 0) Log.Info($"bpm: queued {added} library track(s) for analysis");
    }

    /// <summary>True when the job was accepted (false: cached, queued already, or missing).</summary>
    private bool Queue(string? path, bool priority)
    {
        if (string.IsNullOrEmpty(path) || Volatile.Read(ref _disposed) != 0) return false;
        if (DisabledReason != null) return false;
        try
        {
            if (!File.Exists(path)) return false;
            // A cached grid is final unless it predates key estimation and no
            // file tag supplies a key: that is the single backfill case.
            if (_store.Has(path) && !_store.NeedsKeyPass(path)) return false;
        }
        catch { return false; }

        if (!_pending.TryAdd(path, 0)) return false;

        Interlocked.Increment(ref _queued);
        if (priority) _onDemand.Enqueue(path);
        else _bulk.Writer.TryWrite(path);
        _signal.Release();
        return true;
    }

    /// <summary>
    /// Take the next track the worker may start: a deck's request always, the
    /// library pass only when <see cref="BpmQueuePolicy"/> says playback is not in
    /// the way. Both lanes are peeked rather than drained, so a held-back pass
    /// keeps its track queued for when the room is quiet again.
    /// </summary>
    private string? TakeNext()
    {
        bool deckWaiting = _onDemand.TryPeek(out var deckPath);
        bool libraryWaiting = _bulk.Reader.TryPeek(out var libraryPath);

        string? next = _policy.Take(
            _isPlaying(),
            deckWaiting ? deckPath : null,
            libraryWaiting ? libraryPath : null);
        if (next == null) return null;

        // Remove only what the policy chose; the other lane keeps its track.
        if (deckWaiting && next == deckPath) _onDemand.TryDequeue(out _);
        else _bulk.Reader.TryRead(out _);
        return next;
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? path = TakeNext();

                if (path == null)
                {
                    // 500 ms: a short wait keeps Dispose responsive without a
                    // dedicated waiter task per queue.
                    try { await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                await ProcessAsync(path).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("bpm worker crashed", ex); }
    }

    private async Task ProcessAsync(string path)
    {
        _pending.TryRemove(path, out _);
        Interlocked.Decrement(ref _queued);

        if (_cts.IsCancellationRequested || DisabledReason != null) return;

        try
        {
            if (_store.Has(path) && !_store.NeedsKeyPass(path)) return;

            var started = Stopwatch.StartNew();
            var grid = await AnalyseAsync(path).ConfigureAwait(false);
            if (grid == null)
            {
                Interlocked.Increment(ref _failed);
                Log.Warn($"bpm: no grid for {Path.GetFileName(path)} ({_failed} failed)");
                return;
            }

            _store.Save(path, grid);
            int done = Interlocked.Increment(ref _analysed);
            string keyNote = grid.Key != null
                ? $"key {grid.Key} ({grid.KeyConfidence:0.00})"
                : grid.KeyAnalysed ? "no key" : "key n/a";
            Log.Info($"bpm: analysed \"{Path.GetFileName(path)}\" {grid.Bpm:0.0} BPM, {keyNote} in " +
                     $"{started.ElapsedMilliseconds} ms (grid {grid.Beats.Count} beats, " +
                     $"{done} done, {QueuedCount} queued)");

            try { Analysed?.Invoke(path, grid); }
            catch (Exception ex) { Log.Error("bpm: analysed handler failed", ex); }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            Log.Warn($"bpm analysis failed for {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private async Task<BeatGrid?> AnalyseAsync(string path)
    {
        if (!EnsureAnalyser()) return null;

        string line = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), path });
        string? response = await SendAsync(line).ConfigureAwait(false);
        if (response == null) return null;

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
            Log.Warn($"bpm: {Path.GetFileName(path)}: {error}");
            return null;
        }

        double bpm = root.GetProperty("bpm").GetDouble();
        double? firstMs = root.TryGetProperty("firstBeatMs", out var f) && f.ValueKind == JsonValueKind.Number
            ? f.GetDouble() : null;
        double durationMs = root.TryGetProperty("durationMs", out var d) ? d.GetDouble() : 0;
        double p10 = root.TryGetProperty("bpmP10", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : 0;
        double p90 = root.TryGetProperty("bpmP90", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0;
        double confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDouble() : 0;

        string? key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()?.Trim()
            : null;
        double keyConfidence = root.TryGetProperty("keyConfidence", out var kc) && kc.ValueKind == JsonValueKind.Number
            ? kc.GetDouble() : 0;
        // The key pass ran for this response, whether or not it found a key
        // (a null key with a keyReason is a deliberate "no tonal centre").
        bool keyAnalysed = root.TryGetProperty("keyReason", out _) || key != null;

        List<int>? beats = null;
        if (root.TryGetProperty("beatsMs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            beats = new List<int>(arr.GetArrayLength());
            foreach (var b in arr.EnumerateArray()) beats.Add(b.GetInt32());
        }

        return BeatGrid.FromAnalysis(bpm, firstMs, beats, durationMs / 1000.0, p10, p90, confidence,
            key, keyConfidence, keyAnalysed);
    }

    private async Task<string?> SendAsync(string line)
    {
        if (_process == null || _stdin == null || _stdout == null) return null;

        await _stdin.WriteLineAsync(line).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);

        var read = _stdout.ReadLineAsync();
        var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(120), _cts.Token)).ConfigureAwait(false);
        if (done != read)
        {
            Log.Warn("bpm: analyser timed out");
            RestartProcess();
            return null;
        }
        return await read.ConfigureAwait(false);
    }

    /// <summary>
    /// Make sure the analyser process runs, probing the interpreter once. Returns
    /// false (and logs the one-time reason) when this machine cannot analyse.
    /// </summary>
    private bool EnsureAnalyser()
    {
        if (DisabledReason != null) return false;
        if (_process is { HasExited: false }) return true;
        return RestartProcess();
    }

    private bool RestartProcess()
    {
        KillProcess();

        if (!File.Exists(_scriptPath))
        {
            Disable($"analyser script missing at {_scriptPath}");
            return false;
        }

        var python = ResolvePython();
        if (python == null) return false;

        try
        {
            var psi = new ProcessStartInfo(python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(_scriptPath);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            // Keep the worker out of the way of playback and the tags scan.
            psi.Environment["OMP_NUM_THREADS"] = "2";

            var process = Process.Start(psi);
            if (process == null)
            {
                Log.Error("bpm: analyser process could not be started");
                return false;
            }

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn($"bpm[py]: {e.Data}");
            };
            process.BeginErrorReadLine();

            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;
            Log.Info($"bpm: analyser started ({python})");
            return true;
        }
        catch (Exception ex)
        {
            Disable($"analyser failed to start: {ex.Message}");
            return false;
        }
    }

    /// <summary>Log the one-time "off" line; further queue additions are dropped.</summary>
    private void Disable(string reason)
    {
        if (Interlocked.Exchange(ref _disabledReason, reason) != null) return;
        Log.Error($"beat analysis disabled: {reason}");

        // Beat data never fills in when this fires, and the columns just stay
        // blank forever, so say why once in the status console. This is the only
        // place the service disables and the exchange above runs once, so the row
        // cannot repeat per track or reappear on a later pass.
        //
        // Warning, not Failed: nothing was attempted and nothing errored. The
        // console shows this as a standing condition to fix, in amber.
        var row = ActivityFeed.Shared.Start(
            JockeyBox.Activity.ActivityKind.Scan, "beat-analysis-off", "Beat analysis off");
        ActivityFeed.Shared.Finish(
            row,
            DownloadState.Warning,
            "BPM and key columns stay blank: no python with numpy+librosa found. " +
            "Install the stem-separation engine or put a python with librosa on PATH.");
    }

    /// <summary>
    /// Find an interpreter that can actually import the analyser's dependencies.
    /// The separation venv is tried first (it is built with librosa), then
    /// whatever python is on PATH. Logs the resolved path, or the reason none works.
    /// </summary>
    private string? ResolvePython()
    {
        if (Interlocked.Exchange(ref _probed, 1) == 1) return _pythonPath;
        if (DisabledReason != null) return null;

        foreach (var candidate in PythonCandidates())
        {
            var resolved = Probe(candidate);
            if (resolved == null) continue;
            _pythonPath = resolved;
            Log.Info($"beat analysis: python {resolved}");
            return resolved;
        }

        Disable($"no python with numpy+librosa found (tried the separation venv and python on PATH); " +
                "install the stem-separation engine or put a python with librosa on PATH");
        return null;
    }

    /// <summary>Candidate command lines, most likely first.</summary>
    private IEnumerable<string[]> PythonCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_configuredPython)) yield return new[] { _configuredPython! };

        yield return new[] { Path.Combine(AppPaths.Home, "uvr-venv", "Scripts", "python.exe") };

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var ver in new[] { "312", "311", "313", "310" })
            yield return new[] { Path.Combine(localAppData, "Programs", "Python", $"Python{ver}", "python.exe") };
        foreach (var ver in new[] { "312", "311", "313", "310" })
            yield return new[] { Path.Combine(programFiles, $"Python{ver}", "python.exe") };

        yield return new[] { "python" };
        yield return new[] { "python3" };
        yield return new[] { "py", "-3" };
    }

    /// <summary>Run a one-line import in a candidate; returns its sys.executable or null.</summary>
    private string? Probe(string[] candidate)
    {
        var exe = candidate[0];
        if (Path.IsPathRooted(exe) && !File.Exists(exe)) return null;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            for (int i = 1; i < candidate.Length; i++) psi.ArgumentList.Add(candidate[i]);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys, numpy, librosa; print(sys.executable)");

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch { }
                Log.Warn($"beat analysis: python probe timed out for {exe}");
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode == 0 && output.Length > 0) return output;

            Log.Warn($"beat analysis: {exe} cannot import numpy/librosa" +
                     (stderr.Length > 0 ? $": {FirstLine(stderr.ToString())}" : ""));
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"beat analysis: python candidate {exe} unavailable ({ex.Message})");
            return null;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return line.Length > 0 ? line[^1].Trim() : text.Trim();
    }

    private void KillProcess()
    {
        try
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            if (_process is { HasExited: false }) _process.Kill(true);
            _process?.Dispose();
        }
        catch { }
        finally
        {
            _process = null;
            _stdin = null;
            _stdout = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        KillProcess();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _signal.Dispose(); } catch { }
    }
}
