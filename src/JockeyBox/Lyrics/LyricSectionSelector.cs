using System;

namespace JockeyBox.Lyrics;

/// <summary>
/// Turns a lyric-line selection (what the user highlighted in the lyrics panel)
/// into a <see cref="LyricOffsetSection"/> span, with no I/O and no UI types.
///
/// <para>
/// The span of a line runs from its own start to the next line's start - the
/// same window <see cref="WordTimeline"/> uses to place words - so selecting
/// lines 4..7 yields exactly the audio the user hears between the start of line
/// 4 and the start of line 8, with no gap and no overlap with a neighbouring
/// selection. The last line has no successor, so it uses
/// <see cref="WordTimeline.LastLineWindowMs"/> (+4 s), again matching the word
/// timeline. Degenerate documents (duplicate/unsorted times) fall back to
/// <see cref="WordTimeline.DegenerateWindowMs"/> so a selection always has a
/// usable, positive length.
/// </para>
///
/// <para>
/// Everything here is defensive: a null document, an empty line list, indexes
/// out of range or an inverted pair (last before first) return
/// <c>false</c>/<c>null</c> instead of throwing. The caller decides what to do
/// with a rejected selection.
/// </para>
/// </summary>
public static class LyricSectionSelector
{
    /// <summary>
    /// Span for lines <paramref name="firstLineIndex"/>..<paramref name="lastLineIndex"/>
    /// (inclusive, either order accepted).
    /// </summary>
    /// <returns>False when the selection cannot be resolved.</returns>
    public static bool TryFromLines(
        LyricsDocument? document,
        int firstLineIndex,
        int lastLineIndex,
        out LyricOffsetSection section,
        int offsetMs = 0,
        string? label = null)
    {
        section = default;

        var lines = document?.Lines;
        if (lines is null || lines.Count == 0) return false;

        if (firstLineIndex > lastLineIndex) (firstLineIndex, lastLineIndex) = (lastLineIndex, firstLineIndex);
        if (firstLineIndex < 0 || lastLineIndex >= lines.Count) return false;

        var (startMs, endMs) = Span(lines, firstLineIndex, lastLineIndex);
        if (endMs <= startMs) return false;

        section = new LyricOffsetSection(startMs, endMs, offsetMs, label).Clamped();
        return true;
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryFromLines"/>: the section, or null
    /// when the selection is not resolvable.
    /// </summary>
    public static LyricOffsetSection? FromLines(
        LyricsDocument? document,
        int firstLineIndex,
        int lastLineIndex,
        int offsetMs = 0,
        string? label = null) =>
        TryFromLines(document, firstLineIndex, lastLineIndex, out var section, offsetMs, label)
            ? section
            : null;

    /// <summary>
    /// Span from a pair of already-known times (<c>firstLineStartMs</c> and
    /// <c>lastLineEndMs</c>), used when the view has times but not a document.
    /// A non-positive end is replaced with <c>start + LastLineWindowMs</c> and
    /// times are clamped to the non-negative millisecond domain.
    /// </summary>
    public static LyricOffsetSection FromTimes(
        int firstLineStartMs,
        int lastLineEndMs,
        int offsetMs = 0,
        string? label = null)
    {
        int start = Math.Max(0, firstLineStartMs);
        int end = Math.Max(0, lastLineEndMs);
        if (end <= start) end = start + WordTimeline.LastLineWindowMs;
        return new LyricOffsetSection(start, end, offsetMs, label).Clamped();
    }

    /// <summary>
    /// End of line <paramref name="index"/>: the next line's start, or +4 s for
    /// the last line, with the degenerate-window fallback. Public so the section
    /// editor can show the exact span it will save.
    /// </summary>
    public static (int StartMs, int EndMs) LineSpan(LyricsDocument? document, int index)
    {
        var lines = document?.Lines;
        if (lines is null || index < 0 || index >= lines.Count) return (0, 0);

        var (start, end) = Span(lines, index, index);
        return (start, end);
    }

    private static (int StartMs, int EndMs) Span(List<LyricLine> lines, int first, int last)
    {
        int startMs = ClampMs(lines[first].Time);

        // A marker line never ends a section: it is not sung text, so the span
        // must run on to the next real line.
        int next = last + 1;
        while (next < lines.Count && lines[next].IsInstrumental) next++;

        long endMs;
        if (next < lines.Count)
        {
            endMs = ClampMs(lines[next].Time);
        }
        else
        {
            endMs = (long)startMs + WordTimeline.LastLineWindowMs;
        }

        if (endMs <= startMs) endMs = (long)startMs + WordTimeline.DegenerateWindowMs;
        if (endMs > int.MaxValue) endMs = int.MaxValue;

        return (startMs, (int)endMs);
    }

    private static int ClampMs(TimeSpan time)
    {
        var ms = time.TotalMilliseconds;
        if (double.IsNaN(ms) || ms <= 0) return 0;
        if (ms >= int.MaxValue) return int.MaxValue;
        return (int)Math.Round(ms, MidpointRounding.AwayFromZero);
    }
}
