using System;
using System.IO;
using System.Linq;
using System.Threading;
using JockeyBox.Selftest;

namespace JockeyBox.Tests;

/// <summary>
/// Runs the same offline graph self-test that ships as <c>JockeyBox.exe --selftest</c>.
///
/// <para><b>Why the exe path needs a test.</b> The runner is the only thing that
/// renders the real engine end to end, so without this the CI gate would be a
/// script that nothing on the developer machine ever executes. The failure detail
/// is echoed into the assertion message: a red test that says "crossfade-law: max
/// deviation 0.14" is worth a hundred that say "expected true".</para>
///
/// <para>The suite renders a few seconds of audio per case through
/// <c>AudioEngine.StartOffline</c> and touches no device, so it is safe on CI.</para>
/// </summary>
public class SelftestTests
{
    private const int CaseCount = 8;

    private static readonly Lazy<(int ExitCode, string Output)> Result = new(() =>
    {
        using var writer = new StringWriter();
        int exitCode = SelftestRunner.Run(writer);
        return (exitCode, writer.ToString());
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Selftest_AllCases_Pass()
    {
        var (exitCode, output) = Result.Value;

        Assert.True(exitCode == 0,
            $"selftest exited {exitCode}\n{output}");
        Assert.Contains($"SELFTEST OK {CaseCount}/{CaseCount}", output);
        Assert.DoesNotContain("FAIL", output);
    }

    [Fact]
    public void Selftest_PrintsOneLinePerCase()
    {
        string output = Result.Value.Output;

        string[] passLines = output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("PASS ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(CaseCount, passLines.Length);

        foreach (string name in new[]
                 {
                     "silence-at-rest", "peak-headroom", "tempo-ratio", "output-delay",
                     "crossfade-law", "no-zero-reads", "end-of-track-handoff",
                     "deck-output-delay",
                 })
        {
            Assert.Contains(passLines, line => line.StartsWith($"PASS {name}", StringComparison.Ordinal));
        }
    }
}
