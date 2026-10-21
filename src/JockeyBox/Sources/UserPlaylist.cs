using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Sources;

/// <summary>
/// A user-visible playlist. It may be an imported Spotify playlist, an imported
/// YouTube playlist, or a hand-built list mixing both - the queue and the UI
/// treat every entry the same way.
/// </summary>
public partial class UserPlaylist : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = "";

    public MediaSource Source { get; set; } = MediaSource.Manual;

    /// <summary>Spotify playlist id or YouTube list id; null for hand-built playlists.</summary>
    public string? SourceId { get; set; }

    /// <summary>When true, "Sync playlists" re-fetches the source and queues new tracks.</summary>
    [ObservableProperty]
    private bool _syncEnabled;

    /// <summary>Per-playlist layout override (artist-album | artist | playlist); null = use the global setting.</summary>
    public string? LayoutOverride { get; set; }

    public DateTime LastSyncedUtc { get; set; }

    public ObservableCollection<SourceTrack> Tracks { get; set; } = new();

    /// <summary>Fallback display name for any control that renders items without a template.</summary>
    public override string ToString() => Name;

    [JsonIgnore]
    public int TrackCount => Tracks.Count;

    [JsonIgnore]
    public string Subtitle => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    [JsonIgnore]
    public string SourceGlyph => Source switch
    {
        MediaSource.Spotify => "\u266B",
        MediaSource.YouTube => "\u25B6",
        _ => "\u2261",
    };

    [JsonIgnore]
    public bool IsImported => SourceId != null;

    public void NotifyTrackCountChanged() => OnPropertyChanged(nameof(TrackCount));
}
