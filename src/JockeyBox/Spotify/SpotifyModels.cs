using System.Linq;
using System.Text.Json.Serialization;
using JockeyBox.Sources;

namespace JockeyBox.Spotify;

/// <summary>
/// One Spotify playlist as shown in the import picker. <see cref="TrackCount"/>
/// comes from the paged envelope's total, so a 10k-track playlist still costs
/// one request to describe.
/// </summary>
public sealed record SpotifyPlaylistInfo(
    string Id,
    string Name,
    int TrackCount,
    string ImageUrl,
    string OwnerName,
    bool IsLikedSongs)
{
    /// <summary>"12 tracks" / "1 track" - the picker shows this verbatim.</summary>
    public string DisplayCount => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    /// <summary>Accessible/automation name; without this the record's dump leaks into UIA.</summary>
    public override string ToString() => $"{Name} \u00b7 {DisplayCount}";
}

/// <summary>The signed-in Spotify account (only the fields the UI needs).</summary>
public sealed record SpotifyUser(string Id, string DisplayName, string ImageUrl);

// ---- wire DTOs ------------------------------------------------------------
//
// Deliberately small and explicit: [JsonPropertyName] on each member keeps the
// mapping obvious and immune to naming-policy changes. Only the fields the app
// consumes are modelled; everything else in a Spotify payload is ignored.

/// <summary>A track (or episode) object as it appears inside playlist and saved-track pages.</summary>
public sealed class SpotifyTrackDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
    [JsonPropertyName("is_local")] public bool IsLocal { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("artists")] public List<SpotifyArtistDto>? Artists { get; set; }
    [JsonPropertyName("album")] public SpotifyAlbumDto? Album { get; set; }
    [JsonPropertyName("external_ids")] public SpotifyExternalIdsDto? ExternalIds { get; set; }

    /// <summary>
    /// False for entries we cannot download: locally imported files, podcast
    /// episodes, and anonymous tracks (all of which have no stable id).
    /// </summary>
    [JsonIgnore]
    public bool IsPlayable =>
        !string.IsNullOrWhiteSpace(Id)
        && !IsLocal
        && !string.Equals(Type, "episode", StringComparison.OrdinalIgnoreCase);
}

public sealed class SpotifyArtistDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class SpotifyAlbumDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("images")] public List<SpotifyImageDto>? Images { get; set; }
}

public sealed class SpotifyImageDto
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class SpotifyExternalIdsDto
{
    [JsonPropertyName("isrc")] public string? Isrc { get; set; }
}

/// <summary>Mapping from Spotify's wire shape onto the app's common track model.</summary>
public static class SpotifyMapping
{
    /// <summary>
    /// Normalise a track into a <see cref="SourceTrack"/>. <paramref name="playlistName"/>
    /// is stamped on the result so the playlist download layout can use it; the
    /// track state is left at its default (<see cref="SourceTrackState.New"/>).
    /// </summary>
    public static SourceTrack ToSourceTrack(this SpotifyTrackDto dto, string? playlistName) => new()
    {
        Id = dto.Id ?? "",
        Source = MediaSource.Spotify,
        Title = dto.Name ?? "",
        Artist = dto.Artists == null
            ? ""
            : string.Join(", ", dto.Artists
                .Select(a => a.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))),
        Album = dto.Album?.Name ?? "",
        Isrc = dto.ExternalIds?.Isrc ?? "",
        CoverUrl = dto.Album?.Images?.FirstOrDefault()?.Url ?? "",
        DurationMs = dto.DurationMs,
        VideoId = null,
        PlaylistName = playlistName,
    };
}
