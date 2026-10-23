namespace JockeyBox.Lyrics;

/// <summary>
/// Turns a provider's raw word spans (each with an optional start/end) into
/// non-overlapping <see cref="LyricWord"/>s inside one line.
///
/// Shared by <see cref="TtmlParser"/> (span begin/end) and
/// <see cref="LrcmuxParser"/> (millisecond word objects), so both sources get
/// the same degenerate-timeline behaviour without throwing.
/// </summary>
internal static class WordSpanBuilder
{
    /// <summary>Length given to a zero-length word so a span is never empty.</summary>
    private static readonly TimeSpan MinSpan = TimeSpan.FromMilliseconds(1);

    /// <summary>Window for a word with no following boundary at all.</summary>
    private static readonly TimeSpan LastWordWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Build ordered, non-overlapping words for one line.
    /// </summary>
    /// <param name="raw">Spans in document order; a null start is skipped.</param>
    /// <param name="lineStart">Line's own start, used as the initial cursor.</param>
    /// <param name="lineEnd">Known line end, used for the final word.</param>
    public static List<LyricWord> Build(
        IReadOnlyList<(TimeSpan? Start, TimeSpan? End, string Text)> raw,
        TimeSpan lineStart,
        TimeSpan? lineEnd)
    {
        var words = new List<LyricWord>(raw.Count);
        if (raw.Count == 0) return words;

        var cursor = lineStart;

        for (int i = 0; i < raw.Count; i++)
        {
            if (raw[i].Start is not { } start) continue;

            // Malformed/unsorted input must not produce overlapping words.
            if (start < cursor) start = cursor;

            TimeSpan end;
            if (raw[i].End is { } explicitEnd)
            {
                end = explicitEnd;
            }
            else if (NextStart(raw, i) is { } next && next > start)
            {
                end = next;
            }
            else if (lineEnd is { } bound && bound > start)
            {
                end = bound;
            }
            else
            {
                end = start + LastWordWindow;
            }

            // A span may not run past the start of the word that follows it.
            if (NextStart(raw, i) is { } following && following > start && end > following)
                end = following;

            if (end <= start) end = start + MinSpan;

            words.Add(new LyricWord(start, end, raw[i].Text, IsSynthetic: false));
            cursor = end;
        }

        return words;
    }

    /// <summary>Start of the next span that has one, or null.</summary>
    private static TimeSpan? NextStart(IReadOnlyList<(TimeSpan? Start, TimeSpan? End, string Text)> raw, int index)
    {
        for (int i = index + 1; i < raw.Count; i++)
        {
            if (raw[i].Start is { } start) return start;
        }
        return null;
    }
}
