using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JockeyBox.ViewModels;

/// <summary>
/// Transport accessor the Spotify / YouTube tabs need for preview auditioning.
/// The integration lane implements this on top of the main deck (MainViewModel
/// owns the MediaPlayer) and hands it to both tab view models; the tabs never
/// touch MainViewModel directly.
///
/// Contract:
///  - every member is UI-thread safe to read;
///  - <see cref="StateChanged"/> is raised (any thread) whenever position,
///    duration or play state changes, so the strip can refresh;
///  - <see cref="Seek"/> clamps to [0, duration] and, when the deck is idle or
///    paused, starts playback from the sought position — that is the whole
///    point of auditioning a spot before downloading;
///  - <see cref="IsAvailable"/> is false until something is loaded into the
///    preview deck, which disables the slider instead of faking a position.
/// </summary>
public interface IPreviewTransport
{
    double PositionSeconds { get; }

    double DurationSeconds { get; }

    bool IsPlaying { get; }

    /// <summary>False when no media is loaded — the strip shows its hint instead.</summary>
    bool IsAvailable { get; }

    event EventHandler? StateChanged;

    void Seek(double seconds);

    void SetPlaying(bool playing);

    // ---- preview-only extras -------------------------------------------
    // Defaults keep the main-deck transport (DeckPreviewTransport) working
    // unchanged; the streaming transport overrides all of them.

    /// <summary>Output level, 0..1.</summary>
    double Volume => 1;

    bool IsMuted => false;

    /// <summary>True between the click and the first audio frame.</summary>
    bool IsLoading => false;

    /// <summary>Short line the strip shows while loading or after a failure.</summary>
    string? Status => null;

    /// <summary>"stream" or "download" when a temp-file fallback is in use.</summary>
    string? Mode => null;

    void SetVolume(double level)
    {
    }

    void SetMuted(bool muted)
    {
    }
}

/// <summary>Stand-in used until the integration lane wires the real transport.</summary>
public sealed class NullPreviewTransport : IPreviewTransport
{
    public double PositionSeconds => 0;

    public double DurationSeconds => 0;

    public bool IsPlaying => false;

    public bool IsAvailable => false;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public void Seek(double seconds)
    {
    }

    public void SetPlaying(bool playing)
    {
    }
}

/// <summary>
/// The little strip under the results list: play/pause, position, seek slider,
/// duration. Purely a view over <see cref="IPreviewTransport"/>.
/// </summary>
public sealed partial class PreviewBarViewModel : ObservableObject
{
    private readonly IPreviewTransport _transport;

    /// <summary>True while the user is dragging the thumb; transport updates must not fight it.</summary>
    private bool _dragging;

    public PreviewBarViewModel(IPreviewTransport transport)
    {
        _transport = transport;
        transport.StateChanged += (_, _) => RefreshFromTransport();
        RefreshFromTransport();
    }

    /// <summary>Position shown in the strip, in seconds.</summary>
    [ObservableProperty]
    private double _positionSeconds;

    /// <summary>Length of whatever the preview deck holds, in seconds (0 when idle).</summary>
    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>Volume slider position, 0..100 (libVLC's scale).</summary>
    [ObservableProperty]
    private double _volume = 80;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Resolving / buffering / download progress / failure text.</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>Set only while a temp-file fallback is playing.</summary>
    [ObservableProperty]
    private string? _modeText;

    /// <summary>
    /// Name of what the strip is playing, when the host has one to give. The tabs
    /// leave it null, because the list right above them already says which row
    /// the preview belongs to; the search window names the upload here.
    /// </summary>
    [ObservableProperty]
    private string? _title;

    /// <summary>Guards the volume feedback loop between the slider and the transport.</summary>
    private bool _syncingVolume;

    public bool IsSeekable => IsAvailable && DurationSeconds > 0;

    /// <summary>
    /// True for a live stream: something is previewed but its length is not
    /// known, so the strip shows a running clock and no slider. This is the
    /// streaming preview path - nothing was downloaded to learn a duration
    /// from.
    /// </summary>
    public bool IsStreaming => IsAvailable && DurationSeconds <= 0;

    public string PositionText => Format(PositionSeconds);

    /// <summary>No fake total: an unknown length stays "--:--" and the clock above carries the progress.</summary>
    public string DurationText => IsSeekable ? Format(DurationSeconds) : "--:--";

    public string PlayGlyph => IsLoading ? "\u2026" : IsPlaying ? "\u23F8" : "\u25B6";

    public string PlayPauseHint => IsLoading
        ? "Resolving the preview\u2026"
        : IsPlaying
            ? "Pause the preview"
            : "Play the preview";

    /// <summary>True when the middle of the strip shows a note instead of the slider.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    /// <summary>
    /// The slider itself. Gated on the media and nothing else: there is nothing
    /// to put on a track until something is loaded, and that is the only state
    /// that has no bar. A length is not the gate - while it is unknown the track
    /// sits at Maximum 0 and the template's Maximum trigger drops the fill, so
    /// the strip shows an empty groove and a running clock instead of a
    /// full-width "played" claim. Status text must not hide the bar either:
    /// "Buffering 100%..." arrives while the length and the position are already
    /// known, and the strip gives the note and the bar a column each.
    /// </summary>
    public bool ShowSeekBar => IsAvailable;

    public bool HasModeBadge => !string.IsNullOrWhiteSpace(ModeText);

    public string ModeHint => "Fallback: the direct stream was refused, so this preview is a temp-file download";

    public string MuteGlyph => IsMuted ? "\U0001F507" : "\U0001F50A";

    public string MuteHint => IsMuted ? "Unmute the preview" : "Mute the preview";

    public string VolumeHint => "Preview volume";

    public string SeekHint => IsSeekable
        ? "Click or drag to audition a position before downloading"
        : IsStreaming
            ? "Preview is still resolving \u2014 the slider unlocks once the length is known"
            : "Preview something first to seek";

    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(PositionText));

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(IsSeekable));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(SeekHint));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayGlyph));
        OnPropertyChanged(nameof(PlayPauseHint));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayGlyph));
        OnPropertyChanged(nameof(PlayPauseHint));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MuteGlyph));
        OnPropertyChanged(nameof(MuteHint));
    }

    partial void OnStatusTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(ShowSeekBar));
    }

    partial void OnModeTextChanged(string? value) => OnPropertyChanged(nameof(HasModeBadge));

    /// <summary>Slider move: hand the level to the transport.</summary>
    partial void OnVolumeChanged(double value)
    {
        if (_syncingVolume) return;
        _transport.SetVolume(value / 100.0);
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (!IsAvailable) return;
        _transport.SetMuted(!IsMuted);
        IsMuted = !IsMuted;
    }

    partial void OnIsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSeekable));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(SeekHint));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ShowSeekBar));
    }

    /// <summary>Slider pressed: freeze the thumb against incoming transport ticks.</summary>
    public void BeginSeek()
    {
        if (!IsSeekable) return;
        _dragging = true;
    }

    /// <summary>
    /// Slider released: jump the deck to the chosen spot and keep playing from
    /// there. Releasing anywhere on the track is a click (IsMoveToPointEnabled).
    /// </summary>
    public void EndSeek(double seconds)
    {
        if (!IsSeekable)
        {
            _dragging = false;
            return;
        }
        // Mouse-up and DragCompleted both report; only the first one commits.
        if (!_dragging) return;

        _dragging = false;
        var target = Math.Clamp(seconds, 0, DurationSeconds);
        _transport.Seek(target);
        _transport.SetPlaying(true);
        PositionSeconds = target;
        IsPlaying = true;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!IsAvailable) return;

        if (IsPlaying)
        {
            _transport.SetPlaying(false);
            IsPlaying = false;
            return;
        }

        // At the end: start over instead of playing a finished track.
        if (DurationSeconds > 0 && PositionSeconds >= DurationSeconds - 0.5)
        {
            _transport.Seek(0);
            PositionSeconds = 0;
        }

        _transport.SetPlaying(true);
        IsPlaying = true;
    }

    private void RefreshFromTransport()
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(RefreshFromTransport));
            return;
        }

        IsAvailable = _transport.IsAvailable;
        DurationSeconds = _transport.DurationSeconds;
        IsPlaying = _transport.IsPlaying;
        if (!_dragging) PositionSeconds = _transport.PositionSeconds;

        IsLoading = _transport.IsLoading;
        StatusText = _transport.Status;
        ModeText = _transport.Mode == "download" ? "DL" : null;

        // Do not fight the thumb while the user is moving the volume slider.
        if (!_syncingVolume && Math.Abs(Volume - _transport.Volume * 100) >= 1)
        {
            _syncingVolume = true;
            try
            {
                Volume = Math.Round(_transport.Volume * 100);
            }
            finally
            {
                _syncingVolume = false;
            }
        }

        IsMuted = _transport.IsMuted;
    }

    private static string Format(double seconds)
    {
        if (seconds <= 0) return "0:00";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
