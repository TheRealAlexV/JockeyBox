using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--align-lyrics "&lt;path-or-artist-title&gt;"</c>: run the lyric alignment pipeline headlessly.
/// </summary>
internal static class LyricAlignCli
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--align-lyrics") != null;

    public static int Run(string[] args)
    {
        string? spec = Harness.Flag(args, "--align-lyrics");
        if (string.IsNullOrWhiteSpace(spec))
        {
            Console.Error.WriteLine("align-lyrics: --align-lyrics \"<audio-path-or-artist|title>\" is required");
            return 2;
        }

        TrackEntry track;
        if (File.Exists(spec))
        {
            var fi = new FileInfo(spec);
            var tagCache = new TagCache();
            var cached = tagCache.GetTags(fi.FullName);
            var title = !string.IsNullOrWhiteSpace(cached.Title) ? cached.Title : Path.GetFileNameWithoutExtension(fi.Name);
            var artist = !string.IsNullOrWhiteSpace(cached.Artist) ? cached.Artist : "";
            if (string.IsNullOrWhiteSpace(artist))
            {
                int dash = title.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0)
                {
                    artist = title[..dash].Trim();
                    title = title[(dash + 3)..].Trim();
                }
            }

            track = new TrackEntry
            {
                MusicPath = fi.FullName,
                RelativeKey = "align/" + fi.Name,
                RelativePath = fi.Name,
                Title = title,
                Artist = artist,
                Duration = TimeSpan.Zero
            };
        }
        else
        {
            int pipe = spec.IndexOf('|');
            string artist = pipe < 0 ? "" : spec[..pipe].Trim();
            string title = pipe < 0 ? spec.Trim() : spec[(pipe + 1)..].Trim();

            track = new TrackEntry
            {
                MusicPath = spec,
                RelativeKey = "align/" + title.ToLowerInvariant().Replace(' ', '-'),
                RelativePath = title + ".mp3",
                Title = title,
                Artist = artist,
                Duration = TimeSpan.Zero
            };
        }

        Console.Out.WriteLine($"align-lyrics: track=\"{track.Artist} - {track.Title}\" path=\"{track.MusicPath}\"");
        Console.Out.Flush();

        var settings = AppSettings.Load();
        var trackMeta = TrackMetadataStore.Open();
        var lyricsService = new LyricsService(settings);
        using var alignService = new LyricAlignmentService(trackMeta, lyricsService);

        // First resolve lyrics (or load existing)
        var lookup = Task.Run(() => lyricsService.GetLyricsAsync(track, CancellationToken.None)).GetAwaiter().GetResult();
        if (lookup.Document == null || lookup.Document.Lines.Count == 0)
        {
            Console.Error.WriteLine($"align-lyrics: no ground truth lyrics found for track ({lookup.MissStatus})");
            return 1;
        }

        Console.Out.WriteLine($"align-lyrics: got unaligned lyrics ({lookup.Document.Lines.Count} lines) from {lookup.Document.Source}");
        Console.Out.Flush();

        var aligned = Task.Run(() => alignService.AlignAsync(track, lookup.Document, CancellationToken.None)).GetAwaiter().GetResult();
        if (aligned == null)
        {
            Console.Error.WriteLine("align-lyrics: alignment failed (check python sidecar logs or audio file)");
            return 1;
        }

        Console.Out.WriteLine($"align-lyrics: SUCCESS aligned {aligned.Lines.Count} lines (synced={aligned.Synced}, source={aligned.SourceId})");
        return 0;
    }
}
