using System.Collections.ObjectModel;
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

    // --- passthrough bindings (for a uniform column grid) ---

    public string Title => Track?.Title ?? Name;
    public string Artist => Track?.Artist ?? "";
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

    /// <summary>Create a leaf node from a track entry, subscribing to live updates.</summary>
    public static BrowserNode FromTrack(TrackEntry track)
    {
        var node = new BrowserNode(track.RelativePath) { Track = track };
        // Forward karaoke-progress updates from the track to this node so the
        // tree row's KaraokeStatus re-evaluates and shows "⚙ 42%".
        track.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrackEntry.SeparationProgress)
                               or nameof(TrackEntry.Separation)
                               or nameof(TrackEntry.KaraokePath)
                               or nameof(TrackEntry.MusicPath))
            {
                node.SeparationProgress = track.SeparationProgress;
            }
        };
        return node;
    }
}
