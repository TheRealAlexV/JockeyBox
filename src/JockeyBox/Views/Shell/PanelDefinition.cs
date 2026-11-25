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
/// <para>Phase 1 registered <see cref="ShellPanelId.Library"/>, phase 2 adds
/// <see cref="ShellPanelId.Lyrics"/>, and phases 3 and 4 add the rest. <see cref="ShellPanelId"/> already names all seven, so the rail can talk
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

    /// <summary>Lyric lines are the document, so the pane takes the widest column the
    /// centre floor allows: at 1400 that is about 545 px, the same ceiling LIB opens at.</summary>
    private const double LyricsDefaultWidth = 545;

    /// <summary>Below this the line text and the bouncing ball collide. The old lyrics
    /// window refused to shrink past 360; the dock's own caption takes the difference.</summary>
    private const double LyricsMinWidth = 320;

    /// <summary>The width the mixer held as a column in the centre's bottom band. Every
    /// strip, the EQ and the console were laid out against it, so it opens at the same
    /// number here rather than at whatever the dock happens to default to.</summary>
    private const double MixDefaultWidth = 420;

    /// <summary>Below this the four page radios and the strip faders collide: STRIPS,
    /// SOUNDBOARD, CUES and JOBS plus their padding need 300, and the strip fader
    /// column needs 20 more to stay draggable.</summary>
    private const double MixMinWidth = 320;

    /// <summary>The four rail sections (ROUTE, SRC, VIRT, DEV) opened inside a 400 px
    /// flyout clamped to 360..420. 420 is what they were always trying to be, and the
    /// dock's splitter is the way to ask for less.</summary>
    private const double RailPanelDefaultWidth = 420;

    /// <summary>The dock's own floor for everything that is not the library. The
    /// flyout's 360 was the region's clamp, not the content's need: the widest thing
    /// in any of the four is the strip rack's 244 px band plus its padding.</summary>
    private const double RailPanelMinWidth = 320;
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

        // Lyrics is a document the same way the library is: read, not configured.
        // Its caption is the operator's word, singular. The panel's own pop-out button
        // stays off because the dock's caption carries one, and the element is the same
        // one when it sits in a window, so two pop-out paths can never both be live.
        new PanelDefinition(
            ShellPanelId.Lyrics,
            "LYRIC",
            "Lyrics panel",
            LyricsDefaultWidth,
            LyricsMinWidth,
            AllowsPopOut: true,
            () => new LyricsPanel { ShowPopOutButton = false }),

        // The mixer was a 420 px column in the centre's bottom band, and it keeps that
        // number here: the strips, the EQ and the console were laid out against it, so
        // opening it at the dock's own default would re-flow every one of them. Below
        // 320 the four page radios and the strip faders collide.
        new PanelDefinition(
            ShellPanelId.Mix,
            "MIX",
            "Mixer panel: strips, soundboard, cue bank and jobs",
            MixDefaultWidth,
            MixMinWidth,
            AllowsPopOut: true,
            () => new MixerPanel()),

        // ---- the four rail sections that used to share one flyout ----------
        // All four were 400 px wide inside a region clamped to 360..420; they open at
        // the ceiling instead, because the dock's own splitter is the way to make one
        // narrower and the value is remembered per panel. The floor is the dock's 320,
        // not the flyout's clamped 360: the flyout had no splitter, so 360 was an
        // accident of the region rather than a property of any of these four.
        new PanelDefinition(
            ShellPanelId.Routing,
            "ROUTING",
            "Routing panel: where each bus plays",
            RailPanelDefaultWidth,
            RailPanelMinWidth,
            AllowsPopOut: true,
            () => new RoutingPanel()),

        new PanelDefinition(
            ShellPanelId.Sources,
            "SOURCES",
            "Sources panel: the selected strip's stages",
            RailPanelDefaultWidth,
            RailPanelMinWidth,
            AllowsPopOut: true,
            () => new SourcesPanel()),

        new PanelDefinition(
            ShellPanelId.Virtual,
            "VIRTUAL DEVICES",
            "Virtual devices panel: the lines published to the ASIO ring",
            RailPanelDefaultWidth,
            RailPanelMinWidth,
            AllowsPopOut: true,
            () => new VirtualLinesPanel()),

        new PanelDefinition(
            ShellPanelId.Devices,
            "DEVICES",
            "Devices panel: microphone input and monitor output",
            RailPanelDefaultWidth,
            RailPanelMinWidth,
            AllowsPopOut: true,
            () => new DevicesPanel()),
    };
}
