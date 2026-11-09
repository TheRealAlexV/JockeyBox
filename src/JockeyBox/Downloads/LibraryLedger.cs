using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Library;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Downloads;

/// <summary>
/// One downloaded track: the remote identity it was fetched for, where it landed
/// in the library, when it was added, and which playlist it was downloaded from.
/// </summary>
/// <param name="SourceKey">Stable remote identity (Spotify track id / YouTube video id).</param>
/// <param name="Path">Absolute path of the downloaded file.</param>
/// <param name="Title">Track title as written to the file.</param>
/// <param name="Artist">Track artist as written to the file.</param>
/// <param name="AddedUtc">When the entry was first recorded.</param>
/// <param name="PlaylistName">
/// Playlist the download was started from, or null when unknown. Null is the
/// honest answer for files downloaded before provenance existed - a source that
/// was never recorded is never invented.
/// </param>
/// <param name="PlaylistSource">Which service that playlist belonged to; null when unknown.</param>
public sealed record LedgerEntry(
    string SourceKey,
    string Path,
    string Title,
    string Artist,
    DateTime AddedUtc,
    string? PlaylistName = null,
    MediaSource? PlaylistSource = null)
{
    /// <summary>True when this file remembers which playlist it came from.</summary>
    [JsonIgnore]
    public bool HasPlaylistProvenance => !string.IsNullOrWhiteSpace(PlaylistName);
}

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Absent provenance means "unknown", so do not write nulls for it.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>%APPDATA%\JockeyBox\downloads\ledger.json</summary>
    public static string FilePath { get; } = Path.Combine(AppPaths.Home, DirectoryName, FileName);

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

    /// <summary>Entry pointing at <paramref name="path"/> (case-insensitive), or null.</summary>
    public LedgerEntry? FindByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (PathMatches(entry.Path, path.Trim())) return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Every entry whose recorded provenance names <paramref name="playlistName"/>,
    /// whatever service it was downloaded through. Only for playlists that carry no
    /// service of their own (a hand-built list in the YouTube tab): there the name
    /// is the only identity the ledger and the list share, and no other list claims
    /// it. Service-imported lists must use <see cref="FindByPlaylist"/>.
    /// </summary>
    public IReadOnlyList<LedgerEntry> FindByPlaylistName(string playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return Array.Empty<LedgerEntry>();

        var wanted = playlistName.Trim();

        lock (_gate)
        {
            return _entries.Values
                .Where(e => e.PlaylistSource is MediaSource.Spotify or MediaSource.YouTube
                            && string.Equals((e.PlaylistName ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    /// <summary>
    /// Every entry whose recorded provenance names <paramref name="playlistName"/>
    /// on <paramref name="source"/>. This is the honest answer for an imported
    /// playlist whose remote rows are gone: the files still remember where they
    /// came from.
    /// </summary>
    public IReadOnlyList<LedgerEntry> FindByPlaylist(string playlistName, MediaSource source)
    {
        if (string.IsNullOrWhiteSpace(playlistName)) return Array.Empty<LedgerEntry>();

        var wanted = playlistName.Trim();

        lock (_gate)
        {
            return _entries.Values
                .Where(e => e.PlaylistSource == source
                            && string.Equals((e.PlaylistName ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    /// <summary>
    /// Insert or update the entry for <paramref name="sourceKey"/> (case-insensitive)
    /// and persist. The original <see cref="LedgerEntry.AddedUtc"/> is kept on update.
    /// Provenance follows the download that wrote the file: a later put without a
    /// playlist name keeps the provenance already recorded rather than erasing it.
    /// </summary>
    public void Put(
        string sourceKey,
        string path,
        string title,
        string artist,
        string? playlistName = null,
        MediaSource? playlistSource = null)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            Log.Warn("LibraryLedger.Put called without a source key; ignored");
            return;
        }

        var key = sourceKey.Trim();

        lock (_gate)
        {
            var hasPrior = _entries.TryGetValue(key, out var existing);
            var added = hasPrior ? existing!.AddedUtc : DateTime.UtcNow;

            var name = string.IsNullOrWhiteSpace(playlistName)
                ? existing?.PlaylistName
                : playlistName.Trim();
            // Provenance is independent of the playlist *name*: a single video
            // downloaded outside any playlist still came from YouTube (or
            // Spotify), and that is the only evidence the Downloaded view has.
            // A put that knows no source keeps whatever was recorded before.
            var source = playlistSource ?? existing?.PlaylistSource;

            _entries[key] = new LedgerEntry(key, path ?? "", title ?? "", artist ?? "", added, name, source);
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
    /// Drop every entry pointing at <paramref name="path"/> (case-insensitive),
    /// used when a downloaded file is deleted from disk. Returns true when
    /// something was removed.
    /// </summary>
    public bool RemoveByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var wanted = path.Trim();

        lock (_gate)
        {
            var keys = _entries
                .Where(pair => PathMatches(pair.Value.Path, wanted))
                .Select(pair => pair.Key)
                .ToList();

            if (keys.Count == 0) return false;

            foreach (var key in keys) _entries.Remove(key);
            Log.Info($"LibraryLedger forgot {keys.Count} entr{(keys.Count == 1 ? "y" : "ies")} for '{wanted}'");
            Save();
            return true;
        }
    }

    /// <summary>
    /// Repoint every entry recorded at <paramref name="oldPath"/> to
    /// <paramref name="newPath"/>, keeping its remote identity, metadata,
    /// timestamp and playlist provenance. Used after a library file was renamed or
    /// moved. Returns how many entries changed; nothing is written when none did,
    /// so the call is idempotent.
    /// </summary>
    public int RepointPath(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return 0;

        lock (_gate)
        {
            var keys = _entries
                .Where(pair => PathMatches(pair.Value.Path, oldPath.Trim()))
                .Select(pair => pair.Key)
                .ToList();

            if (keys.Count == 0) return 0;

            foreach (var key in keys)
                _entries[key] = _entries[key] with { Path = newPath.Trim() };

            Save();
            return keys.Count;
        }
    }

    /// <summary>Case-insensitive path comparison, tolerating relative vs absolute forms.</summary>
    private static bool PathMatches(string? candidate, string wanted)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var trimmed = candidate.Trim();
        if (string.Equals(trimmed, wanted, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            return string.Equals(
                Path.GetFullPath(trimmed), Path.GetFullPath(wanted), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unusable path can only be compared literally, which already failed.
            return false;
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
                var prior = _entries.TryGetValue(key, out var recorded) ? recorded : null;
                _entries[key] = new LedgerEntry(
                    key, path, title, artist, prior?.AddedUtc ?? now, prior?.PlaylistName, prior?.PlaylistSource);
                added++;
            }

            Log.Info($"LibraryLedger seeded {added} entr{(added == 1 ? "y" : "ies")} from the library index");
            Save();
            return added;
        }
    }

    /// <summary>
    /// Fill in provenance for entries written before the ledger had any.
    ///
    /// Two sources, both conservative:
    /// <list type="bullet">
    /// <item><paramref name="fromQueue"/> - queue.json still names the playlist for
    /// rows that were queued but not finished at upgrade time. Exact: same key.</item>
    /// <item><paramref name="fromPlaylists"/> - an imported playlist that still
    /// holds the row and can prove the file is in the library. Used only to name an
    /// entry that already exists, never to invent one, and only when the entry has
    /// no provenance yet (first playlist wins).</item>
    /// </list>
    /// Nothing is guessed from tags: title/artist are already written at download
    /// time, and a source that is not known stays null. Returns the number of
    /// entries named.
    /// </summary>
    public int BackfillProvenance(
        IReadOnlyDictionary<string, (string PlaylistName, MediaSource Source)>? fromQueue = null,
        IEnumerable<(string SourceKey, string PlaylistName, MediaSource Source)>? fromPlaylists = null)
    {
        lock (_gate)
        {
            var named = 0;

            if (fromQueue is { Count: > 0 })
            {
                foreach (var (key, hint) in fromQueue)
                {
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(hint.PlaylistName)) continue;
                    if (!_entries.TryGetValue(key.Trim(), out var entry)) continue;
                    if (entry.HasPlaylistProvenance) continue;

                    _entries[entry.SourceKey] = entry with
                    {
                        PlaylistName = hint.PlaylistName.Trim(),
                        PlaylistSource = hint.Source,
                    };
                    named++;
                }
            }

            if (fromPlaylists is not null)
            {
                foreach (var hint in fromPlaylists)
                {
                    if (string.IsNullOrWhiteSpace(hint.SourceKey) || string.IsNullOrWhiteSpace(hint.PlaylistName))
                        continue;
                    if (!_entries.TryGetValue(hint.SourceKey.Trim(), out var entry)) continue;
                    if (entry.HasPlaylistProvenance) continue;

                    _entries[entry.SourceKey] = entry with
                    {
                        PlaylistName = hint.PlaylistName.Trim(),
                        PlaylistSource = hint.Source,
                    };
                    named++;
                }
            }

            if (named > 0)
            {
                Log.Info($"LibraryLedger back-filled playlist provenance on {named} entr{(named == 1 ? "y" : "ies")}");
                Save();
            }

            return named;
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

                // A file without the provenance property was written before it
                // existed: every entry in it is a candidate for the back-fill.
                var legacy = !json.Contains("\"PlaylistName\"", StringComparison.Ordinal)
                             && !json.Contains("\"PlaylistSource\"", StringComparison.Ordinal);

                var loaded = JsonSerializer.Deserialize<List<LedgerEntry>>(json, JsonOptions);
                if (loaded is null) return;

                foreach (var entry in loaded)
                {
                    if (entry is null || string.IsNullOrWhiteSpace(entry.SourceKey)) continue;
                    _entries[entry.SourceKey.Trim()] = entry;
                }

                if (legacy && _entries.Count > 0)
                {
                    // queue.json is the only surviving record of which playlist a
                    // track that was mid-download came from; ReadPersistedProvenance
                    // never throws and returns nothing when the file is absent.
                    BackfillProvenance(Downloads.DownloadQueue.ReadPersistedProvenance());
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
