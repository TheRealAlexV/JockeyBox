using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JockeyBox.Sources;

/// <summary>
/// Matching helpers used to decide whether a YouTube search hit is actually the
/// track a Spotify entry names, and whether a track is already in the library.
///
/// YouTube titles are hostile: "Artist - Title (Official Video) [4K Remaster]",
/// "Artist - Title (Lyrics)", "Artist - Title ft. Someone (prod. by ...)". So the
/// comparison strips bracketed noise, common video suffixes and punctuation
/// before scoring, and requires the duration to be close.
/// </summary>
public static partial class SourceMatcher
{
    [GeneratedRegex(@"\((?:[^()]*)\)|\[[^\[\]]*\]", RegexOptions.Compiled)]
    private static partial Regex BracketNoise();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex NonAlnum();

    private static readonly string[] VideoNoise =
    {
        "official video", "official music video", "official audio", "official lyric video",
        "lyric video", "lyrics", "hd", "hq", "4k", "8k", "remaster", "remastered",
        "audio", "video", "visualizer", "m v", "mv", "explicit", "clean", "prod",
        "topic", "full song", "full video",
    };

    /// <summary>Lowercase, strip bracketed noise and punctuation, collapse whitespace.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.ToLowerInvariant();
        s = BracketNoise().Replace(s, " ");
        foreach (var noise in VideoNoise)
            s = s.Replace(noise, " ");
        s = NonAlnum().Replace(s, " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>Drops a leading "Artist - " prefix, which YouTube titles carry.</summary>
    public static string StripArtistPrefix(string title, string artist)
    {
        var t = title.Trim();
        var a = artist.Trim();
        if (a.Length == 0) return t;

        foreach (var separator in new[] { " - ", " \u2013 ", " \u2014 ", ": " })
        {
            var idx = t.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (idx <= 0) continue;
            var head = t[..idx];
            if (Normalize(head) == Normalize(a) || Normalize(head).Contains(Normalize(a)))
                return t[(idx + separator.Length)..];
        }
        return t;
    }

    /// <summary>Token-overlap similarity in 0..1, insensitive to word order.</summary>
    public static double Similarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;

        var ta = na.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var overlap = ta.Count(tb.Contains);
        var jaccard = overlap / (double)(ta.Count + tb.Count - overlap);
        // Containment matters more than Jaccard for "song" vs "song extended mix".
        var containment = overlap / (double)Math.Min(ta.Count, tb.Count);
        return Math.Max(jaccard, containment * 0.95);
    }

    /// <summary>Words that mark a video as something other than the released recording.</summary>
    private static readonly string[] JunkMarkers =
    {
        "extended", "remix", "remixed", "live", "cover", "karaoke", "reaction",
        "rehearsal", "instrumental", "8d", "slowed", "reverb", "nightcore",
        "sped up", "loop", "1 hour", "ten hours", "hour version", "mashup",
        "tutorial", "lesson", "bass boosted", "acapella", "stem", "ai cover",
        "grammys", "grammy", "concert", "performance", "teaser", "trailer",
        "behind the scenes", "interview", "making of", "fan made", "fanmade",
    };

    /// <summary>
    /// True when <paramref name="wanted"/> appears as a whole-token phrase inside
    /// <paramref name="candidate"/>. Music uploads routinely append features,
    /// remaster tags and uploader names, so phrase containment - not Jaccard
    /// overlap - is what identifies "the same song".
    /// </summary>
    public static bool ContainsPhrase(string candidate, string wanted)
    {
        var nc = Normalize(candidate);
        var nw = Normalize(wanted);
        if (nw.Length == 0 || nc.Length == 0) return false;
        if (nc == nw) return true;
        return $" {nc} ".Contains($" {nw} ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Score a YouTube candidate against a track, 0..1.
    ///
    /// Weighting reflects what identifies a recording in practice:
    /// title phrase 50%, uploader/artist authority 35%, duration 15%. Extra
    /// title words are ignored (a feature credit must not sink an exact match),
    /// while a duration mismatch is a soft signal rather than a veto - the album
    /// and radio edits of one song differ by minutes but are both correct.
    /// </summary>
    public static (double Score, bool DurationOk) ScoreCandidate(
        SourceTrack track, string candidateTitle, string? channel, TimeSpan candidateDuration)
    {
        var wantedTitle = Normalize(track.Title);
        var stripped = StripArtistPrefix(candidateTitle, track.Artist);

        // 1. Title: phrase containment on either the raw or artist-stripped title.
        double titleScore = 0;
        if (ContainsPhrase(candidateTitle, track.Title) || ContainsPhrase(stripped, track.Title))
            titleScore = 1.0;
        else
            titleScore = Math.Max(Similarity(wantedTitle, candidateTitle),
                                  Similarity(wantedTitle, stripped));

        // 2. Uploader authority: "Artist - Topic" (YouTube Music's official
        //    auto-upload) and the artist's own channel are the released audio;
        //    the artist appearing only in the title is a re-upload.
        var nc = Normalize(channel);
        var na = Normalize(track.Artist);
        double artistSignal;
        if (nc.Length == 0) artistSignal = 0.3;
        else if (nc.Contains("topic")) artistSignal = 1.0;
        else if (na.Length > 0 && (nc == na || nc.Contains(na))) artistSignal = 1.0;
        else if (na.Length > 0 && ContainsPhrase(candidateTitle, track.Artist)) artistSignal = 0.7;
        else artistSignal = 0.3;

        // 3. Duration: continuous, tolerant of radio-edit vs album-length.
        var durationOk = track.DurationMs <= 0 || candidateDuration <= TimeSpan.Zero
            || Math.Abs((candidateDuration - track.Duration).TotalSeconds) <= 10;

        double durationScore;
        if (track.DurationMs <= 0 || candidateDuration <= TimeSpan.Zero)
        {
            durationScore = 0.5;
            durationOk = true;
        }
        else
        {
            var delta = Math.Abs((candidateDuration - track.Duration).TotalSeconds);
            durationScore = delta switch
            {
                <= 5 => 1.0,
                <= 15 => 0.9,
                <= 30 => 0.75,
                <= 60 => 0.6,
                <= 120 => 0.45,
                _ => 0.25,
            };
        }

        var score = 0.50 * titleScore + 0.35 * artistSignal + 0.15 * durationScore;

        // 4. Penalise uploads that are demonstrably a different artefact.
        var searchable = Normalize($"{candidateTitle} {channel}");
        if (JunkMarkers.Any(m => searchable.Contains(m, StringComparison.Ordinal)))
            score -= 0.15;

        return (Math.Clamp(score, 0, 1), durationOk);
    }

    /// <summary>Library dedupe key: artist + title, normalised.</summary>
    public static string LibraryKey(string? artist, string? title) =>
        $"{Normalize(artist)}|{Normalize(title)}";

    /// <summary>Turn a title into a safe Windows filename fragment.</summary>
    public static string SanitizeFileName(string name, int maxLength = 120)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);

        var clean = sb.ToString().Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "track";
        if (clean.Length > maxLength) clean = clean[..maxLength].TrimEnd();

        // Windows reserves these regardless of extension.
        var reserved = new[]
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
            "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
        if (reserved.Contains(clean, StringComparer.OrdinalIgnoreCase)) clean += "_";
        return clean;
    }

    /// <summary>Zero-padded track number for the album layout ("03 Title").</summary>
    public static string TrackNumber(int index) => (index + 1).ToString("00", CultureInfo.InvariantCulture);
}
