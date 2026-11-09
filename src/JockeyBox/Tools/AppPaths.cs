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
    /// Roaming home: <c>%APPDATA%\JockeyBox</c>, or <c>JOCKEYBOX_HOME</c> when set.
    /// </summary>
    public static string Home
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(HomeVariable);
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");
        }
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
