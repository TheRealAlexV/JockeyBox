using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Library;

public enum SeparationState { None, Queued, Running, Failed }

/// <summary>
/// One logical track in the unified browser list: the music file plus, when it
/// exists, its karaoke partner.
///
/// In the merged one-root library the partner sits beside the original and is
/// named "Song.karaoke.ext" (see <see cref="KaraokeNaming"/>); in a split library
/// it mirrors the relative path under the karaoke root. Either way the pair is
/// matched on the base relative path - the extension and the marker are ignored
/// when pairing - so a row keeps one identity across the migration.
/// Observable so the browser row updates live while karaoke tracks are
/// generated in the background.
/// </summary>
public partial class TrackEntry : ObservableObject
{
    /// <summary>
    /// Pairing key: relative path with the extension and any ".karaoke" marker
    /// stripped (case-insensitive matching). The instrumental of "a/b/song.mp3"
    /// carries the key "a/b/song" as well, so playlists, the queue and settings
    /// never see a second identity for one song.
    /// </summary>
    public required string RelativeKey { get; init; }

    /// <summary>Library-relative path (for display): the music file when present, otherwise the karaoke file.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path of the full music file (Deck B), if present.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private string? _musicPath;

    /// <summary>
    /// Absolute path of the instrumental file (Deck A), if present: either the
    /// merged-layout sibling "Song.karaoke.ext" or, for an unmerged library, the
    /// copy under the karaoke root.
    /// </summary>
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
    /// <summary>
    /// Musical key as the file's tag states it ("8A", "Am", "F#m"), or the
    /// analysed estimate when the file has no key tag. Null when neither exists,
    /// so the row shows its em-dash fallback rather than a guessed key.
    /// Observable because analysis often finishes after the row is on screen.
    /// </summary>
    [ObservableProperty]
    private string? _key;

    /// <summary>
    /// Tempo as the file's TBPM tag states it, or the analysed estimate when the
    /// file has no tempo tag. 0 when neither exists, so the row shows its em-dash
    /// fallback rather than a guessed tempo. Observable for the same reason as
    /// <see cref="Key"/>: analysis often finishes after the row is on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmText))]
    private double _bpm;

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
