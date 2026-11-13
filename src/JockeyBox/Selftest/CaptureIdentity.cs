using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JockeyBox.Selftest;

/// <summary>
/// Which binary produced a capture, printed as the first lines of every dump.
///
/// <para><b>Why.</b> A dump that does not name its binary is unfalsifiable: a
/// sandbox copy of the app that predates the change under test produces numbers
/// that look like a bug in the change. That happened - evidence for a lyrics
/// diagnostic was read off an exe whose md5 did not match <c>dist/portable</c>,
/// so an entire round of conclusions belonged to a build that never contained
/// the change. Every capture therefore opens with the running exe, its md5 and
/// byte size, its mtime, the git HEAD of the tree it sits in, and a loud line
/// when its md5 differs from the tree's own <c>dist/portable/JockeyBox.exe</c>.</para>
///
/// <para>Git that cannot be resolved prints <c>unknown</c>: a guessed revision is
/// worse than an absent one, because it silences exactly this check.</para>
/// </summary>
internal static class CaptureIdentity
{
    /// <summary>Exit code for a capture whose own binary could not be identified.</summary>
    public const int UnidentifiedExitCode = 5;

    /// <summary>
    /// The header lines, or null when the running exe cannot be identified at all
    /// (no process path, unreadable file, unreadable bytes). Callers refuse to
    /// write a capture in that case.
    /// </summary>
    public static string[]? Header()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;

        string md5, stamp;
        long size;
        try
        {
            var info = new FileInfo(exe);
            size = info.Length;
            stamp = info.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            using var stream = File.OpenRead(exe);
            md5 = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lines = new System.Collections.Generic.List<string>
        {
            $"identity: exe {exe}",
            $"identity: md5 {md5} ({size.ToString(CultureInfo.InvariantCulture)} B) mtime {stamp}",
        };

        // The exe's own tree first. A sandbox copy has no .git above it, so fall
        // back to JOCKEYBOX_REPO (a capture harness that stages the exe elsewhere
        // names the tree it built from) and then to the working directory's tree,
        // saying the exe sits outside it - the dist line is what ties them.
        string? tree = FindTree(Path.GetDirectoryName(exe));
        bool exeInsideTree = tree is not null;
        tree = FindTree(Environment.GetEnvironmentVariable("JOCKEYBOX_REPO")) ?? tree;
        tree ??= FindTree(Directory.GetCurrentDirectory());

        if (tree is null)
        {
            lines.Add("identity: git unknown (no .git above the exe or the working directory)");
            lines.Add("identity: dist unknown (no tree to resolve dist/portable/JockeyBox.exe)");
            return lines.ToArray();
        }

        lines.Add($"identity: git {Git(tree, "rev-parse", "HEAD")} on {Git(tree, "rev-parse", "--abbrev-ref", "HEAD")}" +
                  $" (tree {tree}{(exeInsideTree ? "" : ", exe outside it")})");

        string deployed = Path.Combine(tree, "dist", "portable", "JockeyBox.exe");
        if (!File.Exists(deployed))
        {
            lines.Add($"identity: dist absent {deployed}");
            return lines.ToArray();
        }

        string deployedMd5;
        try
        {
            using var stream = File.OpenRead(deployed);
            deployedMd5 = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines.Add($"identity: dist unreadable {deployed}: {ex.Message}");
            return lines.ToArray();
        }

        lines.Add(string.Equals(md5, deployedMd5, StringComparison.OrdinalIgnoreCase)
            ? $"identity: dist {deployed} md5 {deployedMd5} matches"
            : $"identity: MISMATCH running md5 {md5} != dist md5 {deployedMd5} ({deployed})");
        return lines.ToArray();
    }

    /// <summary>Print the header to stdout; false when the exe is unidentified.</summary>
    public static bool Require(string[] args, string who)
    {
        string[]? header = Header();
        if (header is null)
        {
            Console.Error.WriteLine($"{who}: UNIDENTIFIED - cannot read the running exe, so the capture would be unattributable");
            Console.Error.Flush();
            return false;
        }

        foreach (string line in header) Console.Out.WriteLine(line);
        Console.Out.Flush();
        return true;
    }

    /// <summary>Nearest directory at or above <paramref name="start"/> holding a <c>.git</c>.</summary>
    private static string? FindTree(string? start)
    {
        for (var dir = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
             dir is not null;
             dir = dir.Parent)
        {
            // A linked worktree has a .git file, not a directory; both mark a root.
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>One git field, or <c>unknown</c> when git is absent, slow or fails.</summary>
    private static string Git(string tree, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tree,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(tree);
            foreach (string argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return "unknown";
            }
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "unknown";
        }
    }
}
