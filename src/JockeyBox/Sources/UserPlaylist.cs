using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Sources;

/// <summary>
/// What a playlist <em>is</em>, i.e. what its membership rows mean:
/// <see cref="Imported"/> mirrors a Spotify/YouTube playlist (rows keyed by the
/// remote id), <see cref="Internal"/> is app-owned (rows keyed by the library
/// file, <see cref="SourceTrack.LocalKey"/>).
/// </summary>
public enum PlaylistKind
{
    /// <summary>Mirror of a remote playlist; membership is remote ids.</summary>
    Imported,
    /// <summary>App-owned list of library files; membership is library keys.</summary>
    Internal,
}

/// <summary>
/// A user-visible playlist. It may be an imported Spotify playlist, an imported
/// YouTube playlist, or an app-owned list of library tracks - the queue and the
/// UI treat the tracks the same way, but membership identity differs (see
/// <see cref="Kind"/>).
/// </summary>
public partial class UserPlaylist : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = "";

    public MediaSource Source { get; set; } = MediaSource.Manual;

    /// <summary>
    /// Membership semantics. Persisted as an int; older files had no field, so
    /// <see cref="NormalizeIdentity"/> derives it on load.
    /// </summary>
    public PlaylistKind Kind { get; set; } = PlaylistKind.Imported;

    /// <summary>Spotify playlist id or YouTube list id; null for app-owned playlists.</summary>
    public string? SourceId { get; set; }

    /// <summary>When true, "Sync playlists" re-fetches the source and queues new tracks.</summary>
    [ObservableProperty]
    private bool _syncEnabled;

    public DateTime LastSyncedUtc { get; set; }

    public ObservableCollection<SourceTrack> Tracks { get; set; } = new();

    /// <summary>
    /// Settle what kind of playlist this is after loading. Idempotent, so it can
    /// run on every load without touching the file again.
    ///
    /// <see cref="Kind"/> is persisted for anything this build creates, so the
    /// inference below only has to serve files written before the field existed.
    /// It must not claim the YouTube tab's hand-built lists: those are
    /// <see cref="MediaSource.Manual"/> with no source id too, but their rows are
    /// remote videos (no <see cref="SourceTrack.LocalKey"/>). An app-owned list is
    /// therefore "Manual, no source id, and every row is a library file".
    /// </summary>
    public void NormalizeIdentity()
    {
        var internalList = Kind == PlaylistKind.Internal
            || (SourceId is null
                && Source == MediaSource.Manual
                && Tracks.Count > 0
                && Tracks.All(t => t.LocalKey is { Length: > 0 }));

        if (internalList)
        {
            Kind = PlaylistKind.Internal;
            Source = MediaSource.Manual;
            SourceId = null;
            SyncEnabled = false;
            return;
        }

        Kind = PlaylistKind.Imported;
    }

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
    public bool IsInternal => Kind == PlaylistKind.Internal;

    /// <summary>
    /// Imported playlists are read-only in the LIBRARY rail: their membership is
    /// managed by the source tab, not here.
    /// </summary>
    [JsonIgnore]
    public bool IsReadOnly => !IsInternal;

    /// <summary>True when the list mirrors a remote playlist (Spotify/YouTube).</summary>
    [JsonIgnore]
    public bool IsImported => !IsInternal;

    public void NotifyTrackCountChanged() => OnPropertyChanged(nameof(TrackCount));
}

/// <summary>
/// One row of the LIBRARY rail. Holds the presentation state the view binds to
/// (selection highlight, honest track count) next to the playlist it stands for;
/// <see cref="Playlist"/> null is the "ALL TRACKS" row.
/// </summary>
public sealed partial class PlaylistRailItem : ObservableObject
{
    public PlaylistRailItem(UserPlaylist? playlist = null) => Playlist = playlist;

    public UserPlaylist? Playlist { get; }

    public bool IsInternal => Playlist?.IsInternal == true;

    public string Title => Playlist?.Name ?? "ALL TRACKS";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _trackCount;

    /// <summary>Count of rows that still resolve to a library file - never the raw membership size.</summary>
    public string CountText => IsCountPending ? "\u2026" : TrackCount.ToString();

    /// <summary>
    /// True until the count has been resolved once (first open after a rescan).
    /// Renders "…" instead of a number, so a stale or invented count is never shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private bool _isCountPending;

    // ---- inline rename / confirm delete --------------------------------
    // Same pattern as YouTube's manage strip: the row itself flips into an
    // editor, so the rail needs no dialog. Imported rows refuse both.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    private bool _isEditing;

    [ObservableProperty] private string _editName = "";

    public bool IsNotEditing => !IsEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConfirmingDelete))]
    private bool _isConfirmingDelete;

    public bool IsNotConfirmingDelete => !IsConfirmingDelete;

    public void BeginEdit()
    {
        if (Playlist is null || Playlist.IsReadOnly) return;
        EditName = Playlist.Name;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    public void BeginDelete()
    {
        if (Playlist is null || Playlist.IsReadOnly) return;
        IsConfirmingDelete = true;
    }

    public void CancelDelete() => IsConfirmingDelete = false;
}
