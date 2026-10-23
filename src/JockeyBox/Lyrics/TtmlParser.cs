using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace JockeyBox.Lyrics;

/// <summary>
/// Parser for TTML lyric documents as served by Better Lyrics (Apple Music
/// syllable TTML) and Unison (community-synced TTML, richsync or linesync).
///
/// Shape handled:
/// <code>
/// &lt;tt&gt;&lt;body&gt;&lt;div&gt;
///   &lt;p begin="19.764" end="21.900"&gt;
///     &lt;span begin="19.764" end="20.105"&gt;When&lt;/span&gt;
///     &lt;span begin="20.105" end="20.326"&gt;you were&lt;/span&gt;
///   &lt;/p&gt;
/// &lt;/div&gt;&lt;/body&gt;&lt;/tt&gt;
/// </code>
///
/// Timestamps accept TTML's clock forms "hh:mm:ss.mmm", "mm:ss.mmm" and
/// "ss.mmm" (Apple Music uses the last one, Unison mixes the first two). A
/// timestamp attribute that exists but cannot be read fails the whole parse,
/// which the providers turn into a quiet null - malformed remote data must
/// never surface as an exception or as half-timed lyrics.
///
/// Decoration spans (<c>ttm:role="x-translation"</c>, <c>x-bg</c>) are dropped
/// from both the line text and the word spans: they are not karaoke tokens.
///
/// Word hygiene: span text is trimmed and inner whitespace runs collapse to a
/// single space. Empty spans (including pure-whitespace separators such as the
/// space between two spans) carry no token and are skipped.
/// </summary>
public static class TtmlParser
{
    private static readonly string[] DecorationRoles = ["x-translation", "x-bg"];

    /// <summary>
    /// Parse a TTML document; null when the XML is malformed, carries no timed
    /// paragraphs, or contains an unreadable timestamp.
    /// </summary>
    /// <param name="ttml">Raw TTML text.</param>
    /// <param name="source">Display name recorded on the document.</param>
    /// <param name="sourceId">Stable provider id recorded on the document.</param>
    public static LyricsDocument? Parse(string ttml, string source, string sourceId = "")
    {
        if (string.IsNullOrWhiteSpace(ttml)) return null;

        XDocument xdoc;
        try
        {
            var settings = new XmlReaderSettings
            {
                // Remote documents: never resolve DTDs or external entities.
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(ttml), settings);
            xdoc = XDocument.Load(reader);
        }
        catch
        {
            return null;
        }

        var body = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
        if (body is null) return null;

        var paragraphs = body.Descendants().Where(e => e.Name.LocalName == "p").ToList();
        if (paragraphs.Count == 0) return null;

        var lines = new List<LyricLine>(paragraphs.Count);
        foreach (var paragraph in paragraphs)
        {
            if (!TryParseParagraph(paragraph, out var line)) return null;
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

    /// <summary>
    /// Parse one &lt;p&gt;. Returns false only for a malformed timestamp (which
    /// rejects the document); true with a null line for an untimed/empty
    /// paragraph that should simply be skipped.
    /// </summary>
    private static bool TryParseParagraph(XElement paragraph, out LyricLine? line)
    {
        line = null;

        var raw = new List<(TimeSpan? Start, TimeSpan? End, string Text)>(8);
        if (!CollectSpans(paragraph, raw)) return false;

        TimeSpan? begin = null;
        if (TryGetAttribute(paragraph, "begin", out var beginText))
        {
            if (!TryParseTimestamp(beginText, out var value)) return false;
            begin = value;
        }

        TimeSpan? end = null;
        if (TryGetAttribute(paragraph, "end", out var endText))
        {
            if (!TryParseTimestamp(endText, out var value)) return false;
            end = value;
        }

        var text = LineText(paragraph);

        // No line timestamp: start at the first timed span if there is one.
        if (begin is null)
        {
            foreach (var span in raw)
            {
                if (span.Start is { } start)
                {
                    begin = start;
                    break;
                }
            }
        }

        // Untimed paragraph (plain metadata/credit) - nothing to display.
        if (begin is null) return true;

        var words = WordSpanBuilder.Build(raw, begin.Value, end);

        if (text.Length == 0)
        {
            if (words.Count == 0) return true;
            text = JoinWords(words);
        }

        line = new LyricLine(begin.Value, text)
        {
            Words = words.Count > 0 ? words : null,
        };
        return true;
    }

    /// <summary>
    /// Collect timed word spans that are direct children of the paragraph,
    /// with decoration (translation/background) subtrees removed first.
    /// </summary>
    private static bool CollectSpans(
        XElement paragraph,
        List<(TimeSpan? Start, TimeSpan? End, string Text)> raw)
    {
        var clean = WithoutDecoration(paragraph);

        foreach (var span in clean.Elements().Where(e => e.Name.LocalName == "span"))
        {
            var text = Collapse(span.Value);
            if (text.Length == 0) continue; // whitespace separator between spans

            TimeSpan? start = null;
            if (TryGetAttribute(span, "begin", out var beginText))
            {
                if (!TryParseTimestamp(beginText, out var value)) return false;
                start = value;
            }

            TimeSpan? end = null;
            if (TryGetAttribute(span, "end", out var endText))
            {
                if (!TryParseTimestamp(endText, out var value)) return false;
                end = value;
            }

            // An untimed span is line text only, not a karaoke token.
            if (start is null) continue;

            raw.Add((start, end, text));
        }
        return true;
    }

    /// <summary>Paragraph text with decoration spans removed, whitespace collapsed.</summary>
    private static string LineText(XElement paragraph) => Collapse(WithoutDecoration(paragraph).Value);

    /// <summary>
    /// Copy of <paramref name="paragraph"/> with translation/background subtrees
    /// removed. Copy-and-remove keeps the surrounding text nodes (the spaces
    /// between spans) intact, so no spacing has to be re-invented.
    /// </summary>
    private static XElement WithoutDecoration(XElement paragraph)
    {
        var clone = new XElement(paragraph);
        clone.Descendants().Where(IsDecoration).Remove();
        return clone;
    }

    private static bool IsDecoration(XElement element)
    {
        if (element.Name.LocalName != "span") return false;

        var role = element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, "role", StringComparison.Ordinal));
        if (role is null) return false;

        foreach (var decoration in DecorationRoles)
        {
            if (string.Equals(role.Value.Trim(), decoration, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Read an attribute by local name, ignoring namespace (TTML attributes are
    /// unqualified, but Apple mixes itunes:/ttm: attributes into the same
    /// elements).
    /// </summary>
    private static bool TryGetAttribute(XElement element, string localName, out string value)
    {
        var attribute = element.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, localName, StringComparison.Ordinal));
        value = attribute?.Value ?? "";
        return attribute is not null && value.Length > 0;
    }

    /// <summary>
    /// Strict TTML clock parse: "hh:mm:ss.mmm", "mm:ss.mmm" or "ss.mmm".
    /// Unknown shapes (signs, exponents, more than three fraction digits,
    /// seconds out of range) return false.
    /// </summary>
    public static bool TryParseTimestamp(string raw, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var parts = text.Split(':');
        if (parts.Length is 0 or > 3) return false;

        long hours = 0;
        long minutes = 0;

        // "hh:mm:ss.mmm" / "mm:ss.mmm" / "ss.mmm"
        if (parts.Length == 3)
        {
            if (!TryParseComponent(parts[0], out hours)) return false;
            if (!TryParseComponent(parts[1], out minutes)) return false;
            if (minutes > 59) return false;
        }
        else if (parts.Length == 2)
        {
            if (!TryParseComponent(parts[0], out minutes)) return false;
        }

        if (!TryParseSeconds(parts[^1], out decimal seconds)) return false;
        if (seconds >= 60m) return false;

        var totalSeconds = hours * 3600m + minutes * 60m + seconds;
        var ticks = (long)decimal.Round(totalSeconds * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero);
        if (ticks < 0) return false;

        value = TimeSpan.FromTicks(ticks);
        return true;
    }

    private static bool TryParseComponent(string raw, out long value)
    {
        value = 0;
        if (raw.Length == 0) return false;
        foreach (var c in raw)
        {
            if (c is < '0' or > '9') return false;
        }
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseSeconds(string raw, out decimal value)
    {
        value = 0;
        if (raw.Length == 0) return false;

        int dot = raw.IndexOf('.');
        var whole = dot < 0 ? raw : raw[..dot];
        if (whole.Length == 0 || !TryParseComponent(whole, out _)) return false;

        if (dot >= 0)
        {
            var fraction = raw[(dot + 1)..];
            // TTML milliseconds: at most three fraction digits.
            if (fraction.Length is 0 or > 3) return false;
            foreach (var c in fraction)
            {
                if (c is < '0' or > '9') return false;
            }
        }

        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Trim and collapse every whitespace run to one space.</summary>
    private static string Collapse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string JoinWords(List<LyricWord> words)
    {
        var sb = new StringBuilder();
        foreach (var word in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(word.Text);
        }
        return sb.ToString();
    }
}
