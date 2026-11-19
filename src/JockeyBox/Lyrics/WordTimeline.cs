namespace JockeyBox.Lyrics;

/// <summary>
/// Builds estimated, word-level timings for lyrics that only carry line-level
/// timing (all four current providers: .lrc sidecar, Spotify Color Lyrics,
/// self-hosted spotify-lyrics-api, LRCLIB).
/// <para>
/// Everything here is deterministic and allocation-sane for a 200-line
/// document: no LINQ in the per-line loop, one array per line.
/// </para>
/// </summary>
public static class WordTimeline
{
    /// <summary>
    /// Floor for an estimated word. Words are never shorter than this unless the
    /// line window cannot fit the floor for every word, in which case the floor
    /// shrinks proportionally (window / word count).
    /// </summary>
    public const int MinWordDurationMs = 120;

    /// <summary>Window used for the last line, which has no next line to bound it.</summary>
    public const int LastLineWindowMs = 4000;

    /// <summary>
    /// Window used when a line's own window is zero or negative (duplicate or
    /// unsorted line times), so such lines still get usable spans.
    /// </summary>
    public const int DegenerateWindowMs = 400;

    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;
    private const long MinWordDurationTicks = MinWordDurationMs * TicksPerMs;
    private static readonly TimeSpan LastLineWindow = TimeSpan.FromMilliseconds(LastLineWindowMs);
    private static readonly TimeSpan DegenerateWindow = TimeSpan.FromMilliseconds(DegenerateWindowMs);

    /// <summary>
    /// Fill in <see cref="LyricLine.Words"/> for every line that has none.
    /// <para>
    /// Rules: unsynced documents (<see cref="LyricsDocument.Synced"/> == false)
    /// get no words at all; lines that already have real word timings are left
    /// untouched, which makes the call idempotent; empty/whitespace lines get
    /// none. A line's window is its own <see cref="LyricLine.Time"/> up to the
    /// next line's time (last line: +4 s), optionally clamped to
    /// <paramref name="trackDuration"/>.
    /// </para>
    /// Never throws: odd input (negative times, zero-length windows, a single
    /// word, null-ish collections) yields no words or safe ones.
    /// </summary>
    /// <param name="doc">Document to enrich in place; ignored when null.</param>
    /// <param name="trackDuration">
    /// Track length when known. Spans are clamped so no synthesised word ends
    /// after it; a duration at or before the line start is ignored.
    /// </param>
    public static void Synthesize(LyricsDocument doc, TimeSpan? trackDuration = null)
    {
        if (doc is null || !doc.Synced) return;

        var lines = doc.Lines;
        if (lines is null || lines.Count == 0) return;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // Leave real (provider / A2-tag) timings alone: this is what makes a
            // second call a no-op.
            if (line.Words is { Count: > 0 }) continue;

            // A marker line stands in for the singing, not for words: tiling its
            // symbols across the gap would leave one of them lit for the whole
            // instrumental.
            if (line.IsInstrumental) continue;

            var (start, end) = LineWindow(lines, i, trackDuration);
            var words = BuildLineWords(line.Text, start, end);
            if (words.Count == 0) continue;

            lines[i] = line with { Words = words };
        }
    }

    /// <summary>
    /// Split one line into words and distribute [start, end) across them.
    /// <para>
    /// Tokens are whitespace-separated, punctuation stays attached, and each
    /// token is weighted by its letter+digit count (minimum 1) so "the" gets a
    /// seventh of the time "extraordinary" does. Durations are exact integer
    /// boundaries, so they sum to the window and never overlap; the last token
    /// absorbs the rounding remainder. <see cref="MinWordDurationMs"/> is then
    /// enforced by stealing ticks from the longest word (never below the floor,
    /// never negative), and the floor itself shrinks when the whole window is
    /// too short for it.
    /// </para>
    /// All returned words have <see cref="LyricWord.IsSynthetic"/> = true.
    /// </summary>
    /// <returns>
    /// A read-only list ordered by start time; empty for blank text or a
    /// zero/negative window.
    /// </returns>
    public static IReadOnlyList<LyricWord> BuildLineWords(string text, TimeSpan start, TimeSpan end)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<LyricWord>();

        long windowTicks = end.Ticks - start.Ticks;
        if (windowTicks <= 0) return Array.Empty<LyricWord>();

        var tokens = Tokenize(text);
        int count = tokens.Count;
        if (count == 0) return Array.Empty<LyricWord>();

        var weights = new int[count];
        long totalWeight = 0;
        for (int i = 0; i < count; i++)
        {
            int weight = Weight(tokens[i]);
            weights[i] = weight;
            totalWeight += weight;
        }
        if (totalWeight <= 0) totalWeight = count;

        // Proportional split through cumulative boundaries: word i spans the
        // slice of the window between boundary i and i+1, so the slices tile the
        // window exactly (sum == windowTicks) with no drift from rounding.
        var durations = new long[count];
        long cumulative = 0;
        long previousBoundary = 0;
        for (int i = 0; i < count; i++)
        {
            cumulative += weights[i];
            long boundary = (long)((Int128)windowTicks * cumulative / totalWeight);
            durations[i] = boundary - previousBoundary;
            previousBoundary = boundary;
        }

        ApplyFloor(durations, windowTicks);

        var words = new LyricWord[count];
        long cursor = start.Ticks;
        for (int i = 0; i < count; i++)
        {
            long length = durations[i];
            words[i] = new LyricWord(
                new TimeSpan(cursor),
                new TimeSpan(cursor + length),
                tokens[i],
                IsSynthetic: true);
            cursor += length;
        }
        return words;
    }

    /// <summary>
    /// Start/end of the window estimated for line <paramref name="index"/>: the
    /// line's own start to the next line's start, the last line bounded by
    /// <see cref="LastLineWindowMs"/> (and by the track duration when known).
    /// Shared with <see cref="LyricEdits"/> so an edited line keeps the same window
    /// its words were estimated in before the edit.
    /// </summary>
    public static (TimeSpan Start, TimeSpan End) LineWindow(
        IReadOnlyList<LyricLine> lines, int index, TimeSpan? trackDuration)
    {
        var start = lines[index].Time;

        TimeSpan end;
        if (index + 1 < lines.Count)
        {
            end = lines[index + 1].Time;
        }
        else
        {
            end = start + LastLineWindow;
        }

        // Duplicate/unsorted line times (or a negative last line) would give a
        // zero or inverted window; use a fixed short window instead.
        if (end <= start) end = start + DegenerateWindow;

        if (trackDuration is { } duration && duration > start && duration < end)
        {
            end = duration;
        }

        return (start, end);
    }

    /// <summary>
    /// Raise every duration to the floor by taking ticks from the longest word,
    /// one pass per adjustment; the loop is bounded (each pass either fixes a
    /// word or saturates a donor) and simply stops if no donor has slack.
    /// </summary>
    private static void ApplyFloor(long[] durations, long windowTicks)
    {
        int count = durations.Length;
        if (count == 0) return;

        long floor = MinWordDurationTicks;
        // Window too short for every word to reach the floor: shrink the floor
        // proportionally so the total stays inside the window.
        if (windowTicks < floor * count) floor = windowTicks / count;
        if (floor <= 0) return;

        int maxPasses = count * count + 1;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            int needy = -1;
            for (int i = 0; i < count; i++)
            {
                if (durations[i] < floor) { needy = i; break; }
            }
            if (needy < 0) return;

            int donor = -1;
            for (int i = 0; i < count; i++)
            {
                if (i == needy || durations[i] <= floor) continue;
                if (donor < 0 || durations[i] > durations[donor]) donor = i;
            }
            if (donor < 0) return; // nothing left to steal

            long need = floor - durations[needy];
            long slack = durations[donor] - floor;
            long move = need < slack ? need : slack;
            durations[donor] -= move;
            durations[needy] += move;
        }
    }

    /// <summary>Split on whitespace; punctuation stays attached to its word.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>(8);
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            tokens.Add(text.Substring(start, i - start));
        }
        return tokens;
    }

    /// <summary>
    /// Weight = letters + digits, punctuation ignored; at least 1 so a token like
    /// "—" or "..." still gets a share (and cannot zero the total).
    /// </summary>
    private static int Weight(string token)
    {
        int count = 0;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsLetterOrDigit(token[i])) count++;
        }
        return count < 1 ? 1 : count;
    }
}
