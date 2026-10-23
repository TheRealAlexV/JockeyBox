using System.Windows;
using JockeyBox.Audio;

namespace JockeyBox.ViewModels;

/// <summary>
/// The Spotify / YouTube preview strips driven by the main deck pair.
///
/// This is the integration the <see cref="IPreviewTransport"/> contract was
/// written for: the tabs audition whatever the transport holds - a stream the
/// resolver just cached - without knowing anything about <see cref="MainViewModel"/>.
///
/// Contract details:
///  - position / duration / play state are read straight off
///    <see cref="DualDeckTransport"/>, so they always match what the active deck is
///    actually doing (the same playhead the 30 Hz UI tick reads for the lyrics);
///  - <see cref="StateChanged"/> is raised by <see cref="NotifyTick"/> from that
///    existing tick, so the strip follows the clock without a second timer;
///  - <see cref="Seek"/> clamps to [0, duration] and starts a stopped or paused
///    transport, because auditioning a position means hearing it;
///  - <see cref="IsAvailable"/> is false while no deck holds media, which is
///    what keeps the strip's slider disabled instead of faking a position.
/// </summary>
public sealed class DeckPreviewTransport : IPreviewTransport
{
    private readonly DualDeckTransport _transport;

    public DeckPreviewTransport(DualDeckTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public double PositionSeconds => _transport.Position.TotalSeconds;

    public double DurationSeconds => _transport.Duration.TotalSeconds;

    public bool IsPlaying => _transport.State == TransportState.Playing;

    public bool IsAvailable => _transport.AnyLoaded;

    public event EventHandler? StateChanged;

    /// <summary>
    /// Called from <c>MainViewModel.OnUiTick</c> (the one existing 30 Hz timer):
    /// the strip re-reads the transport on every frame it is visible.
    /// </summary>
    public void NotifyTick() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Jump the active deck (both its stems) to <paramref name="seconds"/>
    /// (clamped to the loaded media) and make sure it is audible: a stopped or
    /// paused transport is started from the sought position. With nothing loaded
    /// this is a no-op.
    /// </summary>
    public void Seek(double seconds)
    {
        OnUiThread(() =>
        {
            if (!_transport.AnyLoaded) return;

            var duration = _transport.Duration.TotalSeconds;
            var target = Math.Clamp(seconds, 0, Math.Max(0, duration));

            // Seeking the transport moves the active deck (both its stems),
            // which is the same path the main window's seek bar uses.
            _transport.Seek(TimeSpan.FromSeconds(target));
            if (_transport.State != TransportState.Playing) _transport.Play();

            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void SetPlaying(bool playing)
    {
        OnUiThread(() =>
        {
            if (!_transport.AnyLoaded) return;

            if (playing) _transport.Play();
            else _transport.Pause();

            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// The transport is not synchronised, so a call from a background thread is
    /// posted to the dispatcher rather than racing the UI tick.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }
}
