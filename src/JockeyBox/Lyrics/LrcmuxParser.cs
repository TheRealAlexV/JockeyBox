using System.Globalization;
using System.Text.Json;

namespace JockeyBox.Lyrics;

/// <summary>
/// Parser for the lrcmux <c>/get?format=json</c> payload:
/// <code>
/// {
///   "lines": [
///     { "text": "Hello", "start": 12340, "end": 13000,
///       "words": [ { "text": "Hel", "start": 12340, "end": 12500 } ] }
///   ]
/// }
/// </code>
/// All times are <b>milliseconds</b> from the start of the track.
///
/// Deliberately separate from the HTTP client so it can be tested against
/// saved fixtures without a network.
/// </summary>
public static class LrcmuxParser
{
    /// <summary>
    /// Parse the JSON body. Null when the payload is not an object with a
    /// non-empty <c>lines</c> array. A structurally broken line/word is skipped
    /// rather than failing the whole document; a document with no usable line
    /// is null.
    /// </summary>
    public static LyricsDocument? Parse(string json, string source, string sourceId = "")
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("lines", out var linesElement)
                || linesElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var lines = new List<LyricLine>();
            foreach (var lineElement in linesElement.EnumerateArray())
            {
                var line = ParseLine(lineElement);
                if (line is not null) lines.Add(line);
            }

            if (lines.Count == 0) return null;

            lines.Sort(static (a, b) => a.Time.CompareTo(b.Time));

            return new LyricsDocument
            {
                Synced = true,
                Lines = lines,
                Source = source,
                SourceId = sourceId,
            };
        }
        catch
        {
            return null;
        }
    }

    private static LyricLine? ParseLine(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (!TryGetMilliseconds(element, "start", out var start)) return null;

        var text = GetText(element);

        List<LyricWord>? words = null;
        if (element.TryGetProperty("words", out var wordsElement)
            && wordsElement.ValueKind == JsonValueKind.Array)
        {
            // Raw spans in document order; ends stay optional so a row that only
            // carries a start is held until the next word rather than emitted
            // as a zero-length span.
            var raw = new List<(TimeSpan? Start, TimeSpan? End, string Text)>();
            foreach (var wordElement in wordsElement.EnumerateArray())
            {
                if (wordElement.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetMilliseconds(wordElement, "start", out var wordStart)) continue;

                var wordText = GetText(wordElement);
                if (wordText.Length == 0) continue; // empty / whitespace-only span

                TimeSpan? wordEnd = TryGetMilliseconds(wordElement, "end", out var e) && e > wordStart
                    ? e
                    : null;

                raw.Add((wordStart, wordEnd, wordText));
            }

            var lineEnd = TryGetMilliseconds(element, "end", out var le) ? le : (TimeSpan?)null;
            var built = WordSpanBuilder.Build(raw, start, lineEnd);
            if (built.Count > 0) words = built;
        }

        if (text.Length == 0)
        {
            if (words is null) return null;
            text = string.Join(' ', words.Select(static w => w.Text));
        }

        return new LyricLine(start, text) { Words = words };
    }

    private static string GetText(JsonElement element)
    {
        if (!element.TryGetProperty("text", out var textElement)) return "";
        if (textElement.ValueKind != JsonValueKind.String) return "";
        return Collapse(textElement.GetString());
    }

    /// <summary>Read a millisecond timestamp (accepts integer or decimal JSON numbers).</summary>
    private static bool TryGetMilliseconds(JsonElement element, string property, out TimeSpan value)
    {
        value = default;
        if (!element.TryGetProperty(property, out var propertyElement)) return false;

        double milliseconds;
        if (propertyElement.ValueKind == JsonValueKind.Number)
        {
            if (!propertyElement.TryGetDouble(out milliseconds)) return false;
        }
        else if (propertyElement.ValueKind == JsonValueKind.String
                 && double.TryParse(propertyElement.GetString(), NumberStyles.Float,
                     CultureInfo.InvariantCulture, out var parsed))
        {
            milliseconds = parsed;
        }
        else
        {
            return false;
        }

        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return false;

        value = TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond,
            MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>Trim and collapse whitespace runs to a single space.</summary>
    private static string Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new System.Text.StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
