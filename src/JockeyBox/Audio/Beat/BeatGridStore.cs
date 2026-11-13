using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Beat;

/// <summary>
/// The analysed beat grids, persisted as one JSON document and mirrored into the
/// <c>tracks</c> table when the library database is live.
///
/// <para><b>Why JSON first.</b> The database is optional (see
/// <see cref="Data.LibraryDatabase"/>: it can be disabled, or fail to open, and
/// the app then reads the JSON stores). A grid is expensive — seconds of Python
/// per track — so it must not live only in a store that may be off. The JSON file
/// is the durable copy; the SQLite columns are a mirror for queries
/// (<c>bpm</c>, <c>bpm_p10</c>, <c>bpm_p90</c>, <c>bpm_confidence</c>,
/// <c>first_beat_ms</c>) that the browser and the mix planner can read with one
/// SELECT.</para>
///
/// <para><b>Atomic and crash-tolerant.</b> Every write goes to
/// <c>beat-grids.json.tmp</c>, is flushed to the platter, then renamed over the
/// live file — a crash leaves either the previous document or the new one, never a
/// half-written grid map. A corrupt file is moved aside (kept for inspection) and
/// the store starts empty instead of failing the app.</para>
///
/// <para><b>Cheap writes, cheap reads.</b> <see cref="Get"/> and
/// <see cref="Has"/> are dictionary lookups under one short lock. Saves mark the
/// map dirty and write at most once a second from a background loop, so a 771-track
/// pass does not rewrite the whole document 771 times.</para>
/// </summary>
public sealed class BeatGridStore : IBeatGridStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly object _gate = new();

    /// <summary>Serialises the file write: Save() and the flusher can overlap.</summary>
    private readonly object _writeGate = new();
    private readonly Dictionary<string, Entry> _grids =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly Func<BeatGrid, string?, bool>? _mirror;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flusher;

    /// <summary>
    /// Paths whose file carries an explicit key tag, as observed by the last
    /// library scan. In-memory only: the scan always runs before the background
    /// analysis pass, so this hint is present when the queue is built. It exists
    /// so the key backfill never re-analyses a track whose key the tag already
    /// states. Case-insensitive like the grid dictionary.
    /// </summary>
    private readonly HashSet<string> _tagKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The store the shipped app built, so the library scan can read estimated
    /// keys without threading a new dependency through the view model.
    /// Assigned in the constructor (the scan never runs before it) and cleared
    /// on dispose, so a test that builds its own store is unaffected once that
    /// store is gone.
    /// </summary>
    public static BeatGridStore? Shared { get; private set; }

    private bool _dirty;
    private long _lastWriteMs;
    private int _disposed;

    /// <summary>
    /// Set when a corrupt document could not be moved aside: the bytes on disk are
    /// then the only copy of every grid in them, so no save may overwrite them.
    /// </summary>
    private bool _writeBlocked;

    /// <summary>Minimum spacing between background writes.</summary>
    private const int WriteIntervalMs = 1000;

    /// <param name="path">Document path; defaults to <c>%APPDATA%\JockeyBox\beat-grids.json</c>.</param>
    /// <param name="mirror">Optional database mirror; called with each saved grid.</param>
    public BeatGridStore(string? path = null, Func<BeatGrid, string?, bool>? mirror = null)
    {
        _path = path ?? DefaultPath;
        _mirror = mirror ?? DatabaseMirror;
        Load();
        _flusher = Task.Run(FlushLoopAsync);
        Shared = this;   // last store wins; the app builds exactly one
    }

    /// <summary>The document the shipped app uses.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.Home, "beat-grids.json");

    /// <summary>How many grids are cached.</summary>
    public int Count
    {
        get { lock (_gate) return _grids.Count; }
    }

    /// <summary>The document path (for logs and tests).</summary>
    public string FilePath => _path;

    /// <summary>Cached grid for a path, or null.</summary>
    public BeatGrid? Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = Key(path);
        lock (_gate)
            return _grids.TryGetValue(key, out var entry) ? entry.Grid : null;
    }

    /// <summary>Record that the file states its own key in a tag (scan-time hint).</summary>
    public void MarkTagKey(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var key = Key(path);
        lock (_gate) _tagKeys.Add(key);
    }

    /// <summary>
    /// True when this path needs a key pass: a grid exists but predates key
    /// estimation, and no tag supplies a key. Keeps the one-time backfill from
    /// re-analysing tracks that already have a key from either source.
    /// </summary>
    public bool NeedsKeyPass(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var key = Key(path);
        lock (_gate)
            return _grids.TryGetValue(key, out var entry)
                   && !entry.Grid.KeyAnalysed
                   && !_tagKeys.Contains(key);
    }

    /// <summary>True when the path has an analysed grid.</summary>
    public bool Has(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var key = Key(path);
        lock (_gate) return _grids.ContainsKey(key);
    }

    /// <summary>
    /// Every cached grid with its normalised path key, for passes that fill another
    /// store from analysis (the BPM backfill). The key is the same normalisation
    /// every consumer applies, so a caller does not need the original casing.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, BeatGrid>> Snapshot()
    {
        lock (_gate)
            return _grids
                .Select(kv => new KeyValuePair<string, BeatGrid>(kv.Key, kv.Value.Grid))
                .ToList();
    }

    /// <summary>Store a grid. Marks the document dirty; the flusher writes it.</summary>
    public void Save(string path, BeatGrid grid)
    {
        if (string.IsNullOrEmpty(path) || grid == null || grid.Bpm <= 0) return;

        var key = Key(path);
        var entry = new Entry(grid, DateTime.UtcNow);
        lock (_gate)
        {
            _grids[key] = entry;
            _dirty = true;
        }

        try { _mirror?.Invoke(grid, path); }
        catch (Exception ex) { Log.Warn($"beat grid db mirror failed: {ex.Message}"); }

        // First save after a quiet moment writes immediately, so a single deck
        // load survives a kill within the second; a burst rides the flusher.
        if (Environment.TickCount64 - Interlocked.Read(ref _lastWriteMs) >= WriteIntervalMs)
            Write();
    }

    /// <summary>Write now if anything is pending (called on shutdown and by tests).</summary>
    public void Flush()
    {
        bool dirty;
        lock (_gate) dirty = _dirty;
        if (dirty) Write();
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(400, _cts.Token).ConfigureAwait(false);
                bool dirty;
                lock (_gate) dirty = _dirty;
                if (!dirty) continue;
                if (Environment.TickCount64 - Interlocked.Read(ref _lastWriteMs) < WriteIntervalMs) continue;
                Write();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("beat grid store: writer loop failed", ex); }
    }

    /// <summary>Serialise the map to the temp file and rename it over the live one.</summary>
    private void Write()
    {
        lock (_writeGate)
        {
            Dictionary<string, EntryDto> snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                if (_writeBlocked) return;
                Interlocked.Exchange(ref _lastWriteMs, Environment.TickCount64);
                snapshot = new Dictionary<string, EntryDto>(_grids.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, entry) in _grids) snapshot[key] = EntryDto.From(entry);
            }

            var temp = _path + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                // Same directory, same volume: MoveFileEx(REPLACE_EXISTING) is
                // atomic, so a crash keeps either the old document or the new one.
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                lock (_gate) _dirty = true;
                Log.Warn($"beat grid store: write failed ({ex.Message}); keeping the in-memory map");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    private void Load()
    {
        var temp = _path + ".tmp";
        try
        {
            // A leftover temp file is a write that never landed; the live document
            // is the truth.
            if (File.Exists(temp)) File.Delete(temp);

            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(json, JsonOptions);
            if (loaded == null) return;

            foreach (var (key, dto) in loaded)
            {
                if (dto?.ToGrid() is { } grid) _grids[Key(key)] = new Entry(grid, dto.AnalysedUtc);
            }
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"beat grid store: could not read {Path.GetFileName(_path)}: {ex.Message}");
        }
    }

    /// <summary>Move a corrupt document aside so the next run starts clean.</summary>
    private void Quarantine(string reason)
    {
        var target = _path + ".corrupt";
        try
        {
            File.Move(_path, target, overwrite: true);
            Log.Warn($"beat grid store: {_path} was corrupt ({reason}); starting empty, " +
                     $"the document is kept as {target} and its tracks will be re-analysed");
        }
        catch (Exception ex)
        {
            // Nothing preserved the bytes, so the next save would write an empty
            // document over the only copy of every grid in it. Keep reading, refuse
            // to write, and say so: this is the case that used to destroy a pass.
            lock (_gate) { _writeBlocked = true; _dirty = false; }
            Log.Warn($"beat grid store: {_path} was corrupt ({reason}) and could not be moved aside " +
                     $"({ex.Message}); starting empty and refusing to write, so the corrupt document is not overwritten");
        }
    }

    /// <summary>
    /// Mirror one grid into the library database. Best effort: the DB may be off,
    /// the row may not exist yet (first scan), or the file may not be in the
    /// library at all.
    /// </summary>
    private static bool DatabaseMirror(BeatGrid grid, string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var tracks = Data.LibraryDatabase.Tracks;
        if (tracks == null) return false;
        return tracks.UpdateBeatGrid(path, grid.Bpm, grid.BpmP10, grid.BpmP90,
            grid.Confidence, (long)Math.Round(grid.FirstBeatSeconds * 1000.0));
    }

    /// <summary>Normalised dictionary key: the filesystem's own comparison rules.</summary>
    private static string Key(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        }
        catch
        {
            return path;
        }
    }

    private sealed record Entry(BeatGrid Grid, DateTime AnalysedUtc);

    /// <summary>The wire shape: beats in milliseconds keep the document compact.</summary>
    private sealed class EntryDto
    {
        public double Bpm { get; set; }
        public double FirstBeatMs { get; set; }
        public int[]? BeatsMs { get; set; }
        public double BpmP10 { get; set; }
        public double BpmP90 { get; set; }
        public double Confidence { get; set; }

        /// <summary>Estimated Camelot key ("8A"), or null. Never a tag value.</summary>
        public string? Key { get; set; }

        /// <summary>Estimator confidence 0..1.</summary>
        public double KeyConfidence { get; set; }

        /// <summary>True once the key pass has run (even when it found no key).</summary>
        public bool KeyAnalysed { get; set; }

        /// <summary>
        /// The estimator's reason when it reported no key. Optional on the wire:
        /// entries written before this field existed have none and still load.
        /// </summary>
        public string? KeyReason { get; set; }

        public DateTime AnalysedUtc { get; set; }

        public static EntryDto From(Entry entry) => new()
        {
            Bpm = entry.Grid.Bpm,
            FirstBeatMs = entry.Grid.FirstBeatSeconds * 1000.0,
            BeatsMs = entry.Grid.Beats is { Count: > 0 }
                ? entry.Grid.Beats.Select(static b => (int)Math.Round(b * 1000.0)).ToArray()
                : null,
            BpmP10 = entry.Grid.BpmP10,
            BpmP90 = entry.Grid.BpmP90,
            Confidence = entry.Grid.Confidence,
            Key = entry.Grid.Key,
            KeyConfidence = entry.Grid.KeyConfidence,
            KeyAnalysed = entry.Grid.KeyAnalysed,
            KeyReason = entry.Grid.KeyReason,
            AnalysedUtc = entry.AnalysedUtc,
        };

        public BeatGrid? ToGrid()
        {
            if (Bpm <= 0) return null;
            var beats = BeatsMs is { Length: > 0 }
                ? BeatsMs.Select(static b => b / 1000.0).ToArray()
                : null;
            return new BeatGrid(Bpm, FirstBeatMs / 1000.0, beats, BpmP10, BpmP90, Confidence,
                Key, KeyConfidence, KeyAnalysed, KeyReason);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (ReferenceEquals(Shared, this)) Shared = null;
        try { _cts.Cancel(); } catch { }
        try { _flusher.Wait(TimeSpan.FromSeconds(1)); } catch { }
        Flush();
        try { _cts.Dispose(); } catch { }
    }
}
