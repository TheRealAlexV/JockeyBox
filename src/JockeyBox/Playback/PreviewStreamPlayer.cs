using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using JockeyBox.Settings;
using JockeyBox.Tools;
using LibVLCSharp.Shared;

namespace JockeyBox.Playback;

/// <summary>
/// Auditions a track without downloading it, and with a real timeline.
///
/// yt-dlp resolves the audio to a direct googlevideo URL (<c>-g</c>) and libVLC
/// plays that URL. googlevideo honours HTTP Range requests, so libVLC knows the
/// length and can jump anywhere: the strip gets a true duration, a true
/// position and a seek that moves a decoder instead of restarting a pipe.
///
/// When extraction is refused (age gate, live, extractor change) the player
/// falls back to a yt-dlp temp-file download, reporting its percentage, and
/// then plays the local file — reported as "download fallback" in the strip.
///
/// One instance owns one libVLC player for the whole app, so starting a preview
/// in either tab stops the other. Every start bumps a generation and cancels the
/// previous run, so a click-through kills the resolve and the process it spawned.
/// </summary>
public sealed class PreviewStreamPlayer : IDisposable
{
    private const int KillWaitMs = 1500;

    private const int StderrTailChars = 400;

    /// <summary>One libVLC engine for the app; libVLC itself is process-wide.</summary>
    private static readonly object VlcGate = new();
    private static LibVLC? _vlc;
    private static string? _vlcError;

    private static readonly Regex PercentPattern =
        new(@"\[\s*download\s*\]\s*([0-9.]+)%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaPlayer _player;
    private readonly LibVLC? _engine;

    private CancellationTokenSource? _runCts;
    private Process? _ytDlp;
    private Media? _media;
    private string? _sourceUrl;
    private string? _title;
    private string? _status;
    private string? _error;
    private string? _mode;
    private string? _tempFile;

    private int _generation;
    private bool _loading;
    private bool _paused;
    private bool _muted;
    private int _volume = 80;
    private bool _disposed;

    public PreviewStreamPlayer(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _engine = EnsureEngine();
        if (_engine != null)
        {
            _player = new MediaPlayer(_engine);
            _player.Volume = _volume;
            WireEvents();
        }
        else
        {
            // A missing libvlc must degrade to a readable message, not a crash.
            _player = null!;
        }

        // Previews must never outlive the window: the hook is what guarantees
        // no orphaned yt-dlp keeps running after an abrupt shutdown.
        var app = Application.Current;
        if (app != null) app.Exit += (_, _) => Dispose();
    }

    public AppSettings Settings { get; }

    /// <summary>True while a source is selected (resolving, playing or paused).</summary>
    public bool IsPlaying => CurrentUrl != null && !_loading && _media != null && _player.IsPlaying;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>Real position of the decoder in seconds (0 when nothing is loaded).</summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_media == null) return 0;
            }

            try
            {
                var ms = _player.Time;
                return ms <= 0 ? 0 : ms / 1000.0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Real length reported by the container; 0 while unknown (unseekable live).</summary>
    public double DurationSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_media == null) return 0;
            }

            try
            {
                var ms = _player.Length;
                return ms <= 0 ? 0 : ms / 1000.0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public string? CurrentUrl
    {
        get { lock (_gate) return _sourceUrl; }
    }

    public string? Title
    {
        get { lock (_gate) return _title; }
    }

    /// <summary>Short note the strip shows: resolving, buffering, download progress.</summary>
    public string? Status
    {
        get { lock (_gate) return _error ?? _status; }
    }

    /// <summary>"stream" for a direct URL, "download" for the temp-file fallback.</summary>
    public string? Mode
    {
        get { lock (_gate) return _mode; }
    }

    public bool IsLoading
    {
        get { lock (_gate) return _loading; }
    }

    /// <summary>0..1, applied to libVLC's 0..100 scale.</summary>
    public double Volume
    {
        get { lock (_gate) return _volume / 100.0; }
    }

    public bool IsMuted
    {
        get { lock (_gate) return _muted; }
    }

    /// <summary>True when yt-dlp and libVLC are both usable.</summary>
    public bool IsAvailable => _engine != null && ResolveYtDlp() != null;

    /// <summary>Why <see cref="IsAvailable"/> is false, ready for the status line.</summary>
    public string UnavailableReason =>
        _engine == null
            ? _vlcError ?? "libVLC failed to load, so previews cannot play"
            : "yt-dlp is not installed, so previews cannot be streamed";

    /// <summary>
    /// Self-test only: what libVLC reports about the audio path it actually
    /// chose, so a check can tell "playing" from "playing silently". Lives here
    /// because this type is the only owner of the engine.
    /// </summary>
    internal string AudioPathProbe()
    {
        if (_player == null) return "state=none (libVLC did not load)";

        try
        {
            var track = _player.AudioTrack;
            return $"state={_player.State} audioTrack={track.ToString(CultureInfo.InvariantCulture)} " +
                   $"audioTracks={_player.AudioTrackCount} " +
                   $"device=\"{_player.OutputDevice ?? "default"}\" " +
                   $"volume={_player.Volume.ToString(CultureInfo.InvariantCulture)} mute={_player.Mute} " +
                   $"vlc={_engine?.Version ?? "?"}";
        }
        catch (Exception ex)
        {
            return $"probe failed: {ex.Message}";
        }
    }

    /// <summary>Raised when play state, position, length, volume or mute changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised with the note the tab's status line should show.</summary>
    public event EventHandler<string>? StatusChanged;

    // ---- control ---------------------------------------------------------

    /// <summary>
    /// Plays <paramref name="url"/>, stopping whatever was playing first. Returns
    /// immediately: the click is acknowledged with a "resolving" state, and a
    /// newer call cancels this one mid-flight.
    /// </summary>
    public void Play(string url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;

            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            token = _runCts.Token;

            _generation++;
            _sourceUrl = url;
            _title = title;
            _status = "Resolving stream\u2026";
            _error = null;
            _mode = null;
            _loading = true;
            _paused = false;
        }

        if (_engine == null)
        {
            Fail(UnavailableReason);
            return;
        }

        EndPlayback();
        RaiseStateChanged();
        RaiseStatus();
        _ = RunAsync(url, token);
    }

    /// <summary>Stops playback, kills the resolve and forgets the current source.</summary>
    public void Stop()
    {
        bool hadSource;
        lock (_gate)
        {
            hadSource = _sourceUrl != null || _ytDlp != null || _media != null;
            if (!hadSource) return;

            CancelRun();
            ClearSource();
        }

        EndPlayback();
        RaiseStateChanged();
        RaiseStatus();
    }

    /// <summary>Stops only when <paramref name="url"/> is the track being previewed.</summary>
    public void StopIfUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_gate)
        {
            if (!string.Equals(_sourceUrl, url, StringComparison.OrdinalIgnoreCase)) return;
        }

        Stop();
    }

    /// <summary>Moves the decoder; clamps to the reported length.</summary>
    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (_disposed || _media == null) return;
        }

        try
        {
            var length = _player.Length;
            var target = Math.Max(0, seconds);
            if (length > 0) target = Math.Min(target, length / 1000.0 - 0.25);
            _player.Time = (long)(target * 1000);
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview seek failed: {ex.Message}");
        }

        RaiseStateChanged();
    }

    public void SetPlaying(bool playing)
    {
        string? retryUrl;
        string? retryTitle;
        lock (_gate)
        {
            if (_disposed || _sourceUrl == null || _player == null) return;

            retryUrl = _sourceUrl;
            retryTitle = _title;

            // Nothing loaded yet: play means "try again" (a resolve may still be
            // running, in which case its own Playing event takes over).
            if (_media == null)
            {
                if (_loading && _error == null) return;
            }
            else
            {
                retryUrl = null;
            }
        }

        if (_media == null)
        {
            Play(retryUrl!, retryTitle);
            return;
        }

        try
        {
            if (playing)
            {
                if (_player.State == VLCState.Ended)
                {
                    _player.Stop();
                    var media = _media;
                    if (media != null) _player.Play(media);
                }
                else if (!_player.IsPlaying)
                {
                    _player.Play();
                }
            }
            else if (_player.IsPlaying)
            {
                _player.Pause();
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview play/pause failed: {ex.Message}");
        }

        RaiseStateChanged();
    }

    public void SetVolume(double level)
    {
        var value = (int)Math.Round(Math.Clamp(level, 0, 1) * 100);
        lock (_gate)
        {
            if (_disposed) return;
            _volume = value;
        }

        try
        {
            _player.Volume = value;
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview volume failed: {ex.Message}");
        }

        RaiseStateChanged();
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _muted = muted;
        }

        try
        {
            _player.Mute = muted;
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview mute failed: {ex.Message}");
        }

        RaiseStateChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelRun();
            ClearSource();
        }

        EndPlayback();
        _player?.Dispose();
    }

    // ---- resolve / play --------------------------------------------------

    private async Task RunAsync(string url, CancellationToken token)
    {
        try
        {
            var direct = await ResolveDirectUrlAsync(url, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            if (direct != null)
            {
                StartMedia(direct, "stream", token);
                return;
            }

            if (!IsAvailable) return;

            SetStatus("Direct stream refused \u2014 downloading preview\u2026");
            var file = await DownloadToTempAsync(url, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            if (file == null) return;

            StartMedia(file, "download", token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview failed: {ex}");
            Fail($"Preview failed: {ex.Message}");
        }
    }

    /// <summary>Asks yt-dlp for the direct audio URL; null means "use the fallback".</summary>
    private async Task<string?> ResolveDirectUrlAsync(string url, CancellationToken token)
    {
        var ytDlp = ResolveYtDlp();
        if (ytDlp == null)
        {
            Fail("yt-dlp is not installed, so previews cannot be streamed");
            return null;
        }

        var result = await RunYtDlpAsync(
            ytDlp,
            new[]
            {
                "-g",
                "-f", "bestaudio/best",
                "--no-playlist",
                "--no-warnings",
                url,
            },
            onStdoutLine: null,
            token).ConfigureAwait(false);

        if (!result.Started)
        {
            Fail("yt-dlp could not be started");
            return null;
        }

        if (result.Cancelled) return null;

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            Log.Warn($"Preview resolve failed: yt-dlp exit {result.ExitCode}. {Tail(result.Stderr)}");
            return null;
        }

        // -g prints one URL per format; the audio one carries mime=audio.
        var lines = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var audio = lines.LastOrDefault(line =>
                        line.Contains("mime=audio", StringComparison.OrdinalIgnoreCase))
                    ?? lines.LastOrDefault(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase));

        if (audio == null)
        {
            Log.Warn($"Preview resolve returned no URL. {Tail(result.Stdout)}");
            return null;
        }

        Log.Info($"Preview resolved {(_title ?? url)} to a direct stream");
        return audio;
    }

    private void StartMedia(string target, string mode, CancellationToken token)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            Fail("Preview source is not a playable URL");
            return;
        }

        Media media;
        try
        {
            media = new Media(_engine!, uri);
            media.AddOption(":no-video");
            if (mode == "stream")
            {
                // Range-backed URL: let libVLC reconnect and buffer a little.
                media.AddOption(":network-caching=1500");
                media.AddOption(":http-reconnect");
            }
            else
            {
                media.AddOption(":file-caching=1000");
            }
        }
        catch (Exception ex)
        {
            Fail($"libVLC rejected the preview: {ex.Message}");
            return;
        }

        Media? previous;
        lock (_gate)
        {
            if (token.IsCancellationRequested || _disposed)
            {
                media.Dispose();
                return;
            }

            previous = _media;
            _media = media;
            _mode = mode;
            _status = mode == "stream" ? "Buffering\u2026" : null;
            _error = null;
            _loading = mode == "stream";
            _tempFile = mode == "download" ? target : null;
        }

        previous?.Dispose();

        bool started;
        try
        {
            started = _player.Play(media);
        }
        catch (Exception ex)
        {
            Fail($"libVLC could not start playback: {ex.Message}");
            return;
        }

        if (!started)
        {
            Fail("libVLC could not start playback");
            return;
        }

        RaiseStateChanged();
        RaiseStatus();
    }

    // ---- download fallback ----------------------------------------------

    /// <summary>
    /// Downloads the audio to a temp file with yt-dlp, reporting the percentage.
    /// Reached only when <c>-g</c> refuses to hand out a URL.
    /// </summary>
    private async Task<string?> DownloadToTempAsync(string url, CancellationToken token)
    {
        var ytDlp = ResolveYtDlp();
        if (ytDlp == null) return null;

        var directory = Path.Combine(Path.GetTempPath(), "JockeyBox");
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Fail($"Preview temp folder is not writable: {ex.Message}");
            return null;
        }

        var stem = "preview-" + Guid.NewGuid().ToString("N");

        var result = await RunYtDlpAsync(
            ytDlp,
            new[]
            {
                "--no-playlist",
                "--no-warnings",
                "--newline",
                "-f", "bestaudio/best",
                "-o", Path.Combine(directory, stem + ".%(ext)s"),
                url,
            },
            onStdoutLine: line =>
            {
                var match = PercentPattern.Match(line);
                if (!match.Success) return;
                if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    return;

                SetStatus($"Downloading preview {pct.ToString("0", CultureInfo.InvariantCulture)}%\u2026");
            },
            token).ConfigureAwait(false);

        if (result.Cancelled || token.IsCancellationRequested) return null;

        if (!result.Started || result.ExitCode != 0)
        {
            Log.Warn($"Preview download fallback failed: exit {result.ExitCode}. {Tail(result.Stderr)}");
            Fail($"Preview failed: yt-dlp exit {result.ExitCode} \u2014 {Tail(result.Stderr)}");
            return null;
        }

        string? file = null;
        try
        {
            file = Directory.EnumerateFiles(directory, stem + ".*")
                .FirstOrDefault(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview temp scan failed: {ex.Message}");
        }

        if (file == null)
        {
            Fail("Preview failed: the download produced no file");
            return null;
        }

        Log.Info($"Preview fallback downloaded to {Path.GetFileName(file)}");
        return file;
    }

    // ---- process plumbing ------------------------------------------------

    private sealed record ProcessResult(bool Started, bool Cancelled, int ExitCode, string Stdout, string Stderr);

    private async Task<ProcessResult> RunYtDlpAsync(
        string ytDlp,
        IEnumerable<string> args,
        Action<string>? onStdoutLine,
        CancellationToken token)
    {
        var info = new ProcessStartInfo(ytDlp)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Nothing passed on these paths is a relative path - the -o template is an
        // absolute temp path and cookies come from the browser - so the working
        // directory only has to exist. It used to be the app-managed tools folder,
        // which on a machine where the bundled download never ran made Process.Start
        // throw "the directory name is invalid" before yt-dlp was touched.
        var exeDir = Path.GetDirectoryName(ytDlp);
        if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir)) info.WorkingDirectory = exeDir;

        foreach (var argument in args) info.ArgumentList.Add(argument);
        AppendCookieArgs(info);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return new ProcessResult(false, false, -1, "", "process did not start");
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview yt-dlp start failed: {ex.Message}");
            return new ProcessResult(false, false, -1, "", ex.Message);
        }

        lock (_gate)
        {
            if (token.IsCancellationRequested)
            {
                Kill(process);
                return new ProcessResult(true, true, -1, "", "");
            }

            _ytDlp = process;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = new StringBuilder();
        var cancelled = false;

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)) != null)
            {
                stdout.AppendLine(line);
                onStdoutLine?.Invoke(line);
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview yt-dlp read failed: {ex.Message}");
        }

        var stderr = "";
        try
        {
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview yt-dlp stderr read failed: {ex.Message}");
        }

        if (cancelled || token.IsCancellationRequested)
        {
            Kill(process);
            cancelled = true;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_ytDlp, process)) _ytDlp = null;
        }

        var code = cancelled ? -1 : SafeExitCode(process);
        return new ProcessResult(true, cancelled, code, stdout.ToString(), stderr);
    }

    private void AppendCookieArgs(ProcessStartInfo info)
    {
        var browser = Settings.DownloadCookiesBrowser?.Trim().ToLowerInvariant();
        if (browser is "chrome" or "edge" or "firefox")
        {
            info.ArgumentList.Add("--cookies-from-browser");
            info.ArgumentList.Add(browser);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(KillWaitMs);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview process cleanup: {ex.Message}");
        }
    }

    // ---- state -----------------------------------------------------------

    /// <summary>Cancels the resolve in flight; caller holds the gate.</summary>
    private void CancelRun()
    {
        _generation++;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }

    /// <summary>Forgets the current source; caller holds the gate.</summary>
    private void ClearSource()
    {
        _sourceUrl = null;
        _title = null;
        _status = null;
        _error = null;
        _mode = null;
        _loading = false;
        _paused = false;
    }

    /// <summary>Kills the process and frees the player without touching the source fields.</summary>
    private void EndPlayback()
    {
        Process? process;
        Media? media;
        string? temp;
        lock (_gate)
        {
            process = _ytDlp;
            _ytDlp = null;
            media = _media;
            _media = null;
            temp = _tempFile;
            _tempFile = null;
            _loading = false;
            _paused = false;
        }

        if (process != null) Kill(process);

        try
        {
            if (_player != null) _player.Stop();
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview stop failed: {ex.Message}");
        }

        media?.Dispose();

        if (temp != null)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                Log.Debug($"Preview temp delete failed: {ex.Message}");
            }
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _error = message;
            _status = null;
            _loading = false;
            _paused = false;
        }

        Log.Warn($"Preview: {message}");
        EndPlayback();
        RaiseStateChanged();
        RaiseStatus(message);
    }

    private void SetStatus(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _status = message;
        }

        RaiseStateChanged();
        RaiseStatus();
    }

    // ---- libVLC ----------------------------------------------------------

    private static LibVLC? EnsureEngine()
    {
        lock (VlcGate)
        {
            if (_vlc != null || _vlcError != null) return _vlc;

            try
            {
                // Under PublishSingleFile, AppContext.BaseDirectory is the
                // extraction directory, but KeepLibVlcPayloadLoose leaves
                // libvlc\win-x64 beside the real exe, so libVLC has to be
                // resolved from the process's own directory first.
                var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
                var nativeDir = exeDir == null ? null : Path.Combine(exeDir, "libvlc", "win-x64");
                if (nativeDir != null && Directory.Exists(nativeDir))
                    Core.Initialize(nativeDir);
                else
                    Core.Initialize();
                _vlc = new LibVLC(
                    "--no-video",
                    "--no-snapshot-preview",
                    "--intf", "dummy",
                    "--quiet");
            }
            catch (Exception ex)
            {
                _vlcError = $"libVLC failed to load: {ex.Message}";
                Log.Warn(_vlcError);
            }

            return _vlc;
        }
    }

    private void WireEvents()
    {
        _player.TimeChanged += (_, _) => RaiseStateChanged();
        _player.LengthChanged += (_, _) => RaiseStateChanged();

        _player.Playing += (_, _) =>
        {
            lock (_gate)
            {
                _loading = false;
                _status = null;
                _error = null;
                _paused = false;
            }

            RaiseStateChanged();
            RaiseStatusCleared();
        };

        _player.Paused += (_, _) =>
        {
            lock (_gate) _paused = true;
            RaiseStateChanged();
        };

        _player.Buffering += (_, e) =>
        {
            bool cleared = false;
            lock (_gate)
            {
                if (_media == null) return;

                // Only before playback actually starts. VLC re-raises Buffering,
                // usually at 100%, for the rest of the stream, and every raise used
                // to re-set the status that Playing had just cleared - so a playing
                // preview showed a frozen "Buffering 100%..." for as long as it
                // played. Once Playing has fired, the clock is the progress display.
                if (!_loading) return;

                int cache = Math.Clamp((int)Math.Round(e.Cache), 0, 100);
                cleared = cache >= 100 && _status != null;
                _status = cache >= 100 ? null : $"Buffering {cache}%\u2026";
            }

            RaiseStateChanged();
            if (cleared) RaiseStatusCleared();
            else RaiseStatus();
        };

        _player.EncounteredError += (_, _) => Fail("Preview playback failed \u2014 the stream was refused or interrupted");

        _player.EndReached += (_, _) =>
        {
            lock (_gate)
            {
                if (_media == null) return;
                _paused = true;
            }

            RaiseStateChanged();
        };
    }

    private void RaiseStateChanged() => Post(() => StateChanged?.Invoke(this, EventArgs.Empty));

    private void RaiseStatus(string? message = null)
    {
        string? text;
        lock (_gate) text = message ?? _error ?? _status;
        if (string.IsNullOrWhiteSpace(text)) return;
        Post(() => StatusChanged?.Invoke(this, text!));
    }

    /// <summary>Tells the tab its status line no longer applies (playback started or stopped).</summary>
    private void RaiseStatusCleared() => Post(() => StatusChanged?.Invoke(this, ""));

    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    // ---- tools -----------------------------------------------------------

    private string? ResolveYtDlp() => Resolve(
        () => ToolBootstrap.ResolveYtDlp(Settings),
        ToolBootstrap.BundledYtDlpPath,
        "yt-dlp.exe",
        "yt-dlp");

    /// <summary>
    /// Tries the configured location, then the app-managed copy, then PATH,
    /// remembering a miss so a machine without the tool does not re-stat the
    /// whole PATH on every check.
    /// </summary>
    private string? Resolve(Func<string?> configured, string bundled, params string[] pathNames)
    {
        var fromSettings = configured();
        if (!string.IsNullOrWhiteSpace(fromSettings) && File.Exists(fromSettings)) return fromSettings;

        if (File.Exists(bundled)) return bundled;

        var cacheKey = string.Join('|', pathNames);
        if (_missing.ContainsKey(cacheKey)) return null;

        foreach (var name in pathNames)
        {
            foreach (var directory in PathDirectories())
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }
        }

        _missing[cacheKey] = 1;
        return null;
    }

    private static IEnumerable<string> PathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim().Trim('"');
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    private static string Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no stderr)";

        var trimmed = text.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= StderrTailChars
            ? trimmed
            : "\u2026" + trimmed[^StderrTailChars..];
    }
}
