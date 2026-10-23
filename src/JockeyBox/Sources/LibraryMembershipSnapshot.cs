using System.IO;
using JockeyBox.Downloads;
using JockeyBox.Library;

namespace JockeyBox.Sources;

/// <summary>
/// An immutable picture of the library, used to resolve playlist membership on a
/// worker thread.
///
/// The view model used to keep mutable dictionaries (<c>_localKeyIndex</c>,
/// <c>_pathIndex</c>, <c>_metadataIndex</c>) that were read while the UI thread
/// could clear them, and the ledger took its own lock for every row. A snapshot
/// is built once on the UI thread - the only place that may read
/// <c>Tracks</c>/<c>PlaylistStore</c> - and then read concurrently: every lookup
/// below is a plain dictionary read, so a 5,000-row resolution never blocks the
/// window and never observes a half-updated index.
///
/// It is stamped with the library generation and the store identity that
/// produced it, so the view model can keep one around and rebuild it when (and
/// only when) either changed.
/// </summary>
public sealed class LibraryMembershipSnapshot
{
    /// <summary>The library has no tracks yet: everything resolves to nothing.</summary>
    public static LibraryMembershipSnapshot Empty { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, (int, TrackEntry)>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SourceTrack>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<LedgerEntry>>(StringComparer.OrdinalIgnoreCase),
        LibraryIndex.Empty,
        0, 0, 0);

    private readonly HashSet<string> _libraryKeys;
    private readonly Dictionary<string, (int Order, TrackEntry Track)> _trackByKey;
    private readonly Dictionary<string, string> _pathIndex;
    private readonly Dictionary<string, string> _metadataIndex;
    private readonly Dictionary<string, SourceTrack> _sourceById;
    private readonly Dictionary<string, LedgerEntry> _ledgerById;
    private readonly Dictionary<string, List<LedgerEntry>> _ledgerByPlaylistName;
    private readonly LibraryIndex _libraryIndex;

    private LibraryMembershipSnapshot(
        HashSet<string> libraryKeys,
        Dictionary<string, (int Order, TrackEntry Track)> trackByKey,
        Dictionary<string, string> pathIndex,
        Dictionary<string, string> metadataIndex,
        Dictionary<string, SourceTrack> sourceById,
        Dictionary<string, LedgerEntry> ledgerById,
        Dictionary<string, List<LedgerEntry>> ledgerByPlaylistName,
        LibraryIndex libraryIndex,
        int libraryGeneration,
        int storeRevision,
        long storeStamp)
    {
        _libraryKeys = libraryKeys;
        _trackByKey = trackByKey;
        _pathIndex = pathIndex;
        _metadataIndex = metadataIndex;
        _sourceById = sourceById;
        _ledgerById = ledgerById;
        _ledgerByPlaylistName = ledgerByPlaylistName;
        _libraryIndex = libraryIndex;
        LibraryGeneration = libraryGeneration;
        StoreRevision = storeRevision;
        StoreStamp = storeStamp;
    }

    /// <summary>Generation of the library this snapshot was built from.</summary>
    public int LibraryGeneration { get; }

    /// <summary>Playlist store revision this snapshot was built against.</summary>
    public int StoreRevision { get; }

    /// <summary>Durable identity of playlists.json this snapshot was built against.</summary>
    public long StoreStamp { get; }

    /// <summary>Number of library keys indexed.</summary>
    public int LibraryKeyCount => _libraryKeys.Count;

    /// <summary>
    /// Build the indexes from the scanned tracks, the playlist rows (remote ids
    /// are matched to their <see cref="SourceTrack"/>) and the ledger. Runs on
    /// the UI thread: it is the only consumer of the mutable collections.
    /// </summary>
    public static LibraryMembershipSnapshot Build(
        IEnumerable<TrackEntry> tracks,
        IEnumerable<UserPlaylist> playlists,
        IReadOnlyCollection<LedgerEntry> ledgerEntries,
        LibraryIndex libraryIndex,
        int libraryGeneration,
        int storeRevision,
        long storeStamp)
    {
        var libraryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackByKey = new Dictionary<string, (int Order, TrackEntry Track)>(StringComparer.OrdinalIgnoreCase);
        var pathIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceById = new Dictionary<string, SourceTrack>(StringComparer.OrdinalIgnoreCase);

        if (tracks is not null)
        {
            var order = 0;
            foreach (var track in tracks)
            {
                if (track is null) continue;

                var key = SourceTrack.NormalizeLocalKey(track.RelativeKey);
                if (key.Length > 0)
                {
                    libraryKeys.Add(key);
                    // First occurrence wins and keeps the library's own order, so a
                    // playlist rebuild reads like the unfiltered tree.
                    trackByKey.TryAdd(key, (order, track));
                    if (!string.IsNullOrWhiteSpace(track.MusicPath))
                    {
                        var music = track.MusicPath.Trim();
                        pathIndex.TryAdd(music, key);
                        try { pathIndex.TryAdd(Path.GetFullPath(music), key); }
                        catch { /* malformed path: the scanner's form is enough */ }
                    }

                    // A row written before the ledger stored an absolute path can
                    // still hold the scanner's relative one.
                    var relative = (track.RelativePath ?? "").Trim();
                    if (relative.Length > 0) pathIndex.TryAdd(relative, key);
                }

                var meta = MetadataKey(track.Artist, track.Title);
                // Only a track that has a library key can answer for one: an empty
                // key here would put "" in a membership set and inflate a count.
                if (key.Length > 0 && meta.Length > 1) metadataIndex.TryAdd(meta, key);

                order++;
            }
        }

        if (playlists is not null)
        {
            foreach (var playlist in playlists)
            {
                foreach (var track in playlist.Tracks)
                {
                    if (string.IsNullOrWhiteSpace(track.Id)) continue;
                    sourceById.TryAdd(track.Id.Trim(), track);
                }
            }
        }

        var ledgerById = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
        var ledgerByPlaylistName = new Dictionary<string, List<LedgerEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ledgerEntries ?? Array.Empty<LedgerEntry>())
        {
            if (!string.IsNullOrWhiteSpace(entry.SourceKey))
                ledgerById.TryAdd(entry.SourceKey.Trim(), entry);

            if (string.IsNullOrWhiteSpace(entry.PlaylistName)) continue;
            if (!ledgerByPlaylistName.TryGetValue(entry.PlaylistName, out var list))
                ledgerByPlaylistName[entry.PlaylistName] = list = new List<LedgerEntry>(1);
            list.Add(entry);
        }

        return new LibraryMembershipSnapshot(libraryKeys, trackByKey, pathIndex, metadataIndex, sourceById,
            ledgerById, ledgerByPlaylistName, libraryIndex ?? LibraryIndex.Empty,
            libraryGeneration, storeRevision, storeStamp);
    }

    /// <summary>
    /// The library rows for <paramref name="keys"/>, in library order. This is
    /// the playlist fast path: O(rows), with no pass over the whole library and
    /// no filesystem work. Safe to call from any thread.
    /// </summary>
    public List<TrackEntry> TracksForKeys(IReadOnlyCollection<string> keys)
    {
        var found = new List<(int Order, TrackEntry Track)>(keys.Count);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (_trackByKey.TryGetValue(key, out var hit)) found.Add(hit);
        }

        found.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        var rows = new List<TrackEntry>(found.Count);
        foreach (var (_, track) in found) rows.Add(track);
        return rows;
    }

    /// <summary>
    /// Resolve one playlist to the library keys the browser can actually show.
    /// Safe to call from any thread.
    ///
    /// Internal (library file), in order:
    ///  1. the library key the row was added with (<see cref="SourceTrack.LocalKey"/>);
    ///  2. the path the file was downloaded to (survives a re-download that renamed it);
    ///  3. artist + title from the tags (survives a re-encode or a move).
    /// A row that resolves to nothing is not in the set, so the count never
    /// includes a track the browser cannot show.
    ///
    /// Imported (remote id): the ledger line for that id, falling back to the
    /// library index, unioned with the files the ledger says came from this
    /// playlist. Artist/title alone is never used for those rows - it would
    /// "find" a same-named file that never came from the playlist.
    /// </summary>
    public HashSet<string> Resolve(PlaylistMembershipRequest request, CancellationToken ct = default)
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request is null) return resolved;

        foreach (var row in request.Rows)
        {
            ct.ThrowIfCancellationRequested();

            if (request.IsImported)
            {
                var path = ResolveRemotePath(row.Id);
                if (TryKeyForPath(path, out var byId)) resolved.Add(byId);
                continue;
            }

            if (row.LocalKey is { Length: > 0 } key && _libraryKeys.Contains(key))
            {
                resolved.Add(key);
                continue;
            }

            if (TryKeyForPath(row.LibraryPath, out var byPath))
            {
                resolved.Add(byPath);
                continue;
            }

            if (_metadataIndex.TryGetValue(MetadataKey(row.Artist, row.Title), out var byMeta))
                resolved.Add(byMeta);
        }

        // Imported lists also count the files the ledger says came from them: a
        // remote row removed upstream (or never re-fetched) must not hide a
        // download sitting in the library.
        if (request.IsImported)
        {
            foreach (var key in ProvenanceTracks(request))
            {
                ct.ThrowIfCancellationRequested();
                resolved.Add(key);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Library file for a remote track id: the download ledger first (it records
    /// exactly what this app fetched, and only paths that still exist), then the
    /// scanned library. Runs on the worker thread, so the <c>File.Exists</c>
    /// probes here are import-only and never on the UI path.
    /// </summary>
    private string? ResolveRemotePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();

        if (_ledgerById.TryGetValue(trimmed, out var entry) && !string.IsNullOrWhiteSpace(entry.Path))
        {
            try
            {
                if (File.Exists(entry.Path)) return entry.Path;
            }
            catch (Exception) { /* unusable path: fall through to the index */ }
        }

        return _sourceById.TryGetValue(trimmed, out var track) ? _libraryIndex.Resolve(track) : null;
    }

    /// <summary>
    /// Library keys the ledger proves were downloaded from <paramref name="playlist"/>.
    /// A list with a service of its own must match on both name and service,
    /// otherwise two "New Sing" playlists would claim each other's files; a
    /// hand-built list (Manual) matches on the name alone.
    /// </summary>
    private IEnumerable<string> ProvenanceTracks(PlaylistMembershipRequest request)
    {
        if (request.IsInternal || string.IsNullOrWhiteSpace(request.Name)) yield break;
        if (!_ledgerByPlaylistName.TryGetValue(request.Name, out var entries)) yield break;

        foreach (var entry in entries)
        {
            if (request.Source != MediaSource.Manual && entry.PlaylistSource != request.Source) continue;

            if (TryKeyForPath(entry.Path, out var byPath))
            {
                yield return byPath;
                continue;
            }

            if (_metadataIndex.TryGetValue(MetadataKey(entry.Artist, entry.Title), out var byMeta))
                yield return byMeta;
        }
    }

    /// <summary>
    /// Map a file path to the library key it belongs to. Tries the path as the
    /// scanner recorded it, then its full form, so a ledger path and a
    /// <see cref="TrackEntry.MusicPath"/> that differ only by "./" or separators
    /// still match.
    /// </summary>
    private bool TryKeyForPath(string? path, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim();
        if (_pathIndex.TryGetValue(trimmed, out var direct))
        {
            key = direct;
            return true;
        }

        try
        {
            if (_pathIndex.TryGetValue(Path.GetFullPath(trimmed), out var full))
            {
                key = full;
                return true;
            }
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"Could not normalise library path '{trimmed}': {ex.Message}");
        }

        return false;
    }

    /// <summary>Tag identity used to match a row to a file whose name changed.</summary>
    private static string MetadataKey(string? artist, string? title) =>
        SourceTrack.NormalizeLocalKey(artist + "\u241F" + title);
}
