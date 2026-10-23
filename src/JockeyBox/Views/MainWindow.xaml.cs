using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Play/Pause glyph follows VM changes; each deck card owns its waveform.
        UpdatePlayGlyph();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlaying)) UpdatePlayGlyph();
            // Turning word-follow on/off changes which line needs measuring.
            if (e.PropertyName == nameof(MainViewModel.IsWordFollow)) ScheduleBallMeasure();
        };
        _vm.DeckA.PropertyChanged += OnDeckPropertyChanged;
        _vm.DeckB.PropertyChanged += OnDeckPropertyChanged;

        // Seek bar: suspend timer updates while the user is interacting, then
        // commit the seek on release. Preview events cover both thumb drags
        // and click-to-position (IsMoveToPointEnabled).
        SeekBar.PreviewMouseLeftButtonDown += (_, _) => _vm.BeginSeek();
        SeekBar.PreviewMouseLeftButtonUp += (_, _) => _vm.EndSeek(SeekBar.Value);
        SeekBar.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => _vm.EndSeek(SeekBar.Value)));

        // Keep the highlighted lyric centered in the panel.
        _vm.CurrentLyricChanged += ScrollLyricIntoCenter;

        // The bouncing ball's geometry is a view concern: only the view can read
        // where glyphs actually landed. Re-measure whenever the list scrolls or
        // resizes, and whenever the VM reports a new active word.
        _vm.CurrentWordChanged += ScheduleBallMeasure;
        _vm.LyricLines.CollectionChanged += (_, _) => { _vm.ClearLyricWordGeometry(); ScheduleBallMeasure(); };
        LyricsList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => ScheduleBallMeasure()), handledEventsToo: true);
        LyricsBallCanvas.SizeChanged += (_, _) => ScheduleBallMeasure();

        // Same rule as the browser tree: WPF does not select on right-click, and
        // the timing menu acts on the selection. Keep a multi-row selection when
        // the right-click lands on one of its rows.
        LyricsList.PreviewMouseRightButtonDown += (_, e) =>
        {
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item == null || item.IsSelected) return;
            LyricsList.SelectedItems.Clear();
            item.IsSelected = true;
        };

        // Karaoke generation flows that need dialogs.
        _vm.SettingsRequested += () => OpenSettings();
        _vm.EngineInstallRequested += OnEngineInstallRequested;
        _vm.DownloadsRequested += OpenDownloads;

        // Sync TreeView selection to the view model (SelectedItem is read-only
        // in XAML so we wire it in code-behind).
        TrackTree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is BrowserNode node) _vm.SelectedTreeNode = node;
        };

        // Build the "Make Karaoke Track" model submenu dynamically whenever
        // the context menu opens, so it reflects the right-clicked tree node.
        TrackContextMenu.Opened += OnTrackContextMenuOpened;

        Closed += (_, _) => _vm.Dispose();
    }

    private void UpdatePlayGlyph() =>
        PlayButton.Content = _vm.IsPlaying ? "\u23F8" : "\u25B6";

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is BrowserNode node && !node.IsFolder)
            _vm.LoadSelectedTreeTrackCommand.Execute(null);
    }

    /// <summary>WPF does not select rows on right-click; do it so the context menu targets the row under the cursor.</summary>
    private void OnTrackRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item != null) item.IsSelected = true;
    }

    /// <summary>
    /// Populate the "Make Karaoke Track" submenu with one entry per model
    /// preset (filename + description), using the right-clicked node's track.
    /// </summary>
    private void OnTrackContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        var parentItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(i => (string)i.Header == "Make Karaoke Track");
        if (parentItem == null) return;

        parentItem.Items.Clear();

        var node = TrackTree.SelectedItem as BrowserNode;
        var track = node?.Track;
        if (track?.NeedsKaraoke != true)
        {
            parentItem.IsEnabled = false;
            return;
        }
        parentItem.IsEnabled = true;

        foreach (var (filename, description) in Separation.SeparationModels.Presets)
        {
            var subItem = new MenuItem
            {
                Header = filename,
                ToolTip = description,
            };
            var capturedTrack = track;
            var capturedModel = filename;
            subItem.Click += (_, _) =>
                _vm.MakeKaraokeWithModel(capturedTrack, capturedModel);
            parentItem.Items.Add(subItem);
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

    private bool _expanded;

    /// <summary>Expand/collapse top-level folder nodes only (one level) to avoid TreeView container explosion.</summary>
    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ExpandButton.Content = _expanded ? "\u2B06 Collapse all" : "\u2B07 Expand all";
        foreach (var item in TrackTree.Items)
        {
            if (TrackTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi && tvi.HasItems)
            {
                tvi.IsExpanded = _expanded;
                // Also expand immediate sub-folders for convenience.
                foreach (var child in tvi.Items)
                {
                    if (tvi.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem cvi && cvi.HasItems)
                        cvi.IsExpanded = _expanded;
                }
            }
        }
    }

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private DownloadsWindow? _downloadsWindow;

    /// <summary>One queue window per app run; a second request brings it forward.</summary>
    private void OpenDownloads()
    {
        if (_downloadsWindow is { IsLoaded: true })
        {
            if (_downloadsWindow.WindowState == WindowState.Minimized)
                _downloadsWindow.WindowState = WindowState.Normal;
            _downloadsWindow.Activate();
            return;
        }

        _downloadsWindow = new DownloadsWindow(_vm.Downloads) { Owner = this };
        _downloadsWindow.Closed += (_, _) => _downloadsWindow = null;
        _downloadsWindow.Show();
    }

    private void OpenSettings()
    {
        // Library tools work on the rows the main window already scans; hand the
        // settings dialog a getter rather than a second scan.
        var dialog = new SettingsWindow(_vm.SettingsRef, () => _vm.Tracks, _vm.PlaylistsRef) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.OnSettingsSaved();
    }

    /// <summary>
    /// "Make Karaoke Track" was requested but the vocal-removal engine is not
    /// installed yet: confirm, run the installer, then retry the request.
    /// </summary>
    private void OnEngineInstallRequested(Library.TrackEntry track)
    {
        var answer = MessageBox.Show(this,
            "The vocal removal engine (UVR / audio-separator) is not installed yet.\n\n" +
            "Install it now? This creates a private Python environment and downloads " +
            "the AI models (several hundred MB). If Python itself is missing, the " +
            "setup will offer to install it automatically.",
            "Install vocal removal engine?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var setup = new SetupWindow(_vm.SettingsRef) { Owner = this };
        if (setup.ShowDialog() == true && _vm.MakeKaraokeCommand.CanExecute(track))
            _vm.MakeKaraokeCommand.Execute(track);
    }

    /// <summary>
    /// "Set timing for selected lines": the ListBox owns the selection, so the
    /// view maps the selected line view models back to their line indexes and
    /// hands them to the view model. No lyrics, no synced times - the view model
    /// reports why instead of guessing.
    /// </summary>
    private void OnSetLyricTimingForSelection(object sender, RoutedEventArgs e)
    {
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

    private bool _ballMeasureQueued;

    /// <summary>
    /// Queue one measurement pass. Loaded priority runs after the layout/render
    /// pass that a scroll, resize or bold-word change just scheduled, so the
    /// transforms read are the final ones. Coalesces bursts (30 Hz ticks,
    /// scroll storms) into a single pass.
    /// </summary>
    private void ScheduleBallMeasure()
    {
        if (_ballMeasureQueued) return;
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
        for (int i = 0; i < centres.Length; i++)
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
    /// parent and the canvas origin is subtracted.
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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }

    // ---- per-deck waveform rendering ---------------------------------------

    private readonly List<Canvas> _deckWaveforms = new();
    private readonly Dictionary<Canvas, Line> _deckPlayheads = new();

    private void OnDeckWaveformLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (!_deckWaveforms.Contains(canvas)) _deckWaveforms.Add(canvas);
        DrawDeckWaveform(canvas);
    }

    private void OnDeckWaveformSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Canvas canvas) DrawDeckWaveform(canvas);
    }

    /// <summary>A deck's wave data changed (load / karaoke partner found).</summary>
    private void OnDeckPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeckViewModel.Waveform)
                             or nameof(DeckViewModel.KaraokeWaveform))
        {
            DrawDeckWaveforms();
            return;
        }

        // The 30 Hz clock only moves the playhead: no need to rebuild the peaks.
        if (e.PropertyName == nameof(DeckViewModel.WaveformProgress))
            MoveDeckPlayheads();
    }

    private void DrawDeckWaveforms()
    {
        foreach (var canvas in _deckWaveforms) DrawDeckWaveform(canvas);
    }

    private void MoveDeckPlayheads()
    {
        foreach (var (canvas, line) in _deckPlayheads)
        {
            var x = canvas.ActualWidth * GetProgress(canvas);
            line.X1 = x;
            line.X2 = x;
        }
    }

    private static double GetProgress(Canvas canvas) =>
        canvas.DataContext is DeckViewModel deck ? deck.WaveformProgress : 0;

    private void DrawDeckWaveform(Canvas canvas)
    {
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        canvas.Children.Clear();

        var isKaraoke = canvas.Tag as string == "karaoke";
        var deck = canvas.DataContext as DeckViewModel;
        var data = isKaraoke ? deck?.KaraokeWaveform : deck?.Waveform;

        var accent = deck?.DeckTarget == PlaybackDeck.B
            ? Color.FromRgb(0xFF, 0x3D, 0xAD)
            : Color.FromRgb(0x00, 0xC8, 0xFF);
        // The karaoke stem is the same colour, a shade softer, so the two strips
        // read as one track without the sub-stem competing for attention.
        var color = isKaraoke
            ? Color.FromArgb(0xB0, accent.R, accent.G, accent.B)
            : accent;

        if (data != null && data.Count > 1)
            RenderWaveform(canvas, data, w, h, color);

        var x = w * GetProgress(canvas);
        var playhead = new Line
        {
            X1 = x, Y1 = 0, X2 = x, Y2 = h,
            Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            StrokeThickness = 1.5,
        };
        canvas.Children.Add(playhead);
        _deckPlayheads[canvas] = playhead;
    }

    private static void RenderWaveform(Canvas canvas, IReadOnlyList<float> data,
                                      double canvasW, double canvasH, Color color)
    {
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
        };

        var points = new PointCollection();
        for (int i = 0; i < data.Count; i++)
        {
            double x = i / (double)(data.Count - 1) * canvasW;
            double amp = data[i];
            double y = (1 - amp) * (canvasH * 0.9) + canvasH * 0.05;
            points.Add(new Point(x, y));
        }
        polyline.Points = points;

        // Fill polygon (mirrors the peak shape)
        var fillPoints = new PointCollection(points)
        {
            new Point(canvasW, canvasH),
            new Point(0, canvasH),
        };
        var polygon = new Polygon
        {
            Points = fillPoints,
            Fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
        };

        canvas.Children.Add(polygon);
        canvas.Children.Add(polyline);
    }

    // ---- deck seek bars (inside the deck card template) --------------------

    private void OnDeckSeekStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckViewModel deck }) deck.BeginSeek();
    }

    private void OnDeckSeekEnd(object sender, MouseButtonEventArgs e) => CommitDeckSeek(sender);

    private void OnDeckSeekLostCapture(object sender, MouseEventArgs e) => CommitDeckSeek(sender);

    private static void CommitDeckSeek(object sender)
    {
        if (sender is Slider { DataContext: DeckViewModel deck } slider)
            deck.EndSeek(slider.Value);
    }
}
