using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// The net has to be provably able to fire without anyone starving a real UI
/// thread: the decision is a pure function, so these tests pin the boundary, the
/// off switch and the parsed threshold. The breadcrumb tests pin what the report
/// will say - a name while the operation runs, and the previous name once it
/// ends, because a report that names the wrong operation is worse than none.
/// </summary>
public class UiWatchdogTests
{
    // ---- the decision ------------------------------------------------------

    [Fact]
    public void ShouldFire_ZeroThreshold_IsOffHoweverStaleTheTick()
    {
        Assert.False(UiWatchdog.ShouldFire(600_000, 0));
    }

    [Fact]
    public void ShouldFire_NegativeThreshold_IsOff()
    {
        Assert.False(UiWatchdog.ShouldFire(600_000, -100));
    }

    [Fact]
    public void ShouldFire_JustBelowThreshold_DoesNotFire()
    {
        Assert.False(UiWatchdog.ShouldFire(7_999, 8_000));
    }

    [Fact]
    public void ShouldFire_ExactlyAtThreshold_Fires()
    {
        Assert.True(UiWatchdog.ShouldFire(8_000, 8_000));
    }

    [Fact]
    public void ShouldFire_PastThreshold_Fires()
    {
        Assert.True(UiWatchdog.ShouldFire(9_000, 8_000));
    }

    // ---- the setting -------------------------------------------------------

    [Theory]
    [InlineData(null, UiWatchdog.DefaultThresholdMs)]
    [InlineData("", UiWatchdog.DefaultThresholdMs)]
    [InlineData("   ", UiWatchdog.DefaultThresholdMs)]
    [InlineData("garbage", UiWatchdog.DefaultThresholdMs)]
    [InlineData("1500", 1500)]
    [InlineData(" 1500 ", 1500)]
    [InlineData("0", 0)]
    [InlineData("-5", 0)]
    [InlineData("50", 100)]
    public void ParseThreshold_ReadsTheSettingOrFallsBackToTheDefault(string? raw, int expected)
    {
        Assert.Equal(expected, UiWatchdog.ParseThreshold(raw));
    }

    [Fact]
    public void DefaultThreshold_IsEightSeconds()
    {
        Assert.Equal(8_000, UiWatchdog.DefaultThresholdMs);
    }

    // ---- the breadcrumb ----------------------------------------------------

    [Fact]
    public void Breadcrumb_NamesTheOperationWhileHeld_AndClearsAfterwards()
    {
        var before = UiBreadcrumb.Held;

        using (UiBreadcrumb.Enter("playlist store: flush to disk"))
        {
            Assert.Equal("playlist store: flush to disk", UiBreadcrumb.Held);
            Assert.True(UiBreadcrumb.HeldSinceTicks > 0);
        }

        Assert.Equal(before, UiBreadcrumb.Held);
    }

    [Fact]
    public void Breadcrumb_NestsAndRestoresTheOuterName()
    {
        var before = UiBreadcrumb.Held;

        using (UiBreadcrumb.Enter("outer"))
        {
            using (UiBreadcrumb.Enter("inner"))
                Assert.Equal("inner", UiBreadcrumb.Held);

            Assert.Equal("outer", UiBreadcrumb.Held);
        }

        Assert.Equal(before, UiBreadcrumb.Held);
    }

    [Fact]
    public void Breadcrumb_Exit_ClearsWithoutRestoring()
    {
        var before = UiBreadcrumb.Held;

        var scope = UiBreadcrumb.Enter("library scan: apply the scan result");
        UiBreadcrumb.Exit();
        Assert.Equal("", UiBreadcrumb.Held);

        scope.Dispose();
        Assert.Equal(before, UiBreadcrumb.Held);
    }

    // ---- the heartbeat -----------------------------------------------------

    [Fact]
    public void NoteUiTick_MakesTheTickFresh()
    {
        UiBreadcrumb.NoteUiTick();
        var age = UiWatchdog.LastTickAgeMs();

        Assert.InRange(age, 0, 5_000); // generous: a loaded test box can pause this thread
        Assert.False(UiWatchdog.ShouldFire(age, UiWatchdog.DefaultThresholdMs));
    }
}
