using System.Globalization;
using System.IO;
using JockeyBox.Library;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Downloads;

/// <summary>
/// Turns a <see cref="SourceTrack"/> into a library-relative path, resolves that
/// path against the configured Music root, and derives the karaoke path of a
/// music file.
///
/// The library is one tree: the karaoke version of a track lives beside it as
/// "Song.karaoke.ext" (<see cref="KaraokeNaming"/>), so nothing is mirrored into
/// a second root any more. <see cref="AppSettings.KaraokeDirectory"/> survives
/// only as the legacy split root, and the mirror mapping below still honours it
/// when the music root is unknown.
///
/// All layout logic lives here so the download queue, the library scanner and
/// the karaoke chaining code agree on where a file belongs.
/// </summary>
public static class DownloadPaths
{
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";
    private const string UnknownPlaylist = "Downloads";
    private const string DefaultExtension = ".mp3";
    private const string PresetArtistAlbum = "artist-album";
    private const string PresetArtist = "artist";
    private const string PresetPlaylist = "playlist";

    /// <summary>
    /// Everything the layout needs beyond the track itself: the playlist it was
    /// imported from, its 0-based position in that playlist (for "NN Title"),
    /// and a per-download preset override (empty = use the app setting).
    /// </summary>
    /// <param name="Track">The track being written.</param>
    /// <param name="PlaylistName">Playlist name, when the track came from one.</param>
    /// <param name="IndexInPlaylist">0-based position inside the playlist.</param>
    /// <param name="LayoutOverride">Per-download preset; empty falls back to settings.</param>
    public sealed record LayoutContext(
        SourceTrack Track,
        string? PlaylistName,
        int IndexInPlaylist,
        string LayoutOverride);

    /// <summary>
    /// Build the library-relative destination for <paramref name="ctx"/>.
    /// A non-empty <see cref="AppSettings.DownloadLayoutTemplate"/> wins;
    /// otherwise the preset from <see cref="LayoutContext.LayoutOverride"/> (or
    /// <see cref="AppSettings.DownloadLayoutPreset"/>) decides the shape.
    /// Every segment is sanitised for Windows and the file always ends in ".mp3".
    /// </summary>
    public static string BuildRelativePath(LayoutContext ctx, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(settings);

        var track = ctx.Track ?? throw new ArgumentNullException(nameof(ctx));

        var template = settings.DownloadLayoutTemplate?.Trim() ?? "";
        if (template.Length > 0)
        {
            var fromTemplate = FromTemplate(template, ctx, track);
            if (fromTemplate.Length > 0) return fromTemplate;
        }

        var preset = string.IsNullOrWhiteSpace(ctx.LayoutOverride)
            ? settings.DownloadLayoutPreset
            : ctx.LayoutOverride;

        return BuildPreset(preset, ctx, track);
    }

    /// <summary>
    /// Return <paramref name="absolutePath"/> when nothing occupies it, else the
    /// first free "name (2).ext" ... "name (999).ext" candidate.
    ///
    /// A karaoke partner ("Song.karaoke.ext") is not a music track: it never
    /// blocks a download to "Song.ext". A name that itself ends in the karaoke
    /// marker is never free, because the scanner would read it as the
    /// instrumental of the unrelated "Song.ext" - the loop below moves the marker
    /// out of the way instead.
    /// </summary>
    public static string EnsureUnique(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return absolutePath;

        if (!KaraokeNaming.IsKaraokeFile(absolutePath))
        {
            try
            {
                if (!File.Exists(absolutePath)) return absolutePath;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not probe '{absolutePath}'; using it as-is", ex);
                return absolutePath;
            }
        }

        var directory = Path.GetDirectoryName(absolutePath) ?? "";

        for (var n = 2; n <= 999; n++)
        {
            var candidate = Path.Combine(
                directory,
                KaraokeNaming.InsertInBaseName(
                    absolutePath, $" ({n.ToString(CultureInfo.InvariantCulture)})"));
            try
            {
                if (!File.Exists(candidate)) return candidate;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not probe '{candidate}'; using it as-is", ex);
                return candidate;
            }
        }

        var last = Path.Combine(directory, KaraokeNaming.InsertInBaseName(absolutePath, " (999)"));
        Log.Warn($"No free file name for '{absolutePath}' up to 999; reusing '{last}'");
        return last;
    }

    /// <summary>
    /// Resolve a library-relative path against <see cref="AppSettings.MusicDirectory"/>.
    /// An already-rooted path is returned unchanged.
    /// </summary>
    public static string Absolute(AppSettings settings, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "";
        if (Path.IsPathRooted(relativePath)) return relativePath;

        var root = settings?.MusicDirectory?.Trim() ?? "";
        return root.Length == 0 ? relativePath : Path.Combine(root, relativePath);
    }

    /// <summary>
    /// Inverse of <see cref="Absolute"/>: strip the Music root and return the
    /// library-relative remainder. Null-safe; paths outside the root (and paths
    /// that are already relative) come back unchanged.
    /// </summary>
    public static string RelativeTo(AppSettings settings, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return "";

        var full = absolutePath.Trim();
        var root = settings?.MusicDirectory?.Trim() ?? "";
        if (root.Length == 0 || !Path.IsPathRooted(full)) return full;

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = trimmedRoot + Path.DirectorySeparatorChar;

        if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return full[prefix.Length..];

        // Tolerate forward slashes in a path stored on another platform.
        var altPrefix = trimmedRoot + Path.AltDirectorySeparatorChar;
        if (full.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase))
            return full[altPrefix.Length..];

        return full;
    }

    /// <summary>
    /// The karaoke counterpart of a library path.
    ///
    /// In the merged one-root layout that is the sibling "Song.karaoke.ext" -
    /// same folder, same extension, marker inserted before it. When the music
    /// root is unknown (unset, or the path lies outside it) the legacy mapping
    /// into <see cref="AppSettings.KaraokeDirectory"/> is kept, so a settings file
    /// from before the merge still resolves. Accepts absolute or relative input.
    /// </summary>
    public static string KaraokeMirror(AppSettings settings, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";

        var relative = RelativeTo(settings, relativePath);
        if (Path.IsPathRooted(relative)) return relative; // outside the library - leave alone

        relative = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var music = settings?.MusicDirectory?.Trim() ?? "";
        if (music.Length > 0) return KaraokeNaming.KaraokePathFor(Path.Combine(music, relative));

        var karaoke = settings?.KaraokeDirectory?.Trim() ?? "";
        return karaoke.Length == 0 ? relative : Path.Combine(karaoke, relative);
    }

    /// <summary>
    /// Where a newly generated karaoke render for <paramref name="musicPath"/>
    /// belongs. The mode and the source path decide it, in one place
    /// (<see cref="KaraokeNaming.RenderPathFor"/>), and each mode owns a distinct
    /// marker: Instrumental writes "Song.karaoke.mp3", InstrumentalKeepBacking
    /// writes "Song.backing.mp3" and VocalsOnly writes "Song.vocals.mp3", all in
    /// the music file's own folder. Which marker a mode gets is owned by
    /// <see cref="KaraokeNaming.MarkerOf"/>; do not re-derive it here.
    ///
    /// Returns "" only when no folder can be decided - no music path and no
    /// karaoke root to fall back to - which callers report as a setup problem.
    /// When the music folder is unknown but <paramref name="relativeKey"/> and the
    /// legacy karaoke root are known, the old mirror mapping is used.
    /// </summary>
    public static string KaraokeOutputPath(AppSettings settings, string? musicPath,
                                           KaraokeStemMode mode, string? relativeKey = null)
    {
        var exact = KaraokeNaming.RenderPathFor(mode, musicPath);
        if (exact.Length > 0) return exact;

        // Unknown music folder: keep the legacy mirror into the karaoke root.
        var karaokeRoot = settings?.KaraokeDirectory?.Trim() ?? "";
        var key = (relativeKey ?? "").Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (karaokeRoot.Length > 0 && key.Length > 0)
            return Path.Combine(karaokeRoot, KaraokeNaming.RenderName(mode, key));

        return "";
    }

    // ---- internals --------------------------------------------------------

    /// <summary>Folder of a path, or "" when the path has none (or is unusable).</summary>
    private static string FolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        try
        {
            return Path.GetDirectoryName(path.Trim()) ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static string FromTemplate(string template, LayoutContext ctx, SourceTrack track)
    {
        var artist = Sanitize(FirstNonEmpty(track.Artist, UnknownArtist));
        var title = Sanitize(FirstNonEmpty(track.Title, "track"));
        var album = Sanitize(FirstNonEmpty(track.Album, UnknownAlbum));
        var playlist = Sanitize(FirstNonEmpty(ctx.PlaylistName, track.PlaylistName, UnknownPlaylist));
        var trackNumber = SourceMatcher.TrackNumber(Math.Max(0, ctx.IndexInPlaylist));
        var source = SourceLabel(track.Source);

        var expanded = template
            .Replace("{artist}", artist, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{album}", album, StringComparison.OrdinalIgnoreCase)
            .Replace("{track}", trackNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{playlist}", playlist, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", source, StringComparison.OrdinalIgnoreCase);

        var hasExtension = expanded.TrimEnd().EndsWith(DefaultExtension, StringComparison.OrdinalIgnoreCase);

        var parts = expanded.Split(
            new[] { '/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            Log.Warn("Download layout template produced an empty path; falling back to the preset layout");
            return "";
        }

        var segments = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var segment = Sanitize(parts[i]);
            if (i == parts.Length - 1 && !hasExtension) segment += DefaultExtension;
            segments.Add(segment);
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string BuildPreset(string? preset, LayoutContext ctx, SourceTrack track)
    {
        var artist = Sanitize(FirstNonEmpty(track.Artist, UnknownArtist));
        var title = Sanitize(FirstNonEmpty(track.Title, "track"));
        var album = Sanitize(FirstNonEmpty(track.Album, UnknownAlbum));
        var playlist = Sanitize(FirstNonEmpty(ctx.PlaylistName, track.PlaylistName, UnknownPlaylist));
        var trackNumber = SourceMatcher.TrackNumber(Math.Max(0, ctx.IndexInPlaylist));

        var normalised = (preset ?? "").Trim().ToLowerInvariant();
        var segments = normalised switch
        {
            PresetArtist => new List<string> { artist, $"{title}{DefaultExtension}" },

            PresetPlaylist => FirstNonEmpty(ctx.PlaylistName, track.PlaylistName) is { Length: > 0 }
                ? new List<string> { playlist, $"{artist} - {title}{DefaultExtension}" }
                : new List<string> { $"{artist} - {title}{DefaultExtension}" },

            // "artist-album" and anything unknown.
            _ => string.IsNullOrWhiteSpace(track.Album)
                ? new List<string> { artist, $"{title}{DefaultExtension}" }
                : new List<string> { artist, album, $"{trackNumber} {title}{DefaultExtension}" },
        };

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>Sanitise one path segment, splitting any reserved separator first.</summary>
    private static string Sanitize(string segment)
    {
        var cleaned = segment.Replace('/', '_').Replace('\\', '_').Trim();
        return SourceMatcher.SanitizeFileName(cleaned);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    private static string SourceLabel(MediaSource source) => source switch
    {
        MediaSource.Spotify => "Spotify",
        MediaSource.YouTube => "YouTube",
        _ => "Local",
    };
}
