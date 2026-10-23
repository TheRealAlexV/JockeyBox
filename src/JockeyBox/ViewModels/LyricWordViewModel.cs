using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Lyrics;

namespace JockeyBox.ViewModels;

/// <summary>
/// One rendered word inside the current lyric line. <see cref="IsCurrent"/> is
/// only ever set while word-follow mode is on; line mode clears it.
/// </summary>
public partial class LyricWordViewModel : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;

    public LyricWordViewModel(LyricWord word)
    {
        Text = word.Text;
        Start = word.Start;
        End = word.End;
    }

    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    /// <summary>The token exactly as the source wrote it (punctuation and casing untouched).</summary>
    public string Text { get; }
}
