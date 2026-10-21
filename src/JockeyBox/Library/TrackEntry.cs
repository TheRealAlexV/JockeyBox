using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Library;

public enum SeparationState { None, Queued, Running, Failed }

/// <summary>
/// One logical track in the unified browser list. The Music folder is the
/// source of truth; the Karaoke side holds the generated instrumental with the
/// same relative path (extension may differ - pairing ignores it).
/// Observable so the browser row updates live while karaoke tracks are
/// generated in the background.
/// </summary>
public partial class TrackEntry : ObservableObject
{
    /// <summary>Pairing key: relative path with the extension stripped (case-insensitive matching).</summary>
    public required string RelativeKey { get; init; }

    /// <summary>Music-folder relative path (for display).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path of the full music file (Deck B), if present.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private string? _musicPath;

    /// <summary>Absolute path of the instrumental file (Deck A), if present.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private string? _karaokePath;

    /// <summary>Background vocal-removal state for this track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private SeparationState _separation = SeparationState.None;

    /// <summary>Progress 0-100 while Separation == Running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus))]
    private int _separationProgress;

    // Metadata (read from the Music file - it carries the cleanest tags for
    // lyrics lookup - otherwise from the Karaoke file).
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Bpm { get; set; }
    public TimeSpan Duration { get; set; }

    public bool HasBoth => KaraokePath != null && MusicPath != null;

    /// <summary>True when a karaoke version still needs to be generated.</summary>
    public bool NeedsKaraoke => MusicPath != null && KaraokePath == null
                                && Separation is SeparationState.None or SeparationState.Failed;

    /// <summary>Browser icon: generation state or pairing status.</summary>
    public string KaraokeStatus => Separation switch
    {
        SeparationState.Queued => "\u23F3 queued",
        SeparationState.Running => $"\u2699 {SeparationProgress}%",
        SeparationState.Failed => "\u2716 failed",
        _ when HasBoth => "\u2713",
        _ when KaraokePath == null => "\u2b55 needed",
        _ => "no music",
    };

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string BpmText => Bpm > 0 ? Bpm.ToString("0.#") : "";
}
