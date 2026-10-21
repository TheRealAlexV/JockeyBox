using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Sources;

namespace JockeyBox.Downloads;

public enum DownloadState
{
    Queued,
    Searching,
    Downloading,
    Converting,
    Tagging,
    Done,
    Failed,
    Cancelled,
}

/// <summary>Where a queued download is going.</summary>
public enum DownloadKind
{
    /// <summary>Permanent copy in the Music folder, tagged, ledgered.</summary>
    Library,
    /// <summary>Throwaway copy in the stream cache, only there to be played once.</summary>
    Stream,
}

/// <summary>
/// One row in the download queue. Observable so the Downloads window updates
/// live while yt-dlp reports progress.
/// </summary>
public partial class DownloadItem : ObservableObject
{
    /// <summary>Stable de-duplication key (source track id, plus a suffix for repeat requests).</summary>
    public string Key { get; init; } = Guid.NewGuid().ToString("N");

    public required SourceTrack Track { get; init; }
    public DownloadKind Kind { get; init; } = DownloadKind.Library;

    /// <summary>
    /// 0-based position of the track inside the playlist it was queued from.
    /// Drives the "NN Title" layout token (rendered as index+1, so 00 instead of
    /// "01" for every file).
    /// </summary>
    public int IndexInPlaylist { get; init; }

    /// <summary>Per-download layout preset override; null/empty falls back to the app setting.</summary>
    public string? LayoutOverride { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateGlyph), nameof(StageText), nameof(IsActive), nameof(IsFinished))]
    private DownloadState _state = DownloadState.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string _speed = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string _eta = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(DetailText))]
    private string? _error;

    /// <summary>Final file path once the download succeeded.</summary>
    public string? OutputPath { get; set; }

    /// <summary>How many times this item has been attempted (drives the retry limit).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Ranked YouTube ids for this track, best match first. Uploads disappear,
    /// get region-locked or turn members-only, so a dead first choice falls
    /// through to the next plausible match instead of failing the track.
    /// </summary>
    internal List<string> CandidateIds { get; } = new();

    /// <summary>Which entry of <see cref="CandidateIds"/> is being tried.</summary>
    internal int CandidateIndex { get; set; }

    /// <summary>Completes when a Stream download finishes, so playback can await the file.</summary>
    internal TaskCompletionSource<string?>? Completion { get; set; }

    public string Title => Track.Title;
    public string Artist => Track.Artist;

    /// <summary>What UI Automation reads for this row (list items otherwise announce the type name).</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
    public string SourceLabel => Track.Source switch
    {
        MediaSource.Spotify => "Spotify",
        MediaSource.YouTube => "YouTube",
        _ => "Manual",
    };

    public bool IsActive => State is DownloadState.Searching or DownloadState.Downloading
                                    or DownloadState.Converting or DownloadState.Tagging;

    public bool IsFinished => State is DownloadState.Done or DownloadState.Failed or DownloadState.Cancelled;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public string StateGlyph => State switch
    {
        DownloadState.Queued => "\u22ef",
        DownloadState.Searching => "\u2315",
        DownloadState.Downloading => "\u2193",
        DownloadState.Converting => "\u2699",
        DownloadState.Tagging => "\u2699",
        DownloadState.Done => "\u2713",
        DownloadState.Failed => "\u2716",
        DownloadState.Cancelled => "\u2298",
        _ => "",
    };

    public string StageText => State switch
    {
        DownloadState.Queued => "queued",
        DownloadState.Searching => "searching",
        DownloadState.Downloading => "downloading",
        DownloadState.Converting => "converting",
        DownloadState.Tagging => "tagging",
        DownloadState.Done => "done",
        DownloadState.Failed => "failed",
        DownloadState.Cancelled => "cancelled",
        _ => "",
    };

    public string ProgressText => State switch
    {
        DownloadState.Downloading when Percent > 0 => $"{Percent:0}%",
        DownloadState.Done => "done",
        _ => StageText,
    };

    /// <summary>Detail line shown under the title (speed / eta / error).</summary>
    public string DetailText => !string.IsNullOrWhiteSpace(Error)
        ? Error!
        : State == DownloadState.Downloading && (!string.IsNullOrEmpty(Speed) || !string.IsNullOrEmpty(Eta))
            ? string.Join(" \u00b7 ", new[] { Speed, Eta }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : Detail ?? "";
}
