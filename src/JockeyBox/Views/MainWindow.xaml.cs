using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
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
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.Views.Shell;

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
        LoadVirtualLines();

        // The dock's panels remember their width and their window rectangle in the
        // same settings object the view model owns: a second AppSettings.Load() here
        // would be a second copy whose saves overwrite the first.
        PanelHost.Bind(_vm.SettingsRef);
        PanelHost.PoppedOut += OnPanelPoppedOut;

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
        // Endpoint enumeration is off the UI thread and out of the view model's
        // constructor: resolving each device's FriendlyName crosses COM and can
        // take seconds, which used to block start-up long enough for the UI
        // watchdog to kill the process.
        Loaded += async (_, _) => await _vm.RefreshDevicesAsync();
        // Listen for endpoints arriving/leaving from now on: registration is
        // explicit so every start has a matching unregister in Dispose.
        Loaded += (_, _) => _vm.StartDeviceWatch();
        // Start the watchdog clock only now that a frame has rendered: the
        // 8000 ms budget is for a wedged UI thread, not for start-up work.
        ContentRendered += (_, _) => UiWatchdog.Arm();

        // Give the browser row a definite height (see OnShellLayoutChanged). The shell
        // scrolls, so its content is measured at infinite height; that is what turned
        // the track table into a list that realized every row. Writes only when the
        // number it computes changes, so it settles after one extra layout pass.
        LayoutUpdated += OnShellLayoutChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlaying)) UpdatePlayGlyph();
            // A pair load swaps the pads the A strip reads its markers from.
            else if (e.PropertyName == nameof(MainViewModel.PairCues))
            {
                HookCueSlots(_vm.PairCues?.Slots);
                DrawDeckWaveforms();
            }
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

        // The catalogue pane's TreeView selection is wired inside LibraryPanel now: the
        // block that held the tree moved out of this window, and a panel owns its own
        // controls (see Views/LibraryPanel.xaml.cs).

        // The context menu is the shared one from Views/Shared/TrackMenu.xaml:
        // its "Make Karaoke Version" entry is a command bound to the tree's
        // TrackMenu capabilities, so it needs no menu-open handler here.

        // The hotkey layer needs a window handle, and the view model is built
        // before one exists; SourceInitialized is the first moment it is there.
        SourceInitialized += (_, _) => _vm.AttachHotkeyWindow(this);

        // Soundboard pad binding: while a pad is capturing, the next chord is
        // consumed here before any window/command binding can act on it.
        PreviewKeyDown += OnSoundboardHotkeyCapture;
        PreviewKeyDown += OnShellEscape;

        Closed += (_, _) => CloseViewModel();
    }

    /// <summary>
    /// Give the browser row the height the shell can actually afford, so the track
    /// table below it gets a finite viewport to virtualize against.
    ///
    /// <para><b>Why.</b> <c>ShellScroll</c> measures its content at infinite height,
    /// and under that constraint the star rows degrade to content-sized ones, so the
    /// browser row handed its track table no viewport at all. A virtualizing panel with
    /// no viewport realizes a container for every item: 4 150 rows of ~15 elements and
    /// ~10 bindings each, measured and arranged in one layout pass. Measured on this
    /// branch at 1518x900 before the change: 4 150 containers, <c>ShellScroll</c> extent
    /// 117 181 px against an 876 px viewport.</para>
    ///
    /// <para>A definite <see cref="FrameworkElement.Height"/> rather than a
    /// <c>MaxHeight</c>: under an infinite measure a max still lets the row take its cap
    /// as a <i>desired</i> height - 778 px of shell inside a 900 px window - and the
    /// card's own Auto-plus-star pair went on handing the list a 0 px viewport. This
    /// value is exactly what the star split would produce on a normal window, so the
    /// vertical layout matches the shipped one, and it is definite at every depth below
    /// the row, which is what the card's inner Grid needs to pass a viewport down.</para>
    ///
    /// <para>It is derived from the viewport and the fixed rows only, never from the
    /// rows it sizes, so it cannot chase its own layout; it is written only when it
    /// changes, so it settles after the first pass that has a viewport.</para>
    /// </summary>
    private void OnShellLayoutChanged(object? sender, EventArgs e)
    {
        // Two levels now matter: the shell's rows (header, body) and the centre
        // column's rows (decks, waveforms, splitter, documents). The elastic pair is
        // the waveform and the documents row; everything else is a height the
        // documents cannot have, so the band is the viewport minus them. The mixer
        // dock is not in this sum any more: it shares the documents row as a column,
        // so it costs the band width, not height.
        var shellRows = ShellGrid.RowDefinitions;
        var centreRows = CentreHost.RowDefinitions;
        // PHASE 5 LANDMINE: this guard is a count, and phase 5 deletes centre row 3
        // (the documents/dock band moves). Rewritten in the same commit as that delete,
        // or every reader below stops running: the library pane silently keeps no
        // height and the shell scrolls at infinite height. Fix = name the rows you
        // actually need (DocumentsRow / WaveformRow) rather than counting them.
        if (shellRows.Count < 2 || centreRows.Count < 4) return;
        PanelHost.FitToWindow();

        double fixedHeight = shellRows[0].ActualHeight
                             + centreRows[0].ActualHeight
                             + centreRows[2].ActualHeight;
        double band = ShellScroll.ViewportHeight - fixedHeight - 2;
        if (band <= 0) return;

        // Both elastic rows are sized, not just the documents: the shell's root
        // ScrollViewer measures its content at infinite height, so a star row never
        // shrinks to the window and the dock beside it fell off the bottom at 900.
        // Their sum is the band, with the two floors honoured, so at any height that
        // can hold the floors the dock is on screen and nothing is cut off. Below
        // that the shell scrolls, which is what the root ScrollViewer is for.
        double waveFloor = centreRows[1].MinHeight;
        double docFloor = centreRows[3].MinHeight;
        double documents = Math.Min(Math.Max(band * 0.75, docFloor), Math.Max(docFloor, band - waveFloor));
        double waveform = Math.Max(waveFloor, band - documents);

        var documentRow = new GridLength(documents);
        var waveformRow = new GridLength(waveform);
        var hostHeight = documents;
        if (DocumentsRow.Height == documentRow && WaveformRow.Height == waveformRow
            && Math.Abs(BrowserHost.Height - hostHeight) < 0.5) return;

        DocumentsRow.Height = documentRow;
        WaveformRow.Height = waveformRow;
        BrowserHost.Height = hostHeight;

        // The docked panel sits in a column, not a row, so a star column has the same
        // problem the rows had: inside the root ScrollViewer it measures at the tree's
        // full height and the shell scrolls by a thousand pixels with 5425 tracks in
        // it. It gets the body height instead, and scrolls inside itself. The host
        // ignores a height it already has, so this stays a write-on-change.
        double bodyHeight = Math.Max(0, ShellScroll.ViewportHeight
                                        - shellRows[0].ActualHeight
                                        - ShellScroll.Padding.Top - ShellScroll.Padding.Bottom);
        PanelHost.SetBodyHeight(bodyHeight);
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


    /// <summary>
    /// Double-click on a channel-strip parameter row returns that value to neutral, the way
    /// a console fader snaps back. The row's Tag names the parameter, so the mapping lives
    /// in <see cref="MicChannelStripViewModel.ResetParameter"/> instead of twenty handlers.
    /// Preview, not the bubble: a slider's thumb marks the press handled before it would reach us.
    /// </summary>
    private void OnStripValueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement row || row.DataContext is not MicChannelStripViewModel strip) return;

        strip.ResetParameter(row.Tag as string);
        e.Handled = true;
    }

    // Expand-all moved to LibraryPanel.xaml.cs with the tree it walks.

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// Opens the diagnostics panel against the engine this window is already running.
    /// Modeless on purpose: the operator compares the readout with what they hear, and a
    /// modal window would mute nothing but would stop them touching the transport.
    /// </summary>
    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        => new DiagnosticsWindow(_vm.Transport.Engine, _vm.SettingsRef) { Owner = this }.Show();


    /// <summary>
    /// The toolbar's Downloads button (raised by MainViewModel.ShowDownloads)
    /// opens the console on its Downloads tab. Nothing here builds a window any
    /// more: the queue is docked in the main window.
    /// </summary>
    private void RevealConsoleDownloads()
    {
        SetDockOpen(true);
        DockTabJobs.IsChecked = true;
        DockCollapseToggle.Focus();
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
        var dialog = new SettingsWindow(_vm.SettingsRef, () => _vm.Tracks, _vm.PlaylistsRef,
                                        _vm.DeviceWatcher) { Owner = this };
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

    // Drag-to-seek on a primary strip. One pointer, one drag: the captured canvas
    // owns the preview playhead until the release commits a single seek.
    private WaveformDrag? _waveformDrag;
    private Canvas? _dragCanvas;

    // Pads that already report to OnCueSlotChanged. The deck banks live as long
    // as the deck does; the pair bank is rebuilt on every pair load, but its pads
    // are new objects each time, so they hook themselves.
    private readonly HashSet<CueSlotViewModel> _hookedCueSlots = new();

    private void OnDeckWaveformLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (!_deckWaveforms.Contains(canvas)) _deckWaveforms.Add(canvas);
        HookCueSlots((canvas.DataContext as DeckViewModel)?.Cues.Slots);
        DrawDeckWaveform(canvas);
    }

    /// <summary>A pad changed: its marker moved, appeared or went away.</summary>
    private void OnCueSlotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueSlotViewModel.PositionMs)
                             or nameof(CueSlotViewModel.HasPosition))
            DrawDeckWaveforms();
    }

    private void HookCueSlots(IEnumerable<CueSlotViewModel>? slots)
    {
        if (slots == null) return;
        foreach (var slot in slots)
            if (_hookedCueSlots.Add(slot)) slot.PropertyChanged += OnCueSlotChanged;
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
            // The 30 Hz tick must not fight the pointer: while a strip is being
            // dragged, its playhead belongs to the drag preview.
            if (canvas == _dragCanvas) continue;

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

        // Cue pads last of the data layers, so they sit above the peaks but below
        // the playhead; only the primary strip carries them.
        if (!isKaraoke) RenderCueMarkers(canvas, deck, w, h, color);

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
    /// Cue pads on the strip: a short tick growing up from the bottom edge, so it
    /// cannot cover the peaks or the beat ticks, with the pad number beside it.
    /// </summary>
    private void RenderCueMarkers(Canvas canvas, DeckViewModel? deck, double w, double h, Color color)
    {
        if (deck == null) return;

        var slots = deck.DeckTarget == PlaybackDeck.A && _vm.PairCues is { } pair
            ? pair.Slots
            : deck.Cues.Slots;

        var markers = WaveformGeometry.CueMarkers(slots, w, deck.DurationSeconds);
        if (markers.Count == 0) return;

        var brush = new SolidColorBrush(color);
        brush.Freeze();

        // A pad mark hangs from the bottom: the count-against ticks own the top.
        var tickH = Math.Max(5.0, h * 0.35);

        // One geometry for every tick, as with the beat ruler: a bank is at most
        // eight pads, but the redraw rides every load.
        var ticks = new StreamGeometry();
        using (var ctx = ticks.Open())
        {
            foreach (var marker in markers)
            {
                ctx.BeginFigure(new Point(marker.X, h), false, false);
                ctx.LineTo(new Point(marker.X, h - tickH), true, false);
            }
        }
        ticks.Freeze();
        canvas.Children.Add(new Path { Data = ticks, Stroke = brush, StrokeThickness = 1.5 });

        foreach (var marker in markers)
        {
            var label = new TextBlock
            {
                Text = marker.Label,
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                Foreground = brush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, marker.X + 2);
            Canvas.SetTop(label, h - 12);
            canvas.Children.Add(label);
        }
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

    // ---- drag to seek on a waveform strip ----------------------------------

    private void OnDeckWaveformMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || _waveformDrag != null) return;
        if (canvas.DataContext is not DeckViewModel deck) return;

        var drag = new WaveformDrag();
        if (!drag.Begin(e.GetPosition(canvas).X, canvas.ActualWidth, deck.DurationSeconds)) return;

        // The same pair the seek bar uses, so the readout freezes and the commit
        // goes through the transport's own mirroring rules.
        deck.BeginSeek();
        _waveformDrag = drag;
        _dragCanvas = canvas;
        canvas.CaptureMouse();
        PreviewWaveformPlayhead(canvas, drag.X);
        e.Handled = true;
    }

    private void OnDeckWaveformMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas || canvas != _dragCanvas) return;
        if (_waveformDrag is not { } drag) return;

        // Preview only: seeking during a move would fight the audio engine.
        if (drag.Move(e.GetPosition(canvas).X)) PreviewWaveformPlayhead(canvas, drag.X);
    }

    private void OnDeckWaveformMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || canvas != _dragCanvas) return;
        CommitWaveformDrag(canvas, e.GetPosition(canvas).X);
    }

    private void OnDeckWaveformLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas || canvas != _dragCanvas) return;
        CommitWaveformDrag(canvas, null);
    }

    private void CommitWaveformDrag(Canvas canvas, double? x)
    {
        var drag = _waveformDrag;
        var deck = canvas.DataContext as DeckViewModel;

        // Clear first: releasing the capture below re-enters through
        // LostMouseCapture, which must find nothing left to commit.
        _waveformDrag = null;
        _dragCanvas = null;
        if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();

        if (drag?.Release(x) is { } seconds && deck != null) deck.EndSeek(seconds);
    }

    private void PreviewWaveformPlayhead(Canvas canvas, double x)
    {
        if (!_deckPlayheads.TryGetValue(canvas, out var line)) return;
        line.X1 = x;
        line.X2 = x;
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

    // ---- shell panes: rail, library, dock, flyout ---------------------------

    // The pane's default width, its floor, the splitter clamp and the persisted value
    // all live in Views/Shell/DockPanelHost.cs now: a width is a property of the dock,
    // not of this window, and every panel that joins the dock gets the same rule
    // (window width minus the centre's floor, floor 300, ceiling from the layout).

    /// <summary>Which rail toggle opened the flyout, so a second press closes it.</summary>
    private ToggleButton? _flyoutToggle;

    /// <summary>One pop-out window per panel, so a second press raises the open one
    /// instead of stacking an identical duplicate.</summary>
    private readonly Dictionary<ShellPanelId, PanelWindow> _panelWindows = new();

    private void OnLibraryToggleClick(object sender, RoutedEventArgs e) => SetPanelOpen(ShellPanelId.Library, LibraryToggle.IsChecked == true);

    private void OnLyricsToggleClick(object sender, RoutedEventArgs e) => SetPanelOpen(ShellPanelId.Lyrics, LyricsToggle.IsChecked == true);

    /// <summary>Open or close the library pane. Public so the capture harness can show
    /// a state a pointer would have to click through.</summary>
    public void SetLibraryOpen(bool open) => SetPanelOpen(ShellPanelId.Library, open);

    /// <summary>
    /// Open or close one rail panel. The dock holds one panel at a time, so the rail's
    /// toggles are re-read from the host instead of trusted: opening LYRIC while LIB is
    /// up has to leave exactly one pill lit.
    ///
    /// <para>Public for the same reason <see cref="SetLibraryOpen"/> is: the capture
    /// harness has to show a state a pointer would have to click through.</para>
    /// </summary>
    public void SetPanelOpen(ShellPanelId id, bool open)
    {
        if (open) PanelHost.Show(id);
        else if (PanelHost.Current == id) PanelHost.Close();
        SyncRailToggles();
    }

    private void SyncRailToggles()
    {
        LibraryToggle.IsChecked = PanelHost.Current == ShellPanelId.Library;
        LyricsToggle.IsChecked = PanelHost.Current == ShellPanelId.Lyrics;
    }

    /// <summary>The library pane's control, or null before it has ever been shown.
    /// The capture harness selects the LIB tabs (SPOTIFY, YOUTUBE) through it.</summary>
    public LibraryPanel? LibraryPane => PanelHost.ContentOf<LibraryPanel>(ShellPanelId.Library);

    /// <summary>
    /// The dock asked for its current panel in a window. The host does not own windows,
    /// so this is where one is made: it takes the live element (not a copy, so the pane
    /// keeps its scroll position and selection) and gives it back on close.
    /// </summary>
    private void OnPanelPoppedOut(object? sender, ShellPanelId id)
    {
        if (_panelWindows.TryGetValue(id, out var existing))
        {
            existing.Activate();
            return;
        }

        if (PanelHost.DetachContent(id) is not { } content) return;
        if (PanelHost.Definition(id) is not { } definition) return;

        var window = new PanelWindow(definition, content, PanelHost, _vm.SettingsRef)
        {
            Owner = this,
            // The panel binds to the console's view model, and a window is a new root:
            // without this the moved tree inherits a null context, renders empty and
            // shows every "loading" state as if nothing had loaded.
            DataContext = DataContext,
        };
        window.Closed += (_, _) => _panelWindows.Remove(id);
        _panelWindows[id] = window;
        window.Show();
    }

    /// <summary>
    /// Undock a panel into its own window - the same path the dock's button takes.
    /// Public so the capture harness can show the state a pointer would have to click
    /// through, exactly as <see cref="SetLibraryOpen"/> is.
    /// </summary>
    public PanelWindow? PopOutPanel(ShellPanelId id)
    {
        if (PanelHost.Current != id) PanelHost.Show(id);
        PanelHost.PopOutCurrent();
        return _panelWindows.TryGetValue(id, out var window) ? window : null;
    }

    /// <summary>Close every pop-out before the console goes: an owned window outliving
    /// its owner is the one way this app could leave a surface on screen with nothing
    /// behind it.</summary>
    private void ClosePanelWindows()
    {
        foreach (var window in _panelWindows.Values.ToList()) window.Close();
        _panelWindows.Clear();
    }

    /// <summary>Open the dock on a named page: strips, soundboard, cues or jobs.</summary>
    public void SelectDockPage(string page)
    {
        SetDockOpen(true);
        (page switch
        {
            "soundboard" => DockTabSoundboard,
            "cues" => DockTabCues,
            "jobs" => DockTabJobs,
            _ => DockTabMixer,
        }).IsChecked = true;
    }

    /// <summary>Open the flyout on a named section: routing, sources, virtual or devices.</summary>
    public void OpenFlyoutSection(string section)
    {
        switch (section)
        {
            case "routing": OnFlyoutRoutingClick(RailRoutingToggle, new RoutedEventArgs()); break;
            case "sources": OnFlyoutSourcesClick(RailSourcesToggle, new RoutedEventArgs()); break;
            case "virtual": OnFlyoutVirtualClick(RailVirtualToggle, new RoutedEventArgs()); break;
            default: OnFlyoutDevicesClick(RailDevicesToggle, new RoutedEventArgs()); break;
        }
    }

    private void OnDockToggleClick(object sender, RoutedEventArgs e) => SetDockOpen(DockToggle.IsChecked == true);

    private void OnDockCollapseClick(object sender, RoutedEventArgs e) => SetDockOpen(DockCollapseToggle.IsChecked == true);

    /// <summary>Open or close the mixer dock.</summary>
    public void SetDockOpen(bool open)
    {
        DockCollapseToggle.IsChecked = open;
        DockToggle.IsChecked = open;
        DockBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // The dock is a column beside the documents now, not a row under them: folded
        // away it must give its width back, or the closed state would leave a blank
        // strip where the dock used to be.
        DockColumn.Width = open ? new GridLength(DockWidth, GridUnitType.Pixel) : new GridLength(0);
        DockSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Width the mixer dock opens at, before the splitter moves it.</summary>
    private const double DockWidth = 420;

    private void OnFlyoutRoutingClick(object sender, RoutedEventArgs e) => ShowFlyout(RailRoutingToggle, "ROUTING", FlyoutRoutingPanel);

    private void OnFlyoutSourcesClick(object sender, RoutedEventArgs e) => ShowFlyout(RailSourcesToggle, "SOURCES", FlyoutSourcesPanel);

    private void OnFlyoutVirtualClick(object sender, RoutedEventArgs e) => ShowFlyout(RailVirtualToggle, "VIRTUAL DEVICES", FlyoutVirtualPanel);

    private void OnFlyoutDevicesClick(object sender, RoutedEventArgs e) => ShowFlyout(RailDevicesToggle, "DEVICES", FlyoutDevicesPanel);

    /// <summary>
    /// One flyout, four contents. Pressing the toggle of the section already showing
    /// closes the region, so the rail is both the opener and the way back out.
    /// </summary>
    private void ShowFlyout(ToggleButton toggle, string title, FrameworkElement panel)
    {
        if (FlyoutPanel.Visibility == Visibility.Visible && ReferenceEquals(_flyoutToggle, toggle))
        {
            CloseFlyout();
            return;
        }

        RailRoutingToggle.IsChecked = ReferenceEquals(toggle, RailRoutingToggle);
        RailSourcesToggle.IsChecked = ReferenceEquals(toggle, RailSourcesToggle);
        RailVirtualToggle.IsChecked = ReferenceEquals(toggle, RailVirtualToggle);
        RailDevicesToggle.IsChecked = ReferenceEquals(toggle, RailDevicesToggle);

        // The strip band's own fold-out toggle went with the band: it is always open
        // in the panel, and the panel is the fold.
        if (ReferenceEquals(panel, FlyoutSourcesPanel)) _vm.AreStripControlsOpen = true;

        _flyoutToggle = toggle;
        FlyoutTitle.Text = title;
        FlyoutRoutingPanel.Visibility = ReferenceEquals(panel, FlyoutRoutingPanel) ? Visibility.Visible : Visibility.Collapsed;
        FlyoutSourcesPanel.Visibility = ReferenceEquals(panel, FlyoutSourcesPanel) ? Visibility.Visible : Visibility.Collapsed;
        FlyoutVirtualPanel.Visibility = ReferenceEquals(panel, FlyoutVirtualPanel) ? Visibility.Visible : Visibility.Collapsed;
        FlyoutDevicesPanel.Visibility = ReferenceEquals(panel, FlyoutDevicesPanel) ? Visibility.Visible : Visibility.Collapsed;
        FlyoutPanel.Visibility = Visibility.Visible;
    }

    private void OnFlyoutCloseClick(object sender, RoutedEventArgs e) => CloseFlyout();

    private void CloseFlyout()
    {
        FlyoutPanel.Visibility = Visibility.Collapsed;
        _flyoutToggle = null;
        RailRoutingToggle.IsChecked = false;
        RailSourcesToggle.IsChecked = false;
        RailVirtualToggle.IsChecked = false;
        RailDevicesToggle.IsChecked = false;
    }

    /// <summary>
    /// Escape is the flyout's own exit. It is handled before anything else in the
    /// window can claim the key, and it never touches focus: the deck the operator
    /// was on keeps it.
    /// </summary>
    private void OnShellEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (FlyoutPanel.Visibility != Visibility.Visible) return;
        CloseFlyout();
        e.Handled = true;
    }

    // ---- virtual device lines ----------------------------------------------
    // The five flags and the ring name live here and nowhere else. They used to
    // be a wrap panel of checkboxes in Settings as well; two editors over one flag
    // is how the two ends up disagreeing, so the Settings copy was deleted.

    private bool _virtualLinesLoading;

    private void LoadVirtualLines()
    {
        var settings = _vm.SettingsRef;
        _virtualLinesLoading = true;
        try
        {
            VirtualLineDeckA.IsChecked = settings.VirtualAsioDeckA;
            VirtualLineDeckB.IsChecked = settings.VirtualAsioDeckB;
            VirtualLineRecording.IsChecked = settings.VirtualAsioRecording;
            VirtualLineSoundboard.IsChecked = settings.VirtualAsioSoundboard;
            VirtualLineMonitor.IsChecked = settings.VirtualAsioMonitor;
            VirtualLineBusName.Text = settings.VirtualAsioBusName;
        }
        finally { _virtualLinesLoading = false; }
    }

    /// <summary>
    /// A line is a checkbox only because the ring has one section: ticking a second
    /// line releases the first instead of refusing the change in a dialog, which is
    /// not something to be doing mid-show.
    /// </summary>
    private void OnVirtualLineChanged(object sender, RoutedEventArgs e)
    {
        if (_virtualLinesLoading) return;

        var box = (CheckBox)sender;
        _virtualLinesLoading = true;
        try
        {
            if (box.IsChecked == true)
            {
                foreach (var other in new[]
                         {
                             VirtualLineDeckA, VirtualLineDeckB, VirtualLineRecording,
                             VirtualLineSoundboard, VirtualLineMonitor,
                         })
                {
                    if (!ReferenceEquals(other, box)) other.IsChecked = false;
                }
            }

            var settings = _vm.SettingsRef;
            settings.VirtualAsioDeckA = VirtualLineDeckA.IsChecked == true;
            settings.VirtualAsioDeckB = VirtualLineDeckB.IsChecked == true;
            settings.VirtualAsioRecording = VirtualLineRecording.IsChecked == true;
            settings.VirtualAsioSoundboard = VirtualLineSoundboard.IsChecked == true;
            settings.VirtualAsioMonitor = VirtualLineMonitor.IsChecked == true;
            settings.Save();
        }
        finally { _virtualLinesLoading = false; }
    }

    /// <summary>
    /// The ring name is validated here with the same rule the save path uses, so a
    /// typo cannot make the native writer fail at route time, where the operator
    /// would only hear silence.
    /// </summary>
    private void OnVirtualLineNameChanged(object sender, RoutedEventArgs e)
    {
        if (_virtualLinesLoading) return;

        var settings = _vm.SettingsRef;
        string name = VirtualLineBusName.Text.Trim();
        if (name.Length == 0) name = "main";
        if (AppSettings.ValidateVirtualAsioBusName(name) is not null)
        {
            VirtualLineBusName.Text = settings.VirtualAsioBusName;
            return;
        }

        if (string.Equals(name, settings.VirtualAsioBusName, StringComparison.Ordinal)) return;
        settings.VirtualAsioBusName = name;
        VirtualLineBusName.Text = name;
        settings.Save();
    }
}
