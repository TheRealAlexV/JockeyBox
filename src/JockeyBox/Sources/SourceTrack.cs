using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Library;

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

    /// <summary>
    /// Musical key ("8A", "Am") when a remote API or a local tag supplies one.
    /// Null when unknown: the remote row falls back to its em dash rather than
    /// inventing a key.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Name of the playlist this track was imported from - used by the playlist layout preset.</summary>
    public string? PlaylistName { get; set; }

    /// <summary>
    /// Library identity of an app-owned playlist row: <see cref="Library.TrackEntry.RelativeKey"/>
    /// (music-folder relative path, extension stripped) normalised through
    /// <see cref="NormalizeLocalKey"/>. This is what internal playlist membership
    /// keys on, so re-downloading a file under a new name falls back to the
    /// ledger path and then to an artist/title lookup rather than losing the row.
    /// Null for imported rows, whose identity stays <see cref="Id"/>.
    /// </summary>
    public string? LocalKey
    {
        get => _localKey;
        set => _localKey = NormalizeLocalKey(value) is { Length: > 0 } key ? key : null;
    }

    private string? _localKey;

    /// <summary>
    /// Canonical form of a library key: trimmed, '\' folded to '/', lower case,
    /// trailing audio extension dropped. Both the store and the browser compare
    /// membership through this, so a key written on another platform - or for the
    /// same song re-downloaded as .m4a - still matches
    /// <see cref="Library.TrackEntry.RelativeKey"/>, which is extension-stripped.
    /// </summary>
    public static string NormalizeLocalKey(string? key) => StripAudioExtension(
        (key ?? "").Trim().Replace('\\', '/').ToLowerInvariant());

    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".alac",
    };

    private static string StripAudioExtension(string key)
    {
        var slash = key.LastIndexOf('/');
        var dot = key.LastIndexOf('.');
        if (dot <= slash + 1) return key; // no extension on the last segment

        var extension = key[dot..];
        foreach (var known in AudioExtensions)
        {
            if (string.Equals(extension, known, StringComparison.Ordinal)) return key[..dot];
        }

        return key;
    }

    /// <summary>
    /// Give an app-owned row an id when it has none. The id is only a handle for
    /// the store, the queue and the ledger - <see cref="LocalKey"/> is the
    /// identity.
    /// </summary>
    public string EnsureId()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = "manual:" + Guid.NewGuid().ToString("N");
        return Id;
    }

    /// <summary>
    /// Not persisted: this is live queue state, not playlist membership. A saved
    /// "Downloading"/"Queued" survives a crash or a preview download and renders
    /// a row that spins forever; <see cref="PlaylistStore"/> re-derives the state
    /// from the library on load instead.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    [NotifyPropertyChangedFor(nameof(StateGlyph), nameof(IsBusy), nameof(IsTransferring), nameof(ProgressText))]
    private SourceTrackState _state = SourceTrackState.New;

    /// <summary>0-100 while <see cref="Source"/> is being fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(StateGlyph), nameof(IsTransferring))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateGlyph), nameof(IsBusy), nameof(HasLocalFile))]
    private string? _libraryPath;

    /// <summary>
    /// True when a permanent library file exists for this row. The remote row
    /// menus use it to enable only what can work (reveal, separate, open the
    /// file's folder) instead of offering an action that would fail silently.
    /// </summary>
    [JsonIgnore]
    public bool HasLocalFile => !string.IsNullOrWhiteSpace(LibraryPath);

    /// <summary>
    /// True when the local library already holds a karaoke version of this
    /// track. Painted from the scan's index through
    /// <see cref="RemoteLibraryFacts"/> (one dictionary hit per row, never a
    /// ledger walk), and not persisted: it describes the library, not the row.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    [NotifyPropertyChangedFor(nameof(KaraokeGlyph), nameof(KaraokeAutomationName))]
    private bool _hasKaraoke;

    /// <summary>Row-column glyph for karaoke availability: "✓" or nothing.</summary>
    [JsonIgnore]
    public string KaraokeGlyph => HasKaraoke ? "\u2713" : "";

    /// <summary>Screen-reader / live-tree label for the karaoke indicator.</summary>
    [JsonIgnore]
    public string KaraokeAutomationName => HasKaraoke ? "Karaoke available"
        : LocalEntry?.NeedsKaraoke == true ? "Karaoke needed"
        : "No karaoke version";

    /// <summary>
    /// The scanned library entry this row resolved to, or null while the file is
    /// not here. Kept by reference so the remote row binds the same object the
    /// local library does: a separation that starts or finishes on the browser
    /// ticks this row's karaoke column without a rescan. Set only by
    /// <see cref="ApplyLibraryFacts"/>; never persisted.
    /// </summary>
    [JsonIgnore]
    public TrackEntry? LocalEntry { get; private set; }

    /// <summary>
    /// The local library's karaoke column text for this row ("✓", "⭕ needed",
    /// "⚙ 42%", …), or empty when the file is not here yet.
    /// </summary>
    [JsonIgnore]
    public string KaraokeStatus => LocalEntry?.KaraokeStatus ?? "";

    /// <summary>
    /// Take the library's answer for this row: one assignment per property, and
    /// nothing raised unless the answer really changed, so a pass over a few
    /// thousand rows repaints only the handful that moved.
    /// </summary>
    public void ApplyLibraryFacts(RemoteTrackFacts facts)
    {
        var entryChanged = !ReferenceEquals(LocalEntry, facts.Entry);
        var karaokeChanged = HasKaraoke != facts.HasKaraoke;

        LocalEntry = facts.Entry;
        HasKaraoke = facts.HasKaraoke;

        // LocalEntry has no generated setter, so its dependants are raised here.
        if (entryChanged) OnPropertyChanged(nameof(LocalEntry));
        if (entryChanged || karaokeChanged) OnPropertyChanged(nameof(KaraokeStatus));
        if (entryChanged || karaokeChanged) OnPropertyChanged(nameof(HasLocalFile));
        if (entryChanged || karaokeChanged) OnPropertyChanged(nameof(KaraokeAutomationName));
    }

    /// <summary>
    /// Stream-cache file (preview playback) - evictable, not a library copy, so
    /// it gets its own glyph and is never reported as downloaded. Not persisted
    /// for the same reason <see cref="State"/> is not.
    /// </summary>
    [ObservableProperty]
    [JsonIgnore]
    [NotifyPropertyChangedFor(nameof(StateGlyph), nameof(HasPreview))]
    private string? _previewPath;

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

    /// <summary>
    /// Row glyph. "✓" means exactly one thing: a permanent library file exists
    /// (<see cref="LibraryPath"/>). A cache-only preview shows "▶", so a played
    /// preview can never look like a download.
    /// </summary>
    [JsonIgnore]
    public string StateGlyph => State switch
    {
        SourceTrackState.Queued => "\u2193",
        // "⋯" only while the row is genuinely busy AND the worker reports
        // progress. A stale Downloading at 0% is a queue bug (DownloadQueue now
        // restores the prior state), not evidence of work - it renders as
        // unknown instead of spinning forever.
        SourceTrackState.Downloading when IsTransferring => "\u22ef",
        SourceTrackState.Downloading => "\u00b7",
        SourceTrackState.InLibrary => "\u2713",
        SourceTrackState.Failed => "\u2716",
        // "▶" = streamed once, file lives in the evictable stream cache only;
        // "●" = untouched. Only a real library file earns "✓".
        _ => HasPreview ? "\u25b6" : "\u25cf",
    };

    [JsonIgnore]
    public string ProgressText => State switch
    {
        SourceTrackState.InLibrary => "in library",
        // Silence beats "0%": a queued or stale-busy row must not claim progress.
        SourceTrackState.Downloading when IsTransferring => $"{Progress:0}%",
        _ => "",
    };

    /// <summary>
    /// The queue owns this row right now. Renders as "busy" only together with
    /// real progress (see <see cref="IsTransferring"/>).
    /// </summary>
    [JsonIgnore]
    public bool IsBusy => State is SourceTrackState.Queued or SourceTrackState.Downloading;

    /// <summary>
    /// Busy <em>and</em> the worker has reported progress: the only state in
    /// which "⋯" and a percentage may be shown. Anything else is stale.
    /// </summary>
    [JsonIgnore]
    public bool IsTransferring => IsBusy && Progress > 0;

    /// <summary>True when a stream-cache preview file exists for this track.</summary>
    [JsonIgnore]
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath);

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
        LocalKey = LocalKey,
        State = State,
        LibraryPath = LibraryPath,
        PreviewPath = PreviewPath,
    };
}
