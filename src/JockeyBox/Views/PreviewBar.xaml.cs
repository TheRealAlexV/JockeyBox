using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    }

    private PreviewBarViewModel? Vm => DataContext as PreviewBarViewModel;
}
