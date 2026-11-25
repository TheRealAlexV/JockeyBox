using System.Windows;
using System.Windows.Input;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The selected strip's own stages in signal order, EQ through the reverb send, for one
/// channel.
///
/// <para><b>Why the double-click handler lives here.</b> It reads the row's
/// <c>Tag</c> and resets the parameter on the strip that row is bound to, so it
/// names elements inside this panel. It was hooked from <c>MainWindow</c> while the
/// band was a region of that window; a handler on a window cannot reach a named row
/// inside a detached root, and a pop-out would have been dead. Here the panel is the
/// ancestor in both hosts, exactly as the mixer's console commands are.</para>
///
/// <para><b>Why the rack is always open.</b> The band used to fold out over the
/// mixer, covering the controls it was configuring, so it carried its own toggle
/// and a visibility flag. In a panel beside the mix the panel is the fold: showing
/// the strip's stages is what opening SRC means. The flag is still set on load
/// because the rack row is bound to it and a false value would leave an empty
/// panel holding a heading.</para>
/// </summary>
public partial class SourcesPanel
{
    public SourcesPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.AreStripControlsOpen = true;
    }

    /// <summary>Double-click a value row to put that parameter back to neutral.</summary>
    private void OnStripValueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement row || row.DataContext is not MicChannelStripViewModel strip) return;

        strip.ResetParameter(row.Tag as string);
        e.Handled = true;
    }
}
