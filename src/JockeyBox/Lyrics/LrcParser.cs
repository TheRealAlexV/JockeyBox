using System.Text.RegularExpressions;

namespace JockeyBox.Lyrics;

/// <summary>
/// Parser for standard LRC text: lines of "[mm:ss.xx]words". Handles multiple
/// time tags on one line and skips metadata tags like [ar:], [ti:], [offset:].
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d+):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTagRegex();

    /// <summary>Parse full LRC text into a document; null when nothing usable.</summary>
    public static LyricsDocument? Parse(string lrcText, string source)
    {
        var lines = new List<LyricLine>();
        bool anyTimed = false;

        foreach (var rawLine in lrcText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0)
            {
                // Metadata tag or plain text line.
                if (!line.StartsWith('[')) lines.Add(new LyricLine(TimeSpan.Zero, line));
                continue;
            }

            var text = TimeTagRegex().Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                var time = ParseTag(m);
                if (time > TimeSpan.Zero) anyTimed = true;
                lines.Add(new LyricLine(time, text));
            }
        }

        if (lines.Count == 0) return null;
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new LyricsDocument { Synced = anyTimed, Lines = lines, Source = source };
    }

    /// <summary>Parse a single "mm:ss.xx" style tag value (used by API responses).</summary>
    public static TimeSpan ParseTimeTag(string tag)
    {
        var m = TimeTagRegex().Match($"[{tag}]");
        return m.Success ? ParseTag(m) : TimeSpan.Zero;
    }

    private static TimeSpan ParseTag(Match m)
    {
        // TryParse keeps one malformed/overflowing tag from rejecting the
        // whole document (values come from remote services).
        if (!int.TryParse(m.Groups[1].Value, out int minutes)
            || !int.TryParse(m.Groups[2].Value, out int seconds)
            || minutes > 24 * 60) return TimeSpan.Zero;

        int fraction = 0;
        if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int f))
        {
            fraction = f * m.Groups[3].Value.Length switch { 1 => 100, 2 => 10, _ => 1 };
        }
        return new TimeSpan(0, 0, minutes, seconds, fraction);
    }
}
