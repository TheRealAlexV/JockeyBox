using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// A lyrics source. Providers are tried in priority order by LyricsService and
/// return null when they have nothing for the track (or are not configured).
/// </summary>
public interface ILyricsProvider
{
    /// <summary>Stable, settings-safe id ("lrc", "spotify-color", ...). Never localised.</summary>
    string Id { get; }

    /// <summary>Display name shown in the UI.</summary>
    string Name { get; }

    Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct);
}
