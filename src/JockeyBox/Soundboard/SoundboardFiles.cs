using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JockeyBox.Soundboard;

/// <summary>
/// File-naming and path rules for the soundboard folder
/// (<c>%APPDATA%\JockeyBox\soundboard</c>).
///
/// <para>Everything here is pure so the rules the UI relies on - "is this file
/// ours to delete?", "does this name collide?" - are unit-testable without a
/// window, an audio engine or a running app. The delete rule is the important
/// one: a pad may only ever remove a file <i>inside</i> the board folder, and a
/// prefix check that is not separator-aware would let
/// <c>…\soundboard-backup\x.mp3</c> pass as "inside soundboard".</para>
/// </summary>
public static class SoundboardFiles
{
    /// <summary>Audio containers the board accepts from a local file or a URL import.</summary>
    public static readonly IReadOnlyList<string> AudioExtensions = new[]
    {
        ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus", ".aac", ".wma", ".webm",
    };

    /// <summary>Filter string for the file picker.</summary>
    public const string OpenFileFilter =
        "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.opus;*.aac;*.wma|All files|*.*";

    /// <summary>True when the extension is one the board can play.</summary>
    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        return AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="path"/> is the board folder or a file directly
    /// below it. Separator-aware: <c>…\soundboard-2\x.mp3</c> is *not* inside
    /// <c>…\soundboard</c>.
    /// </summary>
    public static bool IsInside(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

        string normalRoot, normalPath;
        try
        {
            normalRoot = Normalise(Path.GetFullPath(root));
            normalPath = Normalise(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            // Invalid characters, unrooted paths on a missing drive, …
            return false;
        }

        normalRoot = normalRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (normalPath.Length == normalRoot.Length)
            return string.Equals(normalPath, normalRoot, StringComparison.OrdinalIgnoreCase);

        return normalPath.StartsWith(
            normalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Board-local file name with characters Windows rejects replaced by '_'.</summary>
    public static string SanitizeFileName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length == 0) return "sound";

        var builder = new StringBuilder(value.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in value)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        // Trailing dots and spaces are legal in .NET paths but not in Win32.
        var cleaned = builder.ToString().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "sound" : cleaned;
    }

    /// <summary>
    /// Title turned into a readable file stem: letters, digits, spaces, dash and
    /// underscore survive, runs of anything else collapse to a single space.
    /// </summary>
    public static string Slug(string? title, int maxLength = 60)
    {
        var source = (title ?? "").Trim();
        if (source.Length == 0) return "sound";

        var builder = new StringBuilder(source.Length);
        var lastWasSpace = false;
        foreach (var c in source)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                builder.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        var slug = builder.ToString().Trim();
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd();
        return slug.Length == 0 ? "sound" : slug;
    }

    /// <summary>
    /// "name.ext" -> "name (2).ext" until the path is free. Loop is bounded so a
    /// pathological folder cannot spin here.
    /// </summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// A free stem for a fresh download: "horn", then "horn (2)", … A download's
    /// extension is chosen by yt-dlp/ffmpeg, so uniqueness is checked across all
    /// media extensions rather than for one concrete file.
    /// </summary>
    public static string UniqueStem(string directory, string stem)
    {
        var safe = SanitizeFileName(stem);
        if (!StemTaken(directory, safe)) return safe;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{safe} ({i})";
            if (!StemTaken(directory, candidate)) return candidate;
        }

        return $"{safe}-{Guid.NewGuid():N}";
    }

    private static bool StemTaken(string directory, string stem)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            foreach (var extension in AudioExtensions)
            {
                if (File.Exists(Path.Combine(directory, stem + extension))) return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Two paths refer to the same file, ignoring separator style.</summary>
    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Normalise(Path.GetFullPath(left)),
                Normalise(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Normalise(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
}
