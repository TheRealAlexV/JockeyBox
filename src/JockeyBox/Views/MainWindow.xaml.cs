using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JockeyBox.Library;
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

        // Play/Pause glyph + waveforms follow VM changes.
        UpdatePlayGlyph();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlaying)) UpdatePlayGlyph();
            if (e.PropertyName is nameof(MainViewModel.WaveformAData)
                                 or nameof(MainViewModel.WaveformBData)
                                 or nameof(MainViewModel.WaveformProgress))
                DrawWaveforms();
        };

        // Seek bar: suspend timer updates while the user is interacting, then
        // commit the seek on release. Preview events cover both thumb drags
        // and click-to-position (IsMoveToPointEnabled).
        SeekBar.PreviewMouseLeftButtonDown += (_, _) => _vm.BeginSeek();
        SeekBar.PreviewMouseLeftButtonUp += (_, _) => _vm.EndSeek(SeekBar.Value);
        SeekBar.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => _vm.EndSeek(SeekBar.Value)));

        // Keep the highlighted lyric centered in the panel.
        _vm.CurrentLyricChanged += ScrollLyricIntoCenter;

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

    /// <summary>Smoothly scroll the lyrics list so the current line sits centered.</summary>
    private void ScrollLyricIntoCenter(LyricLineViewModel line)
    {
        var container = LyricsList.ItemContainerGenerator.ContainerFromItem(line) as FrameworkElement;
        var scrollViewer = FindScrollViewer(LyricsList);
        if (container == null || scrollViewer == null)
        {
            LyricsList.ScrollIntoView(line); // fallback
            return;
        }

        var transform = container.TransformToAncestor(scrollViewer);
        var topInViewport = transform.Transform(new Point(0, 0)).Y;
        var target = scrollViewer.VerticalOffset + topInViewport
                     - scrollViewer.ViewportHeight / 2 + container.ActualHeight / 2;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
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

    // ---- waveform rendering ------------------------------------------------

    private void OnWaveformSizeChanged(object sender, SizeChangedEventArgs e) => DrawWaveforms();

    private void DrawWaveforms()
    {
        var w = WaveformCanvas.ActualWidth;
        var h = WaveformCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        WaveformCanvas.Children.Clear();

        RenderWaveform(_vm.WaveformAData, w, h, Color.FromRgb(0x00, 0xC8, 0xFF), 0, h / 2);
        RenderWaveform(_vm.WaveformBData, w, h, Color.FromRgb(0xFF, 0x3D, 0xAD), h / 2, h / 2);

        // Playhead line
        var px = _vm.WaveformProgress * w;
        WaveformCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = px, Y1 = 0, X2 = px, Y2 = h,
            Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            StrokeThickness = 1.5,
        });
    }

    private void RenderWaveform(List<float> data, double canvasW, double canvasH,
                                Color color, double yOffset, double stripH)
    {
        if (data.Count < 2) return;
        var polyline = new System.Windows.Shapes.Polyline
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
            double y = yOffset + (1 - amp) * (stripH * 0.9) + stripH * 0.05;
            points.Add(new Point(x, y));
        }
        polyline.Points = points;

        // Fill polygon (mirrors the peak shape)
        var fillPoints = new PointCollection(points);
        fillPoints.Add(new Point(canvasW, yOffset + stripH));
        fillPoints.Add(new Point(0, yOffset + stripH));
        var polygon = new System.Windows.Shapes.Polygon
        {
            Points = fillPoints,
            Fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
        };

        WaveformCanvas.Children.Add(polygon);
        WaveformCanvas.Children.Add(polyline);
    }
}
