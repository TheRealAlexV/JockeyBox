using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using JockeyBox.Settings;
using JockeyBox.Soundboard;
using JockeyBox.Tools;
using JockeyBox.YouTube;

namespace JockeyBox.Downloads;

/// <summary>One step of an ingest, for the pad's progress bar and status line.</summary>
/// <param name="Fraction">0-1; -1 means "indeterminate, still working".</param>
/// <param name="Message">Short human text ("Reading video…", "Downloading 42%").</param>
public sealed record SoundboardIngestProgress(double Fraction, string Message);

/// <summary>
/// Outcome of turning a local file or a URL into a file the board owns.
/// <see cref="Error"/> is written for the status line; it is never an exception
/// the caller has to catch.
/// </summary>
public sealed record SoundboardIngestResult(
    bool Ok,
    string? FilePath,
    string? Title,
    string? SourceUrl,
    double DurationSeconds,
    string? Error)
{
    public static SoundboardIngestResult Fail(string error) =>
        new(false, null, null, null, 0, error);
}

/// <summary>
/// Gets audio <i>into</i> <c>%APPDATA%\JockeyBox\soundboard</c>: copies a local
/// file in under a stable name, or downloads a YouTube/URL source with the same
/// yt-dlp + ffmpeg the library importer uses.
///
/// <para><b>Format.</b> Downloads are converted to mp3 (the format the download
/// pipeline already produces and the tools already ship support for). A local
/// file is copied byte-for-byte - re-encoding someone's FLAC on import would only
/// lose quality. Both paths end at a board-local path, so deleting a pad can
/// always reclaim the space.</para>
///
/// <para><b>Threading.</b> Every method is async and awaits the tool processes;
/// progress is reported through <see cref="IProgress{T}"/> which the view model
/// creates on the UI thread, so updates marshal themselves. Nothing here touches
/// the dispatcher directly.</para>
/// </summary>
public sealed class SoundboardIngest
{
    /// <summary>Bitrate used for the mp3 conversion; small enough for a pad, clean enough for a stab.</summary>
    public const int Mp3BitrateKbps = 192;

    private static readonly Regex FfprobeDuration =
        new(@"^\s*\d+(\.\d+)?\s*$", RegexOptions.Compiled);

    private readonly AppSettings _settings;

    public SoundboardIngest(AppSettings settings, string? directory = null)
    {
        _settings = settings;
        Directory = directory ?? Path.Combine(ToolBootstrap.AppDataDir, "soundboard");
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>The board folder this instance writes into.</summary>
    public string Directory { get; }

    // ---------------------------------------------------------------- local file

    /// <summary>
    /// Copy <paramref name="sourcePath"/> into the board folder unless it already
    /// lives there. The name is sanitised and de-duplicated, so two different
    /// "horn.mp3" files coexist as "horn.mp3" and "horn (2).mp3".
    /// </summary>
    public async Task<SoundboardIngestResult> ImportLocalAsync(
        string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return SoundboardIngestResult.Fail("No file was chosen.");

        if (!File.Exists(sourcePath))
            return SoundboardIngestResult.Fail($"File not found: {sourcePath}");

        if (!SoundboardFiles.IsSupported(sourcePath))
            return SoundboardIngestResult.Fail(
                $"{Path.GetExtension(sourcePath)} is not an audio file the board can play.");

        try
        {
            var target = sourcePath;
            if (!SoundboardFiles.IsInside(Directory, sourcePath))
            {
                var name = SoundboardFiles.SanitizeFileName(Path.GetFileName(sourcePath));
                target = SoundboardFiles.UniquePath(Path.Combine(Directory, name));
                File.Copy(sourcePath, target, overwrite: false);
                Log.Info($"soundboard: copied '{sourcePath}' in as '{Path.GetFileName(target)}'");
            }

            var duration = await ProbeDurationAsync(target, _settings, ct).ConfigureAwait(false);
            return new SoundboardIngestResult(
                true, target, Path.GetFileNameWithoutExtension(target), null, duration, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"soundboard: could not import '{sourcePath}'", ex);
            return SoundboardIngestResult.Fail($"Could not copy the file: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- URL

    /// <summary>
    /// Download audio from <paramref name="url"/> into the board folder and return
    /// the file yt-dlp wrote. A missing yt-dlp is a clear error, not an exception:
    /// the caller leaves the pad out and shows the message.
    /// </summary>
    public async Task<SoundboardIngestResult> DownloadAsync(
        string url, IProgress<SoundboardIngestProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return SoundboardIngestResult.Fail("Paste a YouTube link first.");

        var ytDlp = ToolBootstrap.ResolveYtDlp(_settings);
        if (ytDlp == null)
        {
            Log.Error("soundboard: a URL was added but yt-dlp is not installed");
            return SoundboardIngestResult.Fail(
                "yt-dlp is not installed - install it from Settings > Downloads, then try again.");
        }

        try
        {
            progress?.Report(new SoundboardIngestProgress(-1, "Reading video info..."));

            // -J first: the title names the file, and the video id is what the
            // downloader wants. A failure here explains itself (private, age gate).
            var video = await YtDlp.GetVideoAsync(url, _settings, ct).ConfigureAwait(false);
            if (video == null)
            {
                Log.Warn($"soundboard: yt-dlp found no video for {url}");
                return SoundboardIngestResult.Fail("yt-dlp found no video at that link.");
            }

            var stem = SoundboardFiles.UniqueStem(Directory, SoundboardFiles.Slug(video.Title));
            var targetBase = Path.Combine(Directory, stem);

            var convert = ToolBootstrap.HasFfmpeg(_settings);
            if (!convert)
                Log.Warn("soundboard: ffmpeg is missing - keeping the source container instead of mp3");

            var downloadProgress = progress == null
                ? null
                : new Progress<YtProgress>(update =>
                {
                    var fraction = Math.Clamp(update.Percent / 100.0, 0, 1);
                    var message = update.Speed is { Length: > 0 } && update.Speed != "NA"
                        ? $"Downloading {update.Percent:0}% \u00b7 {update.Speed}"
                        : $"Downloading {update.Percent:0}%";
                    progress.Report(new SoundboardIngestProgress(fraction, message));
                });

            progress?.Report(new SoundboardIngestProgress(-1, convert ? "Downloading and converting to mp3..." : "Downloading..."));

            var file = await YtDlp.DownloadBestAudioAsync(
                video.VideoId, targetBase, convert, Mp3BitrateKbps, _settings, downloadProgress, ct)
                .ConfigureAwait(false);

            progress?.Report(new SoundboardIngestProgress(1, "Reading duration..."));

            var duration = await ProbeDurationAsync(file, _settings, ct).ConfigureAwait(false);
            if (duration <= 0) duration = video.DurationSeconds;

            Log.Info($"soundboard: downloaded '{video.Title}' to '{Path.GetFileName(file)}' ({duration:0.#}s)");
            return new SoundboardIngestResult(true, file, video.Title, url, duration, null);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"soundboard: download cancelled for {url}");
            return SoundboardIngestResult.Fail("Download cancelled.");
        }
        catch (YtDlpException ex)
        {
            Log.Error($"soundboard: yt-dlp failed for {url} (exit {ex.ExitCode})", ex);
            return SoundboardIngestResult.Fail($"Download failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"soundboard: download failed for {url}", ex);
            return SoundboardIngestResult.Fail($"Download failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- duration

    /// <summary>
    /// Length of an audio file in seconds, or 0 when it cannot be read. NAudio
    /// handles the common containers cheaply; ffprobe is the fallback for the
    /// ones it does not (opus in webm, some m4a variants).
    /// </summary>
    public static async Task<double> ProbeDurationAsync(
        string path, AppSettings? settings = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

        var direct = await Task.Run(() =>
        {
            try
            {
                using var reader = new NAudio.Wave.AudioFileReader(path);
                return reader.TotalTime.TotalSeconds;
            }
            catch
            {
                return 0d;
            }
        }, ct).ConfigureAwait(false);

        if (direct > 0) return direct;

        return await ProbeWithFfprobeAsync(path, settings, ct).ConfigureAwait(false);
    }

    private static async Task<double> ProbeWithFfprobeAsync(
        string path, AppSettings? settings, CancellationToken ct)
    {
        var ffprobe = ResolveFfprobe(settings);
        if (ffprobe == null)
        {
            Log.Debug($"soundboard: no ffprobe to read the duration of '{path}'");
            return 0;
        }

        try
        {
            var result = await Processes.RunAsync(ffprobe, new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                path,
            }, 20, null, ct).ConfigureAwait(false);

            if (result.ExitCode != 0) return 0;

            var text = result.StdOut.Trim();
            if (!FfprobeDuration.IsMatch(text)) return 0;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? Math.Max(0, seconds)
                : 0;
        }
        catch (Exception ex)
        {
            Log.Debug($"soundboard: ffprobe duration probe failed for '{path}': {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// ffprobe next to the configured ffmpeg, else the bundled copy, else PATH.
    /// The app only bootstraps ffmpeg.exe, so the sibling lookup is the normal hit.
    /// </summary>
    private static string? ResolveFfprobe(AppSettings? settings)
    {
        if (settings != null)
        {
            var ffmpeg = ToolBootstrap.ResolveFfmpeg(settings);
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
                if (File.Exists(sibling)) return sibling;
            }
        }

        var bundled = Path.Combine(ToolBootstrap.ToolsDir, "ffprobe.exe");
        if (File.Exists(bundled)) return bundled;

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var name in new[] { "ffprobe.exe", "ffprobe" })
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception)
            {
                // A malformed PATH entry must not stop the search.
            }
        }

        return null;
    }

    /// <summary>Diagnostic helper used by the settings/log path and tests.</summary>
    public static string DescribeTool(AppSettings settings) =>
        ToolBootstrap.ResolveYtDlp(settings) ?? "(yt-dlp missing)";
}
