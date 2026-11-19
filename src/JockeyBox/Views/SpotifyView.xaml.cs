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

namespace JockeyBox.Views;

/// <summary>
/// Spotify browser tab. Row gestures live here because ListView has no command
/// for double-click or Enter; the commands themselves are on the view model.
/// </summary>
public partial class SpotifyView : UserControl
{
    public SpotifyView()
    {
        InitializeComponent();
    }

    private SpotifyViewModel? Vm => DataContext as SpotifyViewModel;

    /// <summary>
    /// Leaving the tab (switch or close) stops the streamed preview: the audio
    /// belongs to this pane, and nothing should keep playing from a tab the
    /// user has left.
    /// </summary>
    private void OnViewUnloaded(object sender, RoutedEventArgs e) => Vm?.StopPreview();

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) == null) return;
        if (Vm?.PlaySelectedCommand.CanExecute(null) == true)
            Vm.PlaySelectedCommand.Execute(null);
    }

    private void OnTrackKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm?.PlaySelectedCommand.CanExecute(null) == true)
        {
            Vm.PlaySelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>WPF does not select a row on right-click; do it so the menu targets the row under the cursor.</summary>
    private void OnTrackRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null) item.IsSelected = true;
    }

    /// <summary>
    /// The shared row menu needs a capability object, and the shared filter strip
    /// needs a TrackListView. Both are built here because they hold this tab's own
    /// commands, which the XAML cannot reach from inside a context menu or a
    /// DataTemplate.
    /// </summary>
    private TrackMenuCapabilities? _caps;

    private void OnViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => BuildSharedUi();

    private void BuildSharedUi()
    {
        if (DataContext is not SpotifyViewModel vm) return;

        var caps = new TrackMenuCapabilities
        {
            Play = vm.PlaySelectedCommand,
            Preview = vm.PreviewTrackCommand,
            Download = vm.DownloadSelectedCommand,
            MakeKaraoke = vm.SeparateSelectedKaraokeCommand,
            MakeKaraokeKeepBacking = vm.SeparateSelectedKaraokeKeepBackingCommand,
            MakeKaraokeVocalsOnly = vm.SeparateSelectedKaraokeVocalsOnlyCommand,
            ShowInExplorer = vm.RevealSelectedFileCommand,
            FindOnYouTube = vm.OpenSelectedOnYouTubeCommand,
        };

        _caps = caps;
        TrackList.Tag = caps;
        FilterHost.Content = new TrackListView(
            "Filter the track list by title, artist or album",
            text => vm.FilterText = text,
            (field, descending) => vm.ApplySort(field, descending))
        {
            // Only this surface offers "Date added": its rows are the ones with AddedAt.
            SortChoices = TrackSortChoices.Spotify,
        };

        // "Add to Playlist" is a submenu of the user's own playlists, refreshed on
        // every open so a playlist created a second ago is already there.
        if (TrackList.ContextMenu is { } menu) menu.Opened += (_, _) => RefreshPlaylistTargets(vm, caps);
        RefreshPlaylistTargets(vm, caps);
    }

    private static void RefreshPlaylistTargets(SpotifyViewModel vm, TrackMenuCapabilities caps)
    {
        if (vm.LibraryPlaylists is not { } targets) return;

        targets.Refresh();
        caps.PlaylistTargets.Clear();
        foreach (var choice in targets.Choices)
        {
            var target = choice;
            caps.PlaylistTargets.Add(new PlaylistTargetItem(
                target.Header,
                new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                    vm.AddToInternalPlaylistCommand.Execute(target))));
        }

        caps.NotifyStructureChanged();
        Log.Debug($"remote row menu: 'Add to Playlist' filled with {targets.Choices.Count} row(s)");
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

/// <summary>Visible when the bound bool is false — used for the disconnected state.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when a laid-out height is below the threshold carried in ConverterParameter.
/// Used to ask a stretched card "are you short?" without a view-model property, so
/// the fold follows the window instead of a saved setting.
/// </summary>
public sealed class HeightBelowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double height) return false;
        double threshold = double.TryParse(parameter as string, NumberStyles.Number,
            CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
        // Zero means "not measured yet", not "no room": folding on that would make
        // every card start folded, flip back on the first real arrange, and cost a
        // second layout pass (the list panel realizes one row more on it).
        return height > 0 && height < threshold;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
