using System.Text;
using System.Text.RegularExpressions;

namespace JockeyBox.Lyrics;

/// <summary>
/// Parser for standard LRC text: lines of "[mm:ss.xx]words". Handles multiple
/// time tags on one line, honours the [offset:] metadata tag, and skips the
/// other metadata tags like [ar:], [ti:].
/// <para>
/// Also reads A2 / enhanced-LRC word tags inside the line body
/// ("[00:12.00]&lt;00:12.00&gt;Some &lt;00:12.40&gt;words"), turning them into
/// <see cref="LyricLine.Words"/> with real (non-synthetic) timings. Every word
/// tag starts one <see cref="LyricWord"/>; a tag whose segment contains several
/// space-separated tokens is kept as a single word, because the source only
/// timed the start of that segment. Lines without tags keep Words = null.
/// </para>
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d+):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTagRegex();

    /// <summary>"&lt;mm:ss.xx&gt;", "&lt;mm:ss.xxx&gt;" and the "&lt;mm:ss:xx&gt;" variant.</summary>
    [GeneratedRegex(@"<(\d+):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTagRegex();

    /// <summary>Any angle-bracket group; used to detect malformed word markup.</summary>
    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex AnyAngleTagRegex();

    /// <summary>
    /// "[offset:+/-N]" in milliseconds. LRC spec: a positive value shifts every
    /// line LATER (the lyrics are delayed), a negative value shifts them earlier.
    /// </summary>
    [GeneratedRegex(@"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTagRegex();

    /// <summary>
    /// A word left without a following tag (or malformed/unsorted input) still
    /// needs a positive length; this is the window used in that case.
    /// </summary>
    private static readonly TimeSpan LastWordWindow = TimeSpan.FromSeconds(4);

    /// <summary>Minimum length given to a zero-length word so spans never collapse.</summary>
    private static readonly TimeSpan MinWordGap = TimeSpan.FromMilliseconds(1);

    /// <summary>Parse full LRC text into a document; null when nothing usable.</summary>
    /// <param name="lrcText">Raw LRC body.</param>
    /// <param name="source">Display name recorded on the document.</param>
    /// <param name="sourceId">Stable provider id recorded on the document.</param>
    public static LyricsDocument? Parse(string lrcText, string source, string sourceId = "")
    {
        var pending = new List<PendingLine>();
        bool anyTimed = false;

        // Applied to every line and word time, as the spec requires.
        var offset = TimeSpan.FromMilliseconds(ParseOffsetMs(lrcText));

        foreach (var rawLine in (lrcText ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0)
            {
                // Metadata tag or plain text line.
                if (!line.StartsWith('[')) pending.Add(new PendingLine(TimeSpan.Zero, CollapseSpaces(line), null, null));
                continue;
            }

            // Strip the line tags first, then read the word tags out of the body.
            var body = TimeTagRegex().Replace(line, "");
            var lineTime = ClampTime(ParseTag(matches[0]) + offset);
            var parsed = ParseBody(body, lineTime, offset);

            foreach (Match m in matches)
            {
                var time = ClampTime(ParseTag(m) + offset);
                // A large negative [offset:] must not produce times before zero:
                // the highlighter assumes a monotonically increasing timeline.
                if (time > TimeSpan.Zero) anyTimed = true;
                pending.Add(new PendingLine(time, parsed.Text, parsed.Starts, parsed.Texts));
            }
        }

        if (pending.Count == 0) return null;

        // Sort first: a word's end is the next line's start, so the timeline has
        // to be in order before the spans are resolved.
        pending.Sort((a, b) => a.Time.CompareTo(b.Time));

        var lines = new List<LyricLine>(pending.Count);
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            if (p.WordStarts is not { Length: > 0 } starts || p.WordTexts is not { } texts || texts.Length != starts.Length)
            {
                lines.Add(new LyricLine(p.Time, p.Text));
                continue;
            }

            var words = new LyricWord[starts.Length];
            var cursor = TimeSpan.Zero;
            for (int w = 0; w < starts.Length; w++)
            {
                var start = starts[w];
                // Malformed/unsorted tags must not produce overlapping words.
                if (start < cursor) start = cursor;

                var end = w + 1 < starts.Length ? starts[w + 1] : EndOfLine(pending, i, start);
                if (end < start) end = start;
                if (end == start) end = start + MinWordGap;

                words[w] = new LyricWord(start, end, texts[w], IsSynthetic: false);
                cursor = end;
            }

            lines.Add(new LyricLine(p.Time, p.Text) { Words = words });
        }

        return new LyricsDocument { Synced = anyTimed, Lines = lines, Source = source, SourceId = sourceId };
    }

    /// <summary>
    /// Read the document-wide [offset:] value in milliseconds (0 when absent or
    /// malformed). The first tag wins; surrounding whitespace and an explicit
    /// '+' are accepted.
    /// </summary>
    public static int ParseOffsetMs(string lrcText)
    {
        if (string.IsNullOrEmpty(lrcText)) return 0;
        var m = OffsetTagRegex().Match(lrcText);
        return m.Success && int.TryParse(m.Groups[1].Value, out int ms)
            ? Math.Clamp(ms, -600_000, 600_000)
            : 0;
    }

    /// <summary>Parse a single "mm:ss.xx" style tag value (used by API responses).</summary>
    public static TimeSpan ParseTimeTag(string tag)
    {
        var m = TimeTagRegex().Match($"[{tag}]");
        return m.Success ? ParseTag(m) : TimeSpan.Zero;
    }

    /// <summary>
    /// The final word of a line runs to the next line's start, else four seconds
    /// past its own start. Degenerate timelines (next line at/behind the word)
    /// fall back to that four-second window so the span stays positive.
    /// </summary>
    private static TimeSpan EndOfLine(List<PendingLine> pending, int index, TimeSpan wordStart)
    {
        if (index + 1 < pending.Count)
        {
            var next = pending[index + 1].Time;
            if (next > wordStart) return next;
        }

        var fallback = pending[index].Time + LastWordWindow;
        return fallback > wordStart ? fallback : wordStart + LastWordWindow;
    }

    /// <summary>
    /// Split a tag-stripped line body into word segments. Returns no word arrays
    /// (Text only) whenever the line has no word tags or carries malformed ones.
    /// </summary>
    private static (string Text, TimeSpan[]? Starts, string[]? Texts) ParseBody(
        string body, TimeSpan lineTime, TimeSpan offset)
    {
        // Text before the first word tag belongs to the line, so it starts there.
        var text = CollapseSpaces(AnyAngleTagRegex().Replace(body, " "));
        if (!WordTagRegex().IsMatch(body)) return (text, null, null);

        // Any angle-bracket group that is not a well-formed word tag (e.g.
        // "<00:xx.00>", "<00:99.00>") invalidates the line's word timing: keep
        // the readable text and fall back to line-level timing, never throw.
        foreach (Match m in AnyAngleTagRegex().Matches(body))
        {
            if (!TryParseWordTag(m.Value, out _)) return (text, null, null);
        }

        var starts = new List<TimeSpan>(4);
        var texts = new List<string>(4);
        var segmentStart = lineTime;
        int pos = 0;

        foreach (Match tag in WordTagRegex().Matches(body))
        {
            AddSegment(body.AsSpan(pos, tag.Index - pos), segmentStart);
            pos = tag.Index + tag.Length;
            segmentStart = ClampTime(ParseWordTagTime(tag) + offset);
        }
        AddSegment(body.AsSpan(pos), segmentStart);

        return starts.Count == 0 ? (text, null, null) : (text, starts.ToArray(), texts.ToArray());

        void AddSegment(ReadOnlySpan<char> segment, TimeSpan start)
        {
            var word = CollapseSpaces(segment);
            if (word.Length == 0) return;
            starts.Add(start);
            texts.Add(word);
        }
    }

    /// <summary>Collapse every whitespace run to one space and trim the ends.</summary>
    private static string CollapseSpaces(string value) => CollapseSpaces(value.AsSpan());

    /// <inheritdoc cref="CollapseSpaces(string)"/>
    private static string CollapseSpaces(ReadOnlySpan<char> value)
    {
        int start = 0, end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start])) start++;
        while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
        if (start >= end) return "";

        var sb = new StringBuilder(end - start);
        bool pendingSpace = false;
        for (int i = start; i < end; i++)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static TimeSpan ParseWordTagTime(Match m)
    {
        if (!int.TryParse(m.Groups[1].Value, out int minutes)
            || !int.TryParse(m.Groups[2].Value, out int seconds)) return TimeSpan.Zero;

        int fraction = 0;
        if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int f))
        {
            fraction = f * (m.Groups[3].Value.Length switch { 1 => 100, 2 => 10, _ => 1 });
        }
        return new TimeSpan(0, 0, minutes, seconds, fraction);
    }

    /// <summary>True when the whole token is a well-formed "&lt;mm:ss.xx&gt;" tag.</summary>
    private static bool TryParseWordTag(string token, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var m = WordTagRegex().Match(token);
        if (!m.Success || m.Index != 0 || m.Length != token.Length) return false;
        if (!int.TryParse(m.Groups[1].Value, out int minutes) || minutes > 24 * 60) return false;
        if (!int.TryParse(m.Groups[2].Value, out int seconds) || seconds > 59) return false;
        if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, out _)) return false;

        time = ParseWordTagTime(m);
        return true;
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
            // 1 digit = tenths, 2 = hundredths, 3 = milliseconds. The explicit
            // parentheses matter: `.xx` must scale by magnitude, not by value.
            fraction = f * (m.Groups[3].Value.Length switch { 1 => 100, 2 => 10, _ => 1 });
        }
        return new TimeSpan(0, 0, minutes, seconds, fraction);
    }

    private static TimeSpan ClampTime(TimeSpan time) => time < TimeSpan.Zero ? TimeSpan.Zero : time;

    /// <summary>One parsed line before word spans are resolved against its neighbours.</summary>
    private readonly record struct PendingLine(
        TimeSpan Time, string Text, TimeSpan[]? WordStarts, string[]? WordTexts);
}
