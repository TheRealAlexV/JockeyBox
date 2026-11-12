using System.IO;

namespace JockeyBox.Library;

/// <summary>
/// The rule for a vocals-only render: "Artist\Album\Song.vocals.mp3" beside
/// "Artist\Album\Song.mp3".
///
/// It is deliberately <b>not</b> the karaoke marker (see
/// <see cref="KaraokeNaming.VocalsMarker"/> and <see cref="KaraokeNaming.Marker"/>).
/// A vocals-only file is played on its own deck as a track in its own right
/// (reverse karaoke), so it must never become the <see cref="TrackEntry.KaraokePath"/>
/// of its source - a karaoke pair loaded with a vocals file on deck A would play
/// the vocals as if they were the instrumental.
///
/// Everything that has to answer "is this a derived vocals file?" asks here, so
/// the scanner and the row rules cannot disagree.
/// </summary>
public static class DerivedVocals
{
    /// <summary>Title suffix the vocals row carries, so it sorts beside its source.</summary>
    public const string TitleSuffix = " (Vocals)";

    /// <summary>
    /// True when the path names a vocals-only render ("Song.vocals.mp3"). A bare
    /// ".vocals.mp3" has no source song, so it stays an ordinary unmarked file -
    /// the same rule <see cref="KaraokeNaming.IsKaraokeFile"/> applies to a bare
    /// ".karaoke.mp3".
    /// </summary>
    public static bool IsVocalsFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
        return stem.Length > KaraokeNaming.VocalsMarker.Length
               && stem.EndsWith(KaraokeNaming.VocalsMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The source song's base name: "Song.vocals.mp3" gives "Song". A name with
    /// no marker comes back as its plain stem, so callers can use it blindly.
    /// </summary>
    public static string SourceName(string? fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName)) ?? "";
        return stem.Length > KaraokeNaming.VocalsMarker.Length
               && stem.EndsWith(KaraokeNaming.VocalsMarker, StringComparison.OrdinalIgnoreCase)
            ? stem[..^KaraokeNaming.VocalsMarker.Length]
            : stem;
    }

    /// <summary>
    /// The library row key of the source: "a/b/Song.vocals" gives "a/b/Song", so
    /// a vocals row can find the row of the track it was rendered from.
    /// </summary>
    public static string SourceKey(string? relativeKey)
    {
        if (string.IsNullOrWhiteSpace(relativeKey)) return "";
        var dir = Path.GetDirectoryName(relativeKey) ?? "";
        var name = Path.GetFileName(relativeKey);
        return dir.Length == 0 ? SourceName(name) : Path.Combine(dir, SourceName(name));
    }

    /// <summary>
    /// The title of a vocals row: <paramref name="title"/> plus exactly one
    /// " (Vocals)". Idempotent, and it strips a marker the name (or a tag copied
    /// from the file name) may already carry, so a rescan cannot stack suffixes.
    /// </summary>
    public static string TitleWithSuffix(string? title)
    {
        var text = (title ?? "").Trim();
        if (text.EndsWith(KaraokeNaming.VocalsMarker, StringComparison.OrdinalIgnoreCase))
            text = text[..^KaraokeNaming.VocalsMarker.Length].TrimEnd();

        return text.EndsWith(TitleSuffix, StringComparison.OrdinalIgnoreCase)
            ? text
            : text + TitleSuffix;
    }
}
