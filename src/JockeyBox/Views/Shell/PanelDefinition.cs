using System;
using System.Collections.Generic;
using System.Windows;

namespace JockeyBox.Views.Shell;

/// <summary>
/// Everything the shell needs to know about one pane: what to call it, how wide it
/// opens, whether it may leave the dock, and how to build its content.
///
/// <para><see cref="Create"/> is a factory rather than an instance because a panel
/// is only built when it is first shown: seven panes built at window construction
/// would put the whole console's visual tree in memory before anyone asked for it.</para>
/// </summary>
public sealed record PanelDefinition(
    ShellPanelId Id,
    string Title,
    string AccessibleName,
    double DefaultWidth,
    double MinWidth,
    bool AllowsPopOut,
    Func<FrameworkElement> Create);

/// <summary>
/// The panel set, in one place: one registration per pane, so "what can the dock
/// hold" is answered by reading this file instead of hunting the rail.
///
/// <para>Phase 1 registers <see cref="ShellPanelId.Library"/> only, because it is the
/// only pane whose content and widths are built. Phases 3 and 4 add a registration
/// each. <see cref="ShellPanelId"/> already names all seven, so the rail can talk
/// about a panel that is not registered yet; <see cref="IsRegistered"/> is the guard
/// the host and the rail use rather than guessing.</para>
/// </summary>
public static class PanelCatalog
{
    /// <summary>Width the library opens at: 155 px of playlist rail plus the 346 px
    /// the shared track table needs for its six columns, plus the tree's scrollbar
    /// gutter. At 300 the table gets 107 px, so Artist cut mid-word at the pane edge
    /// and the Icons strip was off-screen.</summary>
    private const double LibraryDefaultWidth = 545;

    /// <summary>Below this the pane is not worth opening; the columns give way as
    /// they already do in TrackRows.xaml instead of the whole pane shrinking to a
    /// strip.</summary>
    private const double LibraryMinWidth = 300;

    /// <summary>
    /// The production panel set, in the order the shell offers them. This is a value
    /// handed to a <see cref="DockPanelHost"/>; the host itself knows nothing about
    /// this list, so a test can build a host from two synthetic panes and adding a
    /// pane here is one entry rather than an edit to the control.
    /// </summary>
    public static IReadOnlyList<PanelDefinition> Registered { get; } = new[]
    {
        new PanelDefinition(
            ShellPanelId.Library,
            "LIBRARY",
            "Library panel",
            LibraryDefaultWidth,
            LibraryMinWidth,
            AllowsPopOut: true,
            () => new LibraryPanel()),
    };
}
