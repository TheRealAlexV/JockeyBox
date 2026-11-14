using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>True when <paramref name="args"/> name one of those modes.</summary>
    internal static bool CaptureRequested(string[] args)
    {
        foreach (var flag in CaptureFlags)
            if (Harness.Flag(args, flag) != null) return true;
        return false;
    }

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
    internal static string? Refusal(string resolvedHome, string liveHome, bool allowed, string[] args) =>
        Refusal(resolvedHome, liveHome, allowed, args, SandboxRoots(liveHome));

    /// <summary>The decision with the sandbox roots injected, so both refusals are testable.</summary>
    internal static string? Refusal(
        string resolvedHome, string liveHome, bool allowed, string[] args, IEnumerable<string> sandboxRoots)
    {
        if (allowed) return null;
        // Only a capture is restricted. The windowed app runs here on purpose.
        if (FirstCaptureFlag(args) is not { } flag) return null;

        // Compare canonical paths, never the raw strings. A launcher that used
        // "set JOCKEYBOX_HOME=C:\Dev\home && cmd" handed cmd "C:\Dev\home " - the
        // trailing space resolves to a real directory on Windows but never compares
        // equal to it, so the raw check let a capture through to the live profile.
        string home = Canonical(resolvedHome);
        string live = Canonical(liveHome);

        string? why = SamePath(home, live)
            ? "the operator's live roaming profile"
            : Directory.Exists(live) && !sandboxRoots.Any(root => Under(home, Canonical(root)))
                ? "not under a sandbox root while a live profile exists"
                : null;
        if (why == null) return null;

        string configured = string.Equals(resolvedHome, home, StringComparison.Ordinal)
            ? string.Empty
            : $" (from the configured value '{resolvedHome}')";

        return $"refusing to run {flag}: the resolved home is {home}{configured}, {why}. " +
               $"A capture must not rewrite live settings, playlists or library.db. " +
               $"Point {AppPaths.HomeVariable} at a sandbox directory under one of the sandbox " +
               $"roots, or pass {AllowFlag} if a run against the live home is deliberate.";
    }

    private static string? FirstCaptureFlag(string[] args)
    {
        foreach (string flag in CaptureFlags)
            if (Harness.Flag(args, flag) != null) return flag;
        return null;
    }

    /// <summary>
    /// One spelling for a directory path: surrounding whitespace gone, trailing
    /// separators gone (both kinds, on every host), resolved to an absolute path.
    /// </summary>
    private static string Canonical(string path)
    {
        string trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return path.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            // Unresolvable is not a pass: the string still has to clear the refusals.
            return trimmed;
        }
    }

    /// <summary>Same directory regardless of a trailing separator or casing.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or sits inside it.</summary>
    private static bool Under(string path, string root)
    {
        if (SamePath(path, root)) return true;
        string prefix = root.EndsWith('\\') || root.EndsWith('/') ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where a capture home may live. <c>JOCKEYBOX_SANDBOX_ROOTS</c> (semicolon or
    /// comma separated) wins; otherwise the temp directory and the live drive's
    /// <c>\Dev</c> tree.
    /// </summary>
    public const string SandboxRootsVariable = "JOCKEYBOX_SANDBOX_ROOTS";

    private static IEnumerable<string> SandboxRoots(string liveHome)
    {
        string? declared = Environment.GetEnvironmentVariable(SandboxRootsVariable);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            foreach (string root in declared.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(root)) yield return root.Trim();
            yield break;
        }

        yield return Path.GetTempPath();
        if (Path.GetPathRoot(Canonical(liveHome)) is { Length: > 0 } drive)
            yield return Path.Combine(drive, "Dev");
    }
}
