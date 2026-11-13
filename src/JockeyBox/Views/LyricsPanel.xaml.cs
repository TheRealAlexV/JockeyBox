using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The lyrics surface, shared by the main window and by <see cref="LyricsWindow"/>.
///
/// <para>
/// It reads the <see cref="MainViewModel"/> through the inherited DataContext and
/// holds no per-track state: both hosts bind the same <c>LyricLines</c>, so a
/// pop-out is a second view of one document rather than a second copy.
/// </para>
///
/// <para>
/// <b>The bouncing ball is the one piece of view state.</b> Glyph boxes only exist
/// after layout, in one visual tree, so the measured geometry belongs to whichever
/// host is currently on screen. The VM stores the boxes for the highlighter, which
/// means two measuring hosts would overwrite each other at 30 Hz. So only the
/// panel in the active window measures; the others stay silent and simply mirror
/// <c>BallX</c>/<c>BallY</c> (see <see cref="OnWindowActivated"/>).
/// </para>
/// </summary>
public partial class LyricsPanel : UserControl
{
    /// <summary>Set on the main window's instance so it offers "open in a window".</summary>
    public static readonly DependencyProperty ShowPopOutButtonProperty =
        DependencyProperty.Register(nameof(ShowPopOutButton), typeof(bool), typeof(LyricsPanel),
            new PropertyMetadata(false));

    /// <summary>Raised when the pop-out button is clicked; the main window opens the window.</summary>
    public event EventHandler? PopOutRequested;

    private MainViewModel? _vm;
    private Window? _host;
    private bool _ballMeasureQueued;
    private bool _isBallHost = true;

    public LyricsPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // The right-click rule from the browser tree applies here too: WPF does
        // not select on right-click, and the timing menu acts on the selection.
        LyricsList.PreviewMouseRightButtonDown += (_, e) =>
        {
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item == null || item.IsSelected) return;
            LyricsList.SelectedItems.Clear();
            item.IsSelected = true;
        };

        LyricsList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => ScheduleBallMeasure()), handledEventsToo: true);
        LyricsBallCanvas.SizeChanged += (_, _) => ScheduleBallMeasure();
    }

    public bool ShowPopOutButton
    {
        get => (bool)GetValue(ShowPopOutButtonProperty);
        set => SetValue(ShowPopOutButtonProperty, value);
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e) =>
        PopOutRequested?.Invoke(this, EventArgs.Empty);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
        _host = Window.GetWindow(this);
        if (_host != null)
        {
            _host.Activated += OnWindowActivated;
            _host.Deactivated += OnWindowDeactivated;
            // A panel opened in a background window must not measure immediately.
            _isBallHost = _host.IsActive;
        }
        ScheduleBallMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host != null)
        {
            _host.Activated -= OnWindowActivated;
            _host.Deactivated -= OnWindowDeactivated;
            _host = null;
        }
        Detach();
    }

    /// <summary>
    /// The window that was just made active takes over ball measurement; the other
    /// host stops so the shared geometry always matches the visible layout.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isBallHost = true;
        ScheduleBallMeasure();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => _isBallHost = false;

    private void Attach()
    {
        if (ReferenceEquals(_vm, DataContext)) return;
        Detach();
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;

        _vm.CurrentLyricChanged += ScrollLyricIntoCenter;
        _vm.CurrentWordChanged += ScheduleBallMeasure;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.LyricLines.CollectionChanged += OnLyricLinesChanged;
    }

    private void Detach()
    {
        if (_vm == null) return;
        _vm.CurrentLyricChanged -= ScrollLyricIntoCenter;
        _vm.CurrentWordChanged -= ScheduleBallMeasure;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.LyricLines.CollectionChanged -= OnLyricLinesChanged;
        _vm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Turning word-follow on/off changes which line needs measuring.
        if (e.PropertyName == nameof(MainViewModel.IsWordFollow)) ScheduleBallMeasure();
    }

    private void OnLyricLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _vm?.ClearLyricWordGeometry();
        ScheduleBallMeasure();
    }

    /// <summary>
    /// "Set timing for selected lines": the ListBox owns the selection, so the
    /// view maps the selected line view models back to their line indexes and
    /// hands them to the view model. No lyrics, no synced times - the view model
    /// reports why instead of guessing.
    /// </summary>
    private void OnSetLyricTimingForSelection(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        var indexes = new List<int>(LyricsList.SelectedItems.Count);
        foreach (var item in LyricsList.SelectedItems)
        {
            var index = LyricsList.Items.IndexOf(item);
            if (index >= 0) indexes.Add(index);
        }

        _vm.SetTimingForSelectedLines(indexes);
    }

    /// <summary>Smoothly scroll the lyrics list so the current line sits centered.</summary>
    private void ScrollLyricIntoCenter(LyricLineViewModel line)
    {
        var container = LyricsList.ItemContainerGenerator.ContainerFromItem(line) as FrameworkElement;
        var scrollViewer = FindScrollViewer(LyricsList);
        if (container == null || scrollViewer == null)
        {
            LyricsList.ScrollIntoView(line); // fallback
            ScheduleBallMeasure();
            return;
        }

        var transform = container.TransformToAncestor(scrollViewer);
        var topInViewport = transform.Transform(new Point(0, 0)).Y;
        var target = scrollViewer.VerticalOffset + topInViewport
                     - scrollViewer.ViewportHeight / 2 + container.ActualHeight / 2;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
        ScheduleBallMeasure();
    }

    // ---- bouncing ball geometry -------------------------------------------
    //
    // Measuring text is genuinely a view concern: only the visual tree knows
    // where a word's glyphs landed after wrapping and scaling, and the
    // ScrollViewer owns the vertical offset. The view model keeps the policy
    // (which word, what motion); this half only reports boxes.

    /// <summary>
    /// Queue one measurement pass. Loaded priority runs after the layout/render
    /// pass that a scroll, resize or bold-word change just scheduled, so the
    /// transforms read are the final ones. Coalesces bursts (30 Hz ticks,
    /// scroll storms) into a single pass.
    /// </summary>
    private void ScheduleBallMeasure()
    {
        if (_ballMeasureQueued || !_isBallHost) return;
        _ballMeasureQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(MeasureBallGeometry));
    }

    /// <summary>
    /// Report the current line's word boxes to the view model, in the ball
    /// canvas's coordinate space. Anything missing (container not realised,
    /// mid-rebuild, words still collapsed) hides the ball instead of guessing.
    /// </summary>
    private void MeasureBallGeometry()
    {
        _ballMeasureQueued = false;
        if (_vm == null) return;

        var line = _vm.CurrentLyricLine;
        if (!_vm.IsWordFollow || line is null || !line.HasWords)
        {
            _vm.ClearLyricWordGeometry();
            return;
        }

        if (LyricsList.ItemContainerGenerator.ContainerFromItem(line) is not FrameworkElement lineContainer)
        {
            _vm.ClearLyricWordGeometry(); // line not realised (list rebuilt)
            return;
        }

        var wordsControl = FindDescendant<ItemsControl>(lineContainer);
        if (wordsControl is null || wordsControl.Items.Count != line.Words.Count)
        {
            _vm.ClearLyricWordGeometry();
            return;
        }

        var centres = new double[line.Words.Count];
        double top = double.NaN;
        for (var i = 0; i < centres.Length; i++)
        {
            if (wordsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement wordBox
                || !TryGetRectInBallCanvas(wordBox, out var rect))
            {
                _vm.ClearLyricWordGeometry();
                return;
            }

            centres[i] = rect.Left + rect.Width / 2;
            if (i == 0) top = rect.Top;
        }

        if (double.IsNaN(top))
        {
            _vm.ClearLyricWordGeometry();
            return;
        }

        _vm.SetLyricWordGeometry(line, top, centres);
    }

    /// <summary>
    /// A word's box in the ball canvas's coordinates. The canvas overlays the
    /// list rather than containing it, so both are mapped into their common
    /// parent (this panel's grid) and the canvas origin is subtracted.
    /// </summary>
    private bool TryGetRectInBallCanvas(FrameworkElement element, out Rect rect)
    {
        rect = Rect.Empty;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        if (LyricsBallCanvas.Parent is not Visual root) return false;

        try
        {
            var origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
            var canvasOrigin = LyricsBallCanvas.TransformToAncestor(root).Transform(new Point(0, 0));
            rect = new Rect(origin.X - canvasOrigin.X, origin.Y - canvasOrigin.Y,
                            element.ActualWidth, element.ActualHeight);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Detached / reparented while the pass was queued.
            return false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }
}
