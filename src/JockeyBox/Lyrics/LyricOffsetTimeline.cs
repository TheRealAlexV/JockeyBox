namespace JockeyBox.Lyrics;

/// <summary>
/// The section-aware lyric offset model, kept pure (no I/O, no UI, no state) so
/// the highlighter, the metadata store and the section editor all agree on one
/// rule.
///
/// <para><b>Policy (deliberate, do not "fix" silently):</b></para>
/// <list type="bullet">
/// <item>
/// Sections are <b>half-open</b>: <c>[StartMs, EndMs)</c>. A position exactly at
/// <c>StartMs</c> is inside; a position exactly at <c>EndMs</c> is outside, so
/// two adjacent sections tile with no gap and no double claim. A position
/// exactly at the end of the last section (and past every other section) falls
/// back to the base offset.
/// </item>
/// <item>
/// Sections may not overlap. <see cref="Normalize"/> sorts by start, then by
/// longer span, then by offset/label for a deterministic result, and then
/// <b>drops</b> any section whose start is before the end of the last section
/// that was kept (so the earlier, longer span wins and the layout is
/// <c>Sorted, non-overlapping, trust the list</c>). It never mutates the input
/// and never throws; <see cref="NormalizeWithReport"/> also returns how many
/// sections were dropped so a caller can warn instead of silently losing data.
/// </item>
/// <item>
/// Negative positions clamp to 0 before any comparison (playback position can
/// be slightly negative while a deck seeks), so a section starting at 0 covers
/// them.
/// </item>
/// <item>
/// Two offset behaviours are exposed and they are not interchangeable:
/// <see cref="OffsetAt"/> is an <b>override</b> (a section replaces the base,
/// it is not added to it; outside every section the base is returned) and
/// <see cref="OffsetAtCumulative"/> is <b>base + section delta</b>. The UI uses
/// the override semantics; the cumulative form exists for "nudge this part
/// relative to the track" tooling.
/// </item>
/// </list>
/// </summary>
public static class LyricOffsetTimeline
{
    /// <summary>
    /// Convert a playback position to the millisecond domain used by sections.
    /// Negative, NaN and infinite values clamp to 0; huge values clamp to
    /// <see cref="int.MaxValue"/> instead of overflowing.
    /// </summary>
    public static int ToPositionMs(TimeSpan position)
    {
        var ms = position.TotalMilliseconds;
        if (double.IsNaN(ms) || ms <= 0) return 0;
        if (ms >= int.MaxValue) return int.MaxValue;
        return (int)Math.Round(ms, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Sorted, clamped, non-overlapping copy of <paramref name="sections"/>.
    /// Overlapping later spans are dropped (the earlier, longer span wins);
    /// empty/inverted spans are dropped. Null input yields an empty list.
    /// </summary>
    public static IReadOnlyList<LyricOffsetSection> Normalize(
        IEnumerable<LyricOffsetSection>? sections) => NormalizeWithReport(sections).Sections;

    /// <summary>
    /// <see cref="Normalize"/> plus the number of spans that were dropped as
    /// empty/inverted or overlapping, so the caller can log or tell the user.
    /// </summary>
    public static (IReadOnlyList<LyricOffsetSection> Sections, int Dropped) NormalizeWithReport(
        IEnumerable<LyricOffsetSection>? sections)
    {
        if (sections is null) return (Array.Empty<LyricOffsetSection>(), 0);

        var candidates = new List<LyricOffsetSection>();
        int dropped = 0;
        foreach (var raw in sections)
        {
            var section = raw.Clamped();
            if (!section.IsValid)
            {
                dropped++;
                continue;
            }
            candidates.Add(section);
        }

        if (candidates.Count == 0) return (Array.Empty<LyricOffsetSection>(), dropped);

        // Deterministic order: earliest first; for equal starts the longer span
        // wins the slot (it covers more of the track), then offset and label so
        // two identical-start spans never depend on input order.
        candidates.Sort(static (a, b) =>
        {
            int c = a.StartMs.CompareTo(b.StartMs);
            if (c != 0) return c;
            c = b.EndMs.CompareTo(a.EndMs);
            if (c != 0) return c;
            c = a.OffsetMs.CompareTo(b.OffsetMs);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Label ?? "", b.Label ?? "");
        });

        var kept = new List<LyricOffsetSection>(candidates.Count);
        int lastEnd = int.MinValue;
        foreach (var section in candidates)
        {
            if (kept.Count > 0 && section.StartMs < lastEnd)
            {
                dropped++;
                continue;
            }
            kept.Add(section);
            lastEnd = section.EndMs;
        }

        return (kept.ToArray(), dropped);
    }

    /// <summary>
    /// Offset that applies at <paramref name="positionMs"/> using <b>override</b>
    /// semantics: the first section containing the position supplies its offset
    /// instead of <paramref name="baseOffsetMs"/>; outside every section the base
    /// is returned unchanged.
    /// <para>
    /// <paramref name="sections"/> must be normalised (sorted, non-overlapping);
    /// <see cref="Normalize"/> guarantees that. Unnormalised input is still safe
    /// here - the first match wins - it just may not be the span the user
    /// expected, which is why the store always normalises on write and on load.
    /// </para>
    /// </summary>
    public static int OffsetAt(
        int baseOffsetMs,
        IReadOnlyList<LyricOffsetSection>? sections,
        int positionMs)
    {
        if (positionMs < 0) positionMs = 0;
        if (sections is null) return baseOffsetMs;

        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Contains(positionMs)) return sections[i].OffsetMs;
        }
        return baseOffsetMs;
    }

    /// <summary>
    /// Offset at <paramref name="positionMs"/> using <b>cumulative</b> semantics:
    /// <c>baseOffsetMs + section.OffsetMs</c> inside a section (the section value
    /// is a delta, not an absolute), and the plain base outside. The sum saturates
    /// instead of overflowing.
    /// </summary>
    public static int OffsetAtCumulative(
        int baseOffsetMs,
        IReadOnlyList<LyricOffsetSection>? sections,
        int positionMs)
    {
        if (positionMs < 0) positionMs = 0;
        if (sections is null) return baseOffsetMs;

        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Contains(positionMs)) return SaturatingAdd(baseOffsetMs, sections[i].OffsetMs);
        }
        return baseOffsetMs;
    }

    /// <summary><see cref="OffsetAt(int, IReadOnlyList{LyricOffsetSection}, int)"/> for a <see cref="TimeSpan"/> position.</summary>
    public static int OffsetAt(
        int baseOffsetMs,
        IReadOnlyList<LyricOffsetSection>? sections,
        TimeSpan position) => OffsetAt(baseOffsetMs, sections, ToPositionMs(position));

    /// <summary><see cref="OffsetAtCumulative(int, IReadOnlyList{LyricOffsetSection}, int)"/> for a <see cref="TimeSpan"/> position.</summary>
    public static int OffsetAtCumulative(
        int baseOffsetMs,
        IReadOnlyList<LyricOffsetSection>? sections,
        TimeSpan position) => OffsetAtCumulative(baseOffsetMs, sections, ToPositionMs(position));

    /// <summary>
    /// Index of the section containing <paramref name="positionMs"/>, or -1.
    /// Intended for "which section am I editing/watching", not for the per-frame
    /// highlighter (that uses <see cref="OffsetAt"/>).
    /// </summary>
    public static int SectionIndexAt(IReadOnlyList<LyricOffsetSection>? sections, int positionMs)
    {
        if (positionMs < 0) positionMs = 0;
        if (sections is null) return -1;

        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Contains(positionMs)) return i;
        }
        return -1;
    }

    private static int SaturatingAdd(int left, int right)
    {
        long sum = (long)left + right;
        if (sum > int.MaxValue) return int.MaxValue;
        if (sum < int.MinValue) return int.MinValue;
        return (int)sum;
    }
}
