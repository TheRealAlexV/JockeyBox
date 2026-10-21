using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.ViewModels;

/// <summary>One rendered lyrics line; IsCurrent drives the highlight style.</summary>
public partial class LyricLineViewModel : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;

    public LyricLineViewModel(TimeSpan time, string text)
    {
        Time = time;
        Text = string.IsNullOrWhiteSpace(text) ? "\u266a" : text;
    }

    public TimeSpan Time { get; }
    public string Text { get; }
}
