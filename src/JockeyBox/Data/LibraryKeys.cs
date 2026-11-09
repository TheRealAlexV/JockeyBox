using System.Globalization;
using System.Text;
using JockeyBox.Sources;

namespace JockeyBox.Data;

/// <summary>
/// The normalisation rules the durable store keys on. They are deliberately the
/// same rules the JSON era already used, so a key written by 1.5.0 still matches
/// the same file after the migration:
///
///  * <see cref="PathKey"/> - absolute file path, separators folded to <c>/</c>,
///    duplicate/leading/trailing separators removed, lowercased. This is the
///    identity of a row in <c>tracks</c> (a case- or separator-variant path
///    must not create a second track).
///  * <see cref="LocalKey"/> - <see cref="SourceTrack.NormalizeLocalKey"/>:
///    library-relative path, extension stripped, lowercased. Identity of an
///    app-owned playlist row.
///  * <see cref="MatchKey"/> - <see cref="SourceMatcher.Normalize"/> plus a
///    diacritic fold, i.e. exactly <c>LibraryIndex.Key</c>, so the snapshot
///    resolver answers the same as the in-memory index it replaces.
///  * <see cref="FileNameKeys"/> - file name without extension, plus the same
///    name with a leading track number dropped, as <c>LibraryIndex</c> builds.
/// </summary>
public static class LibraryKeys
{
    /// <summary>Identity of an app-owned playlist row (library-relative, extension stripped).</summary>
    public static string LocalKey(string? key) => SourceTrack.NormalizeLocalKey(key);

    /// <summary>
    /// Absolute path with <c>/</c> separators, no duplicate separators, no
    /// trailing separator. This is the <b>storage</b> form (it backs
    /// <c>tracks.path</c>/<c>tracks.path_key</c> and every membership key), not
    /// the form to hand to the filesystem or the UI on Windows - see
    /// <see cref="NativePath"/> for that. Case is preserved.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var text = path.Trim().Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];

        // Keep a rooted path rooted: "//server/share" and "/mnt/music" must not
        // collapse into a relative-looking string.
        var rooted = text.StartsWith('/');
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join('/', parts);

        return rooted && joined.Length > 0 ? "/" + joined : joined;
    }

    /// <summary>
    /// Identity of a library file: <see cref="NormalizePath"/> lowercased. Used
    /// for <c>tracks.path_key</c> and for playlist membership lookups.
    /// </summary>
    public static string PathKey(string? path) => NormalizePath(path).ToLowerInvariant();

    /// <summary>
    /// A path in the form this process's filesystem APIs and every keyed lookup
    /// expect: <see cref="NormalizePath"/> with the platform separator
    /// (<c>\</c> on Windows, <c>/</c> elsewhere). Every path that leaves the data
    /// layer passes through here, because the ledger, the tag cache and
    /// playlists.json all key on native paths - a forward-slash path on Windows
    /// misses every one of them and silently re-reads tags.
    /// </summary>
    public static string NativePath(string? path)
    {
        var normal = NormalizePath(path);
        if (normal.Length == 0) return "";

        var separator = System.IO.Path.DirectorySeparatorChar;
        return separator == '/' ? normal : normal.Replace('/', separator);
    }

    /// <summary>Strip combining marks so "Björk" and "Bjork" compare equal.</summary>
    public static string Fold(string? text)
    {
        var value = text ?? "";
        var ascii = true;
        foreach (var ch in value)
        {
            if (ch > 0x7F) { ascii = false; break; }
        }

        if (ascii) return value;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Artist/title match key: the library index's normalisation (case,
    /// punctuation, bracket noise) plus the diacritic fold.
    /// </summary>
    public static string MatchKey(string? text) => Fold(SourceMatcher.Normalize(text));

    /// <summary>
    /// Sort key for <c>tracks.sort_title</c>/<c>sort_artist</c>: folded, lower
    /// case, whitespace collapsed. Kept separate from <see cref="MatchKey"/> so
    /// sorting never silently drops punctuation from a displayed name.
    /// </summary>
    public static string SortKey(string? text)
    {
        var folded = Fold(text ?? "").ToLowerInvariant();
        var sb = new StringBuilder(folded.Length);
        var space = false;

        foreach (var ch in folded)
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Normalised file name, plus the same name with a leading track number dropped.</summary>
    public static IEnumerable<string> FileNameKeys(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;

        string name;
        try
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        var key = MatchKey(name);
        if (key.Length == 0) yield break;
        yield return key;

        // "01 Artist - Title" / "12. Title" also answer to the bare name.
        var space = key.IndexOf(' ');
        if (space <= 0) yield break;

        for (var i = 0; i < space; i++)
        {
            if (!char.IsAsciiDigit(key[i])) yield break;
        }

        if (space + 1 < key.Length) yield return key[(space + 1)..];
    }

    /// <summary>Unix milliseconds for a UTC <see cref="DateTime"/>; 0 for default/min values.</summary>
    public static long ToUnixMs(DateTime utc)
    {
        if (utc.Year < 1970) return 0;
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    /// <summary>Inverse of <see cref="ToUnixMs"/>; <see cref="DateTime.MinValue"/> for 0.</summary>
    public static DateTime FromUnixMs(long ms) =>
        ms <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
