using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JockeyBox.Tools;

/// <summary>
/// Minimal rotating file log for the download/import machinery, which fails in
/// ways the UI alone cannot explain (yt-dlp stderr, ffmpeg errors, HTTP bodies).
/// Lives in %APPDATA%\JockeyBox\logs\jockeybox.log and rotates at 2 MB.
///
/// Deliberately not a logging framework: the app ships as a single exe and this
/// needs to stay dependency-free.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The held handle, opened by <see cref="Open"/>. Null until the App
    /// constructor calls it, and null again if opening failed (the per-line
    /// append path below still works, one open per line).
    /// </summary>
    private static StreamWriter? _writer;

    public static string DirectoryPath => Path.Combine(AppPaths.Home, "logs");

    public static string FilePath => Path.Combine(DirectoryPath, "jockeybox.log");

    /// <summary>Raised for every line so the UI can echo the latest activity.</summary>
    public static event Action<string>? LineWritten;

    /// <summary>
    /// Open the log for the life of the process, with <c>AutoFlush</c>, and say so
    /// in the file.
    ///
    /// <para><b>Why before anything else.</b> Two instances exited during their
    /// first second leaving no line, no Application event and no WER report: the
    /// log only started working once a component happened to write to it, so a
    /// crash earlier than that had nowhere to report. Called from the App
    /// constructor, this puts the file - and a line that dates the launch - on
    /// disk before Velopack, home validation, stores or the window exist, so the
    /// next thing that dies is dated too.</para>
    ///
    /// <para>The stream is opened with a 1-byte buffer and <c>AutoFlush</c>, so
    /// each line reaches the kernel when it is written; a process killed after
    /// that point still leaves the line. Safe to call twice.</para>
    /// </summary>
    public static void Open()
    {
        lock (Gate)
        {
            if (_writer != null) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                Rotate();
                var stream = new FileStream(
                    FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _writer.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO ] === jockeybox starting pid {Environment.ProcessId} ===");
            }
            catch
            {
                // An unusable log directory must not stop the launch, and must not
                // stop the crash handlers either: they fall back to the append path.
                _writer = null;
            }
        }
    }

    /// <summary>
    /// Push anything buffered to the OS. The crash handlers call it before dying.
    /// </summary>
    public static void Flush()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { /* a dying process has no better option */ }
        }
    }

    /// <summary>
    /// Read the log from inside this process. <see cref="Open"/> holds the file for
    /// the life of the process, so <c>File.ReadAllText</c>/<c>ReadLines</c> (share
    /// mode <c>Read</c>) fail against it with a sharing violation: readers go
    /// through here instead of each one remembering the share mode.
    /// </summary>
    public static string[] ReadLines()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<string>();
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);
                return lines.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// The line a dying process leaves: which stage it died in, the exception type
    /// and its message. The App's AppDomain and Dispatcher handlers are the only
    /// callers, so the wording is fixed and greppable (<c>[FATAL]</c>).
    /// </summary>
    public static void Fatal(string stage, Exception exception)
    {
        Write("FATAL", $"unhandled exception during {stage}: {exception.GetType().FullName}: {exception.Message}");

        // The type-and-message line above is not enough on its own: the two
        // failures that cost the most were a DllNotFoundException whose Message
        // was only "Dll was not found." and a FileNotFoundException whose Message
        // was empty with the assembly name in FileName. The stack, the target
        // site and FileName are the only identifying data there, so write them.
        Write("FATAL", $"detail: pid={Environment.ProcessId} " +
                       $"tid={Environment.CurrentManagedThreadId} target='{TargetOf(exception)}'" +
                       (exception is FileNotFoundException fnf ? $" file='{fnf.FileName}'" : ""));
        foreach (string line in exception.ToString().Replace("\r\n", "\n").Split('\n'))
            Write("FATAL", "  " + line);
        Flush();
    }

    /// <summary>Where the exception was raised: the API, which names the need.</summary>
    private static string TargetOf(Exception exception)
    {
        try { return $"{exception.TargetSite?.DeclaringType?.FullName}.{exception.TargetSite?.Name}"; }
        catch { return "<unavailable>"; }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Debug(string message) => Write("DEBUG", message);

    /// <summary>
    /// A line that must not queue behind a gate some other thread may hold: tries
    /// the gate for <paramref name="timeoutMs"/> and gives up. Only the UI stall
    /// watchdog uses it - if the UI thread wedged while holding the gate, waiting
    /// here would join the stall instead of ending it. Deliberately silent:
    /// <see cref="LineWritten"/> listeners live on the UI thread.
    /// </summary>
    public static bool TryInfo(string message, int timeoutMs = 200)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO ] {message}";
        var taken = false;
        try
        {
            Monitor.TryEnter(Gate, timeoutMs, ref taken);
            if (!taken) return false;

            Directory.CreateDirectory(DirectoryPath);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var previous = FilePath + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(FilePath, previous);
            }
            File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (taken) Monitor.Exit(Gate);
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    // AutoFlush: the line is on its way to the OS when this returns.
                    _writer.WriteLine(line);
                }
                else
                {
                    // No held handle (the constructor never ran, or opening failed):
                    // one open per line, which also flushes.
                    Directory.CreateDirectory(DirectoryPath);
                    Rotate();
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Logging must never take the app down.
        }

        try { LineWritten?.Invoke(line); } catch { /* listeners are best-effort */ }
    }

    /// <summary>
    /// Rotate at 2 MB. Only called while the gate is held and no writer is open -
    /// a held handle keeps the old file alive, so rotation happens at open time.
    /// </summary>
    private static void Rotate()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes) return;

        var previous = FilePath + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(FilePath, previous);
    }
}
