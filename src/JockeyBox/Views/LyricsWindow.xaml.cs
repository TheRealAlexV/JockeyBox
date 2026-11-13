using System;
using System.ComponentModel;
using System.Windows;
using JockeyBox.Settings;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The lyrics panel in its own window. One instance, hosted by the main window and
/// torn down with it; the panel itself reads the shared <see cref="MainViewModel"/>,
/// so this file only carries the window concerns: always-on-top, geometry, and
/// putting both back on close.
///
/// <para>
/// Modeless on purpose (same rule as <see cref="YouTubeSearchWindow"/>): the
/// operator keeps working in the main window while the lyrics stay readable on a
/// second screen. It is owned by the main window, so it never outlives it and does
/// not take a taskbar slot.
/// </para>
/// </summary>
public partial class LyricsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _geometryLoaded;

    public LyricsWindow(MainViewModel vm, AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = vm;

        TopmostToggle.IsChecked = settings.LyricsWindowTopmost;
        Topmost = settings.LyricsWindowTopmost;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreGeometry();
        _geometryLoaded = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e) => SaveState();

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        _settings.LyricsWindowTopmost = Topmost;
        _settings.Save();
    }

    /// <summary>
    /// Restore the saved rectangle, but only where it still makes sense: a window
    /// saved on a monitor that is no longer attached would otherwise open off
    /// screen. CenterOwner at design size is the fallback.
    /// </summary>
    private void RestoreGeometry()
    {
        var left = _settings.LyricsWindowLeft;
        var top = _settings.LyricsWindowTop;
        var width = _settings.LyricsWindowWidth;
        var height = _settings.LyricsWindowHeight;

        if (width is > 200) Width = width.Value;
        if (height is > 150) Height = height.Value;

        if (left is null || top is null) return;

        var candidate = new Rect(left.Value, top.Value,
            double.IsNaN(Width) ? 780 : Width,
            double.IsNaN(Height) ? 486 : Height);

        if (!IsOnAnyScreen(candidate)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = candidate.Left;
        Top = candidate.Top;
    }

    private static bool IsOnAnyScreen(Rect candidate)
    {
        var screens = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var screensTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        // A sliver of the title bar has to be reachable, not the whole rectangle:
        // partially off screen is a legitimate placement.
        return candidate.Left >= SystemParameters.VirtualScreenLeft - 8
               && candidate.Top >= SystemParameters.VirtualScreenTop - 8
               && candidate.Left + 80 <= screens
               && candidate.Top + 32 <= screensTop;
    }

    private void SaveState()
    {
        if (!_geometryLoaded) return;

        // RestoreBounds is the normal rectangle even when maximized/minimized.
        var bounds = RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;

        // A headless capture parks the window far off screen; saving that
        // rectangle would make the next real launch open nowhere.
        if (!IsOnAnyScreen(bounds)) return;

        _settings.LyricsWindowLeft = bounds.Left;
        _settings.LyricsWindowTop = bounds.Top;
        _settings.LyricsWindowWidth = bounds.Width;
        _settings.LyricsWindowHeight = bounds.Height;
        _settings.Save();
    }
}
