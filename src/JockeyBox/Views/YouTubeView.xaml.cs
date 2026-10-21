using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using JockeyBox.ViewModels;

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
        if (Vm?.ImportCommand.CanExecute(null) == true)
        {
            Vm.ImportCommand.Execute(null);
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
