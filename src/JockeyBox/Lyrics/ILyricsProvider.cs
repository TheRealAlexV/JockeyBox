using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// A lyrics source. Providers are tried in priority order by LyricsService and
/// return null when they have nothing for the track (or are not configured).
/// </summary>
public interface ILyricsProvider
{
    string Name { get; }
    Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct);
}
