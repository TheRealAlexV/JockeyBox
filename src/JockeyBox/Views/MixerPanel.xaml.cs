using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Activity;
using JockeyBox.Tools;

namespace JockeyBox.Views;

/// <summary>
/// The mixer surface: channel strips and the master EQ, the soundboard, the cue bank
/// and the jobs console, on four pages behind one segmented strip.
///
/// <para><b>Why the page radios are not surface switches.</b> A surface is a place in
/// the shell - the rail's MIX button opens and closes it, and the dock gives it a
/// width, a pop-out and a memory of its own. A page is one of four things inside that
/// place, so choosing JOBS must not move the panel or change its width.</para>
///
/// <para><b>Why the console actions live here.</b> "Clear finished" and the per-job
/// Cancel act on <see cref="ActivityFeed.Shared"/>, not on the window, and a popped-out
/// panel is a different window: a command that only existed on <c>MainWindow</c> left
/// both buttons dead the moment the panel left the dock. Bound to this control they
/// resolve in either host, because the panel is the ancestor in both.</para>
/// </summary>
public partial class MixerPanel : UserControl
{
    public MixerPanel()
    {
        // Before InitializeComponent: the XAML binds both, and a binding that meets a
        // null command shows the button disabled rather than failing loudly.
        ClearFinishedActivityCommand = new RelayCommand(
            () => ActivityFeed.Shared.ClearFinished(),
            () => ActivityFeed.Shared.Rows.Any(r => r.IsFinished));
        CancelActivityCommand = new RelayCommand<string>(key =>
        {
            if (!string.IsNullOrEmpty(key)) ActivityFeed.Shared.Cancel(key);
        });

        InitializeComponent();

        // "Clear finished" is only offered when a finished row exists. Rows are made by
        // producers, so the console watches the feed instead of polling it.
        ActivityFeed.Shared.Rows.CollectionChanged += OnRowsChanged;
        HookRows();
    }

    /// <summary>Remove finished rows from the console's job list.</summary>
    public IRelayCommand ClearFinishedActivityCommand { get; }

    /// <summary>Cancel one job by key, from its row in the console.</summary>
    public IRelayCommand<string> CancelActivityCommand { get; }

    /// <summary>
    /// Bring one page to the front: <c>strips</c> (the default), <c>soundboard</c>,
    /// <c>cues</c> or <c>jobs</c>. The capture harness and the Downloads button both
    /// ask by name, so an unknown name lands on the strips rather than nowhere.
    /// </summary>
    public void SelectPage(string page)
    {
        (page switch
        {
            "soundboard" => DockTabSoundboard,
            "cues" => DockTabCues,
            "jobs" => DockTabJobs,
            _ => DockTabMixer,
        }).IsChecked = true;
    }

    private void HookRows()
    {
        foreach (var row in ActivityFeed.Shared.Rows)
        {
            row.PropertyChanged -= OnRowChanged;
            row.PropertyChanged += OnRowChanged;
        }

        ClearFinishedActivityCommand.NotifyCanExecuteChanged();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => HookRows();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityRow.IsFinished))
            ClearFinishedActivityCommand.NotifyCanExecuteChanged();
    }

    // ---- console detail pane ------------------------------------------------
    // Clipboard is a view concern, so these live in code-behind rather than on
    // ActivityFeed: the feed formats the text, the view moves it.

    private void CopyJobDetail_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(ActivityFeed.DescribeRow(ActivityFeed.Shared.SelectedRow), "job detail");

    private void CloseJobDetail_Click(object sender, RoutedEventArgs e)
        => ActivityFeed.Shared.SelectedRow = null;

    private void CopyLogLine_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(ActivityFeed.Shared.SelectedLogLine, "log line");

    private static void CopyToClipboard(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource; another app holding it is
            // not a reason to take the app down.
            Log.Warn($"Copy {what} failed: {ex.Message}");
        }
    }
}
