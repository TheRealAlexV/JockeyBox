using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Library;

/// <summary>
/// One node in the track-browser tree. Folder nodes have children but no
/// Track; leaf nodes carry a TrackEntry. Observable so the tree can update
/// row states live (karaoke status, separation progress).
/// </summary>
public partial class BrowserNode : ObservableObject
{
    public string Name { get; set; } = "";
    public ObservableCollection<BrowserNode> Children { get; } = new();

    public TrackEntry? Track { get; set; }
    public bool IsFolder => Track == null;

    /// <summary>
    /// Stable identity of this node inside one tree build. Leaves use their
    /// track's <see cref="TrackEntry.RelativeKey"/>; folders use their grouping
    /// path. Tree rebuilds (layout switch, search) match against it to restore
    /// selection and expansion.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Artist shown on an artist/album folder row (folders have no track).</summary>
    public string FolderArtist { get; set; } = "";

    /// <summary>Album shown on an album folder row.</summary>
    public string FolderAlbum { get; set; } = "";

    /// <summary>Number of leaf tracks under this folder node (set while building).</summary>
    public int TrackCount { get; set; }

    /// <summary>"12 tracks" for a folder row; empty for leaves.</summary>
    public string CountText => TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";

    /// <summary>Path column: a folder shows how many tracks it holds, a leaf its path.</summary>
    public string PathText => IsFolder ? CountText : RelativePath;

    /// <summary>TreeView row state, bound two-way so a rebuild can restore both.</summary>
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded;

    // --- passthrough bindings (for a uniform column grid) ---

    public string Title => Track?.Title ?? Name;
    public string Artist => Track?.Artist ?? FolderArtist;
    public string Album => Track?.Album ?? FolderAlbum;
    public double Bpm => Track?.Bpm ?? 0;
    public string BpmText => Track?.BpmText ?? "";
    public string DurationText => Track?.DurationText ?? "";
    public string RelativePath => Track?.RelativePath ?? "";
    public string FolderPath => !string.IsNullOrEmpty(RelativePath)
        ? System.IO.Path.GetDirectoryName(RelativePath)?.Replace('\\', '/') ?? ""
        : "";

    // Karaoke status — forwarded from the underlying track so tree rows update live.
    public string KaraokeStatus => Track?.KaraokeStatus ?? "";
    public bool NeedsKaraoke => Track?.NeedsKaraoke ?? false;

    // Expose the track's separation progress for the "⚙ 42%" display.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus))]
    private int _separationProgress;

    public BrowserNode(string name) => Name = name;

    /// <summary>UI Automation reads the row label; the default would be the type name.</summary>
    public override string ToString() => IsFolder ? Name : $"{Artist} - {Title}".TrimStart(' ', '-');

    /// <summary>Live-update subscription to <see cref="Track"/>, kept so a discarded tree can unsubscribe.</summary>
    private PropertyChangedEventHandler? _trackHandler;

    /// <summary>Drop the live-update subscriptions for this node and its children.</summary>
    public void DetachFromTrack()
    {
        if (Track != null && _trackHandler != null)
            Track.PropertyChanged -= _trackHandler;
        _trackHandler = null;
        foreach (var child in Children)
            child.DetachFromTrack();
    }

    /// <summary>Create a leaf node from a track entry, subscribing to live updates.</summary>
    public static BrowserNode FromTrack(TrackEntry track, string? key = null)
    {
        var fileName = System.IO.Path.GetFileName(track.RelativePath.Replace('\\', '/'));
        var node = new BrowserNode(fileName)
        {
            Track = track,
            Key = key ?? "track:" + track.RelativeKey.ToLowerInvariant(),
        };
        // Forward karaoke-progress updates from the track to this node so the
        // tree row's KaraokeStatus re-evaluates and shows "⚙ 42%". The browser
        // is rebuilt on every layout switch and search keystroke, so the
        // handler is kept for DetachFromTrack instead of leaking one per build.
        node._trackHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(TrackEntry.SeparationProgress)
                               or nameof(TrackEntry.Separation)
                               or nameof(TrackEntry.KaraokePath)
                               or nameof(TrackEntry.MusicPath))
            {
                node.SeparationProgress = track.SeparationProgress;
            }
        };
        track.PropertyChanged += node._trackHandler;
        return node;
    }
}
