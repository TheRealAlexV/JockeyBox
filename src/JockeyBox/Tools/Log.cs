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

    public static string DirectoryPath => Path.Combine(AppPaths.Home, "logs");

    public static string FilePath => Path.Combine(DirectoryPath, "jockeybox.log");

    /// <summary>Raised for every line so the UI can echo the latest activity.</summary>
    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        // Diagnostic only (JOCKEYBOX_STALL_WATCH): time the gate wait and the append
        // separately so the watchdog can tell a log-lock stall from a driver stall.
        bool watch = StallWatch.Enabled;
        long waitTs = watch ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            lock (Gate)
            {
                if (watch) StallWatch.RecordLogGate(waitTs);
                Directory.CreateDirectory(DirectoryPath);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var previous = FilePath + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(FilePath, previous);
                }
                long appendTs = watch ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                if (watch) StallWatch.RecordLogAppend(appendTs);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }

        try { LineWritten?.Invoke(line); } catch { /* listeners are best-effort */ }
    }
}
