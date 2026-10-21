using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
