using JockeyBox.Library;
using JockeyBox.Tools;

namespace JockeyBox.Separation;

/// <summary>Why a karaoke request was allowed or refused.</summary>
public enum KaraokeDenial
{
    Allowed,
    NoTrack,
    NoMusicPath,
    AlreadyHasKaraoke,
    NoKaraokeFolder,
    EngineNotInstalled,
}

/// <summary>
/// The pre-flight check behind "Make Karaoke". Every refusal writes exactly one
/// greppable log line naming the track, so a request that visibly did nothing is
/// still findable in the console's LOG list. The operator's complaint was that
/// some karaoke tracks were never mentioned anywhere; silence is what this
/// removes, and being a plain static makes every refusal path testable.
/// </summary>
public static class KaraokeGate
{
    public static KaraokeDenial Check(TrackEntry? track, string? karaokeDirectory, bool engineInstalled)
    {
        if (track is null) return Warn(KaraokeDenial.NoTrack, "no track selected");

        var title = string.IsNullOrWhiteSpace(track.Title) ? track.RelativePath : track.Title;

        if (track.MusicPath is null)
            return Warn(KaraokeDenial.NoMusicPath, $"no music path: {track.RelativeKey}");

        // Not a fault: the operator asked twice, or the file already has its
        // partner. Logged at INFO so it is findable without looking like a failure.
        if (!track.NeedsKaraoke)
        {
            Log.Info($"Karaoke skipped: already has karaoke: {title}");
            return KaraokeDenial.AlreadyHasKaraoke;
        }

        if (string.IsNullOrWhiteSpace(karaokeDirectory))
            return Warn(KaraokeDenial.NoKaraokeFolder, $"no Karaoke folder: {title}");

        if (!engineInstalled)
            return Warn(KaraokeDenial.EngineNotInstalled, $"engine not installed: {title}");

        return KaraokeDenial.Allowed;
    }

    private static KaraokeDenial Warn(KaraokeDenial denial, string what)
    {
        Log.Warn($"Karaoke skipped: {what}");
        return denial;
    }
}
