using System;
using JockeyBox.Selftest;

namespace JockeyBox.Tests;

/// <summary>
/// The capture guard: a headless mode must refuse the operator's live roaming
/// home, because a run that reached it once rewrote library.db, library.json,
/// playlist-cache.json and settings.json. The windowed app must keep working.
/// </summary>
public sealed class CaptureGuardTests
{
    private const string Live = @"C:\Users\Alex\AppData\Roaming\JockeyBox";
    private const string Sandbox = @"C:\Dev\shots\sandbox-home";

    [Theory]
    [InlineData("--uishot")]
    [InlineData("--uishot-dump")]
    [InlineData("--perf")]
    [InlineData("--preview-check")]
    [InlineData("--selftest")]
    [InlineData("--lyrics-probe")]
    [InlineData("--linger-check")]
    public void Refuses_the_live_home_for_every_capture_mode(string flag)
    {
        string? refusal = CaptureHomeGuard.Refusal(Live, Live, allowed: false, new[] { flag });

        Assert.NotNull(refusal);
        Assert.Contains(flag, refusal);
        Assert.Contains(Live, refusal);
        Assert.Contains(CaptureHomeGuard.AllowFlag, refusal);
    }

    [Fact]
    public void Refuses_the_live_home_when_a_trailing_separator_differs()
    {
        Assert.NotNull(CaptureHomeGuard.Refusal(Live + @"\", Live, false, new[] { "--uishot" }));
    }

    [Fact]
    public void Runs_against_a_sandbox_home()
    {
        Assert.Null(CaptureHomeGuard.Refusal(Sandbox, Live, allowed: false, new[] { "--uishot" }));
    }

    [Fact]
    public void Allow_flag_bypasses_the_refusal()
    {
        Assert.Null(CaptureHomeGuard.Refusal(Live, Live, allowed: true, new[] { "--uishot" }));
    }

    [Fact]
    public void Allow_flag_is_read_from_the_command_line()
    {
        string[] optIn = { "--uishot", CaptureHomeGuard.AllowFlag };

        Assert.NotNull(CaptureHomeGuard.Refusal(new[] { "--uishot" }, Live, Live));
        Assert.Null(CaptureHomeGuard.Refusal(optIn, Live, Live));
        Assert.Null(CaptureHomeGuard.Refusal(new[] { "--uishot", "--allow-live-home=true" }, Live, Live));
    }

    [Fact]
    public void The_windowed_app_is_not_restricted()
    {
        Assert.Null(CaptureHomeGuard.Refusal(Live, Live, allowed: false, Array.Empty<string>()));
    }

    [Fact]
    public void Exit_code_is_non_zero_and_does_not_collide()
    {
        // 1 = single-instance refusal, 2 = usage, 3 = clamped, 4 = unsettled.
        Assert.DoesNotContain(CaptureHomeGuard.RefusalExitCode, new[] { 0, 1, 2, 3, 4 });
    }
}
