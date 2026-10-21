using System.IO;
using System.Text.Json;
using JockeyBox.Library;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Downloads;

/// <summary>
/// One downloaded track: the remote identity it was fetched for, where it landed
/// in the library, and when it was added.
/// </summary>
/// <param name="SourceKey">Stable remote identity (Spotify track id / YouTube video id).</param>
/// <param name="Path">Absolute path of the downloaded file.</param>
/// <param name="Title">Track title as written to the file.</param>
/// <param name="Artist">Track artist as written to the file.</param>
/// <param name="AddedUtc">When the entry was first recorded.</param>
public sealed record LedgerEntry(
    string SourceKey,
    string Path,
    string Title,
    string Artist,
    DateTime AddedUtc);

/// <summary>
/// Persistent record of what has already been downloaded, stored as JSON in
/// %APPDATA%\JockeyBox\downloads\ledger.json.
///
/// Two lookups matter in practice: by remote id (so the same Spotify track is
/// never fetched twice) and by <see cref="SourceMatcher.LibraryKey"/> (so a
/// YouTube search hit that is already in the library is recognised even when its
/// remote id differs). A missing or corrupt file is treated as an empty ledger;
/// writes are atomic so a crash cannot truncate it.
/// </summary>
public class LibraryLedger
{
    private const string FileName = "ledger.json";
    private const string DirectoryName = "downloads";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>%APPDATA%\JockeyBox\downloads\ledger.json</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JockeyBox", DirectoryName, FileName);

    /// <summary>Load the ledger from disk; a missing or corrupt file yields an empty ledger.</summary>
    public LibraryLedger()
    {
        Load();
    }

    /// <summary>Snapshot of every entry, safe to enumerate while other threads mutate the ledger.</summary>
    public IReadOnlyCollection<LedgerEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.ToArray();
            }
        }
    }

    /// <summary>Entry stored for a remote id, or null when it was never downloaded.</summary>
    public LedgerEntry? TryGet(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return null;

        lock (_gate)
        {
            return _entries.TryGetValue(sourceKey.Trim(), out var entry) ? entry : null;
        }
    }

    /// <summary>Entry matching artist + title after normalisation, or null.</summary>
    public LedgerEntry? FindByLibraryKey(string artist, string title)
    {
        var wanted = SourceMatcher.LibraryKey(artist, title);

        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (SourceMatcher.LibraryKey(entry.Artist, entry.Title) == wanted) return entry;
            }
        }

        return null;
    }

    /// <summary>Path for artist + title, but only when the file is still on disk.</summary>
    public string? FindPathByLibraryKey(string artist, string title)
    {
        var entry = FindByLibraryKey(artist, title);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) return null;

        try
        {
            return File.Exists(entry.Path) ? entry.Path : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not probe ledger path '{entry.Path}'", ex);
            return null;
        }
    }

    /// <summary>
    /// Insert or update the entry for <paramref name="sourceKey"/> (case-insensitive)
    /// and persist. The original <see cref="LedgerEntry.AddedUtc"/> is kept on update.
    /// </summary>
    public void Put(string sourceKey, string path, string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            Log.Warn("LibraryLedger.Put called without a source key; ignored");
            return;
        }

        var key = sourceKey.Trim();

        lock (_gate)
        {
            var added = _entries.TryGetValue(key, out var existing) ? existing.AddedUtc : DateTime.UtcNow;
            _entries[key] = new LedgerEntry(key, path ?? "", title ?? "", artist ?? "", added);
            Save();
        }
    }

    /// <summary>Drop the entry for a remote id. Returns true when something was removed.</summary>
    public bool Remove(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;

        lock (_gate)
        {
            if (!_entries.Remove(sourceKey.Trim())) return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Record ledger entries for source tracks that <paramref name="index"/>
    /// already resolves to a file on disk. This heals the ledger for a library
    /// that predates the app (or was filled by hand), so the queue stops
    /// re-downloading files it has never heard of.
    ///
    /// Cheap and idempotent: tracks whose key is already recorded with a live
    /// file are skipped, one save happens at most, and nothing is written when
    /// there is nothing new. Returns the number of entries added.
    /// </summary>
    public int SeedFromIndex(LibraryIndex index, IEnumerable<SourceTrack> tracks)
    {
        if (index is null || tracks is null) return 0;

        List<(string Key, string Path, string Title, string Artist)> pending = new();

        lock (_gate)
        {
            // Paths already recorded, so one file never gets a second key.
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entries.Values)
            {
                if (!string.IsNullOrWhiteSpace(entry.Path)) knownPaths.Add(entry.Path);
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks)
            {
                if (track is null || string.IsNullOrWhiteSpace(track.Id)) continue;

                // The queue keys the ledger on the remote id.
                var key = track.Id.Trim();
                if (!seenKeys.Add(key)) continue;

                // A recorded entry with a live file already covers this track.
                if (_entries.TryGetValue(key, out var existing) && File.Exists(existing.Path))
                    continue;

                var path = index.Resolve(track);
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!knownPaths.Add(path)) continue;

                pending.Add((key, path, track.Title ?? "", track.Artist ?? ""));
            }

            if (pending.Count == 0) return 0;

            var now = DateTime.UtcNow;
            var added = 0;
            foreach (var (key, path, title, artist) in pending)
            {
                var addedUtc = _entries.TryGetValue(key, out var prior) ? prior.AddedUtc : now;
                _entries[key] = new LedgerEntry(key, path, title, artist, addedUtc);
                added++;
            }

            Log.Info($"LibraryLedger seeded {added} entr{(added == 1 ? "y" : "ies")} from the library index");
            Save();
            return added;
        }
    }

    /// <summary>Forget entries whose file no longer exists, then persist.</summary>
    public void PruneMissingFiles()
    {
        lock (_gate)
        {
            var missing = new List<string>();

            foreach (var (key, entry) in _entries)
            {
                try
                {
                    if (!File.Exists(entry.Path)) missing.Add(key);
                }
                catch (Exception ex)
                {
                    // Unreachable path (bad drive, permissions) - keep the entry rather than lose history.
                    Log.Error($"Could not probe ledger path '{entry.Path}'; keeping entry", ex);
                }
            }

            foreach (var key in missing) _entries.Remove(key);

            if (missing.Count > 0) Log.Info($"LibraryLedger pruned {missing.Count} missing file(s)");

            Save();
        }
    }

    /// <summary>
    /// Write the ledger to disk atomically ("ledger.json.tmp" then move over the
    /// real file). Failures are logged and swallowed - a lost ledger only costs a
    /// re-check, never a crash.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(_entries.Values.ToList(), JsonOptions);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save the download ledger to '{FilePath}'", ex);
            }
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var loaded = JsonSerializer.Deserialize<List<LedgerEntry>>(json, JsonOptions);
                if (loaded is null) return;

                foreach (var entry in loaded)
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.SourceKey)) continue;
                    _entries[entry.SourceKey.Trim()] = entry;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read the download ledger at '{FilePath}'; starting empty", ex);
                _entries.Clear();
            }
        }
    }
}
