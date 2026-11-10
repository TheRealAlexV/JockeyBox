using System;

namespace JockeyBox.Selftest;

/// <summary>
/// The two things every headless harness command (<see cref="UiShot"/>,
/// <see cref="LibraryDump"/>, <see cref="PerfProbe"/>) must agree on: how long a
/// quiet window proves a settle, and how a flag and its value are spelled.
///
/// <para><b>Why one owner.</b> Both values used to be copied per command. The
/// quiet window was 500 ms in two files while the comment beside it described a
/// re-raise 200-700 ms out, so the top of the distribution landed after the
/// window had already declared quiet, and a capture settled in the gap. The flag
/// parsers disagreed on <c>--flag=value</c>, so a command line that worked for
/// one harness command was silently ignored by another.</para>
/// </summary>
internal static class Harness
{
    /// <summary>
    /// Worst documented delay before the "scan completed" invalidation reloads
    /// the playlist and raises <c>IsPlaylistLoading</c> again (200-700 ms).
    /// </summary>
    public const int ReRaiseWorstMs = 700;

    /// <summary>
    /// The quiet window a settle requires: the re-raise worst case plus margin.
    /// Derived, never a bare literal, so it cannot drift below the latency it
    /// exists to cover.
    /// </summary>
    public const int QuietMs = ReRaiseWorstMs + 200;

    /// <summary>Pump before the first quiet window, so queued startup work starts.</summary>
    public const int PrePumpMs = 50;

    /// <summary>
    /// Default <c>--uishot-wait</c> for a capture. A capture spends the pre-pump
    /// plus up to three quiet windows: the first proves nothing when the re-raise
    /// lands inside it, the second proves nothing if the reload re-raises once
    /// more, and the third is the one that holds. Each round is a full
    /// <see cref="QuietMs"/> wide and the windows pump in 25 ms steps, so the
    /// budget carries that slack. The previous 2500 ms could not cover a re-raise
    /// at the top of the distribution.
    /// </summary>
    public const int DefaultWaitMs = PrePumpMs + (3 * QuietMs) + 250;

    /// <summary>
    /// Default <c>--uishot-wait</c> for a dump. Generous on purpose: a dump reads
    /// data, not pixels, so waiting longer for a settle costs nothing next to
    /// printing an empty library.
    /// </summary>
    public const int DumpDefaultWaitMs = 60000;

    /// <summary>
    /// The one flag parser: <c>--flag value</c> and <c>--flag=value</c> both work.
    /// A bare <c>--flag</c>, or one followed by another flag, yields "" rather
    /// than swallowing the next flag as its value. Returns null when the flag is
    /// absent, so <c>Requested</c> stays a null test.
    /// </summary>
    public static string? Flag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(flag.Length + 1)..];

            if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 1]
                : "";
        }
        return null;
    }
}
