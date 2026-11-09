using JockeyBox.Audio.Beat;

namespace JockeyBox.Library;

/// <summary>
/// The one rule for what key a library row shows.
///
/// <para><b>Tag first.</b> An authored TKEY tag describes the recording; a chroma
/// estimate is a heuristic with a confidence margin. Letting the estimate overwrite
/// correct metadata would be a silent downgrade, so a tag key always wins and
/// analysis only fills a blank.</para>
///
/// <para>The winner is normalised to Camelot (<see cref="KeyNotation"/>) here, so
/// no consumer ever sees a note name in one row and a wheel code in the next.
/// The file's tag and the cached grid are never rewritten.</para>
///
/// <para>Null means "no key known", never an empty string: the row's XAML
/// fallback turns null into the em dash. Callers use this both at scan time
/// (tag or cached grid) and when analysis finishes for an already-visible row.</para>
/// </summary>
public static class TrackKeyResolver
{
    public static string? Resolve(string? tagKey, BeatGrid? grid)
    {
        if (!string.IsNullOrWhiteSpace(tagKey))
            return KeyNotation.Normalize(tagKey.Trim(), out _);
        if (grid is { KeyAnalysed: true, Key: { Length: > 0 } analysed })
            return KeyNotation.Normalize(analysed, out _);
        return null;
    }
}
