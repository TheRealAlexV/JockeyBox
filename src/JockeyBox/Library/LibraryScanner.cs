using System.IO;

namespace JockeyBox.Library;

/// <summary>
/// Scans the Music and Karaoke folders and pairs files by relative path with
/// the extension ignored (generated instrumentals may use a different format
/// than their source). The Music folder is the master list; karaoke-only
/// files are shown so stray leftovers remain visible.
/// </summary>
public class LibraryScanner
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".mp4"
    };

    private readonly TagCache _tagCache;

    public LibraryScanner(TagCache tagCache) => _tagCache = tagCache;

    /// <summary>Relative pairing key: path without extension, case-insensitive.</summary>
    public static string PairKey(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        var dir = Path.GetDirectoryName(rel) ?? "";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(rel));
    }

    /// <summary>
    /// Full scan of both folders. Runs on a background thread; reports
    /// progress as (processed, total) while tags are extracted.
    /// </summary>
    public List<TrackEntry> Scan(string musicRoot, string karaokeRoot,
                                 IProgress<(int done, int total)>? progress = null,
                                 CancellationToken ct = default)
    {
        var tracks = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: the Music tree is the master list. TryAdd keeps the first
        // file when two share a name with different extensions.
        foreach (var file in EnumerateAudioFiles(musicRoot))
        {
            ct.ThrowIfCancellationRequested();
            var key = PairKey(musicRoot, file);
            tracks.TryAdd(key, new TrackEntry
            {
                RelativeKey = key,
                RelativePath = Path.GetRelativePath(musicRoot, file),
                MusicPath = file,
            });
        }

        // Pass 2: attach karaoke files by extension-insensitive relative path.
        foreach (var file in EnumerateAudioFiles(karaokeRoot))
        {
            ct.ThrowIfCancellationRequested();
            var key = PairKey(karaokeRoot, file);
            if (tracks.TryGetValue(key, out var entry))
            {
                // Prefer the first match; warn-worthy duplicates (same name,
                // different extension) simply keep the first one found.
                entry.KaraokePath ??= file;
            }
            else
            {
                tracks[key] = new TrackEntry
                {
                    RelativeKey = key,
                    RelativePath = Path.GetRelativePath(karaokeRoot, file),
                    KaraokePath = file,
                };
            }
        }

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
