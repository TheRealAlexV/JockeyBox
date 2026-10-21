using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using JockeyBox.Settings;

namespace JockeyBox.Tools;

/// <summary>
/// Locates, installs and updates the external command line tools the download
/// pipeline depends on (yt-dlp and ffmpeg).
///
/// Resolution order for both tools is the same everywhere in the app:
/// an explicit path from <see cref="AppSettings"/> wins, then the copy this
/// class downloaded into %APPDATA%\JockeyBox\tools, then whatever is on PATH.
/// Downloads land in the tools folder as "&lt;name&gt;.part" and only replace the
/// live binary once they are complete, so a dropped connection never leaves a
/// truncated yt-dlp.exe behind.
/// </summary>
public static class ToolBootstrap
{
    private const string YtDlpDownloadUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    private const string FfmpegDownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private const int BufferSize = 80 * 1024;

    // One shared client: a fresh HttpClient per download exhausts sockets, and
    // ten minutes covers the ~90 MB ffmpeg zip on a slow line.
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Root of everything the app keeps outside the install folder: %APPDATA%\JockeyBox.</summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");

    /// <summary>Where the self-managed yt-dlp / ffmpeg live.</summary>
    public static string ToolsDir => Path.Combine(AppDataDir, "tools");

    /// <summary>The app-managed yt-dlp binary, whether or not it exists yet.</summary>
    public static string BundledYtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");

    /// <summary>The app-managed ffmpeg binary, whether or not it exists yet.</summary>
    public static string BundledFfmpegPath => Path.Combine(ToolsDir, "ffmpeg.exe");

    /// <summary>
    /// yt-dlp to use for this run: the user's explicit path, else the bundled
    /// copy, else the first one on PATH. Null means "not installed anywhere".
    /// </summary>
    public static string? ResolveYtDlp(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.YtDlpPath) && File.Exists(s.YtDlpPath))
            return s.YtDlpPath;
        if (File.Exists(BundledYtDlpPath))
            return BundledYtDlpPath;
        return FindOnPath("yt-dlp.exe", "yt-dlp");
    }

    /// <summary>ffmpeg to use for this run, resolved the same way as <see cref="ResolveYtDlp"/>.</summary>
    public static string? ResolveFfmpeg(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.FfmpegPath) && File.Exists(s.FfmpegPath))
            return s.FfmpegPath;
        if (File.Exists(BundledFfmpegPath))
            return BundledFfmpegPath;
        return FindOnPath("ffmpeg.exe", "ffmpeg");
    }

    /// <summary>True when an mp3 conversion (or any ffmpeg-backed mux) is possible.</summary>
    public static bool HasFfmpeg(AppSettings s) => ResolveFfmpeg(s) != null;

    /// <summary>
    /// Runs "&lt;exe&gt; --version" and returns the first non-empty line, or null
    /// when the tool is missing, hangs up or writes nothing useful.
    /// </summary>
    public static async Task<string?> GetVersionAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        try
        {
            var result = await RunCaptureAsync(exePath, new[] { "--version" }, ct);
            if (result.ExitCode != 0)
            {
                Log.Warn($"{Path.GetFileName(exePath)} --version exited with code {result.ExitCode}");
                return null;
            }

            foreach (var line in result.StdOut.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the version of {exePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Makes sure a usable yt-dlp exists, downloading the official single-file
    /// Windows build on first use. Returns false (and logs why) instead of
    /// throwing, because callers surface the failure in the download UI.
    /// </summary>
    public static async Task<bool> EnsureYtDlpAsync(AppSettings s, IProgress<string> log, CancellationToken ct)
    {
        var existing = ResolveYtDlp(s);
        if (existing != null)
        {
            log.Report($"yt-dlp: found {existing}");
            Log.Debug($"yt-dlp already available at {existing}");
            return true;
        }

        log.Report("yt-dlp: not installed - downloading ...");
        if (!await DownloadYtDlpAsync(log, ct))
            return false;

        var version = await GetVersionAsync(BundledYtDlpPath, ct);
        s.YtDlpVersion = version ?? "";
        s.Save();

        log.Report(version == null ? "yt-dlp: ready" : $"yt-dlp: ready ({version})");
        return true;
    }

    /// <summary>Makes sure a usable ffmpeg exists, downloading the essentials build on first use.</summary>
    public static async Task<bool> EnsureFfmpegAsync(AppSettings s, IProgress<string> log, CancellationToken ct)
    {
        var existing = ResolveFfmpeg(s);
        if (existing != null)
        {
            log.Report($"ffmpeg: found {existing}");
            Log.Debug($"ffmpeg already available at {existing}");
            return true;
        }

        log.Report("ffmpeg: not installed - downloading ...");
        return await DownloadFfmpegAsync(s, log, ct);
    }

    /// <summary>
    /// First-run setup: yt-dlp first because nothing downloads without it, then
    /// ffmpeg. A missing ffmpeg is only a warning - the pipeline falls back to
    /// the source audio format - so only a yt-dlp failure fails the whole call.
    /// </summary>
    public static async Task<bool> EnsureAllAsync(AppSettings s, IProgress<string> log, CancellationToken ct)
    {
        if (!await EnsureYtDlpAsync(s, log, ct))
            return false;

        if (!await EnsureFfmpegAsync(s, log, ct))
        {
            Log.Warn("ffmpeg is unavailable - downloads will keep the source audio format");
            log.Report("ffmpeg: unavailable - mp3 conversion disabled");
        }

        return true;
    }

    /// <summary>
    /// Updates yt-dlp. A copy this class downloaded updates itself in place with
    /// "-U"; a user-supplied or PATH copy is left untouched and a fresh bundled
    /// copy is downloaded alongside it instead.
    /// </summary>
    public static async Task<bool> UpdateYtDlpAsync(AppSettings s, IProgress<string> log, CancellationToken ct)
    {
        var exe = ResolveYtDlp(s);
        if (exe == null)
        {
            log.Report("yt-dlp: not installed - downloading ...");
            return await DownloadYtDlpAsync(log, ct);
        }

        if (IsInToolsDir(exe))
        {
            log.Report($"yt-dlp: updating {exe} ...");
            var result = await RunCaptureAsync(exe, new[] { "-U" }, ct);
            foreach (var line in TailLines(result.StdOut, 8))
                log.Report($"yt-dlp: {line}");

            if (result.ExitCode != 0)
            {
                Log.Warn($"yt-dlp -U exited with code {result.ExitCode}: {result.StdErr.Trim()}");
                log.Report("yt-dlp: update failed - see log");
                return false;
            }

            var version = await GetVersionAsync(exe, ct);
            s.YtDlpVersion = version ?? "";
            s.Save();
            log.Report(version == null ? "yt-dlp: up to date" : $"yt-dlp: up to date ({version})");
            return true;
        }

        log.Report($"yt-dlp: {exe} is managed outside JockeyBox - downloading a bundled copy instead");
        if (!await DownloadYtDlpAsync(log, ct))
            return false;

        var bundledVersion = await GetVersionAsync(BundledYtDlpPath, ct);
        s.YtDlpVersion = bundledVersion ?? "";
        s.Save();
        return true;
    }

    /// <summary>
    /// Updates ffmpeg by re-downloading the essentials build (ffmpeg has no
    /// reliable self-updater on Windows).
    /// </summary>
    public static async Task<bool> UpdateFfmpegAsync(AppSettings s, IProgress<string> log, CancellationToken ct)
    {
        log.Report("ffmpeg: updating (re-downloading essentials build) ...");
        return await DownloadFfmpegAsync(s, log, ct);
    }

    // ---- downloads ------------------------------------------------------

    private static async Task<bool> DownloadYtDlpAsync(IProgress<string> log, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            Log.Info($"Downloading yt-dlp from {YtDlpDownloadUrl}");
            if (!await DownloadFileAsync(YtDlpDownloadUrl, BundledYtDlpPath, "yt-dlp", log, ct))
                return false;

            Log.Info($"yt-dlp downloaded to {BundledYtDlpPath}");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("yt-dlp download failed", ex);
            log.Report($"yt-dlp: download failed ({ex.Message})");
            return false;
        }
    }

    private static async Task<bool> DownloadFfmpegAsync(AppSettings s, IProgress<string> log, CancellationToken ct)
    {
        var zipPath = Path.Combine(ToolsDir, "ffmpeg-release-essentials.zip");

        try
        {
            Directory.CreateDirectory(ToolsDir);
            Log.Info($"Downloading ffmpeg from {FfmpegDownloadUrl}");
            if (!await DownloadFileAsync(FfmpegDownloadUrl, zipPath, "ffmpeg", log, ct))
                return false;

            log.Report("ffmpeg: extracting ...");
            var extracted = false;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var ffmpegEntry = archive.Entries.FirstOrDefault(
                    e => IsBinEntry(e.FullName, "ffmpeg.exe"));
                if (ffmpegEntry == null)
                {
                    Log.Error($"ffmpeg archive did not contain bin/ffmpeg.exe ({zipPath})");
                    log.Report("ffmpeg: archive did not contain ffmpeg.exe");
                    return false;
                }

                await ExtractEntryAsync(ffmpegEntry, BundledFfmpegPath, ct);
                extracted = true;

                var ffprobeEntry = archive.Entries.FirstOrDefault(
                    e => IsBinEntry(e.FullName, "ffprobe.exe"));
                if (ffprobeEntry != null)
                    await ExtractEntryAsync(ffprobeEntry, Path.Combine(ToolsDir, "ffprobe.exe"), ct);
            }

            if (!extracted) return false;

            var version = await GetVersionAsync(BundledFfmpegPath, ct);
            s.FfmpegVersion = version ?? "";
            s.Save();

            Log.Info($"ffmpeg installed at {BundledFfmpegPath}");
            log.Report(version == null ? "ffmpeg: ready" : $"ffmpeg: ready ({version})");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("ffmpeg download failed", ex);
            log.Report($"ffmpeg: download failed ({ex.Message})");
            return false;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>
    /// Streams <paramref name="url"/> to "&lt;target&gt;.part", reporting progress,
    /// then swaps it into place. Returns false on any network/IO failure; only
    /// cancellation escapes.
    /// </summary>
    private static async Task<bool> DownloadFileAsync(
        string url, string target, string label, IProgress<string> log, CancellationToken ct)
    {
        var part = target + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var reporter = new ThrottledReporter(log, label);

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Error($"{label} download failed: HTTP {(int)response.StatusCode} from {url}");
                log.Report($"{label}: download failed (HTTP {(int)response.StatusCode})");
                return false;
            }

            var total = response.Content.Headers.ContentLength;
            log.Report(total is > 0
                ? $"{label}: downloading {Megabytes(total.Value):0.0} MB ..."
                : $"{label}: downloading ...");

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(
                part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    reporter.Report(received, total);
                }

                if (total is > 0 && received < total.Value)
                {
                    Log.Error($"{label} download truncated: {received} of {total.Value} bytes");
                    log.Report($"{label}: download incomplete - try again");
                    return false;
                }
            }

            File.Move(part, target, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            Log.Error($"{label} download failed", ex);
            log.Report($"{label}: download failed ({ex.Message})");
            return false;
        }
    }

    // ---- process helpers ------------------------------------------------

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        string exePath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {exePath}.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = PumpAsync(process.StandardOutput, stdout, ct);
        var stderrDone = PumpAsync(process.StandardError, stderr, ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await Task.WhenAll(stdoutDone, stderrDone);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                sink.AppendLine(line);
            }
        }
        catch (OperationCanceledException) { /* the caller is tearing the process down */ }
        catch (Exception ex)
        {
            Log.Debug($"Reading tool output failed: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not kill tool process: {ex.Message}");
        }
    }

    // ---- small helpers --------------------------------------------------

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JockeyBox/1.4");
        return client;
    }

    private static string? FindOnPath(params string[] fileNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in fileNames)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }

        return null;
    }

    private static bool IsInToolsDir(string exePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(exePath));
        return string.Equals(
            directory?.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(ToolsDir).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBinEntry(string entryName, string fileName)
    {
        var normalised = entryName.Replace('\\', '/');
        return normalised.EndsWith("/bin/" + fileName, StringComparison.OrdinalIgnoreCase)
            || normalised.Equals("bin/" + fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string destination, CancellationToken ct)
    {
        var part = destination + ".part";
        try
        {
            await using (var source = entry.Open())
            await using (var target = new FileStream(
                part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await source.CopyToAsync(target, ct);
            }

            File.Move(part, destination, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            throw;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static IEnumerable<string> TailLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Skip(Math.Max(0, lines.Count - count));
    }

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not delete {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns a byte stream into at most two status lines a second, so a 90 MB
    /// download does not flood the log or the settings-page status text.
    /// </summary>
    private sealed class ThrottledReporter
    {
        private readonly IProgress<string> _log;
        private readonly string _label;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReportMs = -1000;

        public ThrottledReporter(IProgress<string> log, string label)
        {
            _log = log;
            _label = label;
        }

        public void Report(long received, long? total)
        {
            var now = _clock.ElapsedMilliseconds;
            if (now - _lastReportMs < 500) return;
            _lastReportMs = now;

            _log.Report(total is > 0
                ? $"{_label}: {Megabytes(received):0.0} MB / {Megabytes(total.Value):0.0} MB"
                : $"{_label}: {Megabytes(received):0.0} MB");
        }
    }
}
