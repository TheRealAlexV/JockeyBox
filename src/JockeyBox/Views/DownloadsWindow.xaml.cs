using System.Windows;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// Live download queue. DataContext is the shared DownloadsViewModel so the
/// window mirrors the queue that the browser tabs fill.
/// </summary>
public partial class DownloadsWindow : Window
{
    private readonly DownloadsViewModel _downloads;

    public DownloadsWindow(DownloadsViewModel downloads)
    {
        InitializeComponent();
        _downloads = downloads;
        DataContext = downloads;

        // The poll timer belongs to the view model, which outlives this window,
        // so its lifetime is tied to the window here instead of the constructor.
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _downloads.StartPolling();

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _downloads.StopPolling();
    }
}
