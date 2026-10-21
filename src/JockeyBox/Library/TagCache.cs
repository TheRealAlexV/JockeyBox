using System.IO;
using System.Text.Json;

namespace JockeyBox.Library;

/// <summary>Cached tag data for one file, keyed by path + size + mtime.</summary>
public class CachedTags
{
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Bpm { get; set; }
    public double DurationMs { get; set; }
}

/// <summary>
/// Persistent JSON cache of extracted tags so library rescans are instant.
/// Stored in %APPDATA%\JockeyBox\library.json.
/// </summary>
public class TagCache
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private Dictionary<string, CachedTags> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new(); // scans may overlap briefly during cancellation
    private readonly string _cachePath;

    public TagCache()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");
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
    /// Get tags for a file, reading them with TagLib# only when the file is new
    /// or has changed since the cached read.
    /// </summary>
    public CachedTags GetTags(string filePath)
    {
        var info = new FileInfo(filePath);
        lock (_lock)
        {
            if (_entries.TryGetValue(filePath, out var cached)
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
            result.DurationMs = file.Properties?.Duration.TotalMilliseconds ?? 0;
        }
        catch
        {
            // Unreadable/corrupt tags - keep the filename fallback.
        }
        return result;
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
