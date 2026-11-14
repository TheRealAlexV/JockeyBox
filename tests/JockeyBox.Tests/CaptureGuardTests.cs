using System;
using System.IO;
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

    /// <summary>
    /// The 2026-11-14 01:11 incident: a launcher wrote
    /// <c>set JOCKEYBOX_HOME=C:\Dev\home &amp;&amp; cmd</c>, cmd captured the space, and
    /// the home became <c>"C:\Dev\home "</c>. It resolved to a real directory while
    /// never comparing equal to it, so a capture reached the live profile.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\Alex\AppData\Roaming\JockeyBox ")]        // the space before &&
    [InlineData("  C:\\Users\\Alex\\AppData\\Roaming\\JockeyBox\t")] // surrounding whitespace
    [InlineData(@"C:\Users\Alex\AppData\Roaming\JockeyBox\")]        // trailing separator
    [InlineData(@"C:\Users\Alex\AppData\Roaming\JockeyBox\\")]       // doubled trailing separator
    [InlineData(@"c:\users\alex\appdata\roaming\jockeybox")]
    public void Refuses_a_malformed_live_home(string home)
    {
        // One writable-by-the-test root, so only the live-profile rule can refuse:
        // otherwise the sandbox-root rule would hide a broken live comparison.
        string? refusal = CaptureHomeGuard.Refusal(
            home, Live, allowed: false, new[] { "--uishot" }, new[] { @"C:\" });

        Assert.NotNull(refusal);
        Assert.Contains("--uishot", refusal);
        Assert.Contains("live roaming profile", refusal);
    }

    [Fact]
    public void Refuses_a_home_outside_every_sandbox_root_while_a_live_profile_exists()
    {
        string live = NewDirectory("jb-live-");
        string stray = NewDirectory("jb-stray-");
        string root = NewDirectory("jb-roots-");
        try
        {
            Assert.NotNull(CaptureHomeGuard.Refusal(
                stray, live, allowed: false, new[] { "--uishot" }, new[] { root }));
            Assert.Null(CaptureHomeGuard.Refusal(
                Path.Combine(root, "home"), live, allowed: false, new[] { "--uishot" }, new[] { root }));
        }
        finally
        {
            foreach (string dir in new[] { live, stray, root }) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_live_profile_does_not_invent_a_refusal()
    {
        string live = Path.Combine(Path.GetTempPath(), "jb-no-live-" + Guid.NewGuid().ToString("n"));
        string home = NewDirectory("jb-home-");
        try
        {
            Assert.Null(CaptureHomeGuard.Refusal(
                home, live, allowed: false, new[] { "--uishot" }, Array.Empty<string>()));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static string NewDirectory(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
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
