using System.IO;
using JockeyBox.Library;
using JockeyBox.Sources;

namespace JockeyBox.Data;

/// <summary>
/// Immutable, in-memory view of the library, built once after a scan/import and
/// swapped in through a single volatile field. This is what serves playlist
/// switches: 5 000 membership keys become 5 000 dictionary hits, never 5 000
/// SQL round-trips.
///
/// <para>It answers the same questions <see cref="LibraryIndex"/> answers for the
/// scanner's <see cref="TrackEntry"/> list - including the artist/title scoring
/// and the "is the file still on disk?" check - but from <see cref="TrackRecord"/>
/// rows, and it adds membership lookup by normalised library key.</para>
///
/// <para>Keys accepted by <see cref="TracksForKeys"/> (all normalised the same
/// way the JSON stores did):</para>
/// <list type="bullet">
///   <item>an absolute path key (<c>LibraryKeys.PathKey</c>),</item>
///   <item>a library-relative key (<c>SourceTrack.NormalizeLocalKey</c>), matched
///   against every suffix of the stored path, so "abba/arrival/01 - dancing
///   queen" and "01 - dancing queen" both find the file,</item>
///   <item>a bare file name without extension.</item>
/// </list>
/// </summary>
public sealed class LibrarySnapshot
{
    /// <summary>An empty snapshot: every lookup misses.</summary>
    public static LibrarySnapshot Empty { get; } = new(
        Array.Empty<TrackRecord>(),
        new Dictionary<string, TrackRecord>(0, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, List<TrackRecord>>(0, StringComparer.Ordinal),
        new Dictionary<string, List<Candidate>>(0, StringComparer.Ordinal),
        new Dictionary<string, List<Candidate>>(0, StringComparer.Ordinal),
        new Dictionary<string, List<Candidate>>(0, StringComparer.Ordinal));

    /// <summary>Generation number, bumped by every <see cref="LibrarySnapshotStore.Build"/>.</summary>
    public int Generation { get; private init; }

    /// <summary>Number of indexed files.</summary>
    public int Count => _sorted.Length;

    /// <summary>True when nothing is indexed.</summary>
    public bool IsEmpty => _sorted.Length == 0;

    /// <summary>
    /// Files in display order (<c>sort_artist, sort_title, path</c>). Never
    /// mutated after construction; safe to enumerate from any thread.
    /// </summary>
    public IReadOnlyList<TrackRecord> Tracks => _sorted;

    private readonly TrackRecord[] _sorted;
    private readonly Dictionary<string, TrackRecord> _byPathKey;
    private readonly Dictionary<string, List<TrackRecord>> _byLocalKey;
    private readonly Dictionary<string, List<Candidate>> _byArtistTitle;
    private readonly Dictionary<string, List<Candidate>> _byTitle;
    private readonly Dictionary<string, List<Candidate>> _byFileName;

    private LibrarySnapshot(
        TrackRecord[] sorted,
        Dictionary<string, TrackRecord> byPathKey,
        Dictionary<string, List<TrackRecord>> byLocalKey,
        Dictionary<string, List<Candidate>> byArtistTitle,
        Dictionary<string, List<Candidate>> byTitle,
        Dictionary<string, List<Candidate>> byFileName,
        int generation = 0)
    {
        _sorted = sorted;
        _byPathKey = byPathKey;
        _byLocalKey = byLocalKey;
        _byArtistTitle = byArtistTitle;
        _byTitle = byTitle;
        _byFileName = byFileName;
        Generation = generation;
    }

    /// <summary>
    /// Build a snapshot from rows. Missing rows are skipped unless
    /// <paramref name="includeMissing"/> is set (a missing track is still
    /// playable metadata for the browser, but not a resolution target).
    /// </summary>
    public static LibrarySnapshot Build(IEnumerable<TrackRecord> tracks, bool includeMissing = false, int generation = 0)
    {
        var rows = new List<TrackRecord>(1024);
        var byPathKey = new Dictionary<string, TrackRecord>(1024, StringComparer.OrdinalIgnoreCase);
        var byLocalKey = new Dictionary<string, List<TrackRecord>>(2048, StringComparer.Ordinal);
        var byArtistTitle = new Dictionary<string, List<Candidate>>(1024, StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<Candidate>>(1024, StringComparer.Ordinal);
        var byFileName = new Dictionary<string, List<Candidate>>(1024, StringComparer.Ordinal);

        foreach (var source in tracks)
        {
            var track = source;
            if (track is null) continue;
            if (track.IsMissing && !includeMissing) continue;
            if (string.IsNullOrWhiteSpace(track.Path)) continue;

            // Rows may arrive in storage form ('/') from a hand-built import or
            // from a test; every path this snapshot hands out (Tracks,
            // TracksForKeys, FindByPath, Resolve) must be in native form.
            track = track with { Path = LibraryKeys.NativePath(track.Path) };

            if (!byPathKey.TryAdd(track.PathKey, track)) continue;

            rows.Add(track);

            // Membership keys: every suffix of the stored path, extension-stripped.
            foreach (var key in PathSuffixKeys(track.Path))
            {
                if (!byLocalKey.TryGetValue(key, out var bucket))
                {
                    bucket = new List<TrackRecord>(1);
                    byLocalKey[key] = bucket;
                }

                bucket.Add(track);
            }

            var artistKey = LibraryKeys.MatchKey(track.Artist);
            var titleKey = LibraryKeys.MatchKey(track.Title);

            // Untagged files fall back to the file name, exactly like LibraryIndex.
            if (titleKey.Length == 0) titleKey = LibraryKeys.MatchKey(track.FileName);

            // Native separators: this is the path Resolve hands back and probes
            // with File.Exists, so it must match what the rest of the app keys on.
            var candidate = new Candidate(track.Path, artistKey, titleKey, track.DurationMs);

            if (artistKey.Length > 0 && titleKey.Length > 0)
                Add(byArtistTitle, artistKey + "|" + titleKey, candidate);

            if (titleKey.Length > 0)
                Add(byTitle, titleKey, candidate);

            foreach (var nameKey in LibraryKeys.FileNameKeys(track.Path))
                Add(byFileName, nameKey, candidate);
        }

        var sorted = rows
            .OrderBy(t => t.SortArtist, StringComparer.Ordinal)
            .ThenBy(t => t.SortTitle, StringComparer.Ordinal)
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .ToArray();

        return new LibrarySnapshot(sorted, byPathKey, byLocalKey, byArtistTitle, byTitle, byFileName, generation);
    }

    // ---- membership --------------------------------------------------------

    /// <summary>
    /// Resolve playlist membership keys to rows, in the order the keys were
    /// given, skipping keys the library cannot resolve (a deleted file must not
    /// shift the rest of the playlist). Duplicate keys yield one row each.
    /// </summary>
    public IReadOnlyList<TrackRecord> TracksForKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0 || _sorted.Length == 0)
            return Array.Empty<TrackRecord>();

        var result = new List<TrackRecord>(keys.Count);
        foreach (var key in keys)
        {
            var track = FindByKey(key);
            if (track is not null) result.Add(track);
        }

        return result;
    }

    /// <summary>
    /// The row for one membership key, or null. Accepts an absolute path, a
    /// library-relative key or a bare file name (see the type remarks).
    /// </summary>
    public TrackRecord? FindByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || _sorted.Length == 0) return null;

        var pathKey = LibraryKeys.PathKey(key);
        if (_byPathKey.TryGetValue(pathKey, out var exact)) return exact;

        var localKey = LibraryKeys.LocalKey(key);
        if (localKey.Length == 0) return null;
        if (_byLocalKey.TryGetValue(localKey, out var bucket) && bucket.Count > 0) return bucket[0];

        // Windows-style absolute path handed to a Linux build (or vice versa):
        // try the trailing segments.
        var slash = localKey.IndexOf('/');
        if (slash > 0 && _byLocalKey.TryGetValue(localKey[(slash + 1)..], out var shorter) && shorter.Count > 0)
            return shorter[0];

        return null;
    }

    /// <summary>Row for an exact absolute path, or null.</summary>
    public TrackRecord? FindByPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null
        : _byPathKey.TryGetValue(LibraryKeys.PathKey(path), out var track) ? track : null;

    // ---- remote-track resolution -------------------------------------------

    /// <summary>
    /// Path of the library file for <paramref name="track"/>, or null. Identical
    /// matching order and scoring to <see cref="LibraryIndex.Resolve"/>: exact
    /// artist+title, then title alone, then the file name written as
    /// "Artist Title" - with a hit only returned while the file exists.
    /// </summary>
    public string? Resolve(SourceTrack track) => Resolve(track, requireFileExists: true);

    /// <summary>As <see cref="Resolve(SourceTrack)"/>, optionally without the disk probe.</summary>
    public string? Resolve(SourceTrack track, bool requireFileExists)
    {
        if (track is null || _sorted.Length == 0) return null;

        var artistKey = LibraryKeys.MatchKey(track.Artist);
        var titleKey = LibraryKeys.MatchKey(track.Title);
        if (titleKey.Length == 0) return null;

        // 1. Same artist and title.
        if (artistKey.Length > 0 &&
            _byArtistTitle.TryGetValue(artistKey + "|" + titleKey, out var exact) &&
            PickBest(exact, track, artistKey, titleKey, requireFileExists) is { } hit)
        {
            return hit;
        }

        // 2. Same title; the artist decides between duplicates.
        if (_byTitle.TryGetValue(titleKey, out var sameTitle) &&
            PickBest(sameTitle, track, artistKey, titleKey, requireFileExists) is { } titled)
        {
            return titled;
        }

        // 3. File name written as "Artist - Title" (tags missing or wrong).
        if (artistKey.Length > 0)
        {
            var nameKey = artistKey + " " + titleKey;
            if (_byFileName.TryGetValue(nameKey, out var byName) &&
                PickBest(byName, track, artistKey, titleKey, requireFileExists) is { } named)
            {
                return named;
            }
        }

        return null;
    }

    /// <summary>True when <see cref="Resolve(SourceTrack)"/> would find a playable file.</summary>
    public bool Contains(SourceTrack track) => Resolve(track) is not null;

    /// <summary>Every tracked path, for a "diff the disk against the snapshot" loop.</summary>
    public IReadOnlyCollection<string> PathKeys => _byPathKey.Keys;

    // ---- internals ---------------------------------------------------------

    private static string? PickBest(
        List<Candidate> candidates, SourceTrack track, string artistKey, string titleKey, bool requireFileExists)
    {
        string? bestPath = null;
        var bestScore = double.NegativeInfinity;

        foreach (var candidate in candidates)
        {
            if (requireFileExists && !File.Exists(candidate.Path)) continue;

            var score = Score(candidate, track, artistKey, titleKey);
            if (score > bestScore)
            {
                bestScore = score;
                bestPath = candidate.Path;
            }
        }

        return bestPath;
    }

    /// <summary>
    /// 0..1 confidence that a candidate is the wanted recording; same weights as
    /// <see cref="LibraryIndex"/> (title 0.45, artist 0.30, duration 0.25).
    /// </summary>
    internal static double Score(in Candidate candidate, SourceTrack track, string artistKey, string titleKey)
    {
        var titleScore = candidate.TitleKey == titleKey
            ? 1.0
            : SourceMatcher.Similarity(candidate.TitleKey, titleKey);

        double artistScore;
        if (artistKey.Length == 0 || candidate.ArtistKey.Length == 0)
        {
            artistScore = 0.5;
        }
        else if (candidate.ArtistKey == artistKey)
        {
            artistScore = 1.0;
        }
        else if (SourceMatcher.ContainsPhrase(candidate.ArtistKey, artistKey) ||
                 SourceMatcher.ContainsPhrase(artistKey, candidate.ArtistKey))
        {
            artistScore = 0.85;
        }
        else
        {
            artistScore = SourceMatcher.Similarity(candidate.ArtistKey, artistKey);
        }

        var durationScore = 0.5;
        if (track.DurationMs > 0 && candidate.DurationMs > 0)
        {
            var delta = Math.Abs(candidate.DurationMs - track.DurationMs) / 1000.0;
            durationScore = delta switch
            {
                <= 3 => 1.0,
                <= 10 => 0.9,
                <= 30 => 0.7,
                <= 60 => 0.5,
                _ => 0.2,
            };
        }

        return 0.45 * titleScore + 0.30 * artistScore + 0.25 * durationScore;
    }

    /// <summary>Every suffix of the normalised path at a segment boundary, extension stripped.</summary>
    private static IEnumerable<string> PathSuffixKeys(string path)
    {
        var normal = LibraryKeys.NormalizePath(path);
        if (normal.Length == 0) yield break;

        // "a/b/song.mp3" -> strip the audio extension the way playlist keys do.
        var stripped = SourceTrack.NormalizeLocalKey(normal);

        var key = stripped;
        while (key.Length > 0)
        {
            yield return key;

            var slash = key.IndexOf('/');
            if (slash < 0) yield break;
            key = key[(slash + 1)..];
        }
    }

    private static void Add(Dictionary<string, List<Candidate>> map, string key, in Candidate candidate)
    {
        if (map.TryGetValue(key, out var list))
        {
            list.Add(candidate);
            return;
        }

        map[key] = new List<Candidate>(1) { candidate };
    }

    /// <summary>One indexed music file, with the normalised keys used for scoring.</summary>
    internal readonly record struct Candidate(
        string Path, string ArtistKey, string TitleKey, double DurationMs);
}

/// <summary>
/// Holds the current <see cref="LibrarySnapshot"/> and swaps it atomically, so a
/// reader always sees a whole snapshot (never a half-built dictionary) and a
/// rebuild never blocks the UI thread.
/// </summary>
public sealed class LibrarySnapshotStore
{
    private volatile LibrarySnapshot _current = LibrarySnapshot.Empty;
    private int _generation;

    /// <summary>The snapshot in force right now. Never null.</summary>
    public LibrarySnapshot Current => _current;

    /// <summary>Convenience read-through, so callers do not reach into <see cref="Current"/>.</summary>
    public IReadOnlyList<TrackRecord> TracksForKeys(IReadOnlyList<string> keys) =>
        _current.TracksForKeys(keys);

    /// <summary>Build from rows and publish. Returns the new snapshot.</summary>
    public LibrarySnapshot Build(IEnumerable<TrackRecord> tracks, bool includeMissing = false)
    {
        var generation = Interlocked.Increment(ref _generation);
        var snapshot = LibrarySnapshot.Build(tracks, includeMissing, generation);
        _current = snapshot;
        return snapshot;
    }

    /// <summary>Load every row from the database and publish it (do this off the UI thread).</summary>
    public LibrarySnapshot BuildFrom(TrackRepository repository, bool includeMissing = false)
    {
        if (repository is null) throw new ArgumentNullException(nameof(repository));
        return Build(repository.All(includeMissing), includeMissing);
    }

    /// <summary>Publish an already-built snapshot (e.g. one built by the importer).</summary>
    public void Publish(LibrarySnapshot snapshot)
    {
        if (snapshot is not null) _current = snapshot;
    }

    /// <summary>Drop back to empty (library root changed, scan cancelled).</summary>
    public void Clear() => _current = LibrarySnapshot.Empty;
}
