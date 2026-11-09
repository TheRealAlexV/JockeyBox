using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Tools;

namespace JockeyBox.Sources;

/// <summary>
/// Resolved playlist membership, kept in memory and mirrored to
/// %APPDATA%\JockeyBox\playlist-cache.json.
///
/// Opening a playlist used to re-resolve every row against the download ledger,
/// the library key index and the tag-derived metadata index on the UI thread.
/// A 300-row list meant hundreds of lookups and a 5,000-row import meant enough
/// of them to stall the window. The answer only changes when the library is
/// rebuilt or the playlist file is written, so it is stored with two stamps:
///
///  * <see cref="LibraryGeneration"/> - bumped by the host whenever
///    <c>Tracks</c>/<c>LibraryIndex</c> are rebuilt (scan, file operation,
///    download completion). Persisted, so a restart that has not rescanned yet
///    can still serve the previous run's answer;
///  * the store stamp - <see cref="PlaylistStore.Revision"/> (incremented by
///    every <see cref="PlaylistStore.Save"/>) plus <see cref="PlaylistStore.FileStamp"/>
///    (a durable identity of playlists.json, so an edit made in an earlier
///    session also invalidates).
///
/// An entry is only served when all three match, so an edit or a rescan can
/// never leave a stale membership in place silently. The cache is bounded to
/// <see cref="MaxPlaylists"/> least-recently-used entries and is disposable:
/// deleting the file costs one resolution pass, never correctness.
/// </summary>
public sealed class PlaylistMembershipCache
{
    private const int SchemaVersion = 1;

    /// <summary>How many playlists' resolved membership travel to disk.</summary>
    public const int MaxPlaylists = 50;

    /// <summary>Bursts of cache mutations (a scan bumping once per row) collapse into one write.</summary>
    private static readonly TimeSpan SaveCoalesceDelay = TimeSpan.FromMilliseconds(750);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string AppDataDir => Path.Combine(AppPaths.Home);

    /// <summary>%APPDATA%\JockeyBox\playlist-cache.json - safe to delete.</summary>
    public static string FilePath => Path.Combine(AppDataDir, "playlist-cache.json");

    /// <summary>One playlist's resolved membership plus the stamps that make it valid.</summary>
    private sealed class Entry
    {
        public string PlaylistId { get; set; } = "";

        /// <summary>Number of resolved library keys; mirrors <see cref="Keys"/> for a cheap rail count.</summary>
        public int Count { get; set; }

        /// <summary>Resolved membership as library keys (<see cref="SourceTrack.LocalKey"/> form).</summary>
        public List<string> Keys { get; set; } = new();

        public int LibraryGeneration { get; set; }

        public int StoreRevision { get; set; }

        /// <summary>
        /// Process that wrote the entry. <see cref="StoreRevision"/> counts saves
        /// inside one run, so it is only comparable within the run that produced
        /// it; <see cref="StoreStamp"/> is what carries across restarts.
        /// </summary>
        public string? Session { get; set; }

        public long StoreStamp { get; set; }

        public DateTime LastUsedUtc { get; set; }
    }

    /// <summary>On-disk shape; separate from the entry class so the file names its schema.</summary>
    private sealed class CacheDocument
    {
        public int Version { get; set; } = SchemaVersion;
        public int LibraryGeneration { get; set; }
        public List<Entry> Entries { get; set; } = new();
    }

    private readonly object _gate = new();

    /// <summary>
    /// Serialises writers. A later writer takes its snapshot after acquiring
    /// this, so a slow background write can never overwrite a newer one.
    /// </summary>
    private readonly object _writeGate = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string _cachePath;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private int _libraryGeneration;
    private int _saveScheduled;

    public PlaylistMembershipCache(string? cachePath = null)
    {
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? FilePath : cachePath;
        Load();
    }

    /// <summary>Generation the entries were resolved against; see the class remarks.</summary>
    public int LibraryGeneration
    {
        get { lock (_gate) return _libraryGeneration; }
    }

    /// <summary>Number of playlists currently cached (memory only).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// The library was rebuilt (scan, file operation, download): every entry is
    /// stale from here. Persisted off the UI thread - the file can be a few
    /// megabytes when a large playlist is cached.
    /// </summary>
    public void BumpLibraryGeneration()
    {
        lock (_gate) _libraryGeneration++;
        ScheduleSave();
    }

    /// <summary>
    /// Queue one background write, coalescing a burst of bumps (a scan adds
    /// tracks one at a time) into a single file write: the delay swallows the
    /// burst, the flag swallows the rest. Nothing here is on the UI thread - the
    /// file can be a few megabytes when a large playlist is cached.
    /// </summary>
    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveCoalesceDelay).ConfigureAwait(false);
                Save();
            }
            catch (Exception ex)
            {
                Log.Warn($"Playlist cache: background save failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _saveScheduled, 0);
            }
        });
    }

    /// <summary>
    /// Membership for <paramref name="playlistId"/> when the entry was resolved
    /// against the current library generation and the current playlists.json.
    /// A hit costs one dictionary lookup: no file probes, no ledger reads.
    /// </summary>
    public bool TryGet(string playlistId, int storeRevision, long storeStamp,
        out IReadOnlyList<string> keys)
    {
        keys = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(playlistId)) return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(playlistId, out var entry)) return false;
            if (entry.LibraryGeneration != _libraryGeneration) return false;
            // Revision is a per-run counter: only meaningful for entries this run
            // wrote. The durable stamp is what validates a previous run's entry.
            if (entry.Session == _session && entry.StoreRevision != storeRevision) return false;
            if (entry.StoreStamp != storeStamp) return false;

            // Most-recently-used order only travels to disk on the next write;
            // touching the timestamp must not trigger one.
            entry.LastUsedUtc = DateTime.UtcNow;
            keys = entry.Keys;
            return true;
        }
    }

    /// <summary>Count-only form of <see cref="TryGet"/>, for the rail.</summary>
    public bool TryGetCount(string playlistId, int storeRevision, long storeStamp, out int count)
    {
        count = 0;
        if (!TryGet(playlistId, storeRevision, storeStamp, out var keys)) return false;
        count = keys.Count;
        return true;
    }

    /// <summary>Record (or replace) a playlist's resolved membership.</summary>
    /// <param name="libraryGeneration">
    /// The generation the answer was resolved against. When the library has
    /// already moved on, the entry is dropped instead of being stored under the
    /// new generation - a stale answer must never look current.
    /// </param>
    /// <param name="save">False to batch a pass and call <see cref="Save"/> once.</param>
    public void Put(string playlistId, IReadOnlyList<string> keys, int storeRevision, long storeStamp,
        int libraryGeneration, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return;

        bool stored;
        lock (_gate)
        {
            stored = libraryGeneration == _libraryGeneration;
            if (stored)
            {
                _entries[playlistId] = MakeEntry(playlistId, keys, storeRevision, storeStamp);
                TrimToLimit();
            }
        }

        if (save) Save();
    }

    /// <summary>
    /// Store a whole resolution pass with one file write instead of one per
    /// playlist (the rail warms several lists at once).
    /// </summary>
    public void PutRange(IEnumerable<(string PlaylistId, IReadOnlyList<string> Keys)> entries,
        int storeRevision, long storeStamp, int libraryGeneration)
    {
        lock (_gate)
        {
            if (libraryGeneration != _libraryGeneration) return;

            foreach (var (playlistId, keys) in entries)
            {
                if (string.IsNullOrWhiteSpace(playlistId)) continue;
                _entries[playlistId] = MakeEntry(playlistId, keys, storeRevision, storeStamp);
            }

            TrimToLimit();
        }

        Save();
    }

    private Entry MakeEntry(string playlistId, IReadOnlyList<string>? keys, int storeRevision, long storeStamp)
    {
        var copy = new List<string>(keys?.Count ?? 0);
        if (keys is not null)
        {
            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key)) copy.Add(key);
            }
        }

        return new Entry
        {
            PlaylistId = playlistId,
            Count = copy.Count,
            Keys = copy,
            LibraryGeneration = _libraryGeneration,
            StoreRevision = storeRevision,
            Session = _session,
            StoreStamp = storeStamp,
            LastUsedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Forget one playlist (deleted, or renamed away from its identity).</summary>
    public void Remove(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return;

        bool removed;
        lock (_gate) removed = _entries.Remove(playlistId);
        // Deleting a playlist is a UI action: never rewrite a megabyte-sized json
        // file on the dispatcher.
        if (removed) ScheduleSave();
    }

    /// <summary>Drop every entry but keep the generation (a test/debug seam).</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Save();
    }

    /// <summary>Least-recently-used eviction; the file cannot grow without limit.</summary>
    private void TrimToLimit()
    {
        if (_entries.Count <= MaxPlaylists) return;

        var doomed = _entries.Values
            .OrderBy(e => e.LastUsedUtc)
            .Take(_entries.Count - MaxPlaylists)
            .Select(e => e.PlaylistId)
            .ToList();

        foreach (var id in doomed) _entries.Remove(id);
    }

    /// <summary>Write the whole cache atomically. Never throws.</summary>
    public void Save()
    {
        try
        {
            lock (_writeGate)
            {
                CacheDocument doc;
                lock (_gate)
                {
                    doc = new CacheDocument
                    {
                        LibraryGeneration = _libraryGeneration,
                        // The in-memory map is already trimmed, but a caller that
                        // loaded a larger file must not rewrite it untrimmed.
                        Entries = _entries.Values
                            .OrderByDescending(e => e.LastUsedUtc)
                            .Take(MaxPlaylists)
                            .ToList(),
                    };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? AppDataDir);
                // Write-then-replace, like every other JockeyBox json file: a crash
                // mid-write cannot leave a half file the next start would trip on.
                var temp = _cachePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(doc, JsonOptions));
                File.Move(temp, _cachePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Playlist cache: could not write {Path.GetFileName(_cachePath)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Read <see cref="FilePath"/>. A missing, truncated or unparsable file is
    /// an empty cache - the first resolution fills it in - never an error and
    /// never a crash.
    /// </summary>
    private void Load()
    {
        try
        {
            var path = _cachePath;
            if (!File.Exists(path)) return;

            var doc = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), JsonOptions);
            if (doc is null || doc.Entries is null) return;

            lock (_gate)
            {
                _libraryGeneration = doc.LibraryGeneration;

                foreach (var entry in doc.Entries)
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.PlaylistId)) continue;
                    entry.Keys ??= new List<string>();
                    // A hand-edited/truncated entry must not claim a count its
                    // keys cannot produce.
                    entry.Count = entry.Keys.Count;
                    _entries[entry.PlaylistId] = entry;
                }

                TrimToLimit();
            }

            Log.Debug($"Playlist cache: {_entries.Count} playlist(s) loaded " +
                      $"(library generation {doc.LibraryGeneration}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Playlist cache unreadable, starting empty: {ex.Message}");
        }
    }
}
