using System;
using System.IO;
using JockeyBox.Tools;

namespace JockeyBox.Selftest;

/// <summary>
/// Refuses a headless capture whose resolved home is the operator's real roaming
/// profile.
///
/// <para><b>Why in the program.</b> A run used <c>cmd.exe /c "set JOCKEYBOX_HOME=..."</c>
/// and the variable never reached the child, so <see cref="AppPaths.Home"/> quietly
/// resolved <c>%APPDATA%\JockeyBox</c> and the capture rewrote <c>library.db</c>,
/// <c>library.json</c>, <c>playlist-cache.json</c> and <c>settings.json</c> there.
/// The old guard was a note in a brief, and a note cannot stop a process. This can:
/// every capture mode checks the resolved home before any store is opened, prints a
/// refusal naming that path and the flag, and exits non-zero. The normal windowed
/// app is untouched - the live profile is where it belongs.</para>
/// </summary>
internal static class CaptureHomeGuard
{
    /// <summary>Explicit opt-in for the rare deliberate run against the live home.</summary>
    public const string AllowFlag = "--allow-live-home";

    /// <summary>
    /// Exit code for a refused capture. Distinct from 1 (single-instance refusal),
    /// 2 (usage), 3 (clamped) and 4 (unsettled), so a caller can tell "I refused
    /// your home" from "the capture ran and was not evidence".
    /// </summary>
    public const int RefusalExitCode = 5;

    /// <summary>
    /// The headless modes that open a library or write settings. <c>--uishot-dump</c>
    /// is listed before <c>--uishot</c> only for message accuracy.
    /// </summary>
    private static readonly string[] CaptureFlags =
    [
        "--uishot", "--uishot-dump", "--perf", "--preview-check",
        "--selftest", "--lyrics-probe", "--linger-check",
    ];

    /// <summary>
    /// The home a real launch resolves with no override: <c>%APPDATA%\JockeyBox</c>.
    /// </summary>
    public static string LiveHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");

    /// <summary>Null when the launch may proceed; otherwise the refusal text.</summary>
    public static string? Refusal(string[] args) =>
        Refusal(args, AppPaths.Home, LiveHome);

    /// <summary>The real entry point with the homes injected, so the flag parse is testable.</summary>
    internal static string? Refusal(string[] args, string resolvedHome, string liveHome) =>
        Refusal(resolvedHome, liveHome, Harness.Flag(args, AllowFlag) != null, args);

    /// <summary>
    /// Pure decision, so the refusal, the sandbox case and the opt-in are testable
    /// without touching a real profile or the environment.
    /// </summary>
    internal static string? Refusal(string resolvedHome, string liveHome, bool allowed, string[] args)
    {
        if (allowed) return null;
        // Only a capture is restricted. The windowed app runs here on purpose.
        if (FirstCaptureFlag(args) is not { } flag) return null;
        if (!SamePath(resolvedHome, liveHome)) return null;

        return $"refusing to run {flag}: the resolved home is {resolvedHome}, the operator's live " +
               $"roaming profile. A capture must not rewrite live settings, playlists or library.db. " +
               $"Point {AppPaths.HomeVariable} at a sandbox directory, or pass {AllowFlag} if a run " +
               "against the live home is deliberate.";
    }

    private static string? FirstCaptureFlag(string[] args)
    {
        foreach (string flag in CaptureFlags)
            if (Harness.Flag(args, flag) != null) return flag;
        return null;
    }

    /// <summary>
    /// Same directory regardless of a trailing separator or the casing Windows
    /// preserves but does not compare.
    /// </summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}
