using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// Preview transport strip. Slider gestures live here: WPF's Slider only reports
/// Value, so we bracket the drag so the view model can hold back transport
/// updates while the user is scrubbing, then seek once on release.
/// </summary>
public partial class PreviewBar : UserControl
{
    /// <summary>True between Thumb.DragStarted and DragCompleted; that pair owns the commit.</summary>
    private bool _dragging;

    /// <summary>
    /// Draw the panel frame around the strip. Off when the host already has one
    /// (the Find on YouTube window), so the row carries a single border.
    /// </summary>
    public static readonly DependencyProperty ShowFrameProperty =
        DependencyProperty.Register(nameof(ShowFrame), typeof(bool), typeof(PreviewBar),
            new PropertyMetadata(true));

    public PreviewBar()
    {
        InitializeComponent();

        // Press anywhere on the track: IsMoveToPointEnabled jumps the thumb on
        // mouse-down, so the value is already the one the user asked for.
        SeekBar.PreviewMouseLeftButtonDown += (_, _) => Vm?.BeginSeek();

        // A plain click on the track never starts a Thumb drag, so commit here.
        // A real drag commits in DragCompleted with the final value instead
        // (Preview mouse-up fires before the Thumb has moved its last pixel).
        SeekBar.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_dragging) return;
            Vm?.EndSeek(SeekBar.Value);
        };

        SeekBar.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _dragging = true));
        SeekBar.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _dragging = false;
                Vm?.EndSeek(SeekBar.Value);
            }));

        // A keyboard has no drag phase, so each key is its own press-and-release.
        SeekBar.PreviewKeyDown += OnSeekKeyDown;
    }

    public bool ShowFrame
    {
        get => (bool)GetValue(ShowFrameProperty);
        set => SetValue(ShowFrameProperty, value);
    }

    private PreviewBarViewModel? Vm => DataContext as PreviewBarViewModel;

    /// <summary>
    /// Arrows, page keys and Home/End seek, committing through the same
    /// Begin/End bracket the mouse uses - without it the slider only moved its
    /// thumb and the deck never followed, which made the strip unusable from
    /// the keyboard.
    /// </summary>
    private void OnSeekKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { IsSeekable: true } vm) return;

        var target = e.Key switch
        {
            Key.Left => vm.PositionSeconds - 1,
            Key.Right => vm.PositionSeconds + 1,
            Key.PageDown => vm.PositionSeconds - 10,
            Key.PageUp => vm.PositionSeconds + 10,
            Key.Home => 0,
            Key.End => vm.DurationSeconds,
            _ => double.NaN
        };

        if (double.IsNaN(target)) return;

        e.Handled = true;
        vm.BeginSeek();
        vm.EndSeek(target);
    }
}
