using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Tools;

namespace JockeyBox.Sources;

/// <summary>
/// Persists the user's playlists (Spotify imports, YouTube imports and
/// app-owned library lists).
///
/// <para><b>1.5.0 behaviour</b>: the whole graph went to
/// <c>%APPDATA%\JockeyBox\playlists.json</c> on every mutation, on the UI
/// thread (95 ms serialise + 20 ms write for 11 000 rows). The file is still the
/// compatibility mirror - external editors and a 1.5.0 rollback read it - but the
/// write is now debounced by <see cref="JsonMirrorDelayMs"/> and serialised on a
/// worker from a shallow snapshot, so a drag or a rename no longer pays it.
/// <see cref="Revision"/> still moves synchronously with the mutation (the
/// membership cache depends on that), and <see cref="FileStamp"/> follows the
/// file once the mirror lands. When <see cref="Data.LibraryDatabase"/> is
/// available the same graph is written through to SQLite (<c>playlists</c> +
/// <c>playlist_items</c>) and <em>that</em> is what <see cref="Load"/> prefers on
/// the next start.</para>
///
/// <para><b>Constructor cost</b>: load only. It never rewrites the database, so
/// constructing a store on the UI thread cannot block on a 5 000-row
/// transaction. Mutators write the small JSON file and queue the durable write
/// on <see cref="WriteQueue"/>; <see cref="Save"/> stays synchronous for tests
/// and for a clean shutdown.</para>
///
/// Track membership and metadata are stored; track *state* is not (see
/// <see cref="SourceTrack.State"/>) - it is re-derived from the library on load,
/// so a crashed download or a preview can never come back as a stuck row.
///
/// App-owned (<see cref="PlaylistKind.Internal"/>) membership is the library key
/// (<see cref="SourceTrack.LocalKey"/>), never a remote id; imported membership
/// stays the remote id.
/// </summary>
public class PlaylistStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string AppDataDir => Path.Combine(AppPaths.Home);

    public static string FilePath => Path.Combine(AppDataDir, "playlists.json");

    /// <summary>Copy of the pre-migration file, written once before the first migration save.</summary>
    public static string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Schema version written on every save. A file without a version (or with a
    /// lower one) is migrated on load: identities are normalised, the pre-migration
    /// copy is kept at <see cref="BackupPath"/>, then the new shape is written.
    /// </summary>
    private const int CurrentVersion = 2;

    private readonly object _lock = new();
    private readonly JockeyBoxDatabase? _database;
    private readonly PlaylistRepository? _repository;

    /// <summary>
    /// True only when the graph in memory was read from <see cref="_repository"/>'s
    /// database. <see cref="WriteThrough"/> may let the repository delete rows that
    /// are absent from that graph <em>only</em> in this state: a store running off
    /// <c>playlists.json</c> (the constructor's fallback, a fresh install) holds a
    /// graph that may lag the database, and delete-by-absence would then destroy
    /// playlists the user never touched.
    /// </summary>
    private readonly bool _graphFromDatabase;

    private WriteQueue? _writeQueue;
    private bool _disposed;
    private bool _refreshing;

    // Per-load counters behind LastLoadStats (see PlaylistLoadStats).
    private double _statAvailabilityMs;
    private int _statAvailabilityCalls;
    private double _statWorstRowMs;
    private int _jsonMirrorWrites;
    private string _statWorstRowId = "";

    /// <summary>
    /// Every store the process created, so shutdown can flush all of their write
    /// queues (see <see cref="FlushAll"/>) without the host tracking instances.
    /// Weak: a store that was disposed and dropped must not be kept alive.
    /// </summary>
    private static readonly List<WeakReference<PlaylistStore>> Live = new();

    public ObservableCollection<UserPlaylist> Playlists { get; } = new();

    /// <summary>Raised after a mutation that changed the on-disk content.</summary>
    public event Action? Changed;

    /// <summary>
    /// Incremented by every <see cref="Save"/>. Anything derived from the playlist
    /// file (see <see cref="PlaylistMembershipCache"/>) stamps itself with this so
    /// an add/remove/rename can never be served a pre-edit answer. In-session only;
    /// pair it with <see cref="FileStamp"/> for values that outlive the process.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    private int _revision;

    /// <summary>
    /// Durable identity of playlists.json: last-write ticks mixed with its length.
    /// Changes on any save, including one made by an earlier session, so a cache
    /// entry from the last run is not trusted after the file was edited.
    /// </summary>
    public long FileStamp { get; private set; }

    /// <summary>
    /// Where the graph currently in memory came from: <c>db</c> when it was read
    /// from SQLite, <c>json</c> when <c>playlists.json</c> had to be parsed. Used
    /// by the switch timing line, and by the host to warn when it is on the slow
    /// path.
    /// </summary>
    public string LoadSource { get; private set; } = "json";

    /// <summary>Absolute path of the compatibility file, for the perf probe.</summary>
    public string PerfFilePath => FilePath;

    /// <summary>Bytes + milliseconds of the last playlists.json write, for the perf probe.</summary>
    public string? LastSaveStatsForPerf { get; private set; }

    /// <summary>
    /// How many playlists.json writes the mirror has performed, for the perf
    /// probe. A drag burst should show one, not one per move.
    /// </summary>
    public int JsonMirrorWritesForPerf => Volatile.Read(ref _jsonMirrorWrites);

    /// <summary>
    /// Optional host hook: given a track id, return the library file that exists
    /// for it (or null). <c>MainViewModel</c> points this at the live download
    /// ledger after it constructs one; without it the store falls back to a
    /// read-only snapshot of ledger.json.
    /// </summary>
    public Func<string, string?>? LibraryPathResolver { get; set; }

    /// <summary>
    /// Timing of the last graph read (<see cref="LoadFromDatabase"/> or
    /// <see cref="Load"/>). Exposed for the <c>--perf</c> probe and for the
    /// startup log line; it never changes behaviour.
    /// </summary>
    public PlaylistLoadStats? LastLoadStats { get; private set; }

    public PlaylistStore()
        : this(LibraryDatabase.Current)
    {
    }

    /// <summary>
    /// Test/wiring constructor. Passing null (or a database that has no graph
    /// yet) keeps the JSON-only behaviour of 1.5.0.
    /// </summary>
    public PlaylistStore(JockeyBoxDatabase? database)
    {
        _database = database;
        _repository = database is null ? null : new PlaylistRepository(database);

        lock (Live) Live.Add(new WeakReference<PlaylistStore>(this));

        var started = System.Diagnostics.Stopwatch.StartNew();

        // True only when the read succeeded and the database held no graph: the one
        // state in which seeding it from playlists.json is safe. A *failed* read
        // cannot tell "empty" from "unreadable", so it never seeds.
        var dbKnownEmpty = false;

        try
        {
            if (_repository is not null && _repository.HasPlaylists())
            {
                LoadFromDatabase();
                // LoadFromDatabase falls back to the file when the read races empty.
                _graphFromDatabase = LoadSource == "db";
            }
            else
            {
                Load();
                LoadSource = "json";
                dbKnownEmpty = _repository is not null;
            }
        }
        catch (Exception ex)
        {
            // A broken database must never lose the lists: fall back to JSON.
            Tools.Log.Warn($"playlists.db unreadable, falling back to playlists.json: {ex.Message}");
            Load();
            LoadSource = "json";
        }

        FileStamp = PlaylistFileStamp.Of(FilePath);

        // First start on a database that has no graph yet (fresh install, or an
        // upgrade whose importer has not run): seed it from the file once, on a
        // worker, so *this* start pays the JSON parse and the next one does not.
        // Until then the store is JSON-backed, which is exactly 1.5.0 behaviour.
        // Gate on the result already evaluated above: calling HasPlaylists() again
        // here would throw on a persistent database failure and abort startup.
        if (_repository is not null && LoadSource == "json" && dbKnownEmpty)
            SeedDatabaseFromJson();

        var rows = 0;
        foreach (var playlist in Playlists) rows += playlist.Tracks.Count;
        Tools.Log.Info(
            $"playlist store: {Playlists.Count} playlists / {rows} rows in " +
            $"{started.Elapsed.TotalMilliseconds:F0} ms (source={LoadSource})");
        if (LastLoadStats is { } load)
            Tools.Log.Info($"playlist store stats: {load.Summary}");
    }

    /// <summary>True when saves are mirrored into SQLite as well as the JSON file.</summary>
    public bool IsDatabaseBacked => _repository is not null;

    /// <summary>
    /// True when the last <em>durable</em> (SQLite) write threw. The JSON mirror is
    /// written first and the database wins on load, so a failed mirror means a
    /// delete can come back on the next start: callers report that instead of
    /// claiming a clean success.
    /// </summary>
    public bool LastDatabaseWriteFailed { get; private set; }

    /// <summary>
    /// Write the just-loaded JSON graph into the database in one transaction, off
    /// the startup path. The next constructor takes the <c>db</c> branch and never
    /// parses the file. Failures are logged and ignored: the file is still the
    /// fallback, so a seeding failure costs a slow next start, not data.
    /// </summary>
    private void SeedDatabaseFromJson()
    {
        List<UserPlaylist> snapshot;
        try
        {
            snapshot = CloneGraph();
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"Could not snapshot playlists for seeding: {ex.Message}");
            return;
        }

        if (snapshot.Count == 0) return;

        var stamp = FileStamp;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Queued on the same single-threaded writer every other write uses.
        // Ordering is the point: every delete calls Flush() first, so a repair that
        // removed rows can never be undone by this seed's pre-repair snapshot.
        (_writeQueue ??= new WriteQueue()).Enqueue(SeedQueueKey, () =>
        {
            try
            {
                _repository?.SaveAll(snapshot, stamp);
                var rows = snapshot.Sum(p => p.Tracks.Count);
                Tools.Log.Info(
                    $"playlists seeded into library.db: {snapshot.Count} playlists / {rows} rows in " +
                    $"{watch.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception ex)
            {
                Tools.Log.Warn($"Seeding playlists into library.db failed (next start reads JSON): {ex.Message}");
            }
        });
    }

    private const string SeedQueueKey = "playlists-seed";

    public void Load()
    {
        LoadSource = "json";

        _statAvailabilityMs = 0;
        _statAvailabilityCalls = 0;
        _statWorstRowMs = 0;
        _statWorstRowId = "";

        var total = System.Diagnostics.Stopwatch.StartNew();
        var stats = new PlaylistLoadStats { Source = "json" };
        try
        {
            if (!File.Exists(FilePath))
            {
                LastLoadStats = stats;
                return;
            }

            var read = System.Diagnostics.Stopwatch.StartNew();
            var text = File.ReadAllText(FilePath);
            read.Stop();

            var parse = System.Diagnostics.Stopwatch.StartNew();
            var doc = JsonSerializer.Deserialize<PlaylistDocument>(text, JsonOpts);
            parse.Stop();

            stats = stats with
            {
                FileReadMs = read.Elapsed.TotalMilliseconds,
                JsonParseMs = parse.Elapsed.TotalMilliseconds,
            };

            if (doc?.Playlists == null) { LastLoadStats = stats; return; }

            var migrated = doc.Version < CurrentVersion;
            var materialise = System.Diagnostics.Stopwatch.StartNew();
            var rows = 0;

            Playlists.Clear();
            foreach (var playlist in doc.Playlists)
            {
                if (playlist is null) continue;

                playlist.Tracks ??= new ObservableCollection<SourceTrack>();

                var wasInternal = playlist.Kind == PlaylistKind.Internal;
                var hadRemoteId = playlist.SourceId != null;
                playlist.NormalizeIdentity();
                if (wasInternal != playlist.IsInternal || hadRemoteId != (playlist.SourceId != null))
                    migrated = true;

                // Older files may predate the playlist name being copied onto
                // each track; fill it in so the playlist layout preset works.
                foreach (var track in playlist.Tracks)
                {
                    track.PlaylistName ??= playlist.Name;
                    SanitizeStateTimed(track);
                    rows++;
                }
                Playlists.Add(playlist);
            }

            // A file written before SortOrder existed has every row at 0. The
            // sort is stable, so the file order it already had becomes the
            // explicit 0, 1, 2, ... order and is written back once.
            if (NormalizeSortOrder()) migrated = true;

            materialise.Stop();

            LastLoadStats = stats with
            {
                Playlists = Playlists.Count,
                Rows = rows,
                MaterialiseMs = materialise.Elapsed.TotalMilliseconds,
                AvailabilityTotalMs = _statAvailabilityMs,
                AvailabilityCalls = _statAvailabilityCalls,
                WorstRowMs = _statWorstRowMs,
                WorstRowId = _statWorstRowId,
                TotalMs = total.Elapsed.TotalMilliseconds,
            };

            if (migrated) SaveAfterBackup();
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"playlists.json unreadable, starting empty: {ex.Message}");
            LastLoadStats = stats with { TotalMs = total.Elapsed.TotalMilliseconds };
        }
    }

    /// <summary>
    /// First migration write: keep the pre-migration file beside it as
    /// <c>playlists.json.bak</c>. An existing backup is never overwritten - it is
    /// the oldest, i.e. the one that still has the data a later bug could lose.
    /// </summary>
    private void SaveAfterBackup()
    {
        try
        {
            if (File.Exists(FilePath) && !File.Exists(BackupPath))
            {
                File.Copy(FilePath, BackupPath, overwrite: false);
                Tools.Log.Info($"playlists.json migrated; original kept at {BackupPath}");
            }
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"Could not back up playlists.json before migrating: {ex.Message}");
        }

        Save();
    }

    /// <summary>Same post-load fixes the JSON path applies: playlist name and honest state.</summary>
    private void ApplyLibraryState(UserPlaylist playlist)
    {
        foreach (var track in playlist.Tracks)
        {
            track.PlaylistName ??= playlist.Name;
            SanitizeStateTimed(track);
        }
    }

    /// <summary>
    /// <see cref="SanitizeState"/> plus the two numbers that make a slow load
    /// explainable: the summed cost of the per-row library probe and the single
    /// worst row. Only the bulk load paths use this; a single row update keeps
    /// calling <see cref="SanitizeState"/> directly.
    /// </summary>
    private void SanitizeStateTimed(SourceTrack track)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SanitizeState(track);
        watch.Stop();

        var ms = watch.Elapsed.TotalMilliseconds;
        _statAvailabilityMs += ms;
        _statAvailabilityCalls++;
        if (ms > _statWorstRowMs)
        {
            _statWorstRowMs = ms;
            _statWorstRowId = track.Id ?? "";
        }
    }

    /// <summary>
    /// Re-derive every track's state from the library. Call after wiring
    /// <see cref="LibraryPathResolver"/> so an already-loaded list picks up the
    /// live ledger (MainViewModel does this once at startup).
    /// </summary>
    public void SanitizeStates()
    {
        foreach (var playlist in Playlists)
        {
            foreach (var track in playlist.Tracks) SanitizeState(track);
        }
    }

    /// <summary>
    /// Replace whatever was loaded with a state the library can actually prove:
    /// "in library" when a live file exists on disk, otherwise "New". Transient
    /// states (Queued/Downloading) are never kept - they are what turns a
    /// previewed track into a row stuck at "⋯ 0%" across restarts.
    ///
    /// Imported membership is resolved by remote id through the ledger. Internal
    /// membership is *never* nulled here: its row is the app's own record of a
    /// library file, so a missing ledger line (manual, pre-provenance, re-encoded)
    /// must not erase it - the browser resolves it later by
    /// <see cref="SourceTrack.LocalKey"/>, then by path, then by artist/title.
    /// </summary>
    private void SanitizeState(SourceTrack track)
    {
        if (track is null) return;

        if (track.Id.StartsWith("manual:", StringComparison.Ordinal))
        {
            track.Progress = 0;
            track.Error = null;
            track.State = SourceTrackState.InLibrary;
            return;
        }

        var path = ResolveLibraryPath(track);
        if (!string.IsNullOrWhiteSpace(path))
        {
            track.LibraryPath = path;
            track.State = SourceTrackState.InLibrary;
            track.Progress = 0;
            track.Error = null;
            return;
        }

        track.Progress = 0;
        if (track.State is SourceTrackState.Queued
            or SourceTrackState.Downloading
            or SourceTrackState.InLibrary)
        {
            track.LibraryPath = null;
            track.State = SourceTrackState.New;
        }
    }

    private string? ResolveLibraryPath(SourceTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.Id)) return null;

        try
        {
            var resolve = LibraryPathResolver ??= CreateLedgerLookup();
            return resolve(track.Id.Trim());
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"Library lookup for '{track.Id}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Default resolver: a one-off snapshot of the download ledger. Only used by
    /// stores created before the host wires <see cref="LibraryPathResolver"/>
    /// (and by the Settings window's throwaway store).
    /// </summary>
    private static Func<string, string?> CreateLedgerLookup()
    {
        var ledger = new Downloads.LibraryLedger();

        return id =>
        {
            var entry = ledger.TryGet(id);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) return null;

            try
            {
                return File.Exists(entry.Path) ? entry.Path : null;
            }
            catch (Exception ex)
            {
                Tools.Log.Warn($"Could not probe ledger path '{entry.Path}': {ex.Message}");
                return null;
            }
        };
    }

    /// <summary>
    /// Synchronous save: writes playlists.json, then blocks until the same graph
    /// has landed in SQLite (when a database is in use). Tests and shutdown use
    /// this; mutators use the deferred path.
    ///
    /// <para>Any older queued mirror is drained first, so a deferred snapshot from
    /// before this call can never land after it and undo the save.</para>
    /// </summary>
    public void Save()
    {
        Flush();
        Persist(immediate: true);
    }

    /// <summary>
    /// Flush every live store to SQLite (shutdown path). Returns how many stores
    /// were flushed. Never throws: a store that cannot flush must not stop the
    /// other one, or the app from closing.
    /// </summary>
    public static int FlushAll()
    {
        var stores = new List<PlaylistStore>(2);
        lock (Live)
        {
            Live.RemoveAll(weak => !weak.TryGetTarget(out _));
            foreach (var weak in Live)
            {
                if (weak.TryGetTarget(out var store)) stores.Add(store);
            }
        }

        foreach (var store in stores)
        {
            try
            {
                store.Flush();
            }
            catch (Exception ex)
            {
                Tools.Log.Error("Flushing the playlist write queue failed", ex);
            }
        }

        return stores.Count;
    }

    /// <summary>
    /// Save from a mutator. <see cref="Revision"/> moves here, synchronously, so
    /// a cache stamped with it can never serve a pre-edit answer; the JSON mirror
    /// is debounced on a worker (see <see cref="ScheduleJsonMirror"/>) and the
    /// SQLite write is coalesced on <see cref="WriteQueue"/>, so a burst of edits
    /// (a drag, a rename, a 200-track add) costs one of each.
    /// </summary>
    private void Persist(bool immediate = false)
    {
        Interlocked.Increment(ref _revision);

        if (immediate)
        {
            CancelJsonMirror();
            WriteJsonFile(SnapshotForJson());
            WriteThrough(immediate: true);
            return;
        }

        ScheduleJsonMirror();
        WriteThrough(immediate: false);
    }

    // ---- JSON compatibility mirror ----------------------------------------

    /// <summary>Quiet window: a burst of mutations writes the file once, 250 ms later.</summary>
    private const int JsonMirrorDelayMs = 250;

    private Timer? _jsonMirror;

    /// <summary>
    /// The document the timer will write. Captured by the mutator that dirtied
    /// the mirror, on its own thread: enumerating <see cref="UserPlaylist.Tracks"/>
    /// from the timer thread would race an edit the UI thread is free to make.
    /// Non-null means "a write is pending".
    /// </summary>
    private PlaylistDocument? _pendingJson;

    /// <summary>
    /// Mark the mirror dirty and (re)arm the one-shot timer. Cheap enough to call
    /// from every mutator: no serialisation, no I/O, no allocation after the
    /// first call.
    /// </summary>
    private void ScheduleJsonMirror()
    {
        if (_disposed) return;

        // One list copy per playlist (references, not rows): cheap enough to take
        // on every keystroke of a rename, and it is the only thread-safe moment
        // to read the graph.
        Interlocked.Exchange(ref _pendingJson, SnapshotForJson());

        var timer = _jsonMirror;
        if (timer is null)
        {
            lock (_lock)
            {
                timer = _jsonMirror ??= new Timer(_ => MirrorJsonToDisk(), null,
                    Timeout.Infinite, Timeout.Infinite);
            }
        }

        try
        {
            timer.Change(JsonMirrorDelayMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race; Dispose flushed whatever was pending.
        }
    }

    /// <summary>
    /// Run a pending mirror now, on the calling thread. Used by <see cref="Save"/>,
    /// <see cref="Flush"/>, <see cref="Dispose"/> and by an external-edit reload
    /// (which must not lose this session's edits to a file that was just seen to
    /// change under us).
    /// </summary>
    private void FlushJsonMirror()
    {
        _jsonMirror?.Change(Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.Exchange(ref _pendingJson, null) is { } pending)
            WriteJsonFile(pending);
    }

    /// <summary>Drop a pending mirror without writing it (the file on disk wins).</summary>
    private void CancelJsonMirror()
    {
        _jsonMirror?.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref _pendingJson, null);
    }

    private void MirrorJsonToDisk()
    {
        if (Interlocked.Exchange(ref _pendingJson, null) is not { } pending) return;
        WriteJsonFile(pending);
    }

    /// <summary>
    /// Immutable-enough view of the graph for a background write: one new list per
    /// playlist (thousands of reference copies, not of rows), so a mutator that
    /// runs during serialisation cannot make the enumerator throw. The document
    /// is a mirror; a row renamed mid-write is written on the next one.
    /// </summary>
    private PlaylistDocument SnapshotForJson()
    {
        lock (_lock)
        {
            var playlists = new List<UserPlaylist>(Playlists.Count);
            foreach (var playlist in Playlists) playlists.Add(playlist.ShallowCopy());
            return new PlaylistDocument { Version = CurrentVersion, Playlists = playlists };
        }
    }

    private void WriteJsonFile(PlaylistDocument doc)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);

            var serialize = System.Diagnostics.Stopwatch.StartNew();
            var json = JsonSerializer.Serialize(doc, JsonOpts);
            serialize.Stop();
            var write = System.Diagnostics.Stopwatch.StartNew();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // atomic-ish replace
            write.Stop();
            Interlocked.Increment(ref _jsonMirrorWrites);
            RecordSaveStats(json.Length, serialize.Elapsed.TotalMilliseconds, write.Elapsed.TotalMilliseconds,
                background: true);

            // Publish the identity of what is now on disk *before* telling the
            // host, so a Changed handler that consults the membership cache sees
            // the new stamps and misses (rather than serving the pre-edit answer).
            FileStamp = PlaylistFileStamp.Of(FilePath);

            // Keep the database's copy of the stamp in step with the file, or the
            // next start mistakes our own mirror for an external edit and reimports
            // the whole document.
            if (_repository is not null)
            {
                try
                {
                    _repository.SetJsonStamp(FileStamp);
                }
                catch (Exception ex)
                {
                    Tools.Log.Error("Recording the playlists.json stamp in library.db failed", ex);
                }
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Tools.Log.Error("Saving playlists.json failed", ex);
        }
    }

    /// <summary>
    /// Mirror the graph into SQLite. The clone happens here, on the caller's
    /// thread: the deferred writer must never enumerate an ObservableCollection
    /// that the UI thread is free to mutate.
    /// </summary>
    private void WriteThrough(bool immediate)
    {
        if (_repository is null || _disposed) return;

        List<UserPlaylist> snapshot;
        try
        {
            snapshot = CloneGraph();
        }
        catch (Exception ex)
        {
            Tools.Log.Error("Snapshotting playlists for SQLite failed", ex);
            return;
        }

        if (immediate)
        {
            try
            {
                _repository.SaveAll(snapshot, FileStamp, pruneAbsent: _graphFromDatabase);
                LastDatabaseWriteFailed = false;
            }
            catch (Exception ex)
            {
                // The JSON file already holds the new state, so a failed mirror
                // must not surface as a lost save - but it *is* a delete that will
                // not stick, so callers get to see it (LastDatabaseWriteFailed).
                LastDatabaseWriteFailed = true;
                Tools.Log.Warn(
                    $"Saving playlists to library.db failed; the database still holds the old graph " +
                    $"(a delete made now can return on the next start): {ex.Message}");
            }

            return;
        }

        // Stamp captured now, not inside the queued work: the graph being
        // written is the one the file had at this instant.
        var stamp = FileStamp;
        var pruneAbsent = _graphFromDatabase;
        (_writeQueue ??= new WriteQueue()).Enqueue(WriteQueueKey, () =>
        {
            try
            {
                _repository.SaveAll(snapshot, stamp, pruneAbsent);
            }
            catch (Exception ex)
            {
                Tools.Log.Error("Saving playlists to library.db failed", ex);
            }
        });
    }

    private const string WriteQueueKey = "playlists";

    private void RecordSaveStats(long chars, double serializeMs, double writeMs, bool background)
    {
        LastSaveStatsForPerf =
            $"chars={chars} serialize={serializeMs:F1}ms write={writeMs:F1}ms background={background}";
    }

    private List<UserPlaylist> CloneGraph()
    {
        List<UserPlaylist> copy;
        lock (_lock)
        {
            copy = new List<UserPlaylist>(Playlists.Count);
            foreach (var playlist in Playlists)
            {
                var clone = new UserPlaylist
                {
                    Id = playlist.Id,
                    Name = playlist.Name,
                    Source = playlist.Source,
                    Kind = playlist.Kind,
                    SourceId = playlist.SourceId,
                    SyncEnabled = playlist.SyncEnabled,
                    LastSyncedUtc = playlist.LastSyncedUtc,
                };

                foreach (var track in playlist.Tracks) clone.Tracks.Add(track.Clone());
                copy.Add(clone);
            }
        }

        return copy;
    }

    /// <summary>
    /// Load the graph from SQLite. Falls back to the JSON file (via
    /// <see cref="Load"/>) when the database holds no playlists, which is the
    /// state before <see cref="LegacyImporter"/> has run.
    /// </summary>
    private void LoadFromDatabase()
    {
        if (_repository is null) { Load(); return; }

        var dbWatch = System.Diagnostics.Stopwatch.StartNew();
        var loaded = _repository.LoadAll();
        dbWatch.Stop();
        if (loaded.Count == 0) { Load(); return; }
        LoadSource = "db";

        _statAvailabilityMs = 0;
        _statAvailabilityCalls = 0;
        _statWorstRowMs = 0;
        _statWorstRowId = "";

        var total = System.Diagnostics.Stopwatch.StartNew();
        var dbReadMs = dbWatch.Elapsed.TotalMilliseconds;

        Playlists.Clear();
        var rows = 0;
        var rebuild = System.Diagnostics.Stopwatch.StartNew();
        foreach (var playlist in loaded)
        {
            playlist.NormalizeIdentity();
            playlist.SortOrder = Playlists.Count;
            ApplyLibraryState(playlist);
            Playlists.Add(playlist);
            rows += playlist.Tracks.Count;
        }
        rebuild.Stop();

        LastLoadStats = new PlaylistLoadStats
        {
            Source = "db",
            Playlists = Playlists.Count,
            Rows = rows,
            DbReadMs = dbReadMs,
            MaterialiseMs = rebuild.Elapsed.TotalMilliseconds,
            AvailabilityTotalMs = _statAvailabilityMs,
            AvailabilityCalls = _statAvailabilityCalls,
            WorstRowMs = _statWorstRowMs,
            WorstRowId = _statWorstRowId,
            TotalMs = total.Elapsed.TotalMilliseconds,
        };

        Interlocked.Increment(ref _revision);
    }

    /// <summary>
    /// Membership of one playlist straight from SQLite, materialised as one list
    /// (a single indexed query, ordered by position). Returns null when there is
    /// no database, or the playlist is not in it, so the caller can fall back to
    /// the in-memory graph.
    ///
    /// <para>This is the fast path for the remote tabs: a 5 000-row list costs one
    /// query instead of a full deserialise of <c>playlists.json</c>.</para>
    /// </summary>
    public IReadOnlyList<SourceTrack>? LoadPlaylistRows(string playlistId)
    {
        if (_repository is null || _disposed) return null;

        try
        {
            // The header is already in memory: the graph was loaded from this very
            // table, so a switch needs one query - the membership - and no second
            // round trip to read a name the caller already has.
            UserPlaylist? meta = null;
            foreach (var candidate in Playlists)
            {
                if (!string.Equals(candidate.Id, playlistId, StringComparison.Ordinal)) continue;
                meta = candidate;
                break;
            }

            if (meta is null) return null;

            var query = System.Diagnostics.Stopwatch.StartNew();
            var items = _repository.LoadItems(playlistId);
            query.Stop();

            // Empty in the database but not in the graph means the mirror is behind
            // the JSON file, which stays authoritative: let the caller use the graph.
            if (items.Count == 0 && meta.Tracks.Count > 0) return null;

            var map = System.Diagnostics.Stopwatch.StartNew();
            var rows = new List<SourceTrack>(items.Count);
            foreach (var item in items)
            {
                var row = new SourceTrack
                {
                    Id = string.IsNullOrEmpty(item.RowId) ? item.SourceKey : item.RowId,
                    Source = item.Source == MediaSource.Manual ? meta.Source : item.Source,
                    Title = item.Title,
                    Artist = item.Artist,
                    Album = item.Album,
                    Isrc = item.Isrc,
                    CoverUrl = item.CoverUrl,
                    DurationMs = item.DurationMs,
                    VideoId = item.VideoId,
                    LibraryPath = item.LibraryPath,
                    PlaylistName = string.IsNullOrEmpty(meta.Name) ? null : meta.Name,
                    LocalKey = meta.Kind == PlaylistKind.Internal ? item.SourceKey : null,
                };

                // Only a row the database already records a file for needs the
                // host lookup here. Everything else is a plain remote row, and
                // asking the ledger / library index about 5 000 of those is what
                // made materialising a big playlist cost 170 ms. The caller's
                // reconcile pass (one pass over the same rows, with the ledger
                // indexed by artist+title) derives the rest.
                if (!string.IsNullOrWhiteSpace(row.LibraryPath)) SanitizeState(row);

                rows.Add(row);
            }

            map.Stop();
            Tools.Log.Debug($"playlist rows '{playlistId}': query={query.Elapsed.TotalMilliseconds:F1}ms map={map.Elapsed.TotalMilliseconds:F1}ms rows={rows.Count}");
            return rows;
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"playlist rows '{playlistId}' unreadable from library.db: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-read <c>playlists.json</c> when another process (or the user) changed it
    /// since this store last saw it, and mirror the new graph into SQLite.
    ///
    /// <para>JSON stays authoritative: the file that changed on disk wins, the
    /// database is rebuilt from it, and <see cref="Changed"/> tells the host to
    /// refresh its rails. Returns true when a reload happened. Cheap on the normal
    /// path - one <c>stat</c> - which is why the host can call it on every
    /// playlist switch.</para>
    /// </summary>
    public bool RefreshFromDiskIfChanged()
    {
        if (_disposed) return false;

        var stamp = PlaylistFileStamp.Of(FilePath);
        if (stamp == 0 || stamp == FileStamp) return false;

        lock (_lock)
        {
            if (_refreshing) return false;
            _refreshing = true;
        }

        try
        {
            Tools.Log.Info($"playlists.json changed on disk (stamp {FileStamp} -> {stamp}); reloading");
            CancelJsonMirror();          // a pending mirror must not undo the external edit
            Load();                      // json wins, exactly as before
            FileStamp = PlaylistFileStamp.Of(FilePath);

            if (_repository is not null)
            {
                try
                {
                    _repository.SaveAll(CloneGraph(), FileStamp);
                }
                catch (Exception ex)
                {
                    Tools.Log.Error("Refreshing playlists in library.db after an external edit failed", ex);
                }
            }

            Interlocked.Increment(ref _revision);
            Changed?.Invoke();
            return true;
        }
        finally
        {
            lock (_lock) _refreshing = false;
        }
    }

    /// <summary>
    /// Run the deferred writer to completion (used by <see cref="Dispose"/> and
    /// by tests that assert on the database right after a mutation).
    /// </summary>
    public void Flush()
    {
        FlushJsonMirror();

        WriteQueue? queue;
        lock (_lock) queue = _writeQueue;
        queue?.Flush();
    }

    public void Dispose()
    {
        WriteQueue? queue;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            queue = _writeQueue;
            _writeQueue = null;
        }

        lock (Live)
        {
            for (var i = Live.Count - 1; i >= 0; i--)
            {
                if (!Live[i].TryGetTarget(out var target) || ReferenceEquals(target, this)) Live.RemoveAt(i);
            }
        }

        queue?.Flush();
        queue?.Dispose();

        // The mirror is a compatibility artefact, not the source of truth, but a
        // clean shutdown must still leave the file matching what the user sees.
        FlushJsonMirror();
        _jsonMirror?.Dispose();
    }

    /// <summary>
    /// Ticks+XOR-length of playlists.json, or 0 when it cannot be read. Stable
    /// across processes (unlike <see cref="HashCode"/>, which is seeded per run).
    /// </summary>
    private static long ProbeFileStamp() => PlaylistFileStamp.Of(FilePath);

    /// <summary>Create a playlist. A source id makes it an imported mirror.</summary>
    public UserPlaylist Create(string name, MediaSource source = MediaSource.Manual, string? sourceId = null)
    {
        var playlist = new UserPlaylist
        {
            Name = name,
            Source = source,
            SourceId = sourceId,
            SyncEnabled = sourceId != null && source != MediaSource.Manual,
            SortOrder = NextSortOrder(),
        };
        playlist.NormalizeIdentity();
        Playlists.Add(playlist);
        Persist();
        return playlist;
    }

    /// <summary>Create an app-owned playlist (LIBRARY rail's "+ New").</summary>
    public UserPlaylist CreateInternal(string name)
    {
        var playlist = new UserPlaylist
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New playlist" : name.Trim(),
            Source = MediaSource.Manual,
            Kind = PlaylistKind.Internal,
            SourceId = null,
            SyncEnabled = false,
            SortOrder = NextSortOrder(),
        };
        Playlists.Add(playlist);
        Persist();
        return playlist;
    }

    /// <summary>
    /// The next free rail position: one past the last row. The run is kept
    /// contiguous, so this can never land in the middle of an existing slot.
    /// </summary>
    private int NextSortOrder() => Playlists.Count == 0 ? 0 : Playlists.Max(p => p.SortOrder) + 1;

    /// <summary>
    /// Rewrite every position as 0, 1, 2, ... in current collection order:
    /// contiguous by construction, no ties and no gaps a later insert could
    /// fall into.
    /// </summary>
    private void RenumberSortOrder()
    {
        for (var i = 0; i < Playlists.Count; i++) Playlists[i].SortOrder = i;
    }

    /// <summary>
    /// A file or database from before <see cref="UserPlaylist.SortOrder"/> has
    /// every row at 0. The sort is stable, so the order those rows already had
    /// becomes an explicit contiguous run. Returns true when anything changed.
    /// </summary>
    private bool NormalizeSortOrder()
    {
        var ordered = Playlists.OrderBy(p => p.SortOrder).ToList();
        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SortOrder != i) changed = true;
            ordered[i].SortOrder = i;
        }

        if (!changed) return false;

        Playlists.Clear();
        foreach (var playlist in ordered) Playlists.Add(playlist);
        return true;
    }

    /// <summary>
    /// Move one app-owned playlist one slot up (<paramref name="delta"/> -1) or
    /// down (+1) the rail. Returns false at the ends, so the caller can disable
    /// the command instead of clamping a move into a duplicate slot.
    ///
    /// <para>Reordering arranges rows; it does not edit a playlist's contents,
    /// and only app-owned rows appear in this rail. Imported rows keep their
    /// relative order and are renumbered with the rest.</para>
    /// </summary>
    /// <summary>
    /// Whether a move is possible, using the same bounds the mutator enforces.
    /// The rail's command enablement calls this, so the menu and the store can
    /// never disagree about the ends of the list.
    /// </summary>
    public bool CanMovePlaylist(UserPlaylist? playlist, int delta)
    {
        if (playlist is null || delta == 0 || !playlist.IsInternal) return false;

        var rail = Playlists.Where(p => p.IsInternal).OrderBy(p => p.SortOrder).ToList();
        var from = rail.IndexOf(playlist);
        var to = from + delta;
        return from >= 0 && to >= 0 && to < rail.Count;
    }

    public bool MovePlaylist(UserPlaylist playlist, int delta)
    {
        if (playlist is null || delta == 0 || !playlist.IsInternal) return false;

        var rail = Playlists.Where(p => p.IsInternal).OrderBy(p => p.SortOrder).ToList();
        var from = rail.IndexOf(playlist);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= rail.Count) return false;

        var neighbour = rail[to];
        Playlists.Remove(playlist);
        var anchor = Playlists.IndexOf(neighbour);
        Playlists.Insert(delta < 0 ? anchor : anchor + 1, playlist);

        RenumberSortOrder();
        Persist();
        return true;
    }

    public bool Rename(UserPlaylist playlist, string name)
    {
        if (!playlist.IsInternal) return false;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;
        if (string.Equals(playlist.Name, trimmed, StringComparison.Ordinal)) return true;

        playlist.Name = trimmed;
        foreach (var track in playlist.Tracks) track.PlaylistName = trimmed;
        Persist();
        return true;
    }

    /// <summary>
    /// Delete a playlist the user asked to delete, whatever its <see cref="UserPlaylist.Kind"/>:
    /// a row the user can see must be removable, and an imported mirror is still a
    /// row the user can see. (Library-side callers only ever offer app-owned rows.)
    /// Durable on purpose: the JSON mirror and library.db are both written now, so a
    /// deleted row cannot come back from the database on the next start.
    /// </summary>
    /// <summary>
    /// The one deletion body. <see cref="Delete"/> and <see cref="Remove"/> are the
    /// same operation with different return types, and they stay that way on
    /// purpose: when they were two copies, only one of them flushed the write queue
    /// and a queued pre-delete snapshot resurrected the row (and its items) on the
    /// next start. Every durability rule for a user delete belongs here, once.
    /// </summary>
    private bool RemoveCore(UserPlaylist playlist)
    {
        if (!Playlists.Remove(playlist)) return false;
        // Close the gap so the run stays 0, 1, 2, ...: a later insertion then
        // cannot land in the middle of the deleted row's slot.
        RenumberSortOrder();
        // Flush first, exactly as Save() does: a deferred snapshot cloned before
        // this delete is still on the write queue and would re-insert the row
        // after the write below.
        Flush();
        Persist(immediate: true);
        DeleteRowIfNotPruned(playlist.Id);
        return true;
    }

    public bool Delete(UserPlaylist playlist) => RemoveCore(playlist);

    public void Remove(UserPlaylist playlist) => RemoveCore(playlist);

    /// <summary>
    /// Remove several rows with one flush and one durable write (the startup repair
    /// drops a handful of phantom rows in the constructor, on the UI thread).
    /// Returns how many rows actually went.
    /// </summary>
    public int RemoveMany(IEnumerable<UserPlaylist> playlists)
    {
        var removed = new List<string>();
        foreach (var playlist in playlists)
        {
            if (Playlists.Remove(playlist)) removed.Add(playlist.Id);
        }

        if (removed.Count == 0) return 0;

        Flush();
        Persist(immediate: true);
        foreach (var id in removed) DeleteRowIfNotPruned(id);
        return removed.Count;
    }

    /// <summary>
    /// Drop one row explicitly when <see cref="WriteThrough"/>'s prune cannot see
    /// it. The prune only deletes ids absent from a graph that came from the
    /// database (<see cref="_graphFromDatabase"/>), so a delete on a JSON-backed
    /// store has to name its id. Never throws: the JSON mirror already recorded
    /// the removal, and a failed mirror write must not surface as a failed delete.
    /// </summary>
    private void DeleteRowIfNotPruned(string playlistId)
    {
        if (_repository is null || _disposed || _graphFromDatabase) return;

        try
        {
            _repository.Delete(playlistId);
        }
        catch (Exception ex)
        {
            Tools.Log.Error($"Deleting playlist '{playlistId}' from library.db failed", ex);
        }
    }

    public UserPlaylist? FindBySourceId(MediaSource source, string sourceId) =>
        Playlists.FirstOrDefault(p => p.Source == source && p.SourceId == sourceId);

    public UserPlaylist? FindByName(string name) =>
        Playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Add tracks to a playlist, skipping any whose remote id is already
    /// present. Returns how many were actually added.
    /// </summary>
    public int AddTracks(UserPlaylist playlist, IEnumerable<SourceTrack> tracks)
    {
        // App-owned membership is the library key; a remote id would never resolve.
        if (playlist.IsInternal) return 0;

        var existing = new HashSet<string>(playlist.Tracks.Select(t => t.Id), StringComparer.Ordinal);
        var added = 0;
        foreach (var track in tracks)
        {
            if (!existing.Add(track.Id)) continue;
            track.PlaylistName = playlist.Name;
            playlist.Tracks.Add(track);
            added++;
        }

        if (added > 0)
        {
            playlist.NotifyTrackCountChanged();
            Persist();
        }
        return added;
    }

    /// <summary>
    /// Add library files to an app-owned playlist. Membership is the library key
    /// (<see cref="TrackEntry.RelativeKey"/>), extension-stripped, so the same file
    /// is never added twice and a later rename can still be resolved by metadata.
    /// Returns how many rows were actually added.
    /// </summary>
    public int AddLibraryTracks(UserPlaylist playlist, IEnumerable<TrackEntry> tracks)
    {
        if (!playlist.IsInternal) return 0;

        var existing = new HashSet<string>(
            playlist.Tracks.Select(t => t.LocalKey).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var entry in tracks)
        {
            var key = SourceTrack.NormalizeLocalKey(entry?.RelativeKey);
            if (key.Length == 0 || !existing.Add(key)) continue;

            playlist.Tracks.Add(new SourceTrack
            {
                Id = "manual:" + Guid.NewGuid().ToString("N"),
                Source = MediaSource.Manual,
                Title = entry!.Title,
                Artist = entry.Artist,
                Album = entry.Album,
                DurationMs = entry.Duration.TotalMilliseconds,
                LocalKey = key,
                LibraryPath = entry.MusicPath,
                PlaylistName = playlist.Name,
                State = SourceTrackState.InLibrary,
            });
            added++;
        }

        if (added > 0)
        {
            playlist.NotifyTrackCountChanged();
            Persist();
        }
        return added;
    }

    /// <summary>
    /// Add <em>remote</em> tracks (not downloaded yet) to an app-owned playlist.
    ///
    /// The row keeps the remote identity the database already stores for imported
    /// playlists - source, source id / video id, title, artist, duration, cover -
    /// so the playlist shows and plays it immediately through the resolver. When
    /// the library already holds the file, <see cref="SourceTrack.LibraryPath"/>
    /// is filled in as well, which is what makes the row show up in the browser
    /// straight away, and what lets a later download upgrade the same row
    /// (<see cref="LibraryMembershipSnapshot.Resolve"/> tries the key, then the
    /// path, then artist/title).
    ///
    /// Returns how many rows were actually added (0 when the track is already
    /// there).
    /// </summary>
    public int AddRemoteTracks(UserPlaylist playlist, IEnumerable<SourceTrack> tracks)
    {
        if (!playlist.IsInternal) return 0;

        // Identity is the remote id: the same Spotify track or YouTube video must
        // never land twice, no matter which tab it was added from.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in playlist.Tracks)
        {
            if (!string.IsNullOrWhiteSpace(row.Id)) existing.Add(row.Id.Trim());
        }

        var added = 0;
        foreach (var source in tracks)
        {
            if (source is null) continue;

            var id = (source.Id ?? "").Trim();
            if (!IsRemoteIdentity(id) || !existing.Add(id)) continue;

            var path = ResolveLibraryPath(source);

            playlist.Tracks.Add(new SourceTrack
            {
                Id = id,
                Source = source.Source,
                Title = source.Title,
                Artist = source.Artist,
                Album = source.Album,
                Isrc = source.Isrc,
                CoverUrl = source.CoverUrl,
                DurationMs = source.DurationMs,
                VideoId = source.VideoId,
                PlaylistName = playlist.Name,
                // No library key yet: membership resolves by the path once the
                // file exists, and the row stays a remote row until then.
                LocalKey = null,
                LibraryPath = path,
                State = path is null ? SourceTrackState.New : SourceTrackState.InLibrary,
            });
            added++;
        }

        if (added > 0)
        {
            playlist.NotifyTrackCountChanged();
            Persist();
        }
        return added;
    }

    /// <summary>
    /// Remove rows from an app-owned playlist by remote id. The counterpart of
    /// <see cref="AddRemoteTracks"/>: a row added before its file was downloaded
    /// has no library key, so key/metadata removal alone would miss it.
    /// </summary>
    public int RemoveRemoteTracks(UserPlaylist playlist, IEnumerable<SourceTrack> tracks)
    {
        if (!playlist.IsInternal) return 0;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in tracks)
        {
            if (!string.IsNullOrWhiteSpace(source?.Id)) ids.Add(source.Id.Trim());
        }
        if (ids.Count == 0) return 0;

        var doomed = playlist.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && ids.Contains(t.Id.Trim()))
            .ToList();

        if (doomed.Count == 0) return 0;

        foreach (var row in doomed) playlist.Tracks.Remove(row);
        playlist.NotifyTrackCountChanged();
        Persist();
        return doomed.Count;
    }

    /// <summary>True when <paramref name="id"/> is a source id, not an app-owned "manual:" handle.</summary>
    public static bool IsRemoteIdentity(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        !id.Trim().StartsWith("manual:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remove library files from an app-owned playlist by key (or, when the row
    /// has no key, by artist/title). Returns how many rows were removed.
    /// </summary>
    public int RemoveLibraryTracks(UserPlaylist playlist, IEnumerable<TrackEntry> tracks)
    {
        if (!playlist.IsInternal) return 0;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in tracks)
        {
            if (entry is null) continue;
            keys.Add(SourceTrack.NormalizeLocalKey(entry.RelativeKey));
            metadata.Add(RowMetadataKey(entry.Artist, entry.Title));
        }

        var doomed = playlist.Tracks.Where(t =>
                (t.LocalKey != null && keys.Contains(t.LocalKey))
                || (t.LocalKey is null && metadata.Contains(RowMetadataKey(t.Artist, t.Title))))
            .ToList();

        if (doomed.Count == 0) return 0;

        foreach (var row in doomed) playlist.Tracks.Remove(row);
        playlist.NotifyTrackCountChanged();
        Persist();
        return doomed.Count;
    }

    /// <summary>
    /// Repoint playlist rows after a library file was renamed or moved.
    ///
    /// App-owned rows are identified by the library key
    /// (<see cref="SourceTrack.LocalKey"/>): their key becomes
    /// <paramref name="newKey"/> and their cached <see cref="SourceTrack.LibraryPath"/>
    /// becomes <paramref name="newPath"/>, whether or not a path was cached
    /// before. Imported rows keep their remote <see cref="SourceTrack.Id"/>; only
    /// a cached <see cref="SourceTrack.LibraryPath"/> that pointed at the old file
    /// is repointed.
    ///
    /// Idempotent: rows already naming the new key/path are skipped, and nothing
    /// is written when no row changed. Returns how many rows were updated.
    /// </summary>
    public int RepointLibraryFile(string? oldKey, string? newKey, string? oldPath, string? newPath)
    {
        var fromKey = SourceTrack.NormalizeLocalKey(oldKey);
        var toKey = SourceTrack.NormalizeLocalKey(newKey);
        var keyChanged = fromKey.Length > 0 && toKey.Length > 0
                         && !string.Equals(fromKey, toKey, StringComparison.Ordinal);
        var newFull = (newPath ?? "").Trim();
        var pathChanged = newFull.Length > 0 && !string.IsNullOrWhiteSpace(oldPath)
                          && !string.Equals(oldPath!.Trim(), newFull, StringComparison.OrdinalIgnoreCase);

        if (!keyChanged && !pathChanged) return 0;

        var changed = 0;
        foreach (var playlist in Playlists)
        {
            foreach (var track in playlist.Tracks)
            {
                var touched = false;

                if (keyChanged && string.Equals(track.LocalKey, fromKey, StringComparison.OrdinalIgnoreCase))
                {
                    track.LocalKey = toKey;
                    touched = true;
                }

                // Internal rows follow their key even when they never cached a
                // path; imported rows are matched by the path they had cached.
                if (pathChanged
                    && (touched || PathMatches(track.LibraryPath, oldPath!)))
                {
                    track.LibraryPath = newFull;
                    touched = true;
                }

                if (!touched) continue;

                track.State = SourceTrackState.InLibrary;
                track.Progress = 0;
                track.Error = null;
                changed++;
            }
        }

        if (changed > 0) Persist();
        return changed;
    }

    /// <summary>Case-insensitive path comparison, tolerating relative vs absolute forms.</summary>
    private static bool PathMatches(string? candidate, string wanted)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var trimmed = candidate.Trim();
        if (string.Equals(trimmed, wanted.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            return string.Equals(
                Path.GetFullPath(trimmed), Path.GetFullPath(wanted.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string RowMetadataKey(string? artist, string? title) =>
        SourceTrack.NormalizeLocalKey(artist + "\u241F" + title);

    /// <summary>Reorder a row inside an app-owned playlist (atomic save).</summary>
    public bool MoveTrack(UserPlaylist playlist, int fromIndex, int toIndex)
    {
        if (!playlist.IsInternal) return false;
        if (fromIndex < 0 || fromIndex >= playlist.Tracks.Count) return false;
        if (toIndex < 0 || toIndex >= playlist.Tracks.Count) return false;
        if (fromIndex == toIndex) return true;

        playlist.Tracks.Move(fromIndex, toIndex);
        Persist();
        return true;
    }

    private sealed class PlaylistDocument
    {
        /// <summary>Schema version: 2 adds <see cref="UserPlaylist.Kind"/> and local library keys.</summary>
        public int Version { get; set; } = CurrentVersion;
        public List<UserPlaylist> Playlists { get; set; } = new();
    }
}
