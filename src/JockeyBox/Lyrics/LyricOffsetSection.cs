using System.Text.Json.Serialization;

namespace JockeyBox.Lyrics;

/// <summary>
/// A lyric-offset section: a span of the track that uses its own offset instead
/// of (or on top of, see <see cref="LyricOffsetTimeline"/>) the track/global
/// value.
/// <para>
/// Times are milliseconds from the start of the track. The span is
/// <b>half-open</b>: <see cref="StartMs"/> is inside the section and
/// <see cref="EndMs"/> is not. That makes adjacent sections tile without both
/// claiming the boundary, and it keeps <see cref="Contains"/> monotonic
/// (start-inclusive, end-exclusive) for the highlighter's linear scan.
/// </para>
/// <para>
/// A section is a value: two sections with the same four members are equal,
/// which is what the store uses to decide whether anything actually changed.
/// </para>
/// </summary>
/// <param name="StartMs">First millisecond the section applies to (inclusive).</param>
/// <param name="EndMs">First millisecond the section no longer applies to (exclusive).</param>
/// <param name="OffsetMs">
/// Lyric offset for the span, positive = lyrics appear later (same sign as the
/// global/per-track offset). <see cref="LyricOffsetTimeline.OffsetAt"/> returns
/// it as an override; <see cref="LyricOffsetTimeline.OffsetAtCumulative"/> adds
/// it to the base.
/// </param>
/// <param name="Label">Optional user label ("Chorus", "Verse 2"); null when unnamed.</param>
public readonly record struct LyricOffsetSection(
    int StartMs,
    int EndMs,
    int OffsetMs,
    string? Label = null)
{
    /// <summary>True when the span has a positive length.</summary>
    [JsonIgnore]
    public bool IsValid => EndMs > StartMs;

    /// <summary>Length of the span in milliseconds; 0 for an empty/inverted span.</summary>
    [JsonIgnore]
    public int DurationMs => EndMs > StartMs ? EndMs - StartMs : 0;

    /// <summary>
    /// True when <paramref name="positionMs"/> falls inside the span under the
    /// half-open rule: <c>StartMs &lt;= positionMs &lt; EndMs</c>. Negative
    /// positions are never inside a normalised section.
    /// </summary>
    public bool Contains(int positionMs) => positionMs >= StartMs && positionMs < EndMs;

    /// <summary>
    /// True when the two spans share at least one millisecond. Touching spans
    /// (one ends exactly where the next starts) do not overlap.
    /// </summary>
    public bool Overlaps(LyricOffsetSection other) =>
        other.StartMs < EndMs && StartMs < other.EndMs;

    /// <summary>
    /// Normalised copy: negative times clamped to 0, blank label collapsed to
    /// null and trimmed otherwise. Does not change a valid span's length.
    /// </summary>
    public LyricOffsetSection Clamped() => this with
    {
        StartMs = Math.Max(0, StartMs),
        EndMs = Math.Max(0, EndMs),
        Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
    };
}
