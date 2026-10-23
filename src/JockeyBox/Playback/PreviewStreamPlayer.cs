using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Playback;

/// <summary>
/// Auditions a YouTube track without downloading it.
///
/// The pipeline is two processes chained by a pipe:
/// <code>
/// yt-dlp -f bestaudio/best --no-playlist --no-warnings -o - &lt;url&gt;   (stdout)
///   |  ffplay -nodisp -autoexit -loglevel quiet -i pipe:0
/// </code>
/// ffplay decodes the first bytes that arrive, so the sound starts seconds into
/// the track while the rest is still in flight, and nothing ever reaches the
/// disk. Seeking restarts the pair at an offset (<c>yt-dlp --download-sections</c>
/// plus <c>ffplay -ss</c>).
///
/// Everything that touches a process is serialised on one lock and every start
/// bumps a generation counter, so a click-through never leaves a second ffplay
/// playing in the dark: the newer request kills what the older one spawned,
/// including a pipeline that was still resolving its binaries.
/// </summary>
public sealed class PreviewStreamPlayer : IDisposable
{
    /// <summary>How long ffplay is given to render before the audio is called started.</summary>
    private const int StartDelayMs = 1200;

    private const int KillWaitMs = 1500;

    private const int PumpBufferSize = 32 * 1024;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _missing = new(StringComparer.OrdinalIgnoreCase);

    private Process? _ytDlp;
    private Process? _ffplay;
    private CancellationTokenSource? _startCts;
    private string? _sourceUrl;
    private string? _title;
    private int _generation;

    /// <summary>Offset the current pipeline was started at (set by a seek).</summary>
    private int _seekSeconds;

    private bool _paused;
    private bool _disposed;

    /// <summary>Position frozen at the moment playback was paused.</summary>
    private double _pausedAtSeconds;

    /// <summary>Position when the current pipeline started, as a Stopwatch timestamp.</summary>
    private long _runStartTicks;

    /// <summary>Seconds accumulated by pipelines that a seek or a switch has already replaced.</summary>
    private double _elapsedBeforeRun;

    private string? _status;
    private DispatcherTimer? _keepAlive;

    public PreviewStreamPlayer(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Previews must never outlive the window: the hook is what guarantees
        // no orphaned ffplay keeps playing after an abrupt shutdown.
        var app = Application.Current;
        if (app != null) app.Exit += (_, _) => Dispose();
    }

    public AppSettings Settings { get; }

    /// <summary>True while a source is selected (playing or paused).</summary>
    public bool IsPlaying => CurrentUrl != null;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>
    /// Estimated playhead: there is no container to ask for a real position on
    /// a pipe, so this is wall clock since the pipeline started, plus the seek
    /// offset, minus the time spent paused.
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_sourceUrl == null) return 0;
                return _paused
                    ? _pausedAtSeconds
                    : _elapsedBeforeRun + Stopwatch.GetElapsedTime(_runStartTicks).TotalSeconds;
            }
        }
    }

    /// <summary>A stream's length is unknown until it is downloaded; 0 means "unbounded".</summary>
    public double DurationSeconds => 0;

    public string? CurrentUrl
    {
        get { lock (_gate) return _sourceUrl; }
    }

    public string? Title
    {
        get { lock (_gate) return _title; }
    }

    /// <summary>Last human-readable note: buffering, seek fallback, missing tool.</summary>
    public string? Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>True when both halves of the pipeline are installed.</summary>
    public bool IsAvailable => ResolveYtDlp() != null && ResolveFfplay() != null;

    /// <summary>Why <see cref="IsAvailable"/> is false, ready for the status line.</summary>
    public string UnavailableReason =>
        ResolveFfplay() == null
            ? "Install ffmpeg (ffplay) to stream previews"
            : "yt-dlp is not installed, so previews cannot be streamed";

    /// <summary>Raised on the UI thread when play state or position source changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on the UI thread with the note the status line should show.</summary>
    public event EventHandler<string>? StatusChanged;

    // ---- control ---------------------------------------------------------

    /// <summary>
    /// Streams <paramref name="url"/>, stopping whatever was playing first.
    /// Returns immediately: resolution and process spawn happen on a task, and
    /// a newer call cancels this one mid-flight.
    /// </summary>
    public void Play(string url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;

            // Kill the previous pipeline before the new one is even resolved.
            StopCore();
            _generation++;
            _startCts?.Dispose();
            _startCts = new CancellationTokenSource();
            token = _startCts.Token;

            _sourceUrl = url;
            _title = title;
            _seekSeconds = 0;
            _paused = false;
            _pausedAtSeconds = 0;
            _elapsedBeforeRun = 0;
            _status = "Buffering\u2026";
        }

        RaiseStateChanged();
        RaiseStatus();
        _ = StartAsync(url, 0, token);
        StartKeepAlive();
    }

    /// <summary>Stops the pipeline and forgets the current source.</summary>
    public void Stop()
    {
        bool hadSource;
        lock (_gate)
        {
            hadSource = _sourceUrl != null || _ytDlp != null || _ffplay != null;
            if (!hadSource) return;

            _generation++;
            _startCts?.Cancel();
            _startCts?.Dispose();
            _startCts = null;

            StopCore();
            _sourceUrl = null;
            _title = null;
            _seekSeconds = 0;
            _paused = false;
            _pausedAtSeconds = 0;
            _elapsedBeforeRun = 0;
            _status = null;
        }

        StopKeepAlive();
        RaiseStateChanged();
        RaiseStatus();
    }

    /// <summary>Stops only when <paramref name="url"/> is the track being previewed.</summary>
    public void StopIfUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        bool matches;
        lock (_gate)
            matches = _sourceUrl != null && string.Equals(_sourceUrl, url, StringComparison.OrdinalIgnoreCase);

        if (matches) Stop();
    }

    /// <summary>
    /// Restarts the pipeline <paramref name="seconds"/> into the track; the
    /// only way to move a playhead that lives in a stream.
    /// </summary>
    public void Seek(double seconds)
    {
        string url;
        string? title;
        CancellationToken token;

        lock (_gate)
        {
            if (_disposed || _sourceUrl == null) return;

            url = _sourceUrl;
            title = _title;
            _generation++;
            _startCts?.Cancel();
            _startCts?.Dispose();
            _startCts = new CancellationTokenSource();
            token = _startCts.Token;

            _seekSeconds = Math.Max(0, (int)Math.Floor(seconds));
            _elapsedBeforeRun = 0;
            _runStartTicks = Stopwatch.GetTimestamp();
            _paused = false;
            _pausedAtSeconds = 0;
            _status = $"Jumping to {FormatClock(_seekSeconds)}\u2026";

            StopCore();
        }

        RaiseStateChanged();
        RaiseStatus();
        _ = StartAsync(url, _seekSeconds, token);
        StartKeepAlive();
    }

    /// <summary>
    /// Play/pause. Pausing withholds the pipe (see <see cref="PumpAsync"/>), so
    /// ffplay falls silent within its own small buffer and resumes exactly
    /// where it stopped instead of the stream restarting.
    /// </summary>
    public void SetPlaying(bool playing)
    {
        lock (_gate)
        {
            if (_sourceUrl == null) return;
            if (playing == !_paused) return;

            if (playing)
            {
                _runStartTicks = Stopwatch.GetTimestamp();
                _elapsedBeforeRun = _pausedAtSeconds;
                _paused = false;
            }
            else
            {
                _pausedAtSeconds = PositionSeconds;
                _paused = true;
            }
        }

        RaiseStateChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        StopKeepAlive();
    }

    // ---- pipeline -------------------------------------------------------

    private async Task StartAsync(string url, int seekSeconds, CancellationToken token)
    {
        try
        {
            var ytDlp = ResolveYtDlp();
            var ffplay = ResolveFfplay();

            if (ytDlp == null || ffplay == null)
            {
                var reason = ffplay == null
                    ? "Install ffmpeg (ffplay) to stream previews"
                    : "yt-dlp is not installed, so previews cannot be streamed";

                Log.Warn($"Preview stream unavailable: {reason}");
                Fail(reason, token);
                return;
            }

            token.ThrowIfCancellationRequested();

            var ffplayArgs = new List<string>
            {
                "-nodisp",
                "-autoexit",
                "-loglevel", "quiet",
                // Small buffer: the seek offset is honoured almost immediately
                // and a hiccup does not gape for a second.
                "-fflags", "nobuffer",
                "-probesize", "32768",
                "-analyzeduration", "0",
            };

            var ytDlpArgs = new List<string>
            {
                "-f", "bestaudio/best",
                "--no-playlist",
                "--no-warnings",
                "-o", "-",
            };

            if (seekSeconds > 0)
            {
                // Two-pronged seek: yt-dlp starts the section at the offset and
                // ffplay drops anything the source still sends before it
                // (not every extractor honours --download-sections).
                ytDlpArgs.Add("--download-sections");
                ytDlpArgs.Add($"*{FormatSection(seekSeconds)}-");
                ffplayArgs.Add("-ss");
                ffplayArgs.Add(seekSeconds.ToString(CultureInfo.InvariantCulture));
            }

            ffplayArgs.Add("-i");
            ffplayArgs.Add("pipe:0");

            AppendCookieArgs(ytDlpArgs);
            ytDlpArgs.Add(url);

            var ytInfo = new ProcessStartInfo(ytDlp)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ToolBootstrap.ToolsDir,
            };
            foreach (var argument in ytDlpArgs) ytInfo.ArgumentList.Add(argument);

            // ffplay reads the pipe on stdin; its stdout/stderr are drained too
            // because 'quiet' still emits a few lines on failure.
            var ffInfo = new ProcessStartInfo(ffplay)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ffplay) ?? ToolBootstrap.ToolsDir,
            };
            foreach (var argument in ffplayArgs) ffInfo.ArgumentList.Add(argument);

            Process ytProcess;
            Process ffProcess;

            lock (_gate)
            {
                if (token.IsCancellationRequested || !IsCurrentStart(token)) return;

                ytProcess = Process.Start(ytInfo)
                    ?? throw new InvalidOperationException("yt-dlp could not be started");

                try
                {
                    ffProcess = Process.Start(ffInfo)
                        ?? throw new InvalidOperationException("ffplay could not be started");
                }
                catch
                {
                    KillTree(ytProcess);
                    throw;
                }

                _ytDlp = ytProcess;
                _ffplay = ffProcess;
                _runStartTicks = Stopwatch.GetTimestamp();
            }

            Log.Info($"Preview stream started{(seekSeconds > 0 ? $" from {FormatClock(seekSeconds)}" : "")}");
            Log.Debug($"Preview pipeline: {Path.GetFileName(ytDlp)} {'\u2192'} {Path.GetFileName(ffplay)}");

            var pump = PumpAsync(
                ytProcess.StandardOutput.BaseStream,
                ffProcess.StandardInput.BaseStream,
                () => IsPaused,
                token);

            // The pump IS yt-dlp's stdout drain - a second reader there would
            // steal bytes from the pipe and corrupt the audio.
            _ = DrainAsync(ytProcess.StandardError, "yt-dlp");
            _ = DrainAsync(ffProcess.StandardOutput, "ffplay");
            _ = DrainAsync(ffProcess.StandardError, "ffplay");

            _ = WatchAsync(ytProcess, ffProcess, token);

            if (seekSeconds > 0) _ = ProbeSectionAsync(url, token);

            var first = await Task.WhenAny(pump, Task.Delay(StartDelayMs, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (ReferenceEquals(first, pump) && pump.IsFaulted)
                throw pump.Exception!.GetBaseException();

            lock (_gate)
            {
                if (token.IsCancellationRequested || _sourceUrl == null) return;
                if (_status == null || _status.StartsWith("Buffering", StringComparison.Ordinal))
                    _status = null;
            }

            RaiseStateChanged();
            RaiseStatus();
        }
        catch (OperationCanceledException)
        {
            // A newer Play/Stop/Seek owns the pipeline and already cleaned up.
        }
        catch (Exception ex)
        {
            Log.Error("Preview stream failed to start", ex);
            Fail(ex.Message, token);
        }
    }

    /// <summary>
    /// yt-dlp's stdout straight into ffplay's stdin, 32 KB at a time: nothing
    /// is retained in memory beyond that buffer and nothing touches the disk.
    /// While paused the write side simply waits, so the pipe backs up into
    /// yt-dlp's buffer and ffplay stops consuming.
    /// </summary>
    private static async Task PumpAsync(
        Stream from, Stream to, Func<bool> isPaused, CancellationToken token)
    {
        var buffer = new byte[PumpBufferSize];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;

                while (isPaused() && !token.IsCancellationRequested)
                    await Task.Delay(80, token).ConfigureAwait(false);

                await to.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                await to.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Either end closing mid-track (stop, switch, source hiccup) is normal.
        }
    }

    /// <summary>Background line reader so a full pipe can never wedge either process.</summary>
    private static async Task DrainAsync(StreamReader reader, string tool)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0) Log.Debug($"{tool}: {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Notices the pipeline dying and drops the play state exactly once.</summary>
    private async Task WatchAsync(Process ytProcess, Process ffProcess, CancellationToken token)
    {
        try
        {
            var exited = await Task.WhenAny(
                ytProcess.WaitForExitAsync(token),
                ffProcess.WaitForExitAsync(token)).ConfigureAwait(false);

            if (exited.IsCanceled || token.IsCancellationRequested) return;

            var ytCode = SafeExitCode(ytProcess);
            var ffCode = SafeExitCode(ffProcess);

            lock (_gate)
            {
                // A seek or switch already moved on: this pipeline is history.
                if (token.IsCancellationRequested || !ReferenceEquals(_ytDlp, ytProcess)) return;

                Log.Debug($"Preview stream ended: yt-dlp={Describe(ytCode)}, ffplay={Describe(ffCode)}");

                StopCore();
                _sourceUrl = null;
                _title = null;
                _seekSeconds = 0;
                _paused = false;
                _pausedAtSeconds = 0;
                _elapsedBeforeRun = 0;
            }

            StopKeepAlive();
            RaiseStateChanged();
            RaiseStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview stream watcher stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// Confirms that this source accepts <c>--download-sections</c>; when it
    /// does not, the seek silently restarts the pair from the top and the
    /// status line says so instead of pretending the jump worked.
    /// </summary>
    private async Task ProbeSectionAsync(string url, CancellationToken token)
    {
        var ytDlp = ResolveYtDlp();
        if (ytDlp == null) return;

        var info = new ProcessStartInfo(ytDlp)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "--no-playlist", "--no-warnings", "--simulate",
                     "--download-sections", "*0:00:01-", "--print", "%(id)s", url,
                 })
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var probe = Process.Start(info);
            if (probe == null) return;

            var stdout = probe.StandardOutput.ReadToEndAsync(token);
            var stderr = probe.StandardError.ReadToEndAsync(token);
            await probe.WaitForExitAsync(token).ConfigureAwait(false);

            var error = (await stderr.ConfigureAwait(false)).Trim();
            await stdout.ConfigureAwait(false);

            if (probe.ExitCode == 0 || token.IsCancellationRequested) return;

            Log.Debug($"Preview section probe exit {probe.ExitCode}: {Truncate(error, 300)}");

            lock (_gate)
            {
                if (token.IsCancellationRequested) return;

                _status = error.Contains("section", StringComparison.OrdinalIgnoreCase) ||
                          error.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                    ? "This source refuses seeking \u2014 restarted from the top"
                    : "This source cannot be seeked \u2014 playing from the top";
            }

            RaiseStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"Preview section probe failed: {ex.Message}");
        }
    }

    // ---- process plumbing ----------------------------------------------

    /// <summary>Kills the pipeline. The caller holds <see cref="_gate"/>.</summary>
    /// <summary>
    /// True when <paramref name="token"/> still belongs to the newest start
    /// request. CancellationToken is a struct, so compare the values (a stale
    /// token from a replaced pipeline must not be mistaken for the live one).
    /// </summary>
    private bool IsCurrentStart(CancellationToken token) =>
        _startCts is { } current && current.Token.Equals(token);

    private void StopCore()
    {
        var ytDlp = _ytDlp;
        var ffplay = _ffplay;
        _ytDlp = null;
        _ffplay = null;

        if (ytDlp != null || ffplay != null) Log.Debug("Preview stream stopping");

        // ffplay first: it holds the read end, so its exit makes yt-dlp's next
        // write fail at once instead of waiting for another network segment.
        KillTree(ffplay);
        KillTree(ytDlp);
    }

    private static void KillTree(Process? process)
    {
        if (process == null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);

            // Reap it: without the wait the object keeps a handle and the child
            // can linger as a zombie while the app is busy.
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
        finally
        {
            process.Dispose();
        }
    }

    private void Fail(string message, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested) return;

            StopCore();
            _sourceUrl = null;
            _title = null;
            _seekSeconds = 0;
            _paused = false;
            _pausedAtSeconds = 0;
            _elapsedBeforeRun = 0;
            _status = message;
        }

        StopKeepAlive();
        RaiseStateChanged();
        RaiseStatus();
    }

    // ---- tools ----------------------------------------------------------

    private string? ResolveYtDlp() => Resolve(
        () => ToolBootstrap.ResolveYtDlp(Settings),
        ToolBootstrap.BundledYtDlpPath,
        "yt-dlp.exe",
        "yt-dlp");

    /// <summary>
    /// ffplay next to the configured ffmpeg (a full ffmpeg share build ships
    /// it, a choco install drops it beside ffmpeg.exe), then the app-managed
    /// tools folder, then PATH. A missing ffmpeg does not imply a missing
    /// ffplay, so both are looked for independently.
    /// </summary>
    private string? ResolveFfplay() => Resolve(
        () =>
        {
            var ffmpeg = ToolBootstrap.ResolveFfmpeg(Settings);
            var directory = string.IsNullOrWhiteSpace(ffmpeg) ? null : Path.GetDirectoryName(ffmpeg);
            if (string.IsNullOrWhiteSpace(directory)) return null;

            foreach (var name in new[] { "ffplay.exe", "ffplay" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        },
        Path.Combine(ToolBootstrap.ToolsDir, "ffplay.exe"),
        "ffplay.exe",
        "ffplay");

    /// <summary>
    /// Tries the configured location, then the app-managed copy, then PATH,
    /// remembering a miss so a machine without the tool does not re-stat the
    /// whole PATH on every keystroke-triggered check.
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

    private void AppendCookieArgs(List<string> arguments)
    {
        var browser = Settings.DownloadCookiesBrowser?.Trim().ToLowerInvariant();
        if (browser is "chrome" or "edge" or "firefox")
        {
            arguments.Add("--cookies-from-browser");
            arguments.Add(browser);
        }
    }

    // ---- notification ---------------------------------------------------

    /// <summary>
    /// A 250 ms tick keeps the strip's clock moving between transport events.
    /// The timer only lives while something is previewed.
    /// </summary>
    private void StartKeepAlive()
    {
        var app = Application.Current;
        if (app == null) return;

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(StartKeepAlive));
            return;
        }

        if (_keepAlive != null) return;

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += OnKeepAliveTick;
        _keepAlive = timer;
        timer.Start();
    }

    private void OnKeepAliveTick(object? sender, EventArgs e)
    {
        bool alive;
        lock (_gate) alive = _sourceUrl != null;

        if (alive)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        StopKeepAlive();
    }

    private void StopKeepAlive()
    {
        var timer = _keepAlive;
        if (timer == null) return;

        _keepAlive = null;
        var app = Application.Current;

        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                timer.Stop();
                timer.Tick -= OnKeepAliveTick;
            }));
            return;
        }

        timer.Stop();
        timer.Tick -= OnKeepAliveTick;
    }

    private void RaiseStateChanged()
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => StateChanged?.Invoke(this, EventArgs.Empty)));
            return;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseStatus()
    {
        var app = Application.Current;
        var message = Status;

        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(this, message ?? "")));
            return;
        }

        StatusChanged?.Invoke(this, message ?? "");
    }

    // ---- formatting -----------------------------------------------------

    /// <summary>h:mm:ss — the shape <c>--download-sections</c> expects.</summary>
    private static string FormatSection(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string FormatClock(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string Describe(int? code) =>
        code?.ToString(CultureInfo.InvariantCulture) ?? "running";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\u2026";
}
