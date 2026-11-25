using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JockeyBox.Library;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The catalogue pane: LIBRARY and QUEUE. Its XAML is the block that used to sit
/// inside MainWindow as <c>LibraryPane</c>; this file carries the three behaviours
/// that block needs, which used to live in the window's code-behind.
///
/// <para>The panel is built by <see cref="Shell.PanelCatalog"/> and inherits its
/// DataContext from whatever hosts it (the dock, or its own window), so nothing here
/// holds a view-model reference - the window keeps owning the view model.</para>
/// </summary>
public partial class LibraryPanel : UserControl
{
    private bool _expanded;

    public LibraryPanel()
    {
        InitializeComponent();

        // TreeView.SelectedItem is read-only, so the view model is fed from here.
        TrackTree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is BrowserNode node && DataContext is MainViewModel vm)
                vm.SelectedTreeNode = node;
        };
    }

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

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is BrowserNode node && !node.IsFolder && DataContext is MainViewModel vm)
            vm.LoadSelectedTreeTrackCommand.Execute(null);
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
}
