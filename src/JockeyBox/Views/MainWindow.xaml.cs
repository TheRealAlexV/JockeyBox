using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Activity;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Separation;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>
    /// Console actions live on the window because the rows they act on are on
    /// <see cref="ActivityFeed.Shared"/>, not on the view model. Initialised
    /// before <c>InitializeComponent</c> so the XAML bindings find them.
    /// </summary>
    public IRelayCommand<string> CancelActivityCommand { get; } =
        new RelayCommand<string>(key =>
        {
            if (!string.IsNullOrEmpty(key)) ActivityFeed.Shared.Cancel(key);
        });

    public IRelayCommand ClearFinishedActivityCommand { get; } =
        new RelayCommand(() => ActivityFeed.Shared.ClearFinished(),
                         () => ActivityFeed.Shared.Rows.Any(r => r.IsFinished));

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Adopt the saved talk-over mic (device, gain, monitor) and start the
        // peak meter. The transport may not have an engine yet; this is a no-op
        // then and the settings dialog path re-applies it.
        _vm.InitializeMic();

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
        _vm.DownloadsRequested += RevealConsoleDownloads;

        // The queue's status line and cache figures used to tick only while the
        // (now deleted) DownloadsWindow was open; the docked console is always
        // there, so the main window owns the polling lifetime instead.
        _vm.Downloads.StartPolling();
        Closed += (_, _) => _vm.Downloads.StopPolling();

        // "Clear finished" is only offered when a finished row exists. Rows are
        // created by producers, so the window watches the feed rather than
        // polling it.
        ActivityFeed.Shared.Rows.CollectionChanged += OnActivityRowsChanged;
        HookActivityRows();

        // Sync TreeView selection to the view model (SelectedItem is read-only
        // in XAML so we wire it in code-behind).
        TrackTree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is BrowserNode node) _vm.SelectedTreeNode = node;
        };

        // The context menu is the shared one from Views/Shared/TrackMenu.xaml:
        // its "Make Karaoke Version" entry is a command bound to the tree's
        // TrackMenu capabilities, so it needs no menu-open handler here.

        // The hotkey layer needs a window handle, and the view model is built
        // before one exists; SourceInitialized is the first moment it is there.
        SourceInitialized += (_, _) => _vm.AttachHotkeyWindow(this);

        // Soundboard pad binding: while a pad is capturing, the next chord is
        // consumed here before any window/command binding can act on it.
        PreviewKeyDown += OnSoundboardHotkeyCapture;

        Closed += (_, _) => CloseViewModel();
    }

    /// <summary>
    /// Tear the session down from the main window's close without letting a
    /// failure there defeat the close itself.
    ///
    /// <para>WPF decides whether to shut the application down after its Closed
    /// handlers have returned, so an exception out of this teardown does not fail
    /// a save: the window is gone, the Application never reaches its shutdown
    /// decision, and the process is left with no window of any kind while still
    /// holding the home's single-instance lock and port 17890. That is the state
    /// the operator had to stop by pid on 2026-11-11, with no exit-path line in
    /// the log beyond the step that threw. Log it and let the close finish.</para>
    /// </summary>
    private void CloseViewModel()
    {
        try
        {
            _vm.Dispose();
        }
        catch (Exception ex)
        {
            // Type, message and stack: the stack names the call that threw, and
            // TeardownReached names how much of the exit path was skipped, so a
            // reader needs no debugger to know which step broke. Then the close
            // finishes, which is the part that keeps WPF's shutdown decision
            // reachable.
            Tools.Log.Error(
                $"shutdown: view-model teardown failed in '{_vm.TeardownReached}'. " +
                $"Every later step was skipped, including the library.json re-mirror that marks a clean exit. " +
                $"The window still closes, so the process can shut down.{Environment.NewLine}{ex}");
        }
    }

    /// <summary>
    /// Feed the pressed chord to the soundboard while a pad is waiting for its
    /// binding. Entering capture mode is explicit (⌨ on the tile), so this never
    /// steals ordinary typing.
    /// </summary>
    private void OnSoundboardHotkeyCapture(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_vm.Soundboard.TryCaptureHotkey(Keyboard.Modifiers, key))
            e.Handled = true;
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

    /// <summary>
    /// The toolbar's Downloads button (raised by MainViewModel.ShowDownloads)
    /// opens the console on its Downloads tab. Nothing here builds a window any
    /// more: the queue is docked in the main window.
    /// </summary>
    private void RevealConsoleDownloads()
    {
        ConsoleToggle.IsChecked = true;
        ConsoleTabs.SelectedIndex = 1;
        ConsoleToggle.Focus();
    }

    /// <summary>
    /// Keep "Clear finished" in step with the feed: re-hook every row (finished
    /// flags change in place) and re-query the command.
    /// </summary>
    private void HookActivityRows()
    {
        foreach (var row in ActivityFeed.Shared.Rows)
        {
            row.PropertyChanged -= OnActivityRowChanged;
            row.PropertyChanged += OnActivityRowChanged;
        }

        ClearFinishedActivityCommand.NotifyCanExecuteChanged();
    }

    private void OnActivityRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => HookActivityRows();

    private void OnActivityRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityRow.IsFinished))
            ClearFinishedActivityCommand.NotifyCanExecuteChanged();
    }

    private void OpenSettings()
    {
        // Library tools work on the rows the main window already scans; hand the
        // settings dialog a getter rather than a second scan.
        var dialog = new SettingsWindow(_vm.SettingsRef, () => _vm.Tracks, _vm.PlaylistsRef) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.OnSettingsSaved();
            _vm.OnSettingsSavedMic();
            // Six-bus routing, soundboard-follows-Main and Karaoke mode: the
            // transport re-reads the saved document, so a Save is audible without
            // restarting (outputs are only reopened when their ids changed).
            _vm.ApplyAudioSettings();
        }
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
        if (setup.ShowDialog() == true)
            _vm.MakeKaraoke(track, KaraokeStemMode.Instrumental);
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
                             or nameof(DeckViewModel.KaraokeWaveform)
                             // Beat ticks ride the same invalidation: a new track
                             // (or its beat grid arriving after analysis) redraws
                             // peaks and ticks together.
                             or nameof(DeckViewModel.BeatMarkers))
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

        // Ticks first, so the peaks and the playhead always sit on top of them.
        if (deck != null) RenderBeatMarkers(canvas, deck, w, h, isKaraoke);

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

    /// <summary>
    /// Beat ticks for the loaded track, drawn behind the peaks and the playhead.
    /// One geometry per class (plain beats, bar starts) keeps a thousand-tick grid
    /// cheap: two path elements instead of a thousand Line objects.
    /// </summary>
    private static void RenderBeatMarkers(Canvas canvas, DeckViewModel deck,
                                          double w, double h, bool isKaraoke)
    {
        // The karaoke sub-strip is 12 px of secondary detail; a beat ruler there
        // would only add noise, so the ticks belong to the primary waveform.
        if (isKaraoke) return;

        var beats = deck.BeatMarkers;
        if (beats == null || beats.Count == 0 || w <= 0 || h <= 0) return;

        // Marks hang from the top edge: long enough to count against, short enough
        // to leave the peak shape readable underneath.
        var tickH = Math.Max(4.0, h * 0.32);
        var barH = Math.Max(7.0, h * 0.60);

        var beatGeo = new StreamGeometry();
        var barGeo = new StreamGeometry();
        using (var bc = beatGeo.Open())
        using (var gc = barGeo.Open())
        {
            for (int i = 0; i < beats.Count; i++)
            {
                var p = beats[i];
                if (double.IsNaN(p) || p < 0 || p > 1) continue;

                var x = p * w;
                var isBar = i % 4 == 0;
                var ctx = isBar ? gc : bc;
                ctx.BeginFigure(new Point(x, 0), false, false);
                ctx.LineTo(new Point(x, isBar ? barH : tickH), true, false);
            }
        }
        beatGeo.Freeze();
        barGeo.Freeze();

        // Light marks at roughly 35%, with every 4th beat longer and brighter so
        // the bar structure reads at a glance without fighting the deck colour.
        const byte beatAlpha = 90;
        const byte barAlpha = 190;

        canvas.Children.Add(new Path
        {
            Data = beatGeo,
            Stroke = new SolidColorBrush(Color.FromArgb(beatAlpha, 0xE8, 0xEA, 0xED)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        });
        canvas.Children.Add(new Path
        {
            Data = barGeo,
            Stroke = new SolidColorBrush(Color.FromArgb(barAlpha, 0xE8, 0xEA, 0xED)),
            StrokeThickness = 1.4,
            IsHitTestVisible = false,
        });
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

    // ---- transport ---------------------------------------------------------

    private DateTime _previousPressedAt = DateTime.MinValue;

    /// <summary>
    /// One press restarts the active track; a second press inside 450 ms steps
    /// back a track. Same button, as on a CD deck.
    /// </summary>
    private void OnPreviousPress(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var now = DateTime.UtcNow;
        var secondPress = now - _previousPressedAt < TimeSpan.FromMilliseconds(450);
        _previousPressedAt = now;

        var command = secondPress ? (ICommand)vm.PlayPreviousCommand : vm.RestartTrackCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ---- deck card activation ---------------------------------------------

    /// <summary>
    /// Any click inside a deck card makes that deck the active one, so a deck's
    /// own transport buttons never move the other deck.
    /// </summary>
    private void OnDeckInteract(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckViewModel deck } &&
            deck.ActivateCommand.CanExecute(null))
        {
            deck.ActivateCommand.Execute(null);
        }
    }
}
