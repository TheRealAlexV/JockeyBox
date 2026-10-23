using System.Diagnostics;
using System.Windows.Threading;
using JockeyBox.Playback;

namespace JockeyBox.Audio;

/// <summary>Playback state of one deck (and of its stems).</summary>
public enum TransportState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// Two independent decks with a crossfader between them.
///
/// The old <c>SyncTransport</c> drove one master clock through a karaoke stem on
/// A and the original stem on B. Here each deck owns a full track (primary stem
/// plus its karaoke sub-stem, see <see cref="DeckChannel"/>) and its own
/// playhead; the fader decides what is heard:
///
///   gainA = cos(fader * pi/2), gainB = sin(fader * pi/2)
///
/// (equal power, so the sum stays around 1 and there is no dip in the middle.)
///
/// <see cref="ActiveDeck"/> is the deck the shared transport surface follows:
/// its playhead drives <see cref="Position"/>/<see cref="Duration"/>/
/// <see cref="State"/>/<see cref="ReachedEnd"/> - that is what the seek bar and
/// the lyrics clock read. The other deck can keep playing; that is the mix.
/// </summary>
public sealed class DualDeckTransport : IDisposable
{
    /// <summary>Default hand-off time when the queue auto-advances.</summary>
    public static readonly TimeSpan DefaultCrossfade = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _crossfadeTimer;
    private readonly Stopwatch _crossfadeClock = new();

    private PlaybackDeck _activeDeck = PlaybackDeck.A;
    private double _crossfader = 0.5;
    private double _fadeFrom;
    private double _fadeTo;
    private TimeSpan _fadeDuration;

    public DualDeckTransport(DeckChannel deckA, DeckChannel deckB)
    {
        DeckA = deckA;
        DeckB = deckB;

        DeckA.StateChanged += OnDeckStateChanged;
        DeckB.StateChanged += OnDeckStateChanged;

        // The crossfade runs on the UI thread, where this transport is used.
        _crossfadeTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _crossfadeTimer.Tick += OnCrossfadeTick;

        SetCrossfader(_crossfader);
    }

    /// <summary>Raised when the active deck's state changes (play, pause, stop, load, activate).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when <see cref="ActiveDeck"/> changes.</summary>
    public event EventHandler? ActiveDeckChanged;

    public DeckChannel DeckA { get; }
    public DeckChannel DeckB { get; }

    /// <summary>Deck whose playhead drives the shared transport surface.</summary>
    public PlaybackDeck ActiveDeck
    {
        get => _activeDeck;
        set
        {
            if (_activeDeck == value) return;
            _activeDeck = value;
            ActiveDeckChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public DeckChannel Active => Channel(ActiveDeck);

    public TransportState State => Active.State;
    public TimeSpan Position => Active.DisplayPosition;
    public TimeSpan Duration => Active.Duration;
    public bool AnyLoaded => DeckA.IsLoaded || DeckB.IsLoaded;
    public bool AnyPlaying =>
        DeckA.State == TransportState.Playing || DeckB.State == TransportState.Playing;

    /// <summary>True when the active deck reaches the end of its media.</summary>
    public bool ReachedEnd => Active.ReachedEnd;

    /// <summary>0 = deck A, 1 = deck B; 0.5 = equal-power centre.</summary>
    public double Crossfader => _crossfader;

    // ---- shared transport (acts on the active deck) ----------------------

    public void Play() => PlayDeck(ActiveDeck);

    public void Pause() => PauseDeck(ActiveDeck);

    public void TogglePlayPause() => TogglePlayPause(ActiveDeck);

    public void Stop() => StopDeck(ActiveDeck);

    public void Seek(TimeSpan position) => SeekDeck(ActiveDeck, position);

    public void SkipForward(double seconds = 10) => SeekDeck(ActiveDeck, Position + TimeSpan.FromSeconds(seconds));

    public void SkipBack(double seconds = 10) => SeekDeck(ActiveDeck, Position - TimeSpan.FromSeconds(seconds));

    /// <summary>Kept for callers that announce a media swap; decks stop their own outputs.</summary>
    public void OnTracksChanged()
    {
        // Loads are per deck now: <see cref="LoadOnDeck"/> stops that deck's
        // stems and leaves the other deck's audio untouched.
    }

    // ---- per-deck operations --------------------------------------------

    public DeckChannel Channel(PlaybackDeck deck) => deck == PlaybackDeck.A ? DeckA : DeckB;

    public bool IsDeckLoaded(PlaybackDeck deck) => Channel(deck).IsLoaded;

    public bool IsDeckPlaying(PlaybackDeck deck) => Channel(deck).State == TransportState.Playing;

    /// <summary>Load a whole track (primary + optional karaoke sub-stem) onto one deck.</summary>
    public void LoadOnDeck(PlaybackDeck deck, string? primaryPath, string? karaokePath)
    {
        Channel(deck).Load(primaryPath, karaokePath);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Play a deck. If the other deck is not currently playing, automatically
    /// move the crossfader so the newly started deck is audible (DJ-style cue).</summary>
    public void PlayDeck(PlaybackDeck deck, bool autoCue = true)
    {
        Channel(deck).Play();
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (autoCue)
        {
            var other = deck == PlaybackDeck.A ? PlaybackDeck.B : PlaybackDeck.A;
            if (!IsDeckPlaying(other) && _crossfadeTimer.IsEnabled == false)
            {
                var side = deck == PlaybackDeck.A ? 0.0 : 1.0;
                if (Math.Abs(_crossfader - side) >= 0.01)
                {
                    // MainViewModel mirrors Crossfader from here on its 30 Hz tick.
                    SetCrossfader(side);
                }
            }
        }
    }

    public void PauseDeck(PlaybackDeck deck)
    {
        Channel(deck).Pause();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlayPause(PlaybackDeck deck)
    {
        if (Channel(deck).State == TransportState.Playing) PauseDeck(deck);
        else PlayDeck(deck);
    }

    public void StopDeck(PlaybackDeck deck)
    {
        Channel(deck).Stop();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SeekDeck(PlaybackDeck deck, TimeSpan position)
    {
        Channel(deck).Seek(position);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-prime a deck after a device/offset change.</summary>
    public void ApplyDeckSettingsChange(PlaybackDeck deck) => Channel(deck).ApplySettings();

    public void ActivateDeck(PlaybackDeck deck) => ActiveDeck = deck;

    // ---- crossfader ------------------------------------------------------

    public void SetCrossfader(double value)
    {
        // A hand on the fader wins over a running automation ramp.
        _crossfadeTimer.Stop();
        _crossfadeClock.Reset();
        ApplyFader(value);
    }

    private void ApplyFader(double value)
    {
        _crossfader = Math.Clamp(value, 0, 1);
        ApplyCrossfader();
    }

    /// <summary>
    /// Ramp the fader to <paramref name="deck"/> over <paramref name="duration"/>
    /// (default <see cref="DefaultCrossfade"/>, 2 s). The ramp is linear in fader
    /// position; the gains themselves follow the equal-power curve.
    /// </summary>
    public void CrossfadeToDeck(PlaybackDeck deck) => CrossfadeToDeck(deck, DefaultCrossfade);

    public void CrossfadeToDeck(PlaybackDeck deck, TimeSpan duration)
    {
        _fadeFrom = _crossfader;
        _fadeTo = deck == PlaybackDeck.A ? 0 : 1;
        _fadeDuration = duration;

        if (duration <= TimeSpan.Zero)
        {
            _crossfadeTimer.Stop();
            ApplyFader(_fadeTo);
            return;
        }

        _crossfadeClock.Restart();
        _crossfadeTimer.Start();
    }

    private void OnCrossfadeTick(object? sender, EventArgs e)
    {
        var progress = _crossfadeClock.Elapsed.TotalMilliseconds / _fadeDuration.TotalMilliseconds;
        if (progress >= 1)
        {
            _crossfadeTimer.Stop();
            _crossfadeClock.Reset();
            ApplyFader(_fadeTo);
            return;
        }

        ApplyFader(_fadeFrom + (_fadeTo - _fadeFrom) * progress);
    }

    private void ApplyCrossfader()
    {
        var turn = _crossfader * (Math.PI / 2);
        DeckA.FaderGain = Math.Cos(turn);
        DeckB.FaderGain = Math.Sin(turn);
    }

    private void OnDeckStateChanged(object? sender, EventArgs e) =>
        StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _crossfadeTimer.Stop();
        _crossfadeTimer.Tick -= OnCrossfadeTick;

        DeckA.StateChanged -= OnDeckStateChanged;
        DeckB.StateChanged -= OnDeckStateChanged;
        DeckA.Dispose();
        DeckB.Dispose();
    }
}
