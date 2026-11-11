using System.Windows;
using System.Windows.Input;
using JockeyBox.ViewModels;
using JockeyBox.YouTube;

namespace JockeyBox.Views;

/// <summary>
/// The "find an upload" window. It owns nothing: the query, the results, the
/// audition strip and the pick action all live in
/// <see cref="YouTubeSearchViewModel"/>, so this file is only window plumbing -
/// focus on open, keyboard shortcuts, and stopping the preview on close.
///
/// Modeless on purpose: an operator who has just found the right upload keeps
/// working behind it instead of having to dismiss it first. It is still owned by
/// the main window, so it stays above it and never outlives it.
/// </summary>
public partial class YouTubeSearchWindow : Window
{
    private readonly YouTubeSearchViewModel _vm;

    public YouTubeSearchWindow(YouTubeSearchViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Land on results, not on an empty box: the query came from the row, so
        // search it immediately and leave the whole query selected for the case
        // where the operator wants to widen or narrow it.
        QueryBox.Focus();
        QueryBox.SelectAll();

        if (_vm.Results.Count == 0 && !_vm.IsSearching)
            _vm.SearchCommand.Execute(null);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Closing is the only stop button the window needs to have: whatever it
        // started streaming ends here, and a preview started in a tab survives.
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _vm.Shutdown();
    }

    /// <summary>Double-click a row to audition it - the list equivalent of the PREVIEW button.</summary>
    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.PreviewCommand.Execute(_vm.SelectedResult);

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.PreviewCommand.Execute(_vm.SelectedResult);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>Escape anywhere in the window closes it, not only from the list.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
