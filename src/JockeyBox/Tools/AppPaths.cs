using System;
using System.IO;

namespace JockeyBox.Tools;

/// <summary>
/// One place that answers "where does this app keep its per-user state".
///
/// <para><b>Why it exists.</b> Verification has to run without touching the
/// operator's profile: the console session may be in use, and a test that reads
/// or writes the real <c>%APPDATA%\JockeyBox</c> can clobber live settings, logs,
/// playlists and the library database. Setting <c>JOCKEYBOX_HOME</c> redirects
/// every store (settings, logs, caches, database, queues, tokens) to a sandbox
/// directory, so the same binary can be exercised end to end in isolation.</para>
/// </summary>
public static class AppPaths
{
    public const string HomeVariable = "JOCKEYBOX_HOME";
    public const string LocalHomeVariable = "JOCKEYBOX_LOCAL_HOME";

    /// <summary>
    /// Exit code for a launch refused because <c>JOCKEYBOX_HOME</c> is set but
    /// unusable. Distinct from 5 (capture refused the live home) and from 2 (usage).
    /// </summary>
    public const int BadHomeExitCode = 6;

    /// <summary>The home a launch with no override resolves: <c>%APPDATA%\JockeyBox</c>.</summary>
    public static string LiveHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");

    /// <summary>
    /// Roaming home: <c>%APPDATA%\JockeyBox</c>, or <c>JOCKEYBOX_HOME</c> when set.
    ///
    /// <para><b>A set override never falls back.</b> A value that is set but malformed
    /// (trailing space, unrooted relative path, a drive that does not exist) is
    /// returned exactly as configured rather than quietly becoming the live profile.
    /// That silent fallback is how a launcher that captured a trailing space in the
    /// value reached the operator's real home; <see cref="HomeProblem"/> is what
    /// turns it into a refusal at startup.</para>
    /// </summary>
    public static string Home
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(HomeVariable);
            return string.IsNullOrWhiteSpace(overridden) ? LiveHome : overridden;
        }
    }

    /// <summary>
    /// Null when <c>JOCKEYBOX_HOME</c> is unset (or empty, which means unset) or
    /// names a usable directory; otherwise the reason it cannot be used, naming the
    /// configured value and the canonical form it produced.
    /// </summary>
    public static string? HomeProblem =>
        HomeProblemFor(Environment.GetEnvironmentVariable(HomeVariable));

    /// <summary>
    /// The pure decision, with the existence probe injected so a test can describe
    /// an absent drive without needing one.
    /// </summary>
    internal static string? HomeProblemFor(string? configured, Func<string, bool>? rootExists = null)
    {
        // Empty means "unset": a launcher that sets an empty variable intends the
        // normal home, and that is a portable, unambiguous thing to do.
        if (string.IsNullOrWhiteSpace(configured)) return null;
        rootExists ??= Directory.Exists;

        string Problem(string why, string canonical) =>
            $"{HomeVariable} is set to '{configured}' but {why}. Canonical form: '{canonical}'. " +
            $"JockeyBox refuses to start: a home it cannot honour must never silently become " +
            $"{LiveHome}. Fix the value or unset {HomeVariable} (an empty value means unset) and start again.";

        if (configured.Trim() != configured)
            return Problem("carries surrounding whitespace - a launcher quoted it wrongly",
                SafeFullPath(configured.Trim()));

        if (!Path.IsPathRooted(configured))
            return Problem("is not an absolute path - a relative home would resolve against " +
                           "whatever directory the app happened to start in", SafeFullPath(configured));

        string canonical;
        try
        {
            canonical = Path.GetFullPath(configured);
        }
        catch (Exception ex)
        {
            return Problem($"cannot be canonicalised ({ex.GetType().Name}: {ex.Message})", configured);
        }

        string root = Path.GetPathRoot(canonical) ?? "";
        if (root.Length > 0 && !rootExists(root))
            return Problem($"names a root that does not exist ('{root}')", canonical);

        if (File.Exists(canonical))
            return Problem("names a file, not a directory", canonical);

        return null;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>
    /// Local home: <c>%LOCALAPPDATA%\JockeyBox</c>, or <c>JOCKEYBOX_LOCAL_HOME</c>
    /// when set. Falls back to <see cref="Home"/>'s override sibling so a sandbox
    /// never needs two variables set by hand.
    /// </summary>
    public static string LocalHome
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(LocalHomeVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            string? roaming = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrWhiteSpace(roaming)) return Path.Combine(roaming, "local");

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JockeyBox");
        }
    }

    /// <summary>True when this process was pointed at a sandbox home.</summary>
    public static bool IsSandboxed =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeVariable));

    /// <summary>Create the directory if it is missing and return it.</summary>
    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
