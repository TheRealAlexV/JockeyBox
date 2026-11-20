using System;
using System.IO;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A launch that dies in its first second must leave a line behind. Two JockeyBox
/// instances vanished with no log line, no Application event and no WER report,
/// so the log can no longer be something that starts working only once the window
/// is up: <see cref="Log.Open"/> is called from the App constructor, before any
/// startup work, and every line is on disk when the call returns.
///
/// <para>These tests hold the two properties the startup handlers rely on - the
/// file exists and is readable before the app has done anything else, and a fatal
/// marker reaches disk without an explicit flush - because a handler that writes
/// into a buffer the dying process never flushes is the same silence with extra
/// steps.</para>
/// </summary>
public class StartupEvidenceTests
{
    [Fact]
    public void Open_MakesTheLogReadableBeforeAnythingElseRuns()
    {
        Log.Open();

        // Read through a second handle while the writer is still open: that is
        // what a diagnostic does after the process is gone, minus the process.
        string text = string.Join("\n", Log.ReadLines());

        Assert.Contains("starting", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Environment.ProcessId.ToString(), text);
    }

    [Fact]
    public void Fatal_NamesTheExceptionTypeAndMessageOnDiskWithoutAnExplicitFlush()
    {
        Log.Open();
        var boom = new InvalidOperationException("deliberate startup failure at stage 'late'");

        Log.Fatal("late", boom);

        // Deliberately no Log.Flush(): AutoFlush is the property under test.
        string text = string.Join("\n", Log.ReadLines());

        Assert.Contains("[FATAL]", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("deliberate startup failure at stage 'late'", text);
        Assert.Contains("late", text);
    }
}
