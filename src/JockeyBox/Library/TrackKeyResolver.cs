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
    public static string? Resolve(string? tagKey, BeatGrid? grid) => Resolve(tagKey, grid, out _);

    /// <summary>
    /// As above, and reports whether the winner is the analyser's guess rather
    /// than the file's own assertion, so the row can mark it (see
    /// <see cref="KeyNotation.Display"/>).
    ///
    /// <para><b>The cached-key trap.</b> <c>library.json</c> keeps an estimated
    /// key in the same <c>Key</c> field as a TKEY tag - <c>TagCache.SetKey</c>
    /// fills only a blank - so after one scan a guess is indistinguishable from a
    /// tag by provenance of the field alone. A cached key that equals this
    /// track's own estimate is therefore treated as that estimate: calling a
    /// guess an authored tag is the error that makes the column lie, while the
    /// reverse (a real tag that happens to agree with the estimate) only shows a
    /// tilde it had not earned.</para>
    /// </summary>
    public static string? Resolve(string? tagKey, BeatGrid? grid, out bool estimated)
    {
        estimated = false;
        var analysed = grid is { KeyAnalysed: true, Key: { Length: > 0 } }
            ? KeyNotation.Normalize(grid.Key, out _)
            : null;

        if (!string.IsNullOrWhiteSpace(tagKey))
        {
            var tagged = KeyNotation.Normalize(tagKey.Trim(), out _);
            estimated = analysed is not null && string.Equals(tagged, analysed, StringComparison.Ordinal);
            return tagged;
        }

        if (analysed is null) return null;
        estimated = true;
        return analysed;
    }
}
