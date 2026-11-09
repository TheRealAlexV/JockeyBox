using System.IO;
using JockeyBox.Audio.Beat;

namespace JockeyBox.Library;

/// <summary>
/// Scans the library and pairs each music file with its karaoke partner.
///
/// Two layouts are supported at once:
/// <list type="bullet">
/// <item><b>Merged (one root)</b> - the partner sits beside its original as
/// "Song.karaoke.ext" (see <see cref="KaraokeNaming"/>). This is what the
/// migration produces and what new separations write.</item>
/// <item><b>Split (two roots)</b> - the partner mirrors the music tree under the
/// karaoke root, matched by an extension-insensitive relative path. Kept so an
/// app updated before the migration runs still shows every track.</item>
/// </list>
///
/// Either way the music tree is the master list, <see cref="TrackEntry.RelativeKey"/>
/// is the base key ("artist/album/song") for both files of a pair, and
/// karaoke-only files are still shown so stray leftovers remain visible.
/// </summary>
public class LibraryScanner
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".mp4"
    };

    private readonly TagCache _tagCache;

    public LibraryScanner(TagCache tagCache) => _tagCache = tagCache;

    /// <summary>
    /// Relative pairing key: path without extension, case-insensitive, and
    /// without the karaoke marker. "Artist\Album\Song.karaoke.flac" therefore
    /// yields the same key as "Artist\Album\Song.mp3", which is what merges the
    /// pair into one row (and what keeps the key a base key everywhere else).
    /// </summary>
    public static string PairKey(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        var dir = Path.GetDirectoryName(rel) ?? "";
        return Path.Combine(dir, KaraokeNaming.BaseFileName(rel));
    }

    /// <summary>
    /// Full scan of the library. <paramref name="karaokeRoot"/> may be blank or
    /// the same folder as <paramref name="musicRoot"/> (the merged layout); it is
    /// then not walked a second time. Runs on a background thread and reports
    /// progress as (processed, total) while tags are extracted.
    /// </summary>
    public List<TrackEntry> Scan(string musicRoot, string karaokeRoot,
                                 IProgress<(int done, int total)>? progress = null,
                                 CancellationToken ct = default)
    {
        var tracks = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: the music tree is the master list. Karaoke files are skipped
        // here and attached in pass 2, so a "Song.karaoke.mp3" can never claim the
        // row that belongs to "Song.mp3" whatever order the file system returns
        // them in. TryAdd keeps the first file when two share a key with
        // different extensions.
        var musicFiles = EnumerateAudioFiles(musicRoot).ToList();
        foreach (var file in musicFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (KaraokeNaming.IsKaraokeFile(file)) continue;

            var key = PairKey(musicRoot, file);
            tracks.TryAdd(key, new TrackEntry
            {
                RelativeKey = key,
                RelativePath = Path.GetRelativePath(musicRoot, file),
                MusicPath = file,
            });
        }

        // Pass 2a: merged-layout partners living in the music tree.
        AttachKaraoke(musicRoot, musicFiles.Where(KaraokeNaming.IsKaraokeFile), tracks, ct);

        // Pass 2b: the legacy split layout. Skipped when both settings name the
        // same folder (or the karaoke root is unset), so the merged library is
        // never scanned twice and cannot produce duplicate rows.
        if (!SameRoot(musicRoot, karaokeRoot))
            AttachKaraoke(karaokeRoot, EnumerateAudioFiles(karaokeRoot), tracks, ct);

        // Pass 3: metadata (cached; only new/changed files hit TagLib#).
        var list = tracks.Values.OrderBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        int done = 0;
        foreach (var track in list)
        {
            ct.ThrowIfCancellationRequested();
            var metaSource = track.MusicPath ?? track.KaraokePath!;
            try
            {
                var tags = _tagCache.GetTags(metaSource);
                track.Title = tags.Title;
                track.Artist = tags.Artist;
                track.Album = tags.Album;
                track.Bpm = tags.Bpm;

                // Precedence: an explicit TKEY tag always wins. Only when the
                // file states no key do we show the analysis estimate, which is
                // marked as such on the grid so a later tag supersedes it.
                if (!string.IsNullOrWhiteSpace(tags.Key)) BeatGridStore.Shared?.MarkTagKey(metaSource);
                track.Key = TrackKeyResolver.Resolve(tags.Key, BeatGridStore.Shared?.Get(metaSource));

                track.Duration = TimeSpan.FromMilliseconds(tags.DurationMs);
            }
            catch
            {
                track.Title = Path.GetFileNameWithoutExtension(metaSource);
            }
            progress?.Report((++done, list.Count));
        }

        _tagCache.Prune(list.SelectMany(t => new[] { t.KaraokePath, t.MusicPath })
                            .Where(p => p != null)!);
        _tagCache.Save();
        return list;
    }

    /// <summary>
    /// Attach <paramref name="files"/> as karaoke partners by base relative key:
    /// a matching music row gains its <see cref="TrackEntry.KaraokePath"/>, and a
    /// file with no music row becomes a karaoke-only row (as before). The first
    /// match wins; same-name/different-extension duplicates are rare and keeping
    /// one preserves the old behaviour.
    /// </summary>
    private static void AttachKaraoke(string root, IEnumerable<string> files,
                                      Dictionary<string, TrackEntry> tracks, CancellationToken ct)
    {
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var key = PairKey(root, file);
            if (tracks.TryGetValue(key, out var entry))
            {
                entry.KaraokePath ??= file;
            }
            else
            {
                tracks[key] = new TrackEntry
                {
                    RelativeKey = key,
                    RelativePath = Path.GetRelativePath(root, file),
                    KaraokePath = file,
                };
            }
        }
    }

    /// <summary>
    /// True when both settings name one folder (or the karaoke root is unset):
    /// the merged library is scanned once, not twice.
    /// </summary>
    private static bool SameRoot(string musicRoot, string karaokeRoot)
    {
        if (string.IsNullOrWhiteSpace(karaokeRoot)) return true;
        if (string.IsNullOrWhiteSpace(musicRoot)) return false;

        return string.Equals(NormalizeRoot(musicRoot), NormalizeRoot(karaokeRoot),
                              StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.GetFullPath(root.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable root can only be compared as written.
            return root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static IEnumerable<string> EnumerateAudioFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Skip junctions/symlinks: a cyclic reparse point would otherwise
            // recurse forever. (Also keeps the default hidden/system skip.)
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (AudioExtensions.Contains(Path.GetExtension(file)))
                yield return file;
        }
    }
}
