using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Sources;

namespace JockeyBox.YouTube;

/// <summary>
/// One row of the YouTube tab's playlist rail.
///
/// The rail is no longer a straight view of <see cref="PlaylistStore"/>: when the
/// account is connected its rows are the channel's own playlists
/// (<c>playlists.list?mine=true</c>), most of which have never been opened here
/// and therefore have no store row at all. A row is either
///
///  * <see cref="IsRemote"/> - a playlist on the signed-in channel. Selecting it
///    loads its tracks through the Data API and, on first use, materialises a
///    store row so the rest of the app (downloads, queue, playback) keeps
///    working on real <see cref="UserPlaylist"/> objects; or
///  * local-only - an import or hand-built list that is not on the channel
///    (someone else's playlist, or one deleted on YouTube). It keeps working
///    exactly as it did before this rail existed.
///
/// Rows are stable objects keyed by <see cref="Key"/>: a refresh updates the
/// row in place, so the picker keeps its selection and the pane keeps its rows.
/// </summary>
public sealed partial class YouTubeRailItem : ObservableObject
{
    /// <summary>Group header for the channel's own playlists.</summary>
    public const string RemoteGroup = "ON YOUTUBE";

    /// <summary>Group header for everything that lives only in JockeyBox.</summary>
    public const string LocalGroup = "LOCAL ONLY";

    /// <summary>Stable identity across rebuilds: "remote:&lt;id&gt;" or "local:&lt;store id&gt;".</summary>
    public string Key { get; init; } = "";

    [ObservableProperty] private string _title = "";

    [ObservableProperty] private string _group = LocalGroup;

    /// <summary>True for a playlist that exists on the signed-in channel (writable).</summary>
    [ObservableProperty] private bool _isRemote;

    /// <summary>Items on YouTube for a remote row; tracks on disk for a local one.</summary>
    [ObservableProperty] private int _count;

    /// <summary>The store row behind this rail row; null until a remote row is opened.</summary>
    [ObservableProperty] private UserPlaylist? _playlist;

    /// <summary>The YouTube playlist id, for remote rows (and imports that were matched).</summary>
    [ObservableProperty] private string? _remoteId;

    public string CountText => IsRemote ? $"{Count} item(s)" : $"{Count} track(s)";

    /// <summary>One-line description for the picker's tooltip.</summary>
    public string ToolTip => IsRemote
        ? $"{Title} on YouTube - {CountText}. Select to load its tracks (or + Remote to add tracks to it)."
        : $"{Title} exists only in JockeyBox - {CountText}. Not on your YouTube channel.";

    partial void OnCountChanged(int value) => RaiseText();

    partial void OnIsRemoteChanged(bool value) => RaiseText();

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(ToolTip));

    private void RaiseText()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>
    /// Copy the freshly computed fields of <paramref name="fresh"/> onto this
    /// row, keeping the row instance (and therefore the selection) alive.
    /// </summary>
    public YouTubeRailItem MergeFrom(YouTubeRailItem fresh)
    {
        Title = fresh.Title;
        Group = fresh.Group;
        IsRemote = fresh.IsRemote;
        Count = fresh.Count;
        RemoteId = fresh.RemoteId;
        // Keep a materialised store row: dropping it would lose the pane's rows.
        Playlist = fresh.Playlist ?? Playlist;
        return this;
    }
}
