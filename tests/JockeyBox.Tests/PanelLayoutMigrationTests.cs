using JockeyBox.Settings;
using JockeyBox.Views.Shell;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The lyrics pop-out used to be its own window with its own saved rectangle. That
/// window is gone, so the rectangle has to move to the LYRIC panel's entry in
/// <see cref="AppSettings.PanelLayout"/> or the operator's second-screen placement is
/// silently lost and the panel reopens on the primary monitor.
///
/// <para>Pure settings logic, no window: the restore itself is
/// <see cref="WindowGeometry"/>'s job and the harness resizes a popped window for its
/// own render, so a capture cannot tell a seeded width from a forced one.</para>
/// </summary>
public class PanelLayoutMigrationTests
{
    private static AppSettings WithLegacyLyricsRectangle() => new()
    {
        LyricsWindowLeft = 1200,
        LyricsWindowTop = 40,
        LyricsWindowWidth = 700,
        LyricsWindowHeight = 420,
        LyricsWindowTopmost = true,
    };

    [Fact]
    public void LyricsPanelSeedsItsEntryFromTheOldWindowsRectangle()
    {
        var settings = WithLegacyLyricsRectangle();

        var entry = settings.Panel(ShellPanelId.Lyrics);

        Assert.Equal(1200, entry.WindowLeft);
        Assert.Equal(40, entry.WindowTop);
        Assert.Equal(700, entry.WindowWidth);
        Assert.Equal(420, entry.WindowHeight);
        Assert.True(entry.Topmost);
        Assert.Same(entry, settings.PanelLayout["Lyrics"]);
    }

    [Fact]
    public void SeedingHappensOnceSoLaterWindowWritesWin()
    {
        var settings = WithLegacyLyricsRectangle();
        var entry = settings.Panel(ShellPanelId.Lyrics);
        entry.WindowLeft = 64;

        // The legacy values are still in the file; a second read must not undo the
        // operator moving the panel after the upgrade.
        settings.LyricsWindowLeft = 1200;
        Assert.Equal(64, settings.Panel(ShellPanelId.Lyrics).WindowLeft);
    }

    [Fact]
    public void AnUntouchedWindowSeedsNothing()
    {
        // Null means "never placed": the panel must fall back to its own default
        // rather than inheriting a rectangle that was never written.
        var entry = new AppSettings().Panel(ShellPanelId.Lyrics);

        Assert.Null(entry.WindowLeft);
        Assert.Null(entry.WindowWidth);
        Assert.False(entry.Topmost);
    }

    [Fact]
    public void TheLibraryEntryDoesNotTakeTheLyricsRectangle()
    {
        var settings = WithLegacyLyricsRectangle();

        var library = settings.Panel(ShellPanelId.Library);

        Assert.Null(library.WindowLeft);
        Assert.Null(library.WindowWidth);
        Assert.False(library.Topmost);
    }
}
