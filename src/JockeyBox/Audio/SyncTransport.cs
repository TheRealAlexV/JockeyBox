using System.Diagnostics;

namespace JockeyBox.Audio;

public enum TransportState { Stopped, Playing, Paused }

/// <summary>
/// Unified transport controlling both decks against a single software master
/// clock. The master clock (base position + stopwatch) is the source of truth
/// for the seek bar and lyrics; decks are primed sample-accurately against it.
///
/// Every play/pause/seek/delay-change is a hard resync point: both decks are
/// re-primed against the master position and started back-to-back, so WASAPI
/// clock drift can never accumulate across transport operations.
/// </summary>
public class SyncTransport : IDisposable
{
    private readonly Stopwatch _clock = new();
    private TimeSpan _basePosition = TimeSpan.Zero;

    public SyncTransport(AudioDeck deckA, AudioDeck deckB)
    {
        DeckA = deckA;
        DeckB = deckB;
    }

    public AudioDeck DeckA { get; }
    public AudioDeck DeckB { get; }

    public TransportState State { get; private set; } = TransportState.Stopped;

    public event EventHandler? StateChanged;

    /// <summary>Master playback position (drives seek bar + lyrics).</summary>
    public TimeSpan Position
    {
        get
        {
            var pos = State == TransportState.Playing ? _basePosition + _clock.Elapsed : _basePosition;
            var dur = Duration;
            return pos > dur ? dur : pos;
        }
    }

    /// <summary>Longest deck duration, extended by any positive deck delay.</summary>
    public TimeSpan Duration
    {
        get
        {
            var a = DeckA.IsLoaded ? DeckA.Duration + TimeSpan.FromMilliseconds(Math.Max(0, DeckA.DelayMs)) : TimeSpan.Zero;
            var b = DeckB.IsLoaded ? DeckB.Duration + TimeSpan.FromMilliseconds(Math.Max(0, DeckB.DelayMs)) : TimeSpan.Zero;
            return a > b ? a : b;
        }
    }

    public bool AnyLoaded => DeckA.IsLoaded || DeckB.IsLoaded;

    /// <summary>True once the master position has run past every deck's audio.</summary>
    public bool ReachedEnd => State == TransportState.Playing && Position >= Duration && Duration > TimeSpan.Zero;

    public void Play()
    {
        if (!AnyLoaded || State == TransportState.Playing) return;
        StartBothAt(_basePosition);
    }

    public void Pause()
    {
        if (State != TransportState.Playing) return;
        _basePosition = Position;          // freeze the clock
        _clock.Reset();
        DeckA.StopAndDisposeOutput();
        DeckB.StopAndDisposeOutput();
        SetState(TransportState.Paused);
    }

    public void TogglePlayPause()
    {
        if (State == TransportState.Playing) Pause();
        else Play();
    }

    public void Stop()
    {
        _clock.Reset();
        _basePosition = TimeSpan.Zero;
        DeckA.StopAndDisposeOutput();
        DeckB.StopAndDisposeOutput();
        SetState(TransportState.Stopped);
    }

    /// <summary>Seek both decks to an absolute master position.</summary>
    public void Seek(TimeSpan position)
    {
        if (!AnyLoaded) return;
        position = Clamp(position);

        if (State == TransportState.Playing)
        {
            StartBothAt(position);         // re-prime + restart = perfect resync
        }
        else
        {
            _basePosition = position;
            SetState(State);               // notify position observers
        }
    }

    public void SkipForward(double seconds = 10) => Seek(Position + TimeSpan.FromSeconds(seconds));
    public void SkipBack(double seconds = 10) => Seek(Position - TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Called after a deck's DelayMs or DeviceId changes. While playing, both
    /// decks restart at the current position so the new routing/offset applies
    /// in perfect sync; otherwise nothing to do (next Prime picks it up).
    /// </summary>
    public void ApplyDeckSettingsChange()
    {
        if (State == TransportState.Playing)
            StartBothAt(Position);
    }

    /// <summary>Track load/unload: stop outputs and reset the clock to 0.</summary>
    public void OnTracksChanged()
    {
        Stop();
    }

    private void StartBothAt(TimeSpan position)
    {
        position = Clamp(position);
        DeckA.StopAndDisposeOutput();
        DeckB.StopAndDisposeOutput();

        // Both pipelines are fully built and positioned while stopped, so the
        // two Start() calls below execute back-to-back. Residual device start
        // jitter is single-digit ms; any constant offset is exactly what the
        // per-deck DelayMs setting exists to trim.
        //
        // A deck whose device failed (unplugged mid-set, exclusive-mode theft)
        // is skipped so the surviving deck keeps playing - critical for live use.
        bool aReady = TryPrime(DeckA, position);
        bool bReady = TryPrime(DeckB, position);

        _basePosition = position;
        if (aReady) DeckA.Start();
        if (bReady) DeckB.Start();
        _clock.Restart();
        SetState(TransportState.Playing);
    }

    private static bool TryPrime(AudioDeck deck, TimeSpan position)
    {
        if (!deck.IsLoaded) return false;
        try
        {
            deck.Prime(position);
            return true;
        }
        catch
        {
            deck.StopAndDisposeOutput();
            return false;
        }
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        var d = Duration;
        return t > d ? d : t;
    }

    private void SetState(TransportState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Stop();
        DeckA.Dispose();
        DeckB.Dispose();
    }
}
