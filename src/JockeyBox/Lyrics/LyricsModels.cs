using System.Text.Json.Serialization;

namespace JockeyBox.Lyrics;

/// <summary>
/// One timed word inside a lyric line.
/// <see cref="IsSynthetic"/> is true when the span was estimated by
/// <see cref="WordTimeline.Synthesize"/> instead of coming from the source
/// (A2/enhanced LRC word tags or a provider that supplies real timings).
/// </summary>
public record LyricWord(TimeSpan Start, TimeSpan End, string Text, bool IsSynthetic);

/// <summary>One display line of lyrics with its start time (zero if unsynced).</summary>
public record LyricLine(TimeSpan Time, string Text)
{
    /// <summary>
    /// Per-word timings for this line, or null when the source only has
    /// line-level timing. Never an empty list: a line either carries at least
    /// one word or null. Words are ordered by <see cref="LyricWord.Start"/> and
    /// do not overlap.
    /// </summary>
    public IReadOnlyList<LyricWord>? Words { get; init; }
}

/// <summary>Parsed lyrics for a track, ready for the UI panel.</summary>
public class LyricsDocument
{
    public required bool Synced { get; init; }
    public required List<LyricLine> Lines { get; init; }

    /// <summary>Human-readable provider name, e.g. "LRCLIB".</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Stable provider id ("lrc", "lrcmux", "betterlyrics", "unison",
    /// "spotify-color", "spotify-api", "lrclib"), used for per-source caching and
    /// for remembering a user's pick per track. Empty only for documents restored
    /// from a pre-source-id cache file.
    /// </summary>
    public string SourceId { get; init; } = "";

    /// <summary>
    /// True when at least one line carries per-word timings. Computed, so it can
    /// never disagree with <see cref="Lines"/>; ignored by the lyrics cache.
    /// </summary>
    [JsonIgnore]
    public bool HasWordTimings
    {
        get
        {
            var lines = Lines;
            if (lines is null) return false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Words is { Count: > 0 }) return true;
            }
            return false;
        }
    }

    /// <summary>Total number of timed words across all lines (0 when none).</summary>
    [JsonIgnore]
    public int WordCount
    {
        get
        {
            var lines = Lines;
            if (lines is null) return 0;
            int count = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var words = lines[i].Words;
                if (words is not null) count += words.Count;
            }
            return count;
        }
    }

    public static LyricsDocument Unsynced(IEnumerable<string> lines, string source, string sourceId = "") => new()
    {
        Synced = false,
        Source = source,
        SourceId = sourceId,
        Lines = lines.Select(l => new LyricLine(TimeSpan.Zero, l)).ToList(),
    };
}
