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
using JockeyBox.Tools;
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

    // ---- console detail pane ------------------------------------------------
    // Clipboard is a view concern, so these live in code-behind rather than on
    // ActivityFeed: the feed formats the text, the view moves it.

    private void CopyJobDetail_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(ActivityFeed.DescribeRow(ActivityFeed.Shared.SelectedRow), "job detail");

    private void CloseJobDetail_Click(object sender, RoutedEventArgs e)
        => ActivityFeed.Shared.SelectedRow = null;

    private void CopyLogLine_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(ActivityFeed.Shared.SelectedLogLine, "log line");

    private static void CopyToClipboard(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource; another app holding it is
            // not a reason to take the app down.
            Log.Warn($"Copy {what} failed: {ex.Message}");
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Adopt the saved talk-over mic (device, gain, monitor) and start the
        // peak meter. The transport may not have an engine yet; this is a no-op
        // then and the settings dialog path re-applies it.
        _vm.InitializeMic();

        // MON: the header's switch and the two deck sends. Same shape as the mic -
        // adopt the saved state, then push it to the engine.
        _vm.InitializeMonitor();

        // LOW / MID / HIGH: the mix EQ between the deck cards. Same shape again, and
        // before the first block is rendered, so a persisted mix is already in
        // circuit rather than waiting for the first drag.
        _vm.InitializeEq();

        // Play/Pause glyph follows VM changes; each deck card owns its waveform.
        UpdatePlayGlyph();
        // The transport row's link and sync switches, and the cue banks, are wired
        // from a partial beside MainViewModel; its constructor belongs to another
        // lane, so the window is what calls the init hook.
        Loaded += (_, _) => _vm.InitializeDeckUi();

        // Bound what the browser row can measure (see OnShellLayoutChanged). The
        // shell scrolls, so its content is measured at infinite height; that is what
        // turned the track table into a list that realized every row. Writes only when
        // the cap it computes changes, so it settles after one extra layout pass.
        LayoutUpdated += OnShellLayoutChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlaying)) UpdatePlayGlyph();
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

        // Lyrics panel: the control owns its own scroll/ball wiring so the pop-out
        // window gets the same behaviour without a second copy here.
        LyricsHost.PopOutRequested += (_, _) => OpenLyricsWindow();

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

        Closed += (_, _) => CloseLyricsWindow();
        Closed += (_, _) => CloseViewModel();
    }

    /// <summary>Last cap written to <see cref="BrowserHost"/>, so a layout pass that
    /// recomputes the same number does not touch the element.</summary>
    private double _browserMaxHeight = double.NaN;

    /// <summary>
    /// Cap the browser row's height at the band the shell can actually give it.
    ///
    /// <para><b>Why.</b> <c>ShellScroll</c> measures its content at infinite height,
    /// and under that constraint every star row degrades to a content-sized one - so
    /// the browser row handed its track table no viewport at all. A virtualizing
    /// panel with no viewport realizes a container for every item, which is the
    /// freeze: 4 150 rows of ~15 elements and ~10 bindings each, measured and
    /// arranged in one layout pass. Measured at 1518x900 before this: 4 150
    /// containers, <c>ShellScroll</c> extent 117 181 px against an 876 px
    /// viewport.</para>
    ///
    /// <para>The cap only has to be an upper bound on the measure - the row keeps its
    /// <c>3*</c> weight, so on a normal window the star arrangement is what decides how
    /// tall the browser actually is and this number never bites. It is derived from the
    /// viewport alone, never from the rows it sizes, so it cannot chase its own
    /// layout: the vertical scrollbar takes width, so the viewport height does not move
    /// when the scrollbar appears or the extent changes.</para>
    /// </summary>
    private void OnShellLayoutChanged(object? sender, EventArgs e)
    {
        var rows = ShellGrid.RowDefinitions;
        if (rows.Count < 8) return;

        // Rows 2 (waveform) and 4 (browser) are the elastic pair, 1:3 in the XAML. The
        // browser gets what the star split would give it: three quarters of the band, or
        // the band minus the waveform's floor when that floor is the binding one. Every
        // other row is a height the browser cannot have, so the band is the viewport
        // minus them. Two px of slack keeps the content a hair under the viewport when
        // it fits, so the scrollbar does not sit exactly on its threshold. The floor is
        // what the browser keeps when the fixed rows have grown past the window: below
        // it the shell scrolls, which is what the shell is for.
        double fixedHeight = rows[0].ActualHeight + rows[1].ActualHeight
            + rows[3].ActualHeight + rows[5].ActualHeight + rows[6].ActualHeight
            + rows[7].ActualHeight;
        double band = ShellScroll.ViewportHeight - fixedHeight - 2;
        double cap = Math.Max(200,
            Math.Min(band * 0.75, band - rows[2].MinHeight));

        if (Math.Abs(cap - _browserMaxHeight) < 0.5) return;
        _browserMaxHeight = cap;
        BrowserHost.Height = cap;
    }

    /// <summary>The single pop-out lyrics window, if one is open.</summary>
    private LyricsWindow? _lyricsWindow;

    /// <summary>
    /// Open (or bring forward) the pop-out lyrics window. One instance for the
    /// session: the panel holds no per-track state, so re-opening only re-activates
    /// the window that already exists.
    /// </summary>
    private void OpenLyricsWindow()
    {
        if (_lyricsWindow != null)
        {
            if (_lyricsWindow.WindowState == WindowState.Minimized)
                _lyricsWindow.WindowState = WindowState.Normal;
            _lyricsWindow.Activate();
            return;
        }

        var window = new LyricsWindow(_vm, _vm.SettingsRef) { Owner = this };
        // A second window must not survive its owner: closing the main window
        // closes this one first, and the reference goes with it.
        window.Closed += (_, _) => _lyricsWindow = null;
        _lyricsWindow = window;
        window.Show();
    }

    /// <summary>Close the pop-out with the main window, without letting a failure stop the close.</summary>
    private void CloseLyricsWindow()
    {
        var window = _lyricsWindow;
        _lyricsWindow = null;
        if (window == null) return;
        try
        {
            window.Close();
        }
        catch
        {
            // The window is going away with the process anyway.
        }
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
