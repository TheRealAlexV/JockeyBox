using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Sources;

/// <summary>Where a track or playlist came from.</summary>
public enum MediaSource
{
    /// <summary>A track someone added by hand (search result, pasted URL).</summary>
    Manual,
    Spotify,
    YouTube,
}

/// <summary>Lifecycle of a source track with respect to the local library.</summary>
public enum SourceTrackState
{
    /// <summary>Known to the app, not downloaded.</summary>
    New,
    /// <summary>Waiting in, or being worked on by, the download queue.</summary>
    Queued,
    Downloading,
    /// <summary>Resolved to a file that exists in the local library.</summary>
    InLibrary,
    Failed,
}

/// <summary>
/// One track from any remote source. Both Spotify imports and YouTube results
/// are normalised into this shape so downloading, playback, playlist handling
/// and karaoke chaining all have a single input type.
///
/// <see cref="Id"/> is the stable remote identity (Spotify track id or YouTube
/// video id) and is what the download ledger keys on.
/// </summary>
public partial class SourceTrack : ObservableObject
{
    public string Id { get; set; } = "";
    public MediaSource Source { get; set; }

    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Isrc { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public double DurationMs { get; set; }

    /// <summary>YouTube id once known (always set for YouTube items, filled in when a Spotify track resolves).</summary>
    public string? VideoId { get; set; }

    /// <summary>Name of the playlist this track was imported from - used by the playlist layout preset.</summary>
    public string? PlaylistName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateGlyph), nameof(IsBusy))]
    private SourceTrackState _state = SourceTrackState.New;

    /// <summary>0-100 while <see cref="Source"/> is being fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateGlyph), nameof(IsBusy))]
    private string? _libraryPath;

    [ObservableProperty]
    private string? _error;

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    [JsonIgnore]
    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";

    /// <summary>"yt-dlp ytsearch" query for this track.</summary>
    [JsonIgnore]
    public string SearchQuery => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} {Title}";

    /// <summary>Fallback display string for controls that render items without a template.</summary>
    public override string ToString() => DisplayName;

    [JsonIgnore]
    public string StateGlyph => State switch
    {
        SourceTrackState.Queued => "\u2193",
        SourceTrackState.Downloading => "\u22ef",
        SourceTrackState.InLibrary => "\u2713",
        SourceTrackState.Failed => "\u2716",
        _ => "\u25cf",
    };

    [JsonIgnore]
    public string ProgressText => State == SourceTrackState.Downloading
        ? $"{Progress:0}%"
        : State == SourceTrackState.InLibrary ? "in library" : "";

    [JsonIgnore]
    public bool IsBusy => State is SourceTrackState.Queued or SourceTrackState.Downloading;

    /// <summary>Copy this track into another playlist without dragging its live state along.</summary>
    public SourceTrack Clone() => new()
    {
        Id = Id,
        Source = Source,
        Title = Title,
        Artist = Artist,
        Album = Album,
        Isrc = Isrc,
        CoverUrl = CoverUrl,
        DurationMs = DurationMs,
        VideoId = VideoId,
        PlaylistName = PlaylistName,
        State = State,
        LibraryPath = LibraryPath,
    };
}
