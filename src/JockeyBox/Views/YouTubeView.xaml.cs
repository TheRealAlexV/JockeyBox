using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.YouTube;

namespace JockeyBox.Views;

/// <summary>
/// YouTube search, import and playlist tab. Row gestures and the two small
/// value formatters live here; everything else is bound to the view model.
/// </summary>
public partial class YouTubeView : UserControl
{
    public YouTubeView()
    {
        InitializeComponent();
    }

    private YouTubeViewModel? Vm => DataContext as YouTubeViewModel;

    // ---- shared row menu + filter/sort strip ------------------------------

    private TrackMenuCapabilities? _caps;
    private TrackListView? _strip;
    private string _filterText = "";
    private ObservableCollection<YtEntry>? _results;
    private Dictionary<object, int>? _rank;

    private void OnViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => BuildSharedUi();

    /// <summary>
    /// The shared menu and strip need this tab's own commands, which XAML cannot
    /// reach from inside a ContextMenu. The filter and the sort run on the
    /// collection view, so they work on the results list without asking the view
    /// model to change: search order belongs to YouTube, not to us.
    /// </summary>
    private void BuildSharedUi()
    {
        if (DataContext is not YouTubeViewModel vm) return;

        _caps = new TrackMenuCapabilities
        {
            Play = vm.PlayResultCommand,
            Preview = vm.PreviewResultCommand,
            Download = vm.DownloadResultCommand,
            AddToPlaylist = vm.AddResultToPlaylistCommand,
        };
        ResultList.Tag = _caps;

        _strip = new TrackListView(
            "Filter results by title or channel",
            text => { _filterText = text; ApplyResultView(); },
            (_, _) => ApplyResultView());
        FilterHost.Content = _strip;

        // A new search replaces the results list (clear + adds) without going
        // through ApplyResultView, so the arrival ranks must follow it or they
        // point at the previous search's positions. Rebuild in place: the view
        // defers its own CustomSort refresh past this handler, then reads the
        // fresh map.
        if (!ReferenceEquals(_results, vm.Results))
        {
            if (_results is not null) _results.CollectionChanged -= OnResultsChanged;
            _results = vm.Results;
            _results.CollectionChanged += OnResultsChanged;
        }
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rank is null || _results is null) return;
        _rank.Clear();
        for (var i = 0; i < _results.Count; i++) _rank[_results[i]] = i;
    }

    private void ApplyResultView()
    {
        if (Vm is null) return;

        var view = CollectionViewSource.GetDefaultView(Vm.Results);
        view.Filter = o => MatchesFilter(o as YtEntry);

        // Same comparer as the library and Spotify lists, so a column orders the
        // same way everywhere. CustomSort must come after the clear: touching
        // SortDescriptions drops a custom sort set before it.
        //
        // WPF sorts CustomSort with ArrayList.Sort (introsort, unstable), so the
        // arrival rank is the final tie-break: ties keep result order instead of
        // an arbitrary order that changes on every refresh. The map is built from
        // the source list, which the view never reorders.
        view.SortDescriptions.Clear();
        if (view is ListCollectionView list)
        {
            _rank = new Dictionary<object, int>(
                Vm.Results.Count, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < Vm.Results.Count; i++) _rank[Vm.Results[i]] = i;
            list.CustomSort = new TrackSortComparer(
                _strip?.SortField ?? TrackSortField.TrackName,
                _strip?.SortDescending == true,
                _rank);
        }
    }

    private bool MatchesFilter(YtEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        if (entry is null) return false;

        var query = _filterText.Trim();
        return entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Uploader.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Leaving the tab (switch or close) stops the streamed preview: the audio
    /// belongs to this pane, and nothing should keep playing from a tab the
    /// user has left.
    /// </summary>
    private void OnViewUnloaded(object sender, RoutedEventArgs e) => Vm?.StopPreview();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm?.SearchCommand.CanExecute(null) == true)
        {
            Vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnImportKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm?.LoadImporterCommand.CanExecute(null) == true)
        {
            Vm.LoadImporterCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
        if (Vm?.PlayResultCommand.CanExecute(null) == true)
            Vm.PlayResultCommand.Execute(null);
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm?.PlayResultCommand.CanExecute(null) == true)
        {
            Vm.PlayResultCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item != null) item.IsSelected = true;
    }

    private void OnPlaylistTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) == null) return;
        if (Vm?.PlayPlaylistTrackCommand.CanExecute(null) == true)
            Vm.PlayPlaylistTrackCommand.Execute(null);
    }

    private void OnPlaylistTrackKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm?.PlayPlaylistTrackCommand.CanExecute(null) == true)
        {
            Vm.PlayPlaylistTrackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPlaylistTrackRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null) item.IsSelected = true;
    }

    /// <summary>
    /// Rebuild "Add to playlist ▸" from the store each time the menu opens.
    /// Built in code (not from ItemsSource) because a context menu is outside the
    /// visual tree - a generated submenu item cannot reach the tab view model by
    /// RelativeSource - and because the list must reflect playlists created since
    /// the tab was opened. See the same method in SpotifyView.
    /// </summary>
    private void OnPlaylistContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListView { ContextMenu: { } menu }) return;
        if (DataContext is not YouTubeViewModel { LibraryPlaylists: { } targets } vm) return;

        var submenu = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => item.Name == "AddToPlaylistMenu");
        if (submenu is null) return;

        targets.Refresh();
        submenu.Items.Clear();
        foreach (var choice in targets.Choices)
        {
            var item = new MenuItem
            {
                Header = choice.Header,
                Command = vm.AddToInternalPlaylistCommand,
                CommandParameter = choice,
            };

            AutomationProperties.SetName(item, choice.Header);
            submenu.Items.Add(item);
        }

        submenu.IsEnabled = targets.Choices.Count > 0;
        Log.Debug($"remote row menu: 'Add to playlist' filled with {targets.Choices.Count} row(s)");
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
}

/// <summary>TimeSpan to "m:ss" (or "h:mm:ss") for duration columns.</summary>
public sealed class YtDurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var span = value switch
        {
            TimeSpan t => t,
            double seconds => TimeSpan.FromSeconds(seconds),
            int seconds => TimeSpan.FromSeconds(seconds),
            _ => TimeSpan.Zero,
        };
        if (span <= TimeSpan.Zero) return "--:--";
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>View count to a short "1.2M views" label, left blank when unknown.</summary>
public sealed class ViewCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is long l ? l : value is int i ? i : 0;
        if (count <= 0) return "";

        return count switch
        {
            >= 1_000_000_000 => $"{count / 1_000_000_000d:0.#}B views",
            >= 1_000_000 => $"{count / 1_000_000d:0.#}M views",
            >= 1_000 => $"{count / 1_000d:0.#}K views",
            _ => $"{count} views",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
