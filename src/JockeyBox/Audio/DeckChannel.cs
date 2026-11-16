using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using JockeyBox.Audio.Graph;
using JockeyBox.Tools;

namespace JockeyBox.Audio;

/// <summary>
/// One DJ deck: the track (primary file) plus, when the track has a karaoke
/// partner, the instrumental that sits inside the same deck.
///
/// <para><b>Why this is a shim now.</b> The 1.x channel owned two
/// <c>AudioDeck</c>s, and each of those opened its own <c>WasapiOut</c>, ran its
/// own playhead off a <see cref="System.Diagnostics.Stopwatch"/> and had to be
/// primed and restarted by hand on every seek, pause and device change. Two
/// devices meant two unsynchronised clocks, so stems drifted and the position
/// readout was a stopwatch guess rather than the audio that was audible.</para>
///
/// <para>In 2.0 the channel is a view over exactly one
/// <see cref="DeckSource"/> inside the shared <see cref="AudioEngine"/>: the
/// render thread owns the single clock, the files are pulled sample-accurately
/// (karaoke offset included) and the crossfader is an engine-side envelope. This
/// type keeps the surface the ViewModels already speak - <see cref="Position"/>,
/// <see cref="State"/>, <see cref="ReachedEnd"/>, <see cref="DelayMs"/> ... - so
/// no ViewModel had to change.</para>
///
/// <see cref="KaraokeGain"/> is the karaoke control:
/// 0 = original only, 1 = karaoke only, 0.5 = both.
/// </summary>
public sealed class DeckChannel : IDisposable
{
    /// <summary>Smallest karaoke offset a user may dial in (ms).</summary>
    public const double MinDelayMs = -5000;

    /// <summary>Largest karaoke offset a user may dial in (ms).</summary>
    public const double MaxDelayMs = 5000;

    /// <summary>Largest per-deck output delay a user may dial in (ms).</summary>
    public const double MaxOutputDelayMs = 2000;

    /// <summary>The engine's deck fader ceiling (matches the VOL slider and the mixer).</summary>
    private const double MaxVolume = 1.2;

    private const string NoOutputFault = "no output configured";

    /// <summary>Lightweight poll so a deck that ends inside the render thread reaches the UI.</summary>
    private readonly DispatcherTimer? _stateTimer;

    private AudioEngine? _engine;
    private DeckSource? _deck;
    private bool _isDeckA;

    /// <summary>Completes once the transport has opened its outputs and started the engine.</summary>
    private Task _ready = Task.CompletedTask;

    private int _loadGeneration;
    private (string? Primary, string? Karaoke, string? Vocals)? _pendingLoad;
    private (string? Primary, string? Karaoke, string? Vocals)? _lastLoad;

    /// <summary>
    /// Guards the load/play handshake. <see cref="Load"/> starts the decode
    /// without waiting and the reader swap is posted to the render thread
    /// afterwards, so a <see cref="Play"/> that arrives in between would run
    /// before the primary exists and be dropped by <see cref="DeckSource.Play"/>.
    /// Whoever sees <see cref="_loadInFlight"/> arms <see cref="_playWhenLoaded"/>
    /// instead, and the load completion starts the deck the moment the swap lands.
    /// </summary>
    private readonly object _playGate = new();
    private bool _loadInFlight;
    private bool _playWhenLoaded;
    private int _outputAvailable;
    private bool _noOutputLogged;
    private int _disposed;

    private string? _fault;
    private string? _deviceId;
    private double _delayMs;
    private double _outputDelayMs;
    private double _volume = 1.0;
    private double _karaokeGain = 0.5;
    private double _tempoRatio = 1.0;

    private DeckSnapshot _snapshot = DeckSnapshot.Empty;

    /// <summary>
    /// Build a deck channel. The engine deck is attached moments later by
    /// <see cref="DualDeckTransport"/>; the ViewModel constructs channels before
    /// the transport exists, so that split is part of the public shape.
    /// </summary>
    public DeckChannel(string name, DeviceService devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        Name = name;

        if (System.Windows.Application.Current == null) return;

        // A deck only changes state on the render thread, so one cheap poll is the
        // reliable way to announce "ended" (and a failed swap) without spinning.
        _stateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _stateTimer.Tick += OnStateTick;
    }

    public string Name { get; }

    public bool IsLoaded => _deck?.TrackPath is not null;

    public bool HasKaraoke => _deck?.HasKaraoke == true;

    /// <summary>True when a vocals-only take is resident on this deck.</summary>
    public bool HasVocals => _deck?.HasVocals == true;

    public string? PrimaryPath => _deck?.TrackPath;

    public string? KaraokePath => _deck?.KaraokePath;

    /// <summary>
    /// Physical endpoint this deck's send goes to (null = none). Kept as a routing
    /// hint only: the transport turns it into an <see cref="AudioRoute"/> and asks
    /// the engine to open the device, so a device change never restarts a playhead.
    /// </summary>
    public string? DeviceId
    {
        get => _deviceId;
        set => _deviceId = value;
    }

    /// <summary>Karaoke offset in milliseconds relative to the deck playhead.</summary>
    public double DelayMs
    {
        get => _delayMs;
        set
        {
            _delayMs = Math.Clamp(value, MinDelayMs, MaxDelayMs);
            _deck?.SetKaraokeSyncOffset(_delayMs);
        }
    }

    /// <summary>
    /// This deck's own output delay in ms (0…2000). It shifts what the audience
    /// hears from this deck alone; the karaoke stem offset above is a different
    /// thing and stays inside the deck.
    /// </summary>
    public double OutputDelayMs
    {
        get => _outputDelayMs;
        set
        {
            _outputDelayMs = Math.Clamp(value, 0, MaxOutputDelayMs);
            _engine?.SetDeckOutputDelayMs(_isDeckA, (int)Math.Round(_outputDelayMs));
        }
    }

    /// <summary>Deck fader (0..1.2, the deck's own volume control).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, MaxVolume);
            ApplyVolume();
        }
    }

    /// <summary>
    /// Karaoke controls: 0 = original only, 1 = karaoke only, 0.5 = both.
    /// </summary>
    public double KaraokeGain
    {
        get => _karaokeGain;
        set
        {
            _karaokeGain = Math.Clamp(value, 0, 1);
            _deck?.SetKaraokeGain((float)_karaokeGain);
        }
    }

    /// <summary>
    /// Choose which resident take is audible (Mix, Original, Karaoke, Vocals) as a
    /// gain ramp in engine frames. All three readers stay open and frame-locked,
    /// so the switch is gapless. Pass-through to <see cref="DeckSource.SetStemMode"/>.
    /// </summary>
    public void SetStemMode(StemKind kind, double rampMs = 0) =>
        _deck?.SetStemMode(kind, rampMs);

    /// <summary>
    /// Equal-power crossfader gain for this side of the mix (0..1). Read-only
    /// mirror of the engine's fader envelope: the transport owns the fader, the
    /// deck only reports what it is being multiplied by right now.
    /// </summary>
    public double FaderGain
    {
        get
        {
            if (_engine == null) return 1.0;
            double fader = _engine.Fader.FaderAt(_engine.FrameIndex);
            return _isDeckA ? Crossfade.GainA(fader) : Crossfade.GainB(fader);
        }
    }

    /// <summary>
    /// Tempo ratio applied by the engine's stretcher (1.0 = original speed).
    /// Position and duration stay in media time, exactly like 1.x.
    /// </summary>
    public double TempoRatio
    {
        get => _tempoRatio;
        set
        {
            _tempoRatio = Math.Clamp(value, 0.5, 2.0);
            _deck?.SetTempo(_tempoRatio);
        }
    }

    /// <summary>Loaded length of the deck (media length of the primary file).</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_deck?.DurationSeconds ?? 0);

    /// <summary>
    /// Position at which the deck is finished: its length, plus the delay tail
    /// when a positively delayed karaoke stem still has audio to render. In deck
    /// (media) time - the tempo ratio is not applied here.
    /// </summary>
    public TimeSpan EndPosition => Duration + TimeSpan.FromMilliseconds(
        HasKaraoke ? Math.Max(0, _delayMs) : 0);

    /// <summary>
    /// What the audience hears: the engine's audible clock (deck position minus
    /// the global output delay), clamped to the deck's end.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            if (_engine == null || _deck == null) return TimeSpan.Zero;

            var pos = TimeSpan.FromSeconds(_engine.AudibleSeconds(_deck));
            if (pos < TimeSpan.Zero) return TimeSpan.Zero;

            var end = EndPosition;
            return pos > end ? end : pos;
        }
    }

    /// <summary>Position shown in the readout/seek bar (never past the media length).</summary>
    public TimeSpan DisplayPosition
    {
        get
        {
            var pos = Position;
            var dur = Duration;
            return pos > dur ? dur : pos;
        }
    }

    /// <summary>
    /// The raw media clock: how much of the file the deck has consumed, with no
    /// stretcher group delay and no bus delay subtracted. Deck sync cues the
    /// slave from this rather than from <see cref="Position"/>, because the
    /// audible clock is deliberately late and moves with the tempo.
    /// </summary>
    public double MediaPositionSeconds => _deck?.MediaPositionSeconds ?? 0;

    /// <summary>
    /// Transport state of the deck. <see cref="TransportState.Failed"/> means the
    /// deck could not be loaded or the engine has no output; <see cref="Error"/>
    /// carries the reason.
    /// </summary>
    public TransportState State
    {
        get
        {
            if (_fault != null) return TransportState.Failed;

            return _deck?.State switch
            {
                DeckSource.DeckState.Playing => TransportState.Playing,
                DeckSource.DeckState.Paused => TransportState.Paused,
                DeckSource.DeckState.Failed => TransportState.Failed,
                // Ended (and Loading/Empty) read as stopped: the track is over, and
                // Play() on an ended deck starts it again from zero.
                _ => TransportState.Stopped,
            };
        }
    }

    /// <summary>Failure reason for <see cref="TransportState.Failed"/> (null when healthy).</summary>
    public string? Error => _fault ?? _deck?.Error;

    /// <summary>
    /// True when the deck has nothing left to play: the engine says the media
    /// ended, or the audible playhead has reached <see cref="EndPosition"/>. A
    /// deck that froze at the end of its file still reports end here, which is
    /// what the UI tick relies on to advance the queue.
    /// </summary>
    public bool ReachedEnd
    {
        get
        {
            var deck = _deck;
            if (deck == null) return false;
            if (deck.State == DeckSource.DeckState.Ended) return true;
            if (deck.State == DeckSource.DeckState.Failed) return false;

            var end = EndPosition;
            if (end <= TimeSpan.Zero) return false;

            return Position >= end;
        }
    }

    /// <summary>Raised whenever the deck's transport state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Load a whole track onto this deck: the primary file plus, when present,
    /// the karaoke partner. Never blocks the caller (the UI thread): the decode
    /// runs on a worker, the swap on the render thread, and a failure lands in
    /// <see cref="StateChanged"/> as <see cref="TransportState.Failed"/>.
    /// </summary>
    public void Load(string? primaryPath, string? karaokePath, string? vocalsPath = null)
    {
        int generation = ++_loadGeneration;
        _fault = null;

        lock (_playGate)
        {
            // A new load supersedes a play request that belonged to the track it
            // replaces: pressing ▶ for the old track must not start the new one.
            _playWhenLoaded = false;
            _loadInFlight = !string.IsNullOrWhiteSpace(primaryPath);
        }

        var deck = _deck;
        if (deck == null)
        {
            // The ViewModel can ask for a track before the transport has bound the
            // engine deck (start-up restore); replay it once Bind() has run.
            _pendingLoad = (primaryPath, karaokePath, vocalsPath);
            RaiseStateChanged();
            return;
        }

        _pendingLoad = null;

        if (string.IsNullOrWhiteSpace(primaryPath))
        {
            _lastLoad = null;
            deck.Unload();
            RaiseStateChanged();
            return;
        }

        // The primary and the karaoke partner are normally different files; the
        // same path would decode the track twice and double it.
        if (string.Equals(karaokePath, primaryPath, StringComparison.OrdinalIgnoreCase))
            karaokePath = null;

        _lastLoad = (primaryPath, karaokePath, vocalsPath);

        _ = LoadCoreAsync(deck, primaryPath, karaokePath, vocalsPath, generation);
        RaiseStateChanged(); // Loading is visible immediately
    }

    /// <summary>
    /// Attach a karaoke partner that appeared <em>after</em> the track was
    /// loaded: Karaoke mode queued the separation while the mix was already
    /// playing, and the stem has now landed.
    ///
    /// <para>The pair is re-opened on a worker (the primary file plus the stem)
    /// and the reader swap happens on the render thread, so the UI thread is
    /// never blocked. The playhead is carried over - the deck resumes at the
    /// audible position it had, still playing or still paused - because a
    /// Karaoke mode flip must not sound like a re-load, let alone a jump. A
    /// failure leaves the reason in <see cref="Error"/> and the caller keeps
    /// playing the original mix; nothing here can strand the deck on a spinner.
    /// </para>
    /// </summary>
    /// <returns>true when the deck ends up splitting the pair (or was already);
    /// false when there is no loaded track, no readable stem, or the pair could
    /// not be re-opened - the deck keeps the original mix in that case.</returns>
    public async Task<bool> AttachKaraokeStemAsync(string? karaokePath)
    {
        if (string.IsNullOrWhiteSpace(karaokePath) || !System.IO.File.Exists(karaokePath))
            return false;

        var deck = _deck;
        if (deck == null)
        {
            // Before the transport is bound: keep the request with the load so
            // Bind() replays it with the stem attached.
            if (_pendingLoad is { } pending)
                _pendingLoad = (pending.Primary, karaokePath, pending.Vocals);
            return _pendingLoad != null;
        }

        if (_lastLoad is not { } last || string.IsNullOrWhiteSpace(last.Primary))
            return false;

        var primary = last.Primary;

        if (string.Equals(karaokePath, primary, StringComparison.OrdinalIgnoreCase))
            return false;

        // Already splitting this track: the engine's KaraokeMode flag is all it needs.
        if (deck.HasKaraoke && string.Equals(deck.KaraokePath, karaokePath, StringComparison.OrdinalIgnoreCase))
            return true;

        // Where the operator hears the deck right now. Read before the swap: the
        // new readers start from exactly this media position.
        double startSeconds = Math.Max(0, DisplayPosition.TotalSeconds);
        bool wasPlaying = State == TransportState.Playing;

        int generation = ++_loadGeneration;
        _fault = null;
        _lastLoad = (primary, karaokePath, last.Vocals);

        lock (_playGate)
        {
            // This pass owns the deck; any armed play belongs to the load it
            // supersedes and is re-applied from the run state below.
            _playWhenLoaded = false;
            _loadInFlight = true;
        }

        return await AttachCoreAsync(deck, primary, karaokePath, startSeconds, wasPlaying, generation)
            .ConfigureAwait(true);
    }

    private async Task<bool> AttachCoreAsync(DeckSource deck, string primary, string karaokePath,
                                             double startSeconds, bool wasPlaying, int generation)
    {
        try
        {
            if (!OutputAvailable)
            {
                Fail(NoOutputFault);
                return false;
            }

            try
            {
                // Re-open the same primary with the stem; the seek lands inside
                // Swap(), so the stem and the original stay sample-aligned.
                await deck.LoadAsync(primary, karaokePath, _delayMs,
                        startSeconds: startSeconds, playWhenReady: false)
                    .ConfigureAwait(false);

                for (int i = 0; i < 200 && deck.State == DeckSource.DeckState.Loading; i++)
                    await Task.Delay(5).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (generation != Volatile.Read(ref _loadGeneration)) return false;

                // The pair could not be re-opened (the files moved mid-set). Put
                // the deck back on the original-only load it would have had, so
                // the mix keeps playing instead of the deck dying on the stem.
                Log.Warn($"{Name}: karaoke stem could not be opened ({ex.Message}) - keeping the original mix");
                _lastLoad = (primary, null, null);
                try
                {
                    await deck.LoadAsync(primary, null, _delayMs,
                            startSeconds: startSeconds, playWhenReady: false)
                        .ConfigureAwait(false);
                    for (int i = 0; i < 200 && deck.State == DeckSource.DeckState.Loading; i++)
                        await Task.Delay(5).ConfigureAwait(false);
                }
                catch (Exception again)
                {
                    if (generation != Volatile.Read(ref _loadGeneration)) return false;
                    Fail(again.Message);
                    return false;
                }

                if (generation != Volatile.Read(ref _loadGeneration)) return false;

                // The original came back cleanly: same clock, no stem.
                ApplyVolume();
                deck.SetTempo(_tempoRatio);
                if (wasPlaying) deck.Play();
                MarshalNotify();
                return false;
            }

            if (generation != Volatile.Read(ref _loadGeneration)) return false;

            ApplyVolume();
            deck.SetTempo(_tempoRatio);
            deck.SetKaraokeGain((float)_karaokeGain);
            deck.SetKaraokeSyncOffset(_delayMs);

            // DeckSource.LoadAsync with playWhenReady:false parks the deck; the
            // playhead was carried in by the seek, so only the run state is left.
            if (wasPlaying) deck.Play();

            MarshalNotify();
            Log.Info($"{Name}: karaoke stem attached at {startSeconds:0.00}s "
                + $"({System.IO.Path.GetFileName(karaokePath)}) - {(wasPlaying ? "playing" : "parked")}");
            return true;
        }
        finally
        {
            CompleteLoad(deck, generation);
        }
    }

    /// <summary>Drop the media; the deck goes quiet on the next render block.</summary>
    public void Unload()
    {
        _loadGeneration++; // an in-flight load must not resurrect the deck
        _pendingLoad = null;
        _lastLoad = null;
        _fault = null;

        lock (_playGate)
        {
            _playWhenLoaded = false;
            _loadInFlight = false;
        }

        _deck?.Unload();
        RaiseStateChanged();
    }

    /// <summary>
    /// Start (or resume) the deck from its current playhead.
    ///
    /// <para>A load in flight (a queue row's ▶ lands here, because the ViewModel
    /// kicks the load off without waiting) is not a reason to ignore the press:
    /// the request is armed and the completion starts the deck as soon as the
    /// reader swap is in. That is the whole point - the caller must not need a
    /// second press.</para>
    /// </summary>
    public void Play()
    {
        if (!EnsureOutput()) return;

        bool deferred;
        lock (_playGate)
        {
            deferred = _loadInFlight;
            if (deferred) _playWhenLoaded = true;
        }

        if (deferred)
        {
            Log.Info($"{Name}: play requested during the load - armed, the reader swap starts the deck");
            RaiseStateChanged(); // "Loading" stays visible until the swap lands
            return;
        }

        if (!IsLoaded) return;

        _deck!.Play(); // Play() on an ended deck rewinds to zero inside DeckSource
        RaiseStateChanged();
    }

    public void Pause()
    {
        if (!EnsureOutput()) return;

        // A press that outran the load is a "start" request; pausing now means the
        // user changed their mind, so the completion must not start the deck.
        lock (_playGate) { _playWhenLoaded = false; }

        if (State != TransportState.Playing) return;

        _deck!.Pause();
        RaiseStateChanged();
    }

    public void Stop()
    {
        if (!EnsureOutput()) return;

        // Same as Pause: a stop while the load is still in flight cancels the
        // armed play; the media lands parked, not running.
        lock (_playGate) { _playWhenLoaded = false; }

        _deck?.Stop();
        RaiseStateChanged();
    }

    /// <summary>Seek the deck; the karaoke stem is moved by the same amount plus its offset.</summary>
    public void Seek(TimeSpan position)
    {
        if (!EnsureOutput()) return;
        if (!IsLoaded) return;

        _deck!.Seek(Clamp(position).TotalSeconds);
        RaiseStateChanged();
    }

    public void SkipForward(double seconds = 10) =>
        Seek(DisplayPosition + TimeSpan.FromSeconds(seconds));

    public void SkipBack(double seconds = 10) =>
        Seek(DisplayPosition - TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Re-apply this deck's live mixer settings after a device/offset change.
    /// Nothing needs re-priming any more: gain, karaoke split, tempo and offset
    /// are engine parameters, so a playing deck keeps its clock.
    /// </summary>
    public void ApplySettings()
    {
        if (_fault == NoOutputFault && OutputAvailable) _fault = null;

        _deck?.SetKaraokeSyncOffset(_delayMs);
        _deck?.SetKaraokeGain((float)_karaokeGain);
        _deck?.SetTempo(_tempoRatio);
        _engine?.SetDeckOutputDelayMs(_isDeckA, (int)Math.Round(_outputDelayMs));
        ApplyVolume();
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- engine binding

    /// <summary>
    /// Attach the engine deck. Called once by <see cref="DualDeckTransport"/>
    /// straight after construction, before any output is opened.
    /// </summary>
    internal void Bind(AudioEngine engine, DeckSource deck, bool isDeckA, Task ready)
    {
        _engine = engine;
        _deck = deck;
        _isDeckA = isDeckA;
        _ready = ready ?? Task.CompletedTask;

        deck.Ended += OnDeckEnded;
        deck.Failed += OnDeckFailed;

        // Recognise a deck the VM restored settings for before the engine existed.
        _deck.SetTempo(_tempoRatio);
        _deck.SetKaraokeGain((float)_karaokeGain);
        _deck.SetKaraokeSyncOffset(_delayMs);
        _engine.SetDeckOutputDelayMs(_isDeckA, (int)Math.Round(_outputDelayMs));
        ApplyVolume();

        _snapshot = Snapshot();
        _stateTimer?.Start();

        if (_pendingLoad is { } pending)
        {
            _pendingLoad = null;

            // A ▶ that landed before the deck was bound is still a play request:
            // replaying the load below supersedes the arming, so carry it over.
            bool armed;
            lock (_playGate) armed = _playWhenLoaded;

            Load(pending.Primary, pending.Karaoke, pending.Vocals);
            if (armed) Play();
        }
    }

    /// <summary>True once the engine has at least one live output.</summary>
    internal bool OutputAvailable => Volatile.Read(ref _outputAvailable) == 1;

    /// <summary>
    /// Called by the transport when the engine's route is up or gone. Losing the
    /// output is not an error state yet - it only makes transport commands
    /// no-ops. Gaining one clears a "no output" failure and reloads the track
    /// that could not be opened, so picking a device in settings just works.
    /// </summary>
    internal void SetOutputAvailable(bool available)
    {
        Volatile.Write(ref _outputAvailable, available ? 1 : 0);

        if (available)
        {
            if (_fault != NoOutputFault) return;

            _fault = null;
            if (_lastLoad is { } last && _deck != null && _deck.TrackPath == null)
            {
                Load(last.Primary, last.Karaoke, last.Vocals);
                return;
            }

            RaiseStateChanged();
            return;
        }

        if (!_noOutputLogged)
        {
            _noOutputLogged = true;
            Log.Warn($"deck {Name}: no output configured - transport commands are ignored");
        }
    }

    // ---------------------------------------------------------------- internals

    private async Task LoadCoreAsync(DeckSource deck, string primary, string? karaoke, string? vocals, int generation)
    {
        try
        {
            // Wait for the engine: the very first load can be queued before the audio
            // thread exists, and a failure here should be a real failure, not a race.
            try { await _ready.ConfigureAwait(false); }
            catch { }

            if (generation != Volatile.Read(ref _loadGeneration)) return;

            if (!OutputAvailable)
            {
                Fail(NoOutputFault);
                return;
            }

            try
            {
                await deck.LoadAsync(primary, karaoke, _delayMs, startSeconds: 0, playWhenReady: false,
                        vocalsPath: vocals)
                    .ConfigureAwait(false);

                // The swap itself runs on the render thread. Wait for it so the state
                // readout flips to Loaded in this pass instead of one poll later.
                for (int i = 0; i < 200 && deck.State == DeckSource.DeckState.Loading; i++)
                    await Task.Delay(5).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (generation != Volatile.Read(ref _loadGeneration)) return;
                Fail(ex.Message);
                return;
            }

            if (generation != Volatile.Read(ref _loadGeneration)) return;

            ApplyVolume();
            _deck?.SetTempo(_tempoRatio);
            MarshalNotify();
        }
        finally
        {
            CompleteLoad(deck, generation);
        }
    }

    /// <summary>
    /// The load pass is over (landed, failed or superseded): release the
    /// load/play handshake and, when a <see cref="Play"/> arrived while the file
    /// was still being swapped in, start the deck now.
    /// </summary>
    private void CompleteLoad(DeckSource deck, int generation)
    {
        bool play;
        lock (_playGate)
        {
            // A newer load (or an unload) owns the state from here on; its own
            // completion handles any play request that belongs to it.
            if (generation != Volatile.Read(ref _loadGeneration)) return;

            _loadInFlight = false;
            play = _playWhenLoaded;
            _playWhenLoaded = false;
        }

        if (!play) return;

        // Nothing to start (unloaded, or the file failed): the deck reports the
        // failure through State/Error - never pretend it is playing.
        if (deck.TrackPath == null || _fault != null) return;

        Log.Info($"{Name}: load landed - starting the armed play ({deck.TrackPath})");
        deck.Play();
        RaiseStateChanged();
    }

    private bool EnsureOutput()
    {
        if (OutputAvailable) return true;

        // Outputs are still being opened: let the command queue up, the engine
        // picks it up as soon as the render thread runs.
        if (!_ready.IsCompleted) return true;

        if (_fault != NoOutputFault)
        {
            _fault = NoOutputFault;
            if (!_noOutputLogged)
            {
                _noOutputLogged = true;
                Log.Warn($"deck {Name}: no output configured - transport commands are ignored");
            }
            RaiseStateChanged();
        }
        return false;
    }

    private void Fail(string reason)
    {
        _fault = string.IsNullOrWhiteSpace(reason) ? "audio failed" : reason;
        Log.Error($"deck {Name}: {_fault}");
        MarshalNotify();
    }

    /// <summary>The deck fader lives on the engine (it multiplies the crossfade curve).</summary>
    private void ApplyVolume()
    {
        if (_engine == null) return;

        if (_isDeckA) _engine.DeckAVolume = (float)_volume;
        else _engine.DeckBVolume = (float)_volume;
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        var end = EndPosition;
        return position > end ? end : position;
    }

    private void OnDeckEnded(DeckSource deck) => MarshalNotify();

    private void OnDeckFailed(DeckSource deck, string error) => MarshalNotify();

    private void OnStateTick(object? sender, EventArgs e) => NotifyIfChanged();

    private DeckSnapshot Snapshot()
    {
        var deck = _deck;
        if (deck == null) return DeckSnapshot.Empty;

        return new DeckSnapshot(
            State,
            deck.TrackPath is not null,
            deck.HasKaraoke,
            deck.TrackPath,
            deck.KaraokePath,
            Error);
    }

    /// <summary>Raise only when something the UI can see actually changed.</summary>
    private void NotifyIfChanged()
    {
        var snapshot = Snapshot();
        if (snapshot == _snapshot) return;

        _snapshot = snapshot;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Force an announcement after a command. Marshalled like every other path:
    /// the transport's handler touches bindable state and must run on the UI
    /// thread even when a command is issued from a worker.
    /// </summary>
    private void RaiseStateChanged() => MarshalNotify();

    /// <summary>Events arrive on the render thread; the UI must hear them on its own.</summary>
    private void MarshalNotify()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            NotifyIfChanged();
            return;
        }

        dispatcher.BeginInvoke(NotifyIfChanged);
    }

    /// <summary>
    /// Detach from the engine. The <see cref="DeckSource"/> belongs to the
    /// <see cref="AudioEngine"/> and is disposed with it, so the channel only
    /// stops listening.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_stateTimer != null)
        {
            _stateTimer.Stop();
            _stateTimer.Tick -= OnStateTick;
        }

        if (_deck != null)
        {
            _deck.Ended -= OnDeckEnded;
            _deck.Failed -= OnDeckFailed;
        }

        StateChanged = null;
    }

    /// <summary>Everything the state-change poll compares.</summary>
    private readonly record struct DeckSnapshot(
        TransportState State,
        bool IsLoaded,
        bool HasKaraoke,
        string? PrimaryPath,
        string? KaraokePath,
        string? Error)
    {
        public static readonly DeckSnapshot Empty =
            new(TransportState.Stopped, false, false, null, null, null);
    }
}
