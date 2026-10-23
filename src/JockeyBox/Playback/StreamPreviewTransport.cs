using JockeyBox.Settings;

namespace JockeyBox.Playback;

/// <summary>
/// The Spotify / YouTube preview strips and the streaming audition player, in
/// one type: a <see cref="PreviewStreamPlayer"/> is what the tabs talk to, and
/// this adapter is the <see cref="ViewModels.IPreviewTransport"/> the strips
/// read their clock from.
///
/// It exists separately from the deck transport because a stream has no
/// seekable timeline: <see cref="DurationSeconds"/> stays 0 (the bar shows the
/// elapsed clock and leaves the slider alone), and <see cref="Seek"/> means
/// "restart the pipeline at that offset", not "move a decoder".
///
/// A live stream is shared by both tabs, matching the old deck transport:
/// starting a preview in one tab silences the other.
/// </summary>
public sealed class StreamPreviewTransport : ViewModels.IPreviewTransport, IDisposable
{
    private readonly PreviewStreamPlayer _player;

    public StreamPreviewTransport(PreviewStreamPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _player.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static readonly object SharedGate = new();
    private static StreamPreviewTransport? _shared;

    /// <summary>
    /// The instance the tabs are wired with. Settings come from the same store
    /// the download pipeline uses, so an explicit yt-dlp / ffmpeg path in the
    /// app's settings is honoured without the view models knowing about it.
    ///
    /// It is one instance for the whole app even before MainViewModel wires it:
    /// a second player would mean two streams playing at once, and the preview
    /// contract is one at a time (starting one stops the other).
    /// </summary>
    public static StreamPreviewTransport CreateDefault()
    {
        lock (SharedGate)
        {
            return _shared ??= new StreamPreviewTransport(new PreviewStreamPlayer(AppSettings.Load()));
        }
    }

    public double PositionSeconds => _player.PositionSeconds;

    /// <summary>A stream has no known length until it is downloaded: 0, not a guess.</summary>
    public double DurationSeconds => 0;

    public bool IsPlaying => _player.IsPlaying && !_player.IsPaused;

    public bool IsAvailable => _player.CurrentUrl != null;

    /// <summary>True when both yt-dlp and ffplay were found, i.e. streaming is possible at all.</summary>
    public bool CanStream => _player.IsAvailable;

    /// <summary>Status line text for the current stream (missing tool, seek fallback, …).</summary>
    public string? Status => _player.Status;

    public string UnavailableText => _player.UnavailableReason;

    public string? CurrentUrl => _player.CurrentUrl;

    public event EventHandler? StateChanged;

    /// <summary>Raised with the note the tab's status line should show.</summary>
    public event EventHandler<string>? StatusChanged
    {
        add => _player.StatusChanged += value;
        remove => _player.StatusChanged -= value;
    }

    public void Play(string url, string? title = null) => _player.Play(url, title);

    public void Stop() => _player.Stop();

    public void StopIfUrl(string? url) => _player.StopIfUrl(url);

    /// <summary>
    /// Restarts the pipe at <paramref name="seconds"/>. The bar keeps the
    /// slider disabled for streams, so this is reached programmatically or
    /// once a length is known.
    /// </summary>
    public void Seek(double seconds) => _player.Seek(seconds);

    public void SetPlaying(bool playing) => _player.SetPlaying(playing);

    public void Dispose() => _player.Dispose();
}
