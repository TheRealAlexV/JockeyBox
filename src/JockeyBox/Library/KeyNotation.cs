namespace JockeyBox.Library;

/// <summary>
/// The one place in the app that knows key notation. Every key that enters the
/// model becomes Camelot - the wheel a DJ mixes on - whoever supplied it: a
/// TKEY note name ("Fm", "C#m"), an enharmonic spelling ("Dbm"), a code that is
/// already Camelot ("04a"), or a chroma estimate (Camelot to begin with).
///
/// <para>Majors land on the B codes, minors on the A codes, and a minor uses
/// the same number as its relative major (three semitones up), which is exactly
/// how the wheel is laid out: 8B = C major and 8A = A minor, 4B = F major and
/// 4A = F minor.</para>
///
/// <para>Anything else is returned verbatim: a garbage tag stays visible rather
/// than silently disappearing, blank stays blank, and null never becomes "".</para>
/// </summary>
public static class KeyNotation
{
    // Circle of fifths: C is 8B, and each fifth up advances the number by one,
    // wrapping 12 -> 1. Pitch class of the i-th fifth is (7 * i) % 12, so the
    // table is computed rather than typed: no octave of typos to make.
    private static readonly int[] MajorCodeByPitchClass = BuildMajorCodes();

    private static int[] BuildMajorCodes()
    {
        var codes = new int[12];
        for (var i = 0; i < 12; i++)
            codes[(7 * i) % 12] = ((8 + i - 1) % 12) + 1;
        return codes;
    }

    /// <summary>
    /// Canonical Camelot form of <paramref name="raw"/>, or the value unchanged
    /// when it is neither a note name nor a valid Camelot code.
    /// <paramref name="understood"/> is false for blanks and for anything not
    /// recognised.
    /// </summary>
    public static string? Normalize(string? raw, out bool understood)
    {
        understood = false;
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var trimmed = raw.Trim();

        if (TryParseCamelotCore(trimmed, out var number, out var letter))
        {
            understood = true;
            return $"{number}{letter}";
        }

        if (TryParseNote(trimmed, out var pitchClass, out var minor))
        {
            understood = true;
            // A minor is the relative minor of the major three semitones above
            // it, so both take the same wheel number; only the letter differs.
            var wheel = minor ? MajorCodeByPitchClass[(pitchClass + 3) % 12] : MajorCodeByPitchClass[pitchClass];
            return $"{wheel}{(minor ? 'A' : 'B')}";
        }

        return raw;
    }

    /// <summary>
    /// True when the value names a key, i.e. it normalises to a Camelot code.
    /// Used by sorting; note names are accepted here too so an un-normalised
    /// value cannot break the order.
    /// </summary>
    public static bool TryParseCamelot(string? value, out int number, out char letter)
    {
        number = 0;
        letter = 'A';
        var normalized = Normalize(value, out var understood);
        return understood && normalized is not null
            && TryParseCamelotCore(normalized, out number, out letter);
    }

    // Camelot: 1-12 then A or B. Tolerates lower case, leading zeros and
    // whitespace between the two ("04a", " 12 b ").
    private static bool TryParseCamelotCore(string value, out int number, out char letter)
    {
        number = 0;
        letter = 'A';
        var i = 0;
        while (i < value.Length && char.IsDigit(value[i])) i++;
        if (i == 0 || i > 2 || !int.TryParse(value[..i], out var parsed)) return false;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        if (i != value.Length - 1) return false;
        var suffix = char.ToUpperInvariant(value[i]);
        if (suffix is not ('A' or 'B') || parsed is < 1 or > 12) return false;
        number = parsed;
        letter = suffix;
        return true;
    }

    // A note name: letter A-G, optional accidentals, optional major/minor
    // suffix. "F", "Fm", "F#m", "Dbm", "Bb minor", "c# MIN" all parse.
    private static bool TryParseNote(string value, out int pitchClass, out bool minor)
    {
        pitchClass = 0;
        minor = false;

        var basePitch = char.ToUpperInvariant(value[0]) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1,
        };
        if (basePitch < 0) return false;

        var i = 1;
        var offset = 0;
        for (; i < value.Length; i++)
        {
            if (value[i] is '#' or '\u266F') offset++;
            else if (value[i] is 'b' or '\u266D') offset--;
            else break;
        }

        var suffix = value[i..].Trim();
        if (suffix.Length > 0 && !TryParseMode(suffix, out minor)) return false;

        pitchClass = ((basePitch + offset) % 12 + 12) % 12;
        return true;
    }

    // "m"/"min"/"minor" are minor; a bare "M" or "maj"/"major" are major.
    // Anything else after a note letter is not a key we can vouch for.
    private static bool TryParseMode(string suffix, out bool minor)
    {
        minor = false;
        if (suffix == "m") { minor = true; return true; }
        if (suffix == "M") return true;
        if (suffix.Equals("min", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("minor", StringComparison.OrdinalIgnoreCase))
        {
            minor = true;
            return true;
        }
        return suffix.Equals("maj", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("major", StringComparison.OrdinalIgnoreCase);
    }
}
