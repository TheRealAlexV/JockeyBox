using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Downloads;

namespace JockeyBox.Activity;

/// <summary>What kind of long-running work an <see cref="ActivityRow"/> reports.</summary>
public enum ActivityKind
{
    Download,
    Karaoke,
    PlaylistLoad,
    Scan,
}

/// <summary>
/// One row in the shared activity feed: any long-running job, no matter which
/// subsystem produced it. State reuses <see cref="DownloadState"/> so the app
/// has a single "busy with something" vocabulary rather than one enum per
/// queue.
/// </summary>
public partial class ActivityRow : ObservableObject
{
    /// <summary>Stable id, unique among the live rows of one producer.</summary>
    public required string Id { get; init; }

    public required ActivityKind Kind { get; init; }

    /// <summary>The thing being worked on, e.g. "Artist - Title".</summary>
    public string Title { get; init; } = "";

    /// <summary>True when <see cref="ActivityFeed.Cancel"/> can stop this row.</summary>
    public bool CanCancel { get; init; }

    /// <summary>Producer hook, invoked by <see cref="ActivityFeed.Cancel"/>.</summary>
    internal Action? CancelAction { get; init; }

    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;

    /// <summary>When the row reached a terminal state; null while it is running.</summary>
    public DateTimeOffset? Finished { get; internal set; }

    [ObservableProperty]
    private DownloadState _state = DownloadState.Queued;

    /// <summary>0-100, or null when the producer has no measurable progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double? _percent;

    /// <summary>Stage detail: speed, ETA, "downloading model… 40%", phase, error.</summary>
    [ObservableProperty]
    private string? _detail;

    public bool IsFinished =>
        State is DownloadState.Done or DownloadState.Failed or DownloadState.Cancelled;

    public string StageText => State switch
    {
        DownloadState.Queued => "queued",
        DownloadState.Searching => "searching",
        DownloadState.Downloading => "downloading",
        DownloadState.Converting => "processing",
        DownloadState.Tagging => "tagging",
        DownloadState.Done => "done",
        DownloadState.Failed => "failed",
        DownloadState.Cancelled => "cancelled",
        _ => "",
    };

    public string PercentText => Percent is { } p ? $"{p:0}%" : "";

    public override string ToString() => $"{Kind}: {Title} ({StageText})";
}
