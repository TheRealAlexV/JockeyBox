using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using JockeyBox.Settings;

namespace JockeyBox.Views.Shell;

/// <summary>
/// One docked panel in its own window. Modeless and owned by the main window, so it
/// never outlives the console and takes no taskbar slot - the same rules the old lyrics
/// pop-out and <see cref="YouTubeSearchWindow"/> follow.
///
/// <para>On close it gives the content back to the dock and the dock reopens on it, so
/// undocking and redocking are one round trip with no rebuild: the operator's scroll
/// position and selection come back exactly as they were.</para>
/// </summary>
public partial class PanelWindow : Window
{
    private readonly PanelDefinition _definition;
    private readonly DockPanelHost _host;
    private readonly AppSettings _settings;
    private FrameworkElement? _content;

    public PanelWindow(PanelDefinition definition, FrameworkElement content, DockPanelHost host, AppSettings settings)
    {
        _definition = definition;
        _host = host;
        _settings = settings;

        InitializeComponent();

        Title = definition.Title;
        Caption.Text = definition.Title;
        AutomationProperties.SetName(this, definition.AccessibleName);

        _content = content;
        PanelContent.Reparent(content, ContentHost);

        var entry = settings.Panel(definition.Id);
        TopmostToggle.IsChecked = entry.Topmost;
        Topmost = entry.Topmost;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var entry = _settings.Panel(_definition.Id);
        WindowGeometry.Restore(this, entry.WindowLeft, entry.WindowTop, entry.WindowWidth, entry.WindowHeight,
            fallbackWidth: 560, fallbackHeight: 640);
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        _settings.Panel(_definition.Id).Topmost = Topmost;
        _settings.Save();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var entry = _settings.Panel(_definition.Id);

        // Saved while the window still has its rectangle: after Closing the bounds are
        // gone. RestoreBounds covers the maximised case, where Width/Height are the
        // screen's rather than the operator's.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (WindowGeometry.IsOnAnyScreen(bounds))
        {
            entry.WindowLeft = bounds.Left;
            entry.WindowTop = bounds.Top;
            entry.WindowWidth = bounds.Width;
            entry.WindowHeight = bounds.Height;
        }
        _settings.Save();

        if (_content != null)
        {
            ContentHost.Content = null;
            _host.ReturnContent(_definition.Id, _content);
            _content = null;
        }
    }
}
