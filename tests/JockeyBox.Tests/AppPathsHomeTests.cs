using System;
using System.IO;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A <c>JOCKEYBOX_HOME</c> that is set but unusable must be a loud failure, never a
/// silent resolution to <c>%APPDATA%\JockeyBox</c>. The capture guard only sees runs
/// carrying a capture flag, so a normal windowed launch with a launcher-mangled value
/// walked straight into the operator's real profile.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class AppPathsHomeTests
{
    [Theory]
    [InlineData(@"C:\Dev\home ", "trailing space")]      // the 2026-11-14 incident value
    [InlineData("sandbox/home", "unrooted relative path")]
    public void A_malformed_override_is_reported_with_its_canonical_form(string configured, string why)
    {
        string? problem = AppPaths.HomeProblemFor(configured, rootExists: _ => true);

        Assert.False(string.IsNullOrWhiteSpace(problem), why);
        Assert.Contains(AppPaths.HomeVariable, problem);
        Assert.Contains(configured, problem);
        Assert.Contains("Canonical form", problem);
        Assert.Contains(AppPaths.LiveHome, problem);   // names the home it refuses to fall back to
        Assert.Contains(AppPaths.HomeVariable, problem);
    }

    [Fact]
    public void An_absent_root_is_reported()
    {
        string? problem = AppPaths.HomeProblemFor(@"Z:\sandbox\home", rootExists: _ => false);

        Assert.NotNull(problem);
        Assert.Contains(@"Z:\", problem);
    }

    [Fact]
    public void A_usable_override_is_accepted_and_an_empty_one_means_unset()
    {
        Assert.Null(AppPaths.HomeProblemFor(@"C:\Dev\hw\sandbox", rootExists: _ => true));
        Assert.Null(AppPaths.HomeProblemFor(""));
        Assert.Null(AppPaths.HomeProblemFor("   "));
        Assert.Null(AppPaths.HomeProblemFor(null));
    }

    /// <summary>
    /// The resolution itself: with a malformed value in the environment the reported
    /// home is what was configured, not the live profile. This is the assertion that
    /// fails if the old "fall back when unusable" behaviour comes back.
    /// </summary>
    [Fact]
    public void A_malformed_override_does_not_resolve_to_the_live_profile()
    {
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, @"C:\Dev\home ");

            Assert.Equal(@"C:\Dev\home ", AppPaths.Home);
            Assert.NotEqual(AppPaths.LiveHome, AppPaths.Home);
            Assert.NotNull(AppPaths.HomeProblem);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
        }
    }

    [Fact]
    public void An_empty_override_still_resolves_to_the_live_profile()
    {
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, "");

            Assert.Equal(AppPaths.LiveHome, AppPaths.Home);
            Assert.Null(AppPaths.HomeProblem);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
        }
    }
}
