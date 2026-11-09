using JockeyBox.Settings;

namespace JockeyBox.Playback;

/// <summary>
/// The Spotify / YouTube preview strips and the libVLC audition player, in one
/// type: a <see cref="PreviewStreamPlayer"/> is what the tabs talk to, and this
/// adapter is the <see cref="ViewModels.IPreviewTransport"/> the strips read
/// their clock, slider and volume from.
///
/// Unlike the old pipe, the player behind this knows its length: a direct
/// googlevideo URL is seekable, so <see cref="DurationSeconds"/> is real and
/// <see cref="Seek"/> moves the decoder.
///
/// A preview is shared by both tabs: starting one in a tab silences the other.
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
    /// the download pipeline uses, so an explicit yt-dlp path in the app's
    /// settings is honoured without the view models knowing about it.
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

    /// <summary>Real length of the stream or fallback file; 0 while unknown.</summary>
    public double DurationSeconds => _player.DurationSeconds;

    public bool IsPlaying => _player.IsPlaying;

    public bool IsAvailable => _player.CurrentUrl != null;

    /// <summary>True when yt-dlp and libVLC were found, i.e. previews can work at all.</summary>
    public bool CanStream => _player.IsAvailable;

    /// <summary>Status line text: resolving, buffering, download progress or a failure.</summary>
    public string? Status => _player.Status;

    /// <summary>"stream" (direct URL) or "download" (temp-file fallback).</summary>
    public string? Mode => _player.Mode;

    public bool IsLoading => _player.IsLoading;

    public double Volume => _player.Volume;

    public bool IsMuted => _player.IsMuted;

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

    public void Seek(double seconds) => _player.Seek(seconds);

    public void SetPlaying(bool playing) => _player.SetPlaying(playing);

    public void SetVolume(double level) => _player.SetVolume(level);

    public void SetMuted(bool muted) => _player.SetMuted(muted);

    public void Dispose() => _player.Dispose();
}
