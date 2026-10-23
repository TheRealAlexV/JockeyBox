using System.IO;

namespace JockeyBox.Library;

/// <summary>
/// The one naming rule for the merged single-root library: the karaoke version of
/// "Artist\Album\Song.mp3" is "Artist\Album\Song.karaoke.mp3".
///
/// The marker sits between the base name and the extension, so the pair shares
/// one folder and the two files may still use different formats (a generated
/// instrumental is often .flac next to a downloaded .mp3). Every place that
/// writes, pairs or looks up a karaoke file goes through these helpers, so the
/// scanner, the separation output path and the legacy mirror mapping cannot
/// disagree about where a partner lives.
/// </summary>
public static class KaraokeNaming
{
    /// <summary>Marker inserted before the extension: "Song" + Marker + ".mp3".</summary>
    public const string Marker = ".karaoke";

    /// <summary>
    /// True when the file name already carries the marker ("Song.karaoke.flac").
    /// A bare ".karaoke.flac" (empty base name) is not a pair, so it keeps a
    /// library row of its own instead of pairing with nothing.
    /// </summary>
    public static bool IsKaraokeFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        return IsKaraokeStem(StemOf(Path.GetFileName(fileName)));
    }

    /// <summary>
    /// Base name of a file name: "Song.karaoke.mp3" and "Song.mp3" both give
    /// "Song". This is what <c>TrackEntry.RelativeKey</c> is built from, so a
    /// karaoke file never becomes a library row of its own when its original
    /// exists.
    /// </summary>
    public static string BaseFileName(string? fileName)
    {
        var stem = StemOf(Path.GetFileName(fileName));
        return IsKaraokeStem(stem) ? stem[..^Marker.Length] : stem;
    }

    /// <summary>
    /// Karaoke file name of a file name: "Song.mp3" becomes "Song.karaoke.mp3".
    /// A name that already carries the marker comes back unchanged, so calling
    /// this twice is safe.
    /// </summary>
    public static string KaraokeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName) ?? "";
        var extension = Path.GetExtension(name);
        var stem = StemOf(name);

        if (stem.Length == 0) return name;    // ".mp3" has no base name to mark
        if (IsKaraokeStem(stem)) return name; // already "Song.karaoke.mp3"

        return stem + Marker + extension;
    }

    /// <summary>
    /// The karaoke partner path of an audio file: same folder, marked name, same
    /// extension. Callers that need another format (a generated .flac beside a
    /// downloaded .mp3) replace the extension themselves.
    /// </summary>
    public static string KaraokePathFor(string? audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath)) return "";

        var name = KaraokeFileName(audioPath);
        var folder = Path.GetDirectoryName(audioPath);
        return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
    }

    /// <summary>
    /// Insert <paramref name="suffix"/> into the base name, before the marker:
    /// "Song.mp3" with " (2)" gives "Song (2).mp3" and "Song.karaoke.mp3" gives
    /// "Song (2).karaoke.mp3". A collision copy therefore stays a partner of the
    /// same track instead of looking like the instrumental of "Song (2)".
    /// </summary>
    public static string InsertInBaseName(string? fileName, string suffix)
    {
        var name = Path.GetFileName(fileName) ?? "";
        if (name.Length == 0 || string.IsNullOrEmpty(suffix)) return name;

        var stem = StemOf(name);
        if (stem.Length == 0) return name;    // ".mp3" has no base name to extend

        var extension = Path.GetExtension(name);
        return IsKaraokeStem(stem)
            ? stem[..^Marker.Length] + suffix + Marker + extension
            : stem + suffix + extension;
    }

    private static string StemOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        return Path.GetFileNameWithoutExtension(fileName) ?? "";
    }

    private static bool IsKaraokeStem(string stem) =>
        stem.Length > Marker.Length
        && stem.EndsWith(Marker, StringComparison.OrdinalIgnoreCase);
}
