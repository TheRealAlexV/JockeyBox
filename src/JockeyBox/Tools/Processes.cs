using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JockeyBox.Tools;

/// <summary>Result of one external tool run.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>True when the tool reported success.</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>Last few lines of combined output, for a log line or a status message.</summary>
    public string Tail(int lines = 4)
    {
        string combined = (StdErr + "\n" + StdOut).Trim();
        var parts = combined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int start = Math.Max(0, parts.Length - lines);
        return string.Join(" | ", parts[start..]).Trim();
    }
}

/// <summary>
/// Small wrapper for the external tools the app shells out to (yt-dlp, ffmpeg,
/// the Python separators). Captures both streams, enforces a timeout and never
/// throws on a non-zero exit — callers decide what a failure means.
/// </summary>
public static class Processes
{
    /// <summary>Run a tool and capture its output.</summary>
    public static async Task<ProcessResult> RunAsync(
        string exe,
        string[] args,
        int timeoutSeconds = 600,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return new ProcessResult(-1, string.Empty, "no executable");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                return new ProcessResult(-1, string.Empty, "process did not start");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return new ProcessResult(-1, stdout.ToString(), stderr + "\n[timed out]");
        }

        string outText, errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();
        return new ProcessResult(process.ExitCode, outText, errText);
    }
}
