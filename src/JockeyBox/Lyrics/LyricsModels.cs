namespace JockeyBox.Lyrics;

/// <summary>One display line of lyrics with its start time (zero if unsynced).</summary>
public record LyricLine(TimeSpan Time, string Text);

/// <summary>Parsed lyrics for a track, ready for the UI panel.</summary>
public class LyricsDocument
{
    public required bool Synced { get; init; }
    public required List<LyricLine> Lines { get; init; }
    public required string Source { get; init; }

    public static LyricsDocument Unsynced(IEnumerable<string> lines, string source) => new()
    {
        Synced = false,
        Source = source,
        Lines = lines.Select(l => new LyricLine(TimeSpan.Zero, l)).ToList(),
    };
}
