using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The lyrics surface, shared by the shell's LYRIC panel and by the window it opens.
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

    /// <summary>
    /// Set while the line-text editor is open: recentring the list on every
    /// <c>CurrentLyricChanged</c> would scroll the line being edited out from under
    /// the operator.
    /// </summary>
    private bool _suppressLyricScroll;

    /// <summary>
    /// First-pass reserve: what must stay on screen under the lyric list before the
    /// layout has been measured (the folders strip plus the shell's own padding).
    /// <see cref="ApplyListCap"/> replaces it with the measured depth of the rows
    /// below the panel as soon as one capped pass has been laid out.
    /// </summary>
    private const double ReservedBelowList = 40;

    /// <summary>A few px so the last line is not flush against the strip below.</summary>
    private const double ComfortGap = 4;

    /// <summary>
    /// Floor for the cap: about four lyric lines, so a window squeezed to its minimum
    /// still shows a usable number of them instead of a sliver. Below this the panel
    /// would rather let the shell scroll than become unreadable.
    /// </summary>
    private const double MinListHeight = 96;

    private ScrollViewer? _shell;
    private double? _belowList;

    public LyricsPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // A second pass is needed after the first cap is laid out: only then is the
        // depth of the rows under this panel readable (see ApplyListCap).
        LyricsList.SizeChanged += (_, _) => ApplyListCap();

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
        ApplyListCap();
        ScheduleBallMeasure();
    }

    /// <summary>
    /// Stops a long lyric set from growing the window.
    ///
    /// <para>The main window wraps its whole layout in a ScrollViewer (ShellScroll),
    /// because the deck card and the activity console grow at runtime. Inside that
    /// scope the measure height is infinite, so a list that reports its whole
    /// document makes the browser+lyrics row as tall as every line: the rows below
    /// it are pushed off the bottom and the overflow surfaces as a window-wide
    /// scrollbar. The lyrics box is the one element there that should never drive
    /// height - it is a document view.</para>
    ///
    /// <para>So the list is capped at the room the shell actually has under this
    /// panel - its own top edge to the bottom of the viewport, less the panel's
    /// header rows and less what must stay on screen below - and scrolls internally
    /// instead. The list keeps filling whatever it is given: the cap is recomputed
    /// from the live viewport on every resize, so a taller window means more lines
    /// visible, never fewer.</para>
    ///
    /// <para>In the pop-out window there is no such scroller and
    /// the row is star-sized, so the list is left uncapped there: the window's size
    /// is the bound and a cap would only throw room away. Nothing here is a fixed
    /// height, which is what made an earlier 30 px clamp hide two lines of the
    /// diagnostic.</para>
    /// </summary>
    private void ApplyListCap()
    {
        var shell = FindAncestor<ScrollViewer>(this);
        if (!ReferenceEquals(shell, _shell))
        {
            if (_shell != null)
            {
                _shell.SizeChanged -= OnShellSizeChanged;
            }

            _shell = shell;
            if (_shell != null)
            {
                _shell.SizeChanged += OnShellSizeChanged;
            }
        }

        if (_shell is null || _shell.ViewportHeight <= 0)
        {
            LyricsList.ClearValue(MaxHeightProperty);
            return;
        }

        double top;
        try
        {
            top = TransformToAncestor(_shell).Transform(new Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            // Detached between the loaded event and this pass: no geometry to read.
            return;
        }

        // Header rows + paddings: everything this panel draws except the list. It is
        // content-independent (the list is the only elastic part), and it is read from
        // the live layout rather than guessed, so a wrapped status line is covered.
        double chrome = Math.Max(0, ActualHeight - LyricsList.ActualHeight);
        double below = _belowList ?? ReservedBelowList;
        double cap = Math.Max(
            MinListHeight,
            _shell.ViewportHeight - top - chrome - below - ComfortGap);

        // Only assign on a real change: assigning MaxHeight invalidates measure, which
        // re-enters this method through SizeChanged.
        if (double.IsNaN(LyricsList.MaxHeight) || Math.Abs(cap - LyricsList.MaxHeight) > 1)
        {
            LyricsList.MaxHeight = cap;
            return;
        }

        // Once one capped pass has been laid out, the distance from this panel's bottom
        // to the end of the shell's content is just the rows under it (the folders
        // strip) - and that is what the cap has to leave room for. Read it, keep it:
        // it is a property of the window's layout, not of the window's height, and
        // measuring it here keeps the number out of the source.
        if (_belowList is null && ActualHeight > 0)
        {
            double measured = _shell.ExtentHeight - top - ActualHeight;
            if (measured > 0 && measured < _shell.ViewportHeight / 2)
            {
                _belowList = measured;
                ApplyListCap();
            }
        }
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) => ApplyListCap();

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
        RefreshOffsetField();
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

        // A document load, a track change or a section edit re-reads the offset;
        // the field follows it, except while the operator is typing into it.
        if (e.PropertyName == nameof(MainViewModel.LyricsOffsetText)) RefreshOffsetField();
    }

    // ---- the offset field (header row 2) ---------------------------------

    /// <summary>
    /// Shows the offset in ms and writes it back through
    /// <see cref="MainViewModel.LyricsOffsetMs"/>, so typing takes the same
    /// three-way dispatch as the slider and the nudge buttons (section, else the
    /// track, else the global default).
    ///
    /// <para><b>No clamp here.</b> The store clamps; a second clamp in the view
    /// could disagree with it. <b>No binding either:</b> a binding would push this
    /// text back on every keystroke and would let a refresh overwrite a half-typed
    /// value, so the two view-model members are read and written by hand.</para>
    ///
    /// <para>Keystrokes typed here stay here: the field marks them handled, so no
    /// window-level binding sees a digit meant for the number.</para>
    /// </summary>
    private void OnOffsetBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;   // never reach a default button
                CommitOffsetField();
                break;

            case Key.Escape:
                e.Handled = true;
                RefreshOffsetField(force: true);
                break;

            case Key.Tab:
                break;              // keyboard navigation keeps working

            default:
                // Editing keys are already handled by the TextBox's own class
                // handler by now; this stops anything above it acting on the key.
                e.Handled = true;
                break;
        }
    }

    private void OnOffsetBoxLostFocus(object sender, RoutedEventArgs e) => CommitOffsetField();

    /// <summary>
    /// Names the number the next move will write, before the operator moves
    /// anything: a section, the loaded track, or the global default.
    /// </summary>
    private void RefreshOffsetField(bool force = false)
    {
        if (_vm == null) return;
        if (OffsetValueBox == null) return;
        if (!force && OffsetValueBox.IsKeyboardFocusWithin) return;
        OffsetValueBox.Text = _vm.LyricsOffsetText;
    }

    private void CommitOffsetField()
    {
        if (_vm == null) return;

        // Writing the same number back would create a track offset where the
        // track had none, so an unchanged field writes nothing.
        if (TryParseOffsetMs(OffsetValueBox.Text, out double ms) && ms != _vm.LyricsOffsetMs)
            _vm.LyricsOffsetMs = ms;

        // Show what the store kept: the clamped value, or the old one when the
        // text was not a number at all.
        OffsetValueBox.Text = _vm.LyricsOffsetText;
    }

    /// <summary>
    /// A signed millisecond count. The readout's " ms" suffix is tolerated so a
    /// value copied out of the field goes back in. Out-of-range values are left
    /// for the store to clamp rather than clamped a second time here.
    /// </summary>
    internal static bool TryParseOffsetMs(string? text, out double ms)
    {
        ms = 0;
        string value = (text ?? string.Empty).Trim();
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            value = value[..^2].Trim();
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return false;
        ms = parsed;
        return true;
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

    /// <summary>
    /// Enable the line-text item only on the host whose window is active. Both
    /// hosts show the same <c>LyricLines</c> collection, so two editors over one
    /// document would be two edits of the same line; the active window owns it, the
    /// same rule the bouncing ball already follows.
    /// </summary>
    private void OnLineMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        foreach (var item in menu.Items)
        {
            if (item is MenuItem { Tag: "edit-line-text" } editItem) editItem.IsEnabled = _isBallHost;
        }
    }

    /// <summary>
    /// "Edit line text…": one modal text box over the selected line(s), prefilled
    /// with the first selected line. The list stays in Extended selection and keeps
    /// its one context menu, so there is no per-word target and no inline editor
    /// fighting the selection.
    /// </summary>
    private void OnEditLyricLineText(object sender, RoutedEventArgs e)
    {
        if (_vm == null || !_isBallHost) return;

        var indexes = new List<int>(LyricsList.SelectedItems.Count);
        foreach (var item in LyricsList.SelectedItems)
        {
            var index = LyricsList.Items.IndexOf(item);
            if (index >= 0) indexes.Add(index);
        }
        if (indexes.Count == 0) return;

        indexes.Sort();
        if (LyricsList.Items[indexes[0]] is not LyricLineViewModel first) return;

        var dialog = new EditLyricLineDialog(first.Text, indexes.Count)
        {
            Owner = Window.GetWindow(this),
        };

        // CurrentLyricChanged recentres the list at 30 Hz while the track plays, so
        // the line under the editor would scroll away mid-edit. Pause the recentring
        // for exactly the time the dialog is up; the next change recentres again.
        _suppressLyricScroll = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
        }
        finally
        {
            _suppressLyricScroll = false;
        }

        _vm.SetLyricLineText(indexes, dialog.LineText);
    }

    /// <summary>Smoothly scroll the lyrics list so the current line sits centered.</summary>
    private void ScrollLyricIntoCenter(LyricLineViewModel line)
    {
        // An open line-text editor owns the row the operator is looking at.
        if (_suppressLyricScroll) return;

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
