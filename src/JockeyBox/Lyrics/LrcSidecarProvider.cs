using System.IO;
using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// Highest-priority provider: a manually curated .lrc file sitting next to the
/// Original (or Karaoke) audio file, e.g. "song.mp3" + "song.lrc".
/// </summary>
public class LrcSidecarProvider : ILyricsProvider
{
    public string Name => "Local .lrc";

    public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        foreach (var audioPath in new[] { track.MusicPath, track.KaraokePath })
        {
            if (audioPath == null) continue;
            var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
            if (!File.Exists(lrcPath)) continue;

            try
            {
                // Sanity cap: real .lrc files are a few KB; refuse absurd ones.
                if (new FileInfo(lrcPath).Length > 2 * 1024 * 1024) continue;

                var doc = LrcParser.Parse(File.ReadAllText(lrcPath), Name);
                if (doc != null) return Task.FromResult<LyricsDocument?>(doc);
            }
            catch
            {
                // unreadable sidecar - fall through to other providers
            }
        }
        return Task.FromResult<LyricsDocument?>(null);
    }
}
