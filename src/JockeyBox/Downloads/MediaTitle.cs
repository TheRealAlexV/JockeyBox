using System.Text.RegularExpressions;

namespace JockeyBox.Downloads;

/// <summary>
/// Turns a raw video title into something worth putting on disk and in ID3
/// tags. YouTube titles are built for clicks, not for libraries:
///
///   "Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams, Nile Rodgers"
///
/// becomes artist "Daft Punk", title "Get Lucky". Spotify metadata is already
/// clean, so nothing here runs for it.
/// </summary>
public static partial class MediaTitle
{
    /// <summary>
    /// Bracketed chunks that only describe the upload rather than the song.
    /// Matched case-insensitively anywhere inside (…) or […].
    /// </summary>
    private static readonly string[] NoiseKeywords =
    {
        "official", "audio", "video", "lyric", "lyrics", "visualizer", "visualiser",
        "hd", "hq", "4k", "8k", "1080p", "720p", "remaster", "remastered",
        "explicit", "clean version", "m/v", "mv", "music video", "clip officiel",
        "audio only", "with lyrics", "full song", "color coded",
    };

    /// <summary>Everything from "ft."/"feat."/"featuring" to the end of the string.</summary>
    [GeneratedRegex(@"\s*[\(\[]?\s*\b(?:ft|feat|featuring)\b\.?\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeaturedTail();

    /// <summary>Bracketed or parenthesised groups, e.g. "(Official Audio)".</summary>
    [GeneratedRegex(@"\s*[\(\[]([^\)\]]*)[\)\]]",
        RegexOptions.CultureInvariant)]
    private static partial Regex BracketedGroup();

    /// <summary>"Artist - Title" / "Artist – Title" / "Artist — Title".</summary>
    [GeneratedRegex(@"^\s*(?<left>[^-–—]{1,80}?)\s*[-–—]\s*(?<right>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DashSplit();

    /// <summary>
    /// Clean the title and, when it names the performer, extract the artist too.
    /// An empty <paramref name="artist"/> lets the title supply one.
    /// </summary>
    /// <returns>The cleaned title and the artist to tag with (never null, may be empty).</returns>
    public static (string Title, string Artist) Clean(string? rawTitle, string? artist)
    {
        var title = (rawTitle ?? "").Trim();
        if (title.Length == 0) return ("", (artist ?? "").Trim());

        var knownArtist = (artist ?? "").Trim();
        var working = title;

        // 1. "Daft Punk - Get Lucky (Official Audio)" -> artist, title
        var split = DashSplit().Match(working);
        if (split.Success)
        {
            var left = split.Groups["left"].Value.Trim();
            var right = split.Groups["right"].Value.Trim();

            var leftIsArtist = knownArtist.Length == 0
                || left.Equals(knownArtist, StringComparison.OrdinalIgnoreCase)
                || knownArtist.Contains(left, StringComparison.OrdinalIgnoreCase)
                || left.Contains(knownArtist, StringComparison.OrdinalIgnoreCase);

            // Only trust the split when the right side still has real words in
            // it: "Song - Part 2" must not become artist "Song".
            if (leftIsArtist && right.Length > 1)
            {
                if (knownArtist.Length == 0) knownArtist = left;
                working = right;
            }
        }

        // 2. Drop the featured-artist tail.
        working = FeaturedTail().Replace(working, "");

        // 3. Drop upload-noise brackets; keep brackets that say something about
        //    the song itself (e.g. "(Acoustic)", "(Live at Wembley)").
        working = BracketedGroup().Replace(working, match =>
        {
            var inner = match.Groups[1].Value;
            var isNoise = NoiseKeywords.Any(k => inner.Contains(k, StringComparison.OrdinalIgnoreCase));
            return isNoise ? "" : match.Value;
        });

        working = CollapseSpaces(working).Trim(' ', '-', '–', '—', ',', '.', '_', '|', '/');
        if (working.Length == 0) working = title; // never ship an empty title

        return (working, CollapseSpaces(knownArtist).Trim());
    }

    /// <summary>Shorten an over-long tag/filename body without cutting a word in half.</summary>
    public static string Clamp(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        var cut = value[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', '-', '_');
    }

    private static string CollapseSpaces(string value) =>
        Regex.Replace(value, @"\s{2,}", " ").Trim();
}
