using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.YouTube;

/// <summary>
/// One video as yt-dlp describes it. Search results, playlist entries and a
/// single-video lookup all normalise into this shape; <see cref="YouTubeApi"/>
/// turns it into a <see cref="Sources.SourceTrack"/> for the rest of the app.
/// </summary>
public record YtEntry(
    string VideoId,
    string Title,
    string Uploader,
    double DurationSeconds,
    string? ThumbnailUrl,
    long ViewCount,
    bool IsLive)
{
    /// <summary>Duration as a <see cref="TimeSpan"/>, for UI formatting.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    /// <summary>
    /// Read out by UI Automation (and written to logs) instead of the record's
    /// generated "YtEntry { VideoId = … }" dump.
    /// </summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Uploader) ? Title : $"{Title} \u00b7 {Uploader}";
}

/// <summary>
/// One progress line from yt-dlp, already split into the fields the download UI
/// shows. <see cref="Percent"/> is 0-100.
/// </summary>
public record YtProgress(double Percent, string TotalSize, string Speed, string Eta);

/// <summary>
/// yt-dlp exited non-zero, or could not be started at all. Carries the tail of
/// stderr because yt-dlp explains most failures (age gate, private video, no
/// formats) only there.
/// </summary>
public class YtDlpException : Exception
{
    public YtDlpException(string message, int exitCode, string stderr) : base(message)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    /// <summary>Process exit code, or -1 when the tool was missing or could not start.</summary>
    public int ExitCode { get; }

    /// <summary>Last lines yt-dlp wrote to stderr (empty when it never ran).</summary>
    public string Stderr { get; }
}

/// <summary>
/// Thin wrapper around the yt-dlp executable: metadata lookups via "-J" and
/// best-audio downloads with an mp3 post-step.
///
/// This class never installs the tool - callers run
/// <see cref="ToolBootstrap.EnsureYtDlpAsync"/> first and surface that failure in
/// the UI. If yt-dlp is still missing every method throws
/// <see cref="YtDlpException"/> with exit code -1.
/// </summary>
public static class YtDlp
{
    private const int BufferSize = 4096;
    private const int StderrTailLines = 20;

    private static readonly Regex BareVideoIdRegex =
        new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    /// <summary>
    /// Searches YouTube. The query is turned into "ytsearchN:query" unless it
    /// already looks like a yt-dlp search expression.
    /// </summary>
    public static async Task<List<YtEntry>> SearchAsync(
        string query, int maxResults, AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<YtEntry>();

        maxResults = Math.Clamp(maxResults, 1, 200);
        var exe = RequireExe(settings);

        var expression = query.Trim();
        if (!expression.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase))
            expression = $"ytsearch{maxResults}:{expression}";

        var arguments = new List<string> { "-J", "--flat-playlist", "--no-warnings", expression };
        AppendCookieArgs(arguments, settings);

        Log.Debug($"yt-dlp search: {expression}");
        var json = await RunJsonAsync(exe, arguments, ct);
        var entries = ParseEntries(json);
        Log.Debug($"yt-dlp search returned {entries.Count} entries for \"{query}\"");
        return entries;
    }

    /// <summary>
    /// Reads a playlist's entries. Accepts a full URL, "list=&lt;id&gt;" or a bare
    /// playlist id (PL…, UU…, OL…, RD…); all forms are normalised to a playlist URL.
    /// </summary>
    public static async Task<List<YtEntry>> GetPlaylistAsync(
        string urlOrId, AppSettings settings, CancellationToken ct, int maxEntries = 0)
    {
        var exe = RequireExe(settings);
        var url = NormalisePlaylistUrl(urlOrId);

        var arguments = new List<string> { "-J", "--flat-playlist", "--no-warnings" };
        // Channel pages can hold thousands of uploads; cap what we pull in.
        if (maxEntries > 0) arguments.AddRange(new[] { "--playlist-end", maxEntries.ToString() });
        arguments.Add(url);
        AppendCookieArgs(arguments, settings);

        Log.Debug($"yt-dlp playlist: {url}");
        var json = await RunJsonAsync(exe, arguments, ct);
        var entries = ParseEntries(json);
        Log.Info($"yt-dlp playlist {url}: {entries.Count} entries");
        return entries;
    }

    /// <summary>
    /// Looks up a single video. Accepts watch?v=, youtu.be/, /shorts/, /embed/ or
    /// a bare 11-character id. Returns null when yt-dlp produces no usable entry.
    /// </summary>
    public static async Task<YtEntry?> GetVideoAsync(
        string urlOrId, AppSettings settings, CancellationToken ct)
    {
        var exe = RequireExe(settings);
        var url = NormaliseVideoUrl(urlOrId);

        var arguments = new List<string> { "-J", "--no-playlist", "--no-warnings", url };
        AppendCookieArgs(arguments, settings);

        Log.Debug($"yt-dlp video: {url}");
        var json = await RunJsonAsync(exe, arguments, ct);
        return ParseEntries(json).FirstOrDefault();
    }

    /// <summary>
    /// Downloads the best audio stream for <paramref name="videoId"/> next to
    /// <paramref name="targetPath"/> and returns the file that was actually
    /// written (yt-dlp picks the container, and an mp3 conversion renames it).
    ///
    /// When mp3 is requested but ffmpeg is unavailable the download silently
    /// falls back to m4a so a missing tool never blocks a download.
    /// </summary>
    public static async Task<string> DownloadBestAudioAsync(
        string videoId,
        string targetPath,
        bool mp3,
        int bitrateKbps,
        AppSettings settings,
        IProgress<YtProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            throw new ArgumentException("A YouTube video id is required.", nameof(videoId));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("A target path is required.", nameof(targetPath));

        var exe = RequireExe(settings);

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(directory);

        // The caller hands in a path whose media extension has already been
        // stripped. GetFileNameWithoutExtension would split at the last dot of a
        // TITLE instead ("Mr. Know It All" -> "Mr"), so only strip a real one.
        var fileName = Path.GetFileName(targetPath);
        var extension = Path.GetExtension(fileName);
        var hasMediaExtension =
            extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
        var stem = hasMediaExtension ? fileName[..^extension.Length] : fileName;
        var basePath = Path.Combine(directory, stem);

        var convertToMp3 = mp3;
        var ffmpeg = ToolBootstrap.ResolveFfmpeg(settings);
        if (convertToMp3 && ffmpeg == null)
        {
            Log.Warn($"mp3 was requested for {videoId} but ffmpeg is missing - keeping the source audio format");
            convertToMp3 = false;
        }

        var arguments = new List<string>
        {
            $"https://www.youtube.com/watch?v={videoId}",
            "--no-playlist",
            "--newline",
            "--no-warnings",
            "--concurrent-fragments", "4",
            "-f", "bestaudio[ext=m4a]/bestaudio/best",
            "-o", basePath + ".%(ext)s",
            "--progress-template",
            "download:%(progress._percent_str)s|%(progress._total_bytes_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
        };

        AppendCookieArgs(arguments, settings);

        if (convertToMp3)
        {
            arguments.Add("-x");
            arguments.Add("--audio-format");
            arguments.Add("mp3");
            arguments.Add("--audio-quality");
            arguments.Add($"{(bitrateKbps > 0 ? bitrateKbps : 320)}K");
            arguments.Add("--ffmpeg-location");
            arguments.Add(Path.GetDirectoryName(ffmpeg!)!);
        }

        var stderrTail = new Queue<string>(StderrTailLines);
        var combinedTail = new Queue<string>(StderrTailLines);

        void HandleLine(string line, bool isStdErr)
        {
            var update = ParseProgress(line);
            if (update != null)
            {
                progress?.Report(update);
                return;
            }

            // yt-dlp's own "[download]  12% of ..." lines are noise once our
            // progress template is in play.
            if (line.StartsWith("[download]", StringComparison.Ordinal))
                return;

            if (isStdErr) Enqueue(stderrTail, line);
            Enqueue(combinedTail, line);
            Log.Debug($"yt-dlp: {line}");
        }

        Log.Info($"Downloading YouTube audio {videoId} -> {basePath}" + (convertToMp3 ? " (mp3)" : ""));

        int exitCode;
        try
        {
            exitCode = await RunProcessAsync(
                exe,
                arguments,
                line => HandleLine(line, isStdErr: false),
                line => HandleLine(line, isStdErr: true),
                ct);
        }
        catch (OperationCanceledException)
        {
            CleanupPartials(basePath);
            throw;
        }

        if (exitCode != 0)
        {
            CleanupPartials(basePath);
            var tail = stderrTail.Count > 0 ? string.Join(Environment.NewLine, stderrTail) : string.Join(Environment.NewLine, combinedTail);
            Log.Error($"yt-dlp failed for {videoId} with exit code {exitCode}: {tail}");
            throw new YtDlpException(
                $"yt-dlp exited with code {exitCode} while downloading {videoId}.", exitCode, tail);
        }

        var file = FindDownloadedFile(basePath);
        if (file == null)
        {
            var tail = string.Join(Environment.NewLine, combinedTail);
            Log.Error($"yt-dlp reported success for {videoId} but no audio file was written next to {basePath}");
            throw new YtDlpException(
                $"yt-dlp finished but produced no audio file for {videoId}.", 0, tail);
        }

        Log.Info($"Downloaded {videoId} to {file}");
        return file;
    }

    /// <summary>
    /// Runs "yt-dlp -U" and reports its output. Returns false when the updater
    /// exits non-zero or the executable is gone; cancellation is rethrown.
    /// </summary>
    public static async Task<bool> UpdateAsync(string exePath, IProgress<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            Log.Warn($"yt-dlp update requested for a missing executable: {exePath}");
            log.Report("yt-dlp: executable not found");
            return false;
        }

        log.Report($"yt-dlp: updating {exePath} ...");
        var result = await RunCaptureAsync(exePath, new[] { "-U" }, ct);

        foreach (var line in TailLines(result.StdOut, 10))
            log.Report($"yt-dlp: {line}");

        if (result.ExitCode != 0)
        {
            Log.Warn($"yt-dlp -U exited with code {result.ExitCode}: {result.StdErr.Trim()}");
            log.Report("yt-dlp: update failed - see log");
            return false;
        }

        var version = await ToolBootstrap.GetVersionAsync(exePath, ct);
        log.Report(version == null ? "yt-dlp: update complete" : $"yt-dlp: update complete ({version})");
        return true;
    }

    // ---- JSON listing ---------------------------------------------------

    private static async Task<string> RunJsonAsync(
        string exePath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await RunCaptureAsync(exePath, arguments, ct);
        if (result.ExitCode != 0)
        {
            var tail = TailLines(result.StdErr, StderrTailLines);
            Log.Error($"yt-dlp exited with code {result.ExitCode}: {tail}");
            throw new YtDlpException(
                $"yt-dlp exited with code {result.ExitCode}.", result.ExitCode, tail);
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
            throw new YtDlpException("yt-dlp returned no output.", 0, TailLines(result.StdErr, StderrTailLines));

        return result.StdOut;
    }

    /// <summary>
    /// Parses the root object or its "entries" array. A missing/odd entry is
    /// skipped rather than failing the whole listing - yt-dlp happily mixes in
    /// unavailable, private and live-upcoming stubs.
    /// </summary>
    private static List<YtEntry> ParseEntries(string json)
    {
        var entries = new List<YtEntry>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return entries;

            if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var entry = ParseEntry(item);
                    if (entry != null) entries.Add(entry);
                }
            }
            else
            {
                var single = ParseEntry(root);
                if (single != null) entries.Add(single);
            }
        }
        catch (JsonException ex)
        {
            Log.Error("yt-dlp returned invalid JSON", ex);
            throw new YtDlpException("yt-dlp returned output that is not valid JSON.", 0, ex.Message);
        }

        return entries;
    }

    private static YtEntry? ParseEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        try
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;

            var duration = GetDouble(item, "duration");
            if (duration <= 0)
                duration = ParseDurationText(GetString(item, "duration_string"));

            return new YtEntry(
                id,
                GetString(item, "title") ?? "",
                GetString(item, "uploader")
                    ?? GetString(item, "channel")
                    ?? GetString(item, "creator")
                    ?? "",
                duration,
                GetThumbnail(item),
                (long)GetDouble(item, "view_count"),
                GetBool(item, "is_live")
                    || string.Equals(GetString(item, "live_status"), "is_live", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Debug($"Skipping unreadable YouTube entry: {ex.Message}");
            return null;
        }
    }

    private static string? GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text) || text == "NA") return null;
                return text.Trim();
            case JsonValueKind.Number:
                return value.GetRawText();
            default:
                return null;
        }
    }

    private static double GetDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 0;
    }

    private static bool GetBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? GetThumbnail(JsonElement item)
    {
        var single = GetString(item, "thumbnail");
        if (!string.IsNullOrWhiteSpace(single)) return single;

        if (item.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            foreach (var thumbnail in thumbnails.EnumerateArray())
            {
                var url = GetString(thumbnail, "url");
                if (!string.IsNullOrWhiteSpace(url)) best = url;
            }

            return best;
        }

        return null;
    }

    /// <summary>Parses yt-dlp's "1:02:03" / "4:07" duration strings.</summary>
    private static double ParseDurationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var parts = text.Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return 0;
            total = total * 60 + value;
        }

        return total;
    }

    // ---- URL normalisation ----------------------------------------------

    private static string NormalisePlaylistUrl(string urlOrId)
    {
        var input = (urlOrId ?? "").Trim();
        if (input.Length == 0)
            throw new ArgumentException("A playlist URL or id is required.", nameof(urlOrId));

        if (input.Contains("list=", StringComparison.OrdinalIgnoreCase))
        {
            if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return input;
            return "https://www.youtube.com/playlist?" + input.TrimStart('?');
        }

        if (input.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
        {
            if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return input;
            return "https://www.youtube.com" + (input.StartsWith('/') ? input : "/" + input);
        }

        if (input.Contains("://", StringComparison.Ordinal)) return input;

        return "https://www.youtube.com/playlist?list=" + input;
    }

    private static string NormaliseVideoUrl(string urlOrId)
    {
        var input = (urlOrId ?? "").Trim();
        if (input.Length == 0)
            throw new ArgumentException("A video id or URL is required.", nameof(urlOrId));

        if (BareVideoIdRegex.IsMatch(input))
            return $"https://www.youtube.com/watch?v={input}";

        if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return input;

        if (input.StartsWith("watch", StringComparison.OrdinalIgnoreCase))
            return "https://www.youtube.com/" + input;

        if (input.StartsWith("youtu.be/", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("www.youtube.com", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("m.youtube.com", StringComparison.OrdinalIgnoreCase))
            return "https://" + input;

        return $"https://www.youtube.com/watch?v={input}";
    }

    // ---- download helpers -----------------------------------------------

    private static YtProgress? ParseProgress(string line)
    {
        const string prefix = "download:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var fields = line[prefix.Length..].Split('|');
        if (fields.Length < 4) return null;

        var percentText = fields[0].Trim().TrimEnd('%');
        if (!double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return null;

        return new YtProgress(percent, fields[1].Trim(), fields[2].Trim(), fields[3].Trim());
    }

    private static void CleanupPartials(string basePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(basePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            var stem = Path.GetFileName(basePath) + ".";
            foreach (var file in Directory.EnumerateFiles(directory, stem + "*"))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not clean up partial downloads next to {basePath}: {ex.Message}");
        }
    }

    private static string? FindDownloadedFile(string basePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(basePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

            var extensions = new[] { ".mp3", ".m4a", ".webm", ".opus" };
            var stem = Path.GetFileName(basePath) + ".";
            return Directory.EnumerateFiles(directory, stem + "*")
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not inspect download output next to {basePath}: {ex.Message}");
            return null;
        }
    }

    private static void AppendCookieArgs(List<string> arguments, AppSettings settings)
    {
        var browser = settings.DownloadCookiesBrowser?.Trim().ToLowerInvariant();
        if (browser is "chrome" or "edge" or "firefox")
        {
            arguments.Add("--cookies-from-browser");
            arguments.Add(browser);
        }
    }

    private static void Enqueue(Queue<string> queue, string line)
    {
        if (queue.Count >= StderrTailLines) queue.Dequeue();
        queue.Enqueue(line);
    }

    private static string TailLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - count)));
    }

    // ---- process plumbing -----------------------------------------------

    private static string RequireExe(AppSettings settings)
    {
        var exe = ToolBootstrap.ResolveYtDlp(settings);
        if (exe != null) return exe;

        Log.Error("YouTube features were requested but yt-dlp is not installed");
        throw new YtDlpException(
            "yt-dlp not found. Install it from the download settings (ToolBootstrap.EnsureYtDlpAsync) and try again.",
            -1,
            "");
    }

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
        try
        {
            if (!process.Start())
                throw new YtDlpException($"Could not start yt-dlp ({exePath}).", -1, "");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new YtDlpException($"Could not start yt-dlp ({exePath}): {ex.Message}", -1, "");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = PumpAsync(process.StandardOutput, line => stdout.AppendLine(line), ct);
        var stderrDone = PumpAsync(process.StandardError, line => stderr.AppendLine(line), ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        await Task.WhenAll(stdoutDone, stderrDone);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs yt-dlp streaming both pipes line by line. Cancellation kills the
    /// whole process tree (yt-dlp spawns ffmpeg) and rethrows.
    /// </summary>
    private static async Task<int> RunProcessAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        Action<string> onStdOut,
        Action<string> onStdErr,
        CancellationToken ct)
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
        try
        {
            if (!process.Start())
                throw new YtDlpException($"Could not start yt-dlp ({exePath}).", -1, "");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new YtDlpException($"Could not start yt-dlp ({exePath}): {ex.Message}", -1, "");
        }

        var stdoutDone = PumpAsync(process.StandardOutput, onStdOut, ct);
        var stderrDone = PumpAsync(process.StandardError, onStdErr, ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Debug($"yt-dlp did not shut down cleanly after cancellation: {ex.Message}");
            }

            throw;
        }

        await Task.WhenAll(stdoutDone, stderrDone);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> handler, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Length == 0) continue;
                try
                {
                    handler(line);
                }
                catch (Exception ex)
                {
                    Log.Debug($"yt-dlp line handler failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* process is being killed */ }
        catch (Exception ex)
        {
            Log.Debug($"Reading yt-dlp output failed: {ex.Message}");
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not kill yt-dlp: {ex.Message}");
        }
    }
}
