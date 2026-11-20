using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Lyrics;

namespace JockeyBox.ViewModels;

/// <summary>One rendered lyrics line; IsCurrent drives the highlight style.</summary>
public partial class LyricLineViewModel : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;

    public LyricLineViewModel(TimeSpan time, string text)
        : this(time, text, null)
    {
    }

    public LyricLineViewModel(TimeSpan time, string text, IReadOnlyList<LyricWord>? words)
        : this(time, text, words, false)
    {
    }

    public LyricLineViewModel(
        TimeSpan time, string text, IReadOnlyList<LyricWord>? words, bool isInstrumental)
    {
        Time = time;
        Text = string.IsNullOrWhiteSpace(text) ? "\u266a" : text;
        IsInstrumental = isInstrumental;
        // A marker line keeps its words: they are what lets the panel lay the
        // glyphs across the row and the follower walk them through the gap.
        Words = words is { Count: > 0 }
            ? words.Select(w => new LyricWordViewModel(w)).ToArray()
            : Array.Empty<LyricWordViewModel>();
    }

    public TimeSpan Time { get; }
    public string Text { get; }

    /// <summary>
    /// True for a marker line standing in for an instrumental stretch (see
    /// <see cref="InstrumentalGap.Marker"/>). It uses the standard line
    /// treatment; the flag is here so a later visual pass can style it without
    /// re-deriving it from the text.
    /// </summary>
    public bool IsInstrumental { get; }

    /// <summary>
    /// Per-word views for the wrapped word-by-word layout; empty when the line
    /// only has line-level timing (the view then falls back to <see cref="Text"/>).
    /// Always order-preserving: the source's word order, never re-split from
    /// <see cref="Text"/>.
    /// </summary>
    public IReadOnlyList<LyricWordViewModel> Words { get; }

    /// <summary>
    /// Whether the view should render the wrapped word layout instead of the
    /// single <see cref="Text"/> block.
    /// <para>
    /// A line that produced a single token (one-word line, or unspaced CJK where
    /// whitespace tokenisation cannot split) has no word-level motion to show and
    /// would lose wrapping, so it keeps the line template. Two or more tokens is
    /// the only case where word follow adds anything.
    /// </para>
    /// </summary>
    public bool HasWords => Words.Count > 1;
}

/// <summary>
/// One entry of the lyrics "try other sources" picker: a fetched document plus
/// the label shown next to it. Applying it pins the source for this track only.
/// </summary>
public class LyricsCandidateViewModel
{
    public LyricsCandidateViewModel(LyricsDocument document, string detail)
    {
        Document = document;
        Detail = detail;
    }

    public LyricsDocument Document { get; }

    /// <summary>Provider display name, e.g. "LRCLIB".</summary>
    public string SourceName => Document.Source;

    /// <summary>Stable provider id, or empty when restored from an old cache.</summary>
    public string SourceId => Document.SourceId;

    /// <summary>"synced · 42 lines" / "unsynced · 38 lines".</summary>
    public string Detail { get; }
}
