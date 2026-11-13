using System.IO;
using System.Text.Json;
using JockeyBox.Tools;

namespace JockeyBox.Library;

/// <summary>Cached tag data for one file, keyed by path + size + mtime.</summary>
public class CachedTags
{
    /// <summary>
    /// Version of the tag reader that filled this entry. Entries written by an
    /// older reader lack newer fields (Key), so they are re-read once instead of
    /// silently showing a blank.
    /// </summary>
    public int Version { get; set; }

    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Bpm { get; set; }

    /// <summary>Musical key exactly as the file's tag states it ("8A", "Am", "F#m"), "" when the file has none.</summary>
    public string Key { get; set; } = "";

    public double DurationMs { get; set; }
}

/// <summary>
/// Persistent JSON cache of extracted tags so library rescans are instant.
/// Stored in %APPDATA%\JockeyBox\library.json.
/// </summary>
public class TagCache
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Bump when ReadTags learns a new field, so existing cache entries are re-read.</summary>
    private const int CacheVersion = 2;

    private Dictionary<string, CachedTags> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new(); // scans may overlap briefly during cancellation
    private readonly string _cachePath;

    public TagCache()
    {
        var dir = Path.Combine(AppPaths.Home);
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "library.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedTags>>(
                    File.ReadAllText(_cachePath));
                if (loaded != null)
                    _entries = new Dictionary<string, CachedTags>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _entries = new(StringComparer.OrdinalIgnoreCase); // corrupt cache - start fresh
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_entries, JsonOpts);
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // cache is best-effort
        }
    }

    /// <summary>
    /// Re-key the cached tags of a file that was renamed or moved, so the next
    /// scan keeps its BPM/duration instead of re-reading the media. Returns false
    /// when nothing was cached for <paramref name="oldPath"/> (or the paths are
    /// equal), which makes the call idempotent. Saves once when it moved a row.
    /// </summary>
    public bool Repoint(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return false;

        lock (_lock)
        {
            if (!_entries.TryGetValue(oldPath, out var tags)) return false;
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return false;

            _entries.Remove(oldPath);
            _entries[newPath] = tags;
        }

        Save();
        return true;
    }

    /// <summary>
    /// Get tags for a file, reading them with TagLib# only when the file is new
    /// or has changed since the cached read.
    /// </summary>
    public CachedTags GetTags(string filePath)
    {
        var info = new FileInfo(filePath);
        lock (_lock)
        {
            if (_entries.TryGetValue(filePath, out var cached)
                && cached.Version == CacheVersion
                && cached.Size == info.Length
                && cached.MTimeTicks == info.LastWriteTimeUtc.Ticks)
            {
                return cached;
            }
        }

        var tags = ReadTags(filePath, info);
        lock (_lock) _entries[filePath] = tags;
        return tags;
    }

    private static CachedTags ReadTags(string filePath, FileInfo info)
    {
        var result = new CachedTags
        {
            Version = CacheVersion,
            Size = info.Length,
            MTimeTicks = info.LastWriteTimeUtc.Ticks,
            Title = Path.GetFileNameWithoutExtension(filePath),
        };

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) result.Title = tag.Title.Trim();
            result.Artist = tag.FirstPerformer?.Trim()
                            ?? tag.FirstAlbumArtist?.Trim() ?? "";
            result.Album = tag.Album?.Trim() ?? "";
            result.Bpm = tag.BeatsPerMinute;
            result.Key = tag.InitialKey?.Trim() ?? "";
            result.DurationMs = file.Properties?.Duration.TotalMilliseconds ?? 0;
        }
        catch
        {
            // Unreadable/corrupt tags - keep the filename fallback.
        }
        return result;
    }

    /// <summary>
    /// Record an analysed tempo on an entry the scan already cached, so the number
    /// lands in library.json instead of living only in a row on screen.
    ///
    /// <para>Tag first, like <see cref="TrackBpmResolver"/>: a non-zero Bpm is the
    /// file's own TBPM assertion and is never replaced by an estimate. No entry is
    /// created either - a grid for a file the library no longer has must not
    /// resurrect an entry the prune just removed.</para>
    ///
    /// <para>Returns true when the value changed, so the caller saves once instead
    /// of on every grid. The caller owns <see cref="Save"/>: the scan writes this
    /// out in the same save that prunes its vanished files.</para>
    /// </summary>
    public bool SetBpm(string path, double bpm)
    {
        if (string.IsNullOrWhiteSpace(path) || bpm <= 0) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var cached) || cached.Bpm > 0) return false;
            cached.Bpm = bpm;
            return true;
        }
    }

    /// <summary>The tempo the cache holds for a path (0 when unknown).</summary>
    public double BpmOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        lock (_lock) return _entries.TryGetValue(path, out var cached) ? cached.Bpm : 0;
    }

    /// <summary>
    /// Record an analysed key on an entry the scan already cached, the same way
    /// <see cref="SetBpm"/> records a tempo - and for the same reason: the value
    /// otherwise exists only in a row on screen.
    ///
    /// <para>Tag first: a TKEY tag (and any value already cached from one) wins,
    /// and no entry is created, so a grid for a file the prune just removed
    /// cannot resurrect it.</para>
    /// </summary>
    public bool SetKey(string path, string? key)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(key)) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var cached) || !string.IsNullOrWhiteSpace(cached.Key))
                return false;
            cached.Key = key.Trim();
            return true;
        }
    }

    /// <summary>Drop cache entries for files that no longer exist in the library.</summary>
    public void Prune(IEnumerable<string> livingPaths)
    {
        var keep = new HashSet<string>(livingPaths, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var stale in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
                _entries.Remove(stale);
        }
    }
}
