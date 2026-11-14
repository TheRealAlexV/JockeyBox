using System;
using System.IO;
using JockeyBox.Separation;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The guard that keeps the venv-launch flake from coming back. The failure was a
/// race on the process-wide <c>JOCKEYBOX_HOME</c>: one class repointed it while
/// another read it twice and compared the two results. The collection attribute is
/// the fix; <see cref="HomeLease"/> is the alarm, so a class that claims the home
/// without being serialised fails by name instead of making an unrelated test flake.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public class HomeLeaseTests
{
    [Fact]
    public void A_second_concurrent_claim_names_both_owners_and_the_fix()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jb-lease-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        using var first = HomeLease.Enter(dir, "FirstClaimant");
        var error = Assert.Throws<InvalidOperationException>(
            () => HomeLease.Enter(dir, "SecondClaimant"));

        Assert.Contains("FirstClaimant", error.Message);
        Assert.Contains("SecondClaimant", error.Message);
        Assert.Contains("JockeyBoxHomeCollection", error.Message);
    }

    [Fact]
    public void Releasing_one_claim_lets_the_next_one_through()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jb-lease-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        using (HomeLease.Enter(dir, "First")) { Assert.True(HomeLease.IsHeld); }
        Assert.False(HomeLease.IsHeld);

        using (HomeLease.Enter(dir, "Second")) { Assert.True(HomeLease.IsHeld); }
        Assert.False(HomeLease.IsHeld);
    }

    [Fact]
    public void The_claim_is_what_the_app_reads_and_dispose_puts_it_back()
    {
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        string dir = Path.Combine(Path.GetTempPath(), "jb-lease-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        using (HomeLease.Enter(dir, nameof(The_claim_is_what_the_app_reads_and_dispose_puts_it_back)))
        {
            Assert.Equal(dir, AppPaths.Home);
            // The path the separator is told to use follows the claim, which is the
            // link between this guard and the assertion that used to flake.
            Assert.Equal(Path.Combine(dir, "uvr-models"), SeparationEngine.ModelsDir);
        }

        Assert.Equal(previous, Environment.GetEnvironmentVariable(AppPaths.HomeVariable));
        Assert.False(HomeLease.IsHeld);
    }
}
