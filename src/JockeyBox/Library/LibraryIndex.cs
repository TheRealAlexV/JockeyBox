using System.Globalization;
using System.IO;
using System.Text;
using JockeyBox.Sources;

namespace JockeyBox.Library;

/// <summary>
/// Lookup from a remote track to the copy already sitting in the music folder.
///
/// The download ledger only knows what this app downloaded, so a library built
/// before JockeyBox (or by another tool) is invisible to it and every remote
/// track looks new. This index is built from the scanned <see cref="TrackEntry"/>
/// rows instead, so "is it already here?" is answered from what is on disk.
///
/// Matching, in order:
///   1. exact normalised "artist title" (tags),
///   2. exact normalised title alone,
///   3. the file name written as "Artist - Title".
/// A hit is only returned when the file still exists. When several files share
/// a title the best-scoring one wins (artist, then duration) - a close-but-wrong
/// playable file beats a needless re-download.
///
/// Normalisation is <see cref="SourceMatcher.Normalize"/> (case, punctuation and
/// bracket/video-noise folding) plus a diacritic fold, because metadata services
/// and file tags disagree about accents ("Beyoncé" vs "Beyonce"); ranking is
/// <see cref="SourceMatcher.Similarity"/>. Rebuild swaps in a new immutable
/// snapshot, so reads may run concurrently with a rebuild.
/// </summary>
public sealed class LibraryIndex
{
    /// <summary>An index with nothing in it: always resolves to null.</summary>
    public static LibraryIndex Empty => new();

    private volatile Snapshot _snapshot = Snapshot.Nothing;

    /// <summary>Number of library files indexed (music files only).</summary>
    public int Count => _snapshot.Count;

    /// <summary>
    /// Replace the contents with <paramref name="tracks"/>. Entries without a
    /// music file are ignored (karaoke-only rows have nothing to play).
    /// </summary>
    public void Rebuild(IEnumerable<TrackEntry> tracks)
    {
        if (tracks is null)
        {
            _snapshot = Snapshot.Nothing;
            return;
        }

        // Rough capacity: a scan is mostly music files, which keeps large
        // libraries free of dictionary resizes.
        var byArtistTitle = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        var byFileName = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var track in tracks)
        {
            if (track is null) continue;

            var path = track.MusicPath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!seenPaths.Add(path)) continue;
            count++;

            var artistKey = Key(track.Artist);
            var titleKey = Key(track.Title);

            // Untagged files fall back to the file name, exactly like the scanner does.
            if (titleKey.Length == 0)
                titleKey = Key(Path.GetFileNameWithoutExtension(path));

            var candidate = new Candidate(path, artistKey, titleKey, track.Duration.TotalMilliseconds);

            if (artistKey.Length > 0 && titleKey.Length > 0)
                Add(byArtistTitle, artistKey + "|" + titleKey, candidate);

            if (titleKey.Length > 0)
                Add(byTitle, titleKey, candidate);

            foreach (var nameKey in FileNameKeys(path))
                Add(byFileName, nameKey, candidate);
        }

        _snapshot = new Snapshot(byArtistTitle, byTitle, byFileName, count);
    }

    /// <summary>
    /// Path of the library file for <paramref name="track"/>, or null when the
    /// library has no match (or the matched file has since disappeared).
    /// </summary>
    public string? Resolve(SourceTrack track)
    {
        if (track is null) return null;

        var snapshot = _snapshot;
        if (snapshot.Count == 0) return null;

        var artistKey = Key(track.Artist);
        var titleKey = Key(track.Title);
        if (titleKey.Length == 0) return null;

        // 1. Same artist and title.
        if (artistKey.Length > 0 &&
            snapshot.ByArtistTitle.TryGetValue(artistKey + "|" + titleKey, out var exact) &&
            PickBest(exact, track, artistKey, titleKey) is { } hit)
        {
            return hit;
        }

        // 2. Same title; the artist decides between duplicates.
        if (snapshot.ByTitle.TryGetValue(titleKey, out var sameTitle) &&
            PickBest(sameTitle, track, artistKey, titleKey) is { } titled)
        {
            return titled;
        }

        // 3. File name written as "Artist - Title" (tags missing or wrong).
        if (artistKey.Length > 0)
        {
            var nameKey = artistKey + " " + titleKey;
            if (snapshot.ByFileName.TryGetValue(nameKey, out var byName) &&
                PickBest(byName, track, artistKey, titleKey) is { } named)
            {
                return named;
            }
        }

        return null;
    }

    /// <summary>True when <see cref="Resolve"/> would find a playable file.</summary>
    public bool Contains(SourceTrack track) => Resolve(track) is not null;

    /// <summary>Best existing candidate, or null when every candidate is gone from disk.</summary>
    private static string? PickBest(
        List<Candidate> candidates, SourceTrack track, string artistKey, string titleKey)
    {
        string? bestPath = null;
        var bestScore = double.NegativeInfinity;

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Path)) continue;

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
    /// 0..1 confidence that a candidate is the wanted recording. Title carries
    /// most of the weight, artist settles covers, duration settles versions
    /// (album vs radio edit, live recordings).
    /// </summary>
    private static double Score(in Candidate candidate, SourceTrack track, string artistKey, string titleKey)
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

    /// <summary>Normalised file name, plus the same name with a leading track number dropped.</summary>
    private static IEnumerable<string> FileNameKeys(string path)
    {
        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(path);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        var key = Key(name);
        if (key.Length == 0) yield break;
        yield return key;

        // "01 Artist - Title" / "12. Title" also answer to the bare name.
        var space = key.IndexOf(' ');
        if (space > 0)
        {
            var digits = true;
            for (var i = 0; i < space; i++)
            {
                if (!char.IsAsciiDigit(key[i])) { digits = false; break; }
            }

            if (digits && space + 1 < key.Length)
                yield return key[(space + 1)..];
        }
    }

    /// <summary>
    /// Lookup key: <see cref="SourceMatcher.Normalize"/> plus a diacritic fold.
    /// ASCII (the overwhelming majority of names) is returned untouched.
    /// </summary>
    private static string Key(string? text)
    {
        var normalized = SourceMatcher.Normalize(text);
        if (normalized.Length == 0) return normalized;

        var ascii = true;
        foreach (var ch in normalized)
        {
            if (ch > 0x7F) { ascii = false; break; }
        }

        if (ascii) return normalized;

        // "Beyoncé" -> "beyonce": decompose, drop the combining marks, recompose.
        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
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
    private readonly record struct Candidate(
        string Path, string ArtistKey, string TitleKey, double DurationMs);

    /// <summary>Immutable lookup tables; replaced wholesale by <see cref="Rebuild"/>.</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Nothing = new(
            new Dictionary<string, List<Candidate>>(0),
            new Dictionary<string, List<Candidate>>(0),
            new Dictionary<string, List<Candidate>>(0),
            0);

        public Snapshot(
            Dictionary<string, List<Candidate>> byArtistTitle,
            Dictionary<string, List<Candidate>> byTitle,
            Dictionary<string, List<Candidate>> byFileName,
            int count)
        {
            ByArtistTitle = byArtistTitle;
            ByTitle = byTitle;
            ByFileName = byFileName;
            Count = count;
        }

        public Dictionary<string, List<Candidate>> ByArtistTitle { get; }
        public Dictionary<string, List<Candidate>> ByTitle { get; }
        public Dictionary<string, List<Candidate>> ByFileName { get; }
        public int Count { get; }
    }
}
