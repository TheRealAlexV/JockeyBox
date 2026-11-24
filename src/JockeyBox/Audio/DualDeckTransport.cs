using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Beat;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Transition;
using JockeyBox.Audio.Virtual;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Audio;

/// <summary>
/// Playback state of one deck (and of its stems). The first three members and
/// their values are the 1.x contract; <see cref="Failed"/> is appended so a deck
/// that cannot load (or that has no output to play through) is distinguishable
/// from an idle one without throwing at the UI.
/// </summary>
public enum TransportState
{
    Stopped,
    Playing,
    Paused,
    Failed,
}

/// <summary>
/// Which decks a transport command applies to: the active one, or both at once
/// (the mixer's A-B link). Deliberately not a third member of
/// <see cref="PlaybackDeck"/>: that enum is persisted and every
/// <c>deck == A ? x : y</c> in the app assumes exactly two values, so a third
/// would silently map onto B.
/// </summary>
public enum TransportTarget
{
    /// <summary>Commands act on <see cref="DualDeckTransport.ActiveDeck"/>.</summary>
    Active,

    /// <summary>Commands act on every deck that has a track loaded.</summary>
    Both,
}

/// <summary>
/// The mixer's two decks and the crossfader between them.
///
/// <para><b>What changed and why.</b> In 1.x this class owned no audio at all: it
/// held two <see cref="DeckChannel"/>s, each of which opened its own per-stem
/// <c>WasapiOut</c> and kept time with a <see cref="System.Diagnostics.Stopwatch"/>,
/// and it implemented the crossfade with a <see cref="System.Windows.Threading.DispatcherTimer"/>
/// climbing a gain value on the UI thread. Three problems came out of that: the
/// two device clocks drifted, the UI tick quantised every fade to ~33 ms (audible
/// as steps on a short cut), and any hiccup on the dispatcher (a slow download, a
/// big library query) froze the fade halfway.</para>
///
/// <para>Now one <see cref="AudioEngine"/> owns one clock and one render thread.
/// This type only translates the ViewModel's vocabulary into engine calls: the
/// crossfader is <see cref="AudioEngine.Fader"/>, ramped in exact frames
/// (<see cref="AudioEngine.RampFader"/>), deck gain is the engine's deck volume
/// and a route change reopens sinks instead of restarting a playhead.</para>
///
/// <para>If the settings enable no output at all, the engine refuses to start by
/// design. The mixer stays usable: playback commands become no-ops that report
/// <see cref="TransportState.Failed"/> on the deck, and nothing throws.</para>
/// </summary>
public sealed class DualDeckTransport : IDisposable
{
    /// <summary>Fade length used by <see cref="CrossfadeToDeck(PlaybackDeck)"/>.</summary>
    public static readonly TimeSpan DefaultCrossfade = TimeSpan.FromSeconds(2);

    private readonly Task _ready;

    /// <summary>Route / mixer values the engine is known to have applied.</summary>
    private AudioRoute? _appliedRoute;
    private int _appliedDelayMs;
    private float _appliedMasterVolume = 1f;

    private int _outputAvailable;
    private bool _outputWarningLogged;
    private int _disposed;
    private PlaybackDeck _activeDeck = PlaybackDeck.A;

    /// <summary>Which decks <see cref="Target"/> commands apply to.</summary>
    private TransportTarget _target = TransportTarget.Active;

    /// <summary>True while the active deck drives the other one (deck sync).</summary>
    private bool _syncActive;

    /// <summary>
    /// Guards the sync mirror, so the slave command the mirror issues cannot
    /// re-enter the mirror.
    /// </summary>
    private bool _mirroring;

    /// <summary>Device ids the mixer currently holds (reopen only when this changes).</summary>
    private string? _appliedMicIds;

    /// <summary>Last device/level/mute/monitor set that was logged.</summary>
    private string? _appliedMicLog;

    /// <summary>
    /// Build the transport around two channels the ViewModel has already created.
    /// Outputs are opened on a background task: a slow USB interface must not
    /// delay the window. <paramref name="grid"/> is the optional BPM/beat source
    /// for auto-mix; without it every transition degrades to a plain crossfade.
    /// </summary>
    public DualDeckTransport(DeckChannel deckA, DeckChannel deckB, IBeatGridProvider? grid = null)
    {
        ArgumentNullException.ThrowIfNull(deckA);
        ArgumentNullException.ThrowIfNull(deckB);

        DeckA = deckA;
        DeckB = deckB;

        BeatGridProvider = grid;

        Engine = new AudioEngine();

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready = ready.Task;

        DeckA.Bind(Engine, Engine.DeckA, isDeckA: true, _ready);
        DeckB.Bind(Engine, Engine.DeckB, isDeckA: false, _ready);

        DeckA.StateChanged += OnDeckStateChanged;
        DeckB.StateChanged += OnDeckStateChanged;

        // No output yet, so a fade has to start from a sane centre instead of 0.
        Engine.Fader.Reset(0.5);
        // The scheduler keeps one provider for its lifetime and asks it on every
        // auto-mix, so the holder can be swapped at any time (the analyser starts
        // after the window is up) without rebuilding the scheduler.
        Transitions = new TransitionScheduler(Engine, new BeatGridRelay(this))
        {
            Mode = TransitionMode.AutoCrossfade,
            FadeSeconds = DefaultCrossfade.TotalSeconds,
        };

        _ = InitializeAsync(ready);
    }

    /// <summary>
    /// Completes when the first route has been applied, or the attempt has failed.
    /// Exists so a test can wait for startup instead of racing it: whether the launch
    /// rewrote the device file is exactly what must be observed after it settled.
    /// </summary>
    internal Task Ready => _ready;

    /// <summary>Raised when either deck, or the active deck, changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when <see cref="ActiveDeck"/> changes.</summary>
    public event EventHandler? ActiveDeckChanged;

    /// <summary>Deck A as the ViewModel knows it.</summary>
    public DeckChannel DeckA { get; }

    /// <summary>Deck B as the ViewModel knows it.</summary>
    public DeckChannel DeckB { get; }

    /// <summary>The single audio engine. Null-safe for callers that probe it before start-up.</summary>
    public AudioEngine Engine { get; }

    /// <summary>Automatic transitions (beat grid, tempo match, scheduled fades).</summary>
    public TransitionScheduler Transitions { get; }

    /// <summary>
    /// Beat/BPM source for auto-mix, or null while nothing has been analysed. Read
    /// through a holder, so assigning it late (after the analyser has warmed up)
    /// takes effect on the next transition without touching the scheduler.
    /// </summary>
    public IBeatGridProvider? BeatGridProvider { get; set; }

    /// <summary>
    /// Master gain (0..1.2). The UI usually writes <c>Engine.MasterVolume</c>
    /// directly; this is the equivalent property for callers holding the
    /// transport. A live write here is not undone by a stale settings file.
    /// </summary>
    public double MasterVolume
    {
        get => Engine.MasterVolume;
        set
        {
            // _appliedMasterVolume is left alone on purpose: AdoptMasterVolume sees
            // the divergence and resyncs instead of pulling an unsaved file value back.
            Engine.MasterVolume = (float)Math.Clamp(value, 0.0, 1.2);
        }
    }

    /// <summary>Global program delay in ms (OBS/video alignment).</summary>
    public int OutputDelayMs
    {
        get => Engine.OutputDelayMs;
        set
        {
            // Deliberately does not touch _appliedDelayMs: that field tracks the
            // value last adopted from the settings file, so a live change made here
            // is never "corrected" back by a file that has not been saved yet.
            Engine.OutputDelayMs = Math.Clamp(value, 0, 2000);
        }
    }

    /// <summary>Deck the transport commands act on by default.</summary>
    public PlaybackDeck ActiveDeck
    {
        get => _activeDeck;
        set
        {
            if (_activeDeck == value) return;

            _activeDeck = value;
            ActiveDeckChanged?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        }
    }

    /// <summary>Shorthand for <see cref="Channel"/> of the <see cref="ActiveDeck"/>.</summary>
    public DeckChannel Active => Channel(ActiveDeck);

    /// <summary>
    /// Which decks the transport commands act on. Independent of
    /// <see cref="ActiveDeck"/>: <see cref="TransportTarget.Both"/> links the
    /// decks (the A-B button), while the active deck still names the master.
    ///
    /// <para>Set automatically by <see cref="LoadOnDeck"/> when a karaoke pair
    /// fills both decks; pressing A or B is expected to put it back to
    /// <see cref="TransportTarget.Active"/> (the ViewModel's job).</para>
    /// </summary>
    public TransportTarget Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            RaiseStateChanged();
        }
    }

    /// <summary>Setter form of <see cref="Target"/> for callers that prefer a method.</summary>
    public void SetTarget(TransportTarget target) => Target = target;

    /// <summary>
    /// The decks a command applies to: the active deck alone, or every deck that
    /// holds a track. An empty second deck is simply skipped, so a fan-out never
    /// touches (or throws on) a deck with nothing to play.
    /// </summary>
    private IReadOnlyList<PlaybackDeck> Targets()
    {
        if (_target == TransportTarget.Active) return new[] { ActiveDeck };

        var decks = new List<PlaybackDeck>(2);
        if (DeckA.IsLoaded) decks.Add(PlaybackDeck.A);
        if (DeckB.IsLoaded) decks.Add(PlaybackDeck.B);
        return decks;
    }

    /// <summary>Transport state of the active deck.</summary>
    public TransportState State => Active.State;

    /// <summary>Playhead of the active deck, clamped to the media length.</summary>
    public TimeSpan Position => Active.DisplayPosition;

    /// <summary>Length of the active deck (zero when nothing is loaded).</summary>
    public TimeSpan Duration => Active.Duration;

    /// <summary>True when either deck holds media.</summary>
    public bool AnyLoaded => DeckA.IsLoaded || DeckB.IsLoaded;

    /// <summary>True when either deck is rolling (the crossfader may be between them).</summary>
    public bool AnyPlaying =>
        DeckA.State == TransportState.Playing || DeckB.State == TransportState.Playing;

    /// <summary>True when the active deck has played to its end.</summary>
    public bool ReachedEnd => Active.ReachedEnd;

    /// <summary>
    /// Crossfader position (0 = deck A, 1 = deck B). Read straight off the
    /// engine's envelope at the current frame, so a running ramp is visible as it
    /// moves instead of lagging behind a cached value.
    /// </summary>
    public double Crossfader => Crossfade.Clamp01(Engine.Fader.FaderAt(Engine.FrameIndex));

    /// <summary>Make a deck the one the shared transport surface follows.</summary>
    public void ActivateDeck(PlaybackDeck deck) => ActiveDeck = deck;

    /// <summary>True once the engine has at least one live output.</summary>
    public bool OutputAvailable => Volatile.Read(ref _outputAvailable) == 1;

    // ------------------------------------------------------------------- mic

    /// <summary>
    /// First open capture, for the legacy single-strip status line. The 2.1 mixer
    /// binds the device list (<see cref="AudioEngine.Mics"/>) instead.
    /// </summary>
    public MicCapture? Mic
    {
        get
        {
            foreach (var strip in Engine.Mics.Strips)
                if (strip.Capture is { IsCapturing: true } capture) return capture;
            return null;
        }
    }

    /// <summary>
    /// Adopt the recording-device list: one capture and one ring per enabled
    /// device, each with its own app-domain volume, mute and monitor. Captures are
    /// only reopened when the set of device ids changed, so moving a fader, muting
    /// or toggling monitor never glitches the input.
    ///
    /// <para>The list is also where the one-time default adoption lives: an install
    /// that never stored a device list gets the system default <i>once</i>
    /// (<see cref="AppSettings.MicAutoSelected"/>); after that an empty list means
    /// None. A saved device that is unplugged stays saved and is skipped, it is
    /// never silently replaced.</para>
    /// </summary>
    public void ApplyMicSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var devices = settings.MicDevices
            .Where(m => !string.IsNullOrWhiteSpace(m.DeviceId))
            .ToList();

        if (devices.Count == 0 && !settings.MicAutoSelected)
        {
            string? defaultId = null;
            try { defaultId = new DeviceService().GetDefaultCaptureDeviceId(); }
            catch (Exception ex) { Log.Warn($"mic default probe failed: {ex.Message}"); }

            if (!string.IsNullOrWhiteSpace(defaultId))
            {
                var adopted = new AppSettings.MicDeviceSettings { DeviceId = defaultId!, Volume = 1f };
                settings.MicDevices.Add(adopted);
                settings.MicAutoSelected = true;
                settings.MicDeviceId = null;
                devices.Add(adopted);
                try { settings.Save(); }
                catch (Exception ex) { Log.Warn($"mic adoption not saved: {ex.Message}"); }
            }
        }

        string idsKey = string.Join("|", devices.Select(d => d.DeviceId));
        if (!string.Equals(idsKey, _appliedMicIds, StringComparison.Ordinal))
        {
            _appliedMicIds = idsKey;

            // Swap then dispose: the render thread reads the array reference once
            // per block, so it either sees the old set or the new one, never a
            // half-replaced collection.
            var strips = new List<MicStrip>(devices.Count);
            foreach (var device in devices) strips.Add(new MicStrip(device.DeviceId));
            Engine.Mics.SetStrips(strips);
        }

        // Gain, mute and monitor are live engine parameters and are applied every
        // pass, so a settings write always wins over a stale UI value.
        var current = Engine.Mics.Strips;
        for (int i = 0; i < current.Count && i < devices.Count; i++)
        {
            current[i].Volume = devices[i].Volume;
            current[i].Muted = devices[i].Muted;
            current[i].Monitor = devices[i].Monitor;
        }

        Engine.SoundboardFollowsMain = settings.SoundboardFollowsMain;

        // The master gate and the per-device strips are stored, not live: without this
        // the toggle only reached the engine at startup. Reuse the startup loader so the
        // attached strips re-read their stored parameters, the new gate reaches them, and
        // the ledger re-aligns (an enabled strip can bring a limiter into the chain).
        // Never saves: this is the apply path, not the edit path.
        Engine.Mics.LoadStoredSettings(AppPaths.Home, settings.MixerEnabled);

        LogMicRoute(current);
    }

    /// <summary>Log the device set once, not on every settings pass.</summary>
    private void LogMicRoute(IReadOnlyList<MicStrip> strips)
    {
        string signature = string.Join("|", strips.Select(s =>
            $"{s.DeviceId}:{s.Volume:0.000}:{s.Muted}:{s.Monitor}:{s.IsOpen}"));
        if (string.Equals(signature, _appliedMicLog, StringComparison.Ordinal)) return;
        _appliedMicLog = signature;

        if (strips.Count == 0)
        {
            Log.Info("mic route: none");
            return;
        }

        Log.Info($"mic route: {strips.Count} device(s): " +
                 string.Join(", ", strips.Select(s => s.DeviceName)));
        foreach (var strip in strips)
        {
            string state = strip.IsOpen ? $"{strip.MixRate} Hz" : "unavailable, skipped";
            Log.Info($"mic: {strip.DeviceName} {state} level {strip.Volume:0%} " +
                     $"mute {(strip.Muted ? "on" : "off")} monitor {(strip.Monitor ? "on" : "off")}");
        }
    }

    // ---------------------------------------------------------------- transport

    /// <summary>Play every target deck (<see cref="Target"/>).</summary>
    public void Play()
    {
        foreach (var deck in Targets()) PlayDeck(deck, autoCue: _target == TransportTarget.Active);
    }

    /// <summary>Pause every target deck.</summary>
    public void Pause()
    {
        foreach (var deck in Targets()) PauseDeck(deck);
    }

    /// <summary>
    /// Play/pause every target deck.
    ///
    /// <para>Absolute, not a per-deck flip: with <see cref="TransportTarget.Both"/>
    /// a cued B must follow a playing A instead of toggling the other way, so the
    /// pair either all plays or all pauses. One deck playing and one paused ends
    /// with both playing; both playing ends with both paused.</para>
    /// </summary>
    public void TogglePlayPause()
    {
        if (_target == TransportTarget.Active)
        {
            TogglePlayPause(ActiveDeck);
            return;
        }

        var decks = Targets();
        bool allPlaying = decks.Count > 0;
        foreach (var deck in decks) allPlaying &= IsDeckPlaying(deck);

        if (allPlaying)
        {
            foreach (var deck in decks) PauseDeck(deck);
        }
        else
        {
            foreach (var deck in decks) PlayDeck(deck, autoCue: false);
        }
    }

    /// <summary>Stop every target deck (media stays loaded).</summary>
    public void Stop()
    {
        foreach (var deck in Targets()) StopDeck(deck);
    }

    /// <summary>Seek every target deck.</summary>
    public void Seek(TimeSpan position)
    {
        foreach (var deck in Targets()) SeekDeck(deck, position);
    }

    /// <summary>Nudge every target deck forward.</summary>
    public void SkipForward(double seconds = 10) => SkipTo(Active.DisplayPosition + TimeSpan.FromSeconds(seconds));

    /// <summary>Nudge every target deck backward.</summary>
    public void SkipBack(double seconds = 10) => SkipTo(Active.DisplayPosition - TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Move every target deck to an absolute position. The commanded position is
    /// used for the sync mirror rather than the master's clock: a deck seek is
    /// posted to the render thread, so the master's clock has not moved yet when
    /// the mirror runs.
    /// </summary>
    private void SkipTo(TimeSpan position)
    {
        foreach (var deck in Targets()) Channel(deck).Seek(position);
        MirrorSeek(ActiveDeck, position);
    }

    /// <summary>Nudge one deck forward (the deck controls' own buttons).</summary>
    public void SkipForwardDeck(PlaybackDeck deck, double seconds = 10) =>
        Channel(deck).SkipForward(seconds);

    /// <summary>Nudge one deck backward.</summary>
    public void SkipBackDeck(PlaybackDeck deck, double seconds = 10) =>
        Channel(deck).SkipBack(seconds);

    /// <summary>
    /// Hook for the track list changing. The engine holds no track list of its
    /// own, so there is nothing to rebuild; the method stays because the
    /// ViewModel calls it on every library refresh.
    /// </summary>
    public void OnTracksChanged()
    {
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- per deck

    /// <summary>The channel behind a deck id.</summary>
    public DeckChannel Channel(PlaybackDeck deck) => deck == PlaybackDeck.A ? DeckA : DeckB;

    public bool IsDeckLoaded(PlaybackDeck deck) => Channel(deck).IsLoaded;

    public bool IsDeckPlaying(PlaybackDeck deck) => Channel(deck).State == TransportState.Playing;

    /// <summary>
    /// Load a track onto a deck (non-blocking; failures arrive via StateChanged).
    /// <paramref name="vocalsPath"/> is the reverse-render take, when the library has
    /// one beside the source: resident as the third stem so the deck can switch to
    /// it without re-pointing a reader.
    /// </summary>
    public void LoadOnDeck(PlaybackDeck deck, string? primaryPath, string? karaokePath,
                           string? vocalsPath = null)
    {
        Channel(deck).Load(primaryPath, karaokePath, vocalsPath);

        // The load funnel is the one place the A-B link self-selects: a karaoke
        // pair filling both decks is the operator's "play it on both" case, and
        // any ordinary load puts the transport back on the active deck alone.
        // Pressing A or B clears it again (the ViewModel sets Target itself).
        Target = !string.IsNullOrWhiteSpace(primaryPath) && !string.IsNullOrWhiteSpace(karaokePath)
            ? TransportTarget.Both
            : TransportTarget.Active;

        RaiseStateChanged();
    }

    /// <summary>
    /// Start a deck. <paramref name="autoCue"/> snaps the crossfader to that side
    /// when the other deck is quiet and no fade is already running - the classic
    /// "hit play and it comes up on the main output" behaviour.
    /// </summary>
    public void PlayDeck(PlaybackDeck deck, bool autoCue = true)
    {
        var channel = Channel(deck);
        channel.Play();

        if (autoCue &&
            Channel(Other(deck)).State != TransportState.Playing &&
            !Engine.Fader.IsRunning(Engine.FrameIndex) &&
            Engine.IsRunning)
        {
            Engine.SetFader(deck == PlaybackDeck.A ? 0.0 : 1.0);
        }

        MirrorPlay(deck);
        RaiseStateChanged();
    }

    /// <summary>Pause a deck (load and playhead are kept).</summary>
    public void PauseDeck(PlaybackDeck deck)
    {
        Channel(deck).Pause();
        MirrorPause(deck);
        RaiseStateChanged();
    }

    /// <summary>Play/pause a deck.</summary>
    public void TogglePlayPause(PlaybackDeck deck)
    {
        if (IsDeckPlaying(deck)) PauseDeck(deck);
        else PlayDeck(deck, autoCue: true);
    }

    /// <summary>Stop a deck; the media stays loaded and the playhead returns to zero.</summary>
    public void StopDeck(PlaybackDeck deck)
    {
        Channel(deck).Stop();
        MirrorStop(deck);
        RaiseStateChanged();
    }

    /// <summary>Seek a deck.</summary>
    public void SeekDeck(PlaybackDeck deck, TimeSpan position)
    {
        Channel(deck).Seek(position);
        MirrorSeek(deck, position);
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- deck sync

    /// <summary>
    /// Lock the two decks together with the active deck as master. Engaging
    /// aligns the slave to the master's media clock and copies its tempo; from
    /// then on play, pause, stop, seek, skip and the master's end of track are
    /// mirrored to the slave.
    ///
    /// <para>No chasing loop is needed: both decks are pulled in the same
    /// 480-frame block off one <c>FrameIndex</c>, so equal media position plus
    /// equal tempo holds to within a block by construction. Disengaging leaves
    /// both decks exactly as they are.</para>
    /// </summary>
    public bool SyncActive
    {
        get => _syncActive;
        set
        {
            if (_syncActive == value) return;
            _syncActive = value;

            if (value) AlignSlave();

            RaiseStateChanged();
        }
    }

    /// <summary>Setter form of <see cref="SyncActive"/>.</summary>
    public void SetSyncActive(bool active) => SyncActive = active;

    /// <summary>
    /// Bring the slave onto the master's playhead and tempo. Runs once, when
    /// sync is engaged: after that the shared block clock keeps them together.
    /// </summary>
    private void AlignSlave()
    {
        var master = Channel(ActiveDeck);
        var slave = Channel(Other(ActiveDeck));
        if (!master.IsLoaded || !slave.IsLoaded) return;

        slave.TempoRatio = master.TempoRatio;

        // A deck seek clamps to that deck's own end, so a master longer than the
        // slave parks the slave on its last frame (silent) rather than wrapping
        // it back into a loop.
        slave.Seek(TimeSpan.FromSeconds(master.MediaPositionSeconds));
    }

    /// <summary>
    /// Run a slave command for a master command, when sync is on and the command
    /// came from the master. <see cref="_mirroring"/> keeps the slave's own
    /// state event from re-entering the mirror.
    /// </summary>
    private void Mirror(PlaybackDeck deck, Action<DeckChannel, DeckChannel> onSlave)
    {
        if (!_syncActive || _mirroring || deck != ActiveDeck) return;

        var master = Channel(deck);
        var slave = Channel(Other(deck));
        if (!slave.IsLoaded) return;

        _mirroring = true;
        try { onSlave(master, slave); }
        finally { _mirroring = false; }
    }

    private void MirrorPlay(PlaybackDeck deck) => Mirror(deck, (master, slave) =>
    {
        // Re-align on play: the master may have been paused (or cued) since the
        // sync was engaged, and a pair that starts apart never catches up.
        slave.Seek(TimeSpan.FromSeconds(master.MediaPositionSeconds));
        slave.Play();
    });

    private void MirrorPause(PlaybackDeck deck) => Mirror(deck, (_, slave) => slave.Pause());

    private void MirrorStop(PlaybackDeck deck) => Mirror(deck, (_, slave) => slave.Stop());

    private void MirrorSeek(PlaybackDeck deck, TimeSpan position) =>
        Mirror(deck, (_, slave) => slave.Seek(position));

    // ---------------------------------------------------------------- fan-out controls

    /// <summary>
    /// Set the deck fader on every target in one call, so a VOL SYNC toggle does
    /// not need one write per deck. Re-entrancy guarding belongs to the ViewModel.
    /// </summary>
    public void SetTargetVolume(double volume)
    {
        foreach (var deck in Targets()) Channel(deck).Volume = volume;
        RaiseStateChanged();
    }

    /// <summary>Set the tempo ratio on every target in one call (TEMPO SYNC).</summary>
    public void SetTargetTempo(double ratio)
    {
        foreach (var deck in Targets()) Channel(deck).TempoRatio = ratio;
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- crossfader

    /// <summary>
    /// Move the crossfader. A hand on the fader always wins: this freezes any
    /// running ramp where it is and jumps from there.
    /// </summary>
    public void SetCrossfader(double value)
    {
        Engine.SetFader(value);
        RaiseStateChanged();
    }

    /// <summary>Fade to a deck using <see cref="DefaultCrossfade"/>.</summary>
    public void CrossfadeToDeck(PlaybackDeck deck) => CrossfadeToDeck(deck, DefaultCrossfade);

    /// <summary>
    /// Fade to a deck over a duration. The ramp is scheduled in engine frames, so
    /// the curve is exact and independent of UI load.
    /// </summary>
    public void CrossfadeToDeck(PlaybackDeck deck, TimeSpan duration)
    {
        double target = deck == PlaybackDeck.A ? 0.0 : 1.0;

        if (duration <= TimeSpan.Zero || !Engine.IsRunning)
        {
            Engine.SetFader(target);
        }
        else
        {
            Engine.RampFader(target, duration.TotalSeconds);
        }

        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// Adopt a settings change made for one deck (device, delay, volume, karaoke
    /// split, tempo). Gains are live engine parameters, so a playing deck is
    /// never re-primed; the route is only reopened when the routing values
    /// actually differ from what the engine is running.
    /// </summary>
    public void ApplyDeckSettingsChange(PlaybackDeck deck)
    {
        Channel(deck).ApplySettings();

        AppSettings settings;
        try { settings = AppSettings.Load(); }
        catch (Exception ex)
        {
            Log.Warn($"audio settings could not be read: {ex.Message}");
            RaiseStateChanged();
            return;
        }

        bool changed = AdoptMasterVolume(settings.MasterVolume);
        changed |= AdoptOutputDelay(settings.OutputDelayMs);
        AdoptTransitions(settings);
        ApplyMicSettings(settings);
        AdoptMonitorSettings(settings);
        AdoptEqSettings(settings);

        var route = BuildRouteFromAppSettings(settings);
        if (!route.Equals(_appliedRoute))
        {
            _appliedRoute = route;
            changed = true;
            _ = ApplyRouteAsync(route);
        }

        if (changed) RaiseStateChanged();
    }

    /// <summary>
    /// Settings as one five-bus engine route.
    ///
    /// <para>A profile that has never had a routing choice is routed to the
    /// system default output instead of playing into silence: "None" has to mean
    /// "the user turned this off", which is only knowable once the settings have
    /// been saved through the dialog (<see cref="AppSettings.AudioRoutingConfigured"/>).
    /// An explicitly empty Main with the flag set stays None.</para>
    ///
    /// <para>The Monitor bus reuses <see cref="AppSettings.MonitorOutputDeviceId"/>
    /// (the 2.0 monitor output): it is the operator's headphone endpoint, and the
    /// new model did not add a second setting for it.</para>
    /// </summary>
    internal static AudioRoute BuildRouteFromAppSettings(AppSettings settings)
    {
        // The 2.1 names survive in the settings file on purpose: renaming the JSON
        // keys would silently drop an existing install's routing to "None" and
        // play into silence, which is the exact failure this change repairs.
        // MainOutputDeviceId is Deck A's output; KaraokeAidOutputDeviceId is deck
        // B's. The master-mix bridge setting is gone (see AppSettings).
        string? deckA = NullIfBlank(settings.MainOutputDeviceId);
        string? deckB = NullIfBlank(settings.KaraokeAidOutputDeviceId);
        string? recording = NullIfBlank(settings.RecordingOutputDeviceId);
        string? soundboard = NullIfBlank(settings.SoundboardOutputDeviceId);
        string? monitor = NullIfBlank(settings.MonitorOutputDeviceId);

        if (!settings.AudioRoutingConfigured &&
            deckA == null && deckB == null && recording == null &&
            soundboard == null && monitor == null)
        {
            deckA = DefaultOutputDeviceId();
            if (deckA != null)
                Log.Info("no audio routing configured yet: routing Deck A to the default output");
        }

        return new AudioRoute(deckA, deckB, recording, soundboard, monitor)
        {
            VirtualBuses = VirtualBusesFromSettings(settings),
            // The device file owns the width, not these settings: it is the one copy the
            // native driver reads, and settings.json is per-user %APPDATA%, which an
            // elevated ASIO host in session 0 can never see. Empty keeps the legacy
            // stereo pair below, so an install that never configured lines is unchanged.
            VirtualLines = DeviceLines(),
            VirtualBusName = string.IsNullOrWhiteSpace(settings.VirtualAsioBusName)
                ? "main"
                : settings.VirtualAsioBusName.Trim(),
        };
    }

    /// <summary>
    /// The device's own line list, from the one file the native driver reads. Empty
    /// means unconfigured and keeps the legacy stereo pair. A file that exists but
    /// cannot be parsed is a fault - the driver refuses it too, so no ring attaches -
    /// and is logged rather than thrown: a settings change must not die on it.
    /// </summary>
    private static IReadOnlyList<VirtualLine> DeviceLines()
    {
        try
        {
            return VirtualDeviceConfig.ConfiguredLines();
        }
        catch (Exception ex)
        {
            Log.Error($"virtual device config '{VirtualDeviceConfig.DefaultPath}' could not be read; " +
                      "publishing the legacy stereo pair and leaving the file alone", ex);
            return Array.Empty<VirtualLine>();
        }
    }

    /// <summary>
    /// The buses that also publish to the virtual ASIO ring, in routing order. The
    /// flags are independent of device routing: a bus can feed a physical device and
    /// the ring at once, and a bus with no device still works as the ring's source.
    /// </summary>
    internal static IReadOnlyList<BusKind> VirtualBusesFromSettings(AppSettings settings)
    {
        var buses = new List<BusKind>(5);
        if (settings.VirtualAsioDeckA) buses.Add(BusKind.DeckA);
        if (settings.VirtualAsioDeckB) buses.Add(BusKind.DeckB);
        if (settings.VirtualAsioRecording) buses.Add(BusKind.Recording);
        if (settings.VirtualAsioSoundboard) buses.Add(BusKind.Soundboard);
        if (settings.VirtualAsioMonitor) buses.Add(BusKind.Monitor);
        return buses;
    }

    /// <summary>The current default render endpoint, or null when the machine has no audio output.</summary>
    private static string? DefaultOutputDeviceId()
    {
        try
        {
            return new DeviceService().GetOutputDevices().FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"default output probe failed: {ex.Message}");
            return null;
        }
    }

    private bool AdoptMasterVolume(float value)
    {
        float target = Math.Clamp(value, 0f, 1.2f);

        // The UI writes the live fader straight to the engine and only persists it
        // later, so a mismatch against our snapshot means "the UI already moved
        // this" - keep it and resync instead of pulling a stale file value back.
        if (Math.Abs(Engine.MasterVolume - _appliedMasterVolume) > 1e-4f)
        {
            _appliedMasterVolume = Engine.MasterVolume;
            return false;
        }

        if (Math.Abs(target - _appliedMasterVolume) <= 1e-4f) return false;

        Engine.MasterVolume = target;
        _appliedMasterVolume = Engine.MasterVolume;
        return true;
    }

    /// <summary>Mirror the MON settings onto the engine (the view model binds these).</summary>
    private void AdoptMonitorSettings(AppSettings settings)
    {
        Engine.MonitorEnabled = settings.MonitorEnabled;
        Engine.MonitorAGain = settings.MonitorAGain;
        Engine.MonitorBGain = settings.MonitorBGain;
    }

    /// <summary>
    /// Mirror the persisted mix EQ onto the engine. Called from the engine's own
    /// start-up and from a settings change, so a saved band is in circuit on the
    /// first block whichever host built the engine - the window is not the only one.
    /// The bands clamp themselves; nothing here can leave the engine outside -50…+50.
    /// </summary>
    private void AdoptEqSettings(AppSettings settings) =>
        Engine.Eq.SetAll(settings.EqLowDb, settings.EqMidDb, settings.EqHighDb);

    private bool AdoptOutputDelay(int value)
    {
        int target = Math.Clamp(value, 0, 2000);

        // Same rule as the master fader: a live change made through
        // OutputDelayMs must not be undone by a file that has not been saved yet.
        if (Engine.OutputDelayMs != _appliedDelayMs)
        {
            _appliedDelayMs = Engine.OutputDelayMs;
            return false;
        }

        if (target == _appliedDelayMs) return false;

        Engine.OutputDelayMs = target;
        _appliedDelayMs = Engine.OutputDelayMs;
        return true;
    }

    /// <summary>Mirror the transition settings the scheduler acts on.</summary>
    private void AdoptTransitions(AppSettings settings)
    {
        Transitions.Mode = settings.TransitionMode;
        Transitions.FadeSeconds = Math.Clamp(settings.TransitionFadeSeconds, 0.05, 60.0);
        Transitions.TempoRangePercent = Math.Clamp(settings.TempoRangePercent, 0.0, 50.0);
    }

    /// <summary>
    /// Push the per-device output delays from settings into the live engine. Called
    /// after the Settings dialog saves; device outputs are rescheduled in place, so
    /// no session is reopened and no other device's delay moves.
    /// </summary>
    public void ApplyDeviceDelays(AppSettings settings) =>
        Engine.SetDeviceDelays(DeviceDelaysFrom(settings));

    private static Dictionary<string, int> DeviceDelaysFrom(AppSettings settings)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, ms) in settings.DeviceOutputDelays)
            if (!string.IsNullOrWhiteSpace(id))
                map[id] = (int)Math.Round(ms);
        return map;
    }

    // ---------------------------------------------------------------- lifecycle

    private async Task InitializeAsync(TaskCompletionSource<bool> ready)
    {
        bool available = false;
        try
        {
            // Let the ViewModel finish restoring per-deck settings (device ids,
            // offsets, volumes) before the route is snapshotted: it runs this line
            // after construction, and a route built too early would open the old
            // devices.
            await Task.Yield();

            var settings = AppSettings.Load();

            // The device file is not touched here. Only the composition root creates it
            // (App.OnStartup -> CreateIfAbsent with an explicit path), so no transport
            // construction - in the app or in a test - can reach a production write.

            var route = BuildRouteFromAppSettings(settings);

            Engine.MasterVolume = Math.Clamp(settings.MasterVolume, 0f, 1.2f);
            Engine.OutputDelayMs = Math.Clamp(settings.OutputDelayMs, 0, 2000);
            Engine.SetDeviceDelays(DeviceDelaysFrom(settings));
            _appliedMasterVolume = Engine.MasterVolume;
            _appliedDelayMs = Engine.OutputDelayMs;
            AdoptTransitions(settings);
            AdoptEqSettings(settings);

            await Engine.SetRouteAsync(route).ConfigureAwait(false);
            _appliedRoute = route;
            LogRoute(route);

            if (HasOutput(route))
            {
                Engine.Start();
                available = Engine.IsRunning;
            }

            if (available)
            {
                Log.Info($"audio engine ready: {Engine.DescribeOutputs()}");
            }
            else
            {
                Log.Warn("no output configured: the mixer stays usable but silent");
            }

            // The voice path is independent of the output route: a mic with no
            // program bus is silent, and a mic device that vanished degrades to
            // silence instead of stopping the music.
            ApplyMicSettings(settings);
        }
        catch (Exception ex)
        {
            Log.Error("audio engine failed to start", ex);
        }
        finally
        {
            SetOutputAvailable(available);
            ready.TrySetResult(true);
            RaiseStateChanged();
        }
    }

    private async Task ApplyRouteAsync(AudioRoute route)
    {
        try
        {
            await Engine.SetRouteAsync(route).ConfigureAwait(false);
            LogRoute(route);

            bool available = HasOutput(route);
            if (available && !Engine.IsRunning) Engine.Start();

            SetOutputAvailable(available && Engine.IsRunning);
        }
        catch (Exception ex)
        {
            Log.Error("audio route change failed", ex);
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Announce the five-bus route, with the friendly device name for each. The
    /// device sessions themselves are logged by <see cref="AudioEngine.SetRouteAsync"/>:
    /// it is the only place that knows how many distinct sessions the route
    /// actually opened (two buses on one device produce one).
    /// </summary>
    private void LogRoute(AudioRoute route)
    {
        Log.Info($"audio route: deckA={Describe(route.DeckADeviceId)} " +
                 $"deckB={Describe(route.DeckBDeviceId)} " +
                 $"recording={Describe(route.RecordingDeviceId)} " +
                 $"soundboard={Describe(route.SoundboardDeviceId)} " +
                 $"monitor={Describe(route.MonitorDeviceId)} mics={Engine.Mics.Count}" +
                 (route.VirtualBuses.Count > 0
                     ? $" virtualAsio={route.VirtualBusName}<-{route.VirtualBuses[0]}"
                     : ""));
    }

    /// <summary>Friendly name for a route id ("none" for None).</summary>
    private string Describe(string? deviceId)
    {
        if (deviceId == null) return "none";
        try
        {
            var device = new DeviceService().GetOutputDevices()
                .FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            return device?.FriendlyName ?? deviceId;
        }
        catch (Exception ex)
        {
            Log.Warn($"audio route name lookup failed: {ex.Message}");
            return deviceId;
        }
    }

    private void SetOutputAvailable(bool available)
    {
        Volatile.Write(ref _outputAvailable, available ? 1 : 0);
        DeckA.SetOutputAvailable(available);
        DeckB.SetOutputAvailable(available);

        if (!available && !_outputWarningLogged)
        {
            _outputWarningLogged = true;
            Log.Warn("audio outputs unavailable: deck commands are ignored until a device is selected");
        }
    }

    private static bool HasOutput(AudioRoute route) => route.AnyOutput;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static PlaybackDeck Other(PlaybackDeck deck) =>
        deck == PlaybackDeck.A ? PlaybackDeck.B : PlaybackDeck.A;

    private void OnDeckStateChanged(object? sender, EventArgs e)
    {
        // Sync: a master that ran out of media takes the slave with it, so the
        // pair never leaves one deck playing into silence. The slave ending on
        // its own needs no handling - it simply stops at its last frame.
        if (_syncActive && !_mirroring && ReferenceEquals(sender, Active) && Active.ReachedEnd)
        {
            var slave = Channel(Other(ActiveDeck));
            if (slave.State == TransportState.Playing)
            {
                _mirroring = true;
                try { slave.Stop(); }
                finally { _mirroring = false; }
            }
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Announce a change to the UI. Deck events can arrive from the render thread
    /// and the ViewModel's handler touches bindable state, so this always lands on
    /// the dispatcher (a pass-through when it is already the UI thread).
    /// </summary>
    private void RaiseStateChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Stop the engine and detach both channels. Guarded so a double dispose (bad
    /// ownership upstream, or a failed start-up) cannot stop the graph twice.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        DeckA.StateChanged -= OnDeckStateChanged;
        DeckB.StateChanged -= OnDeckStateChanged;

        DeckA.Dispose();
        DeckB.Dispose();

        Engine.Mics.Dispose();
        Engine.Dispose(); // Stop() + deck disposal, both idempotent
    }

    /// <summary>
    /// Read-through holder for the scheduler's beat grid. The scheduler captures
    /// this object once; <see cref="BeatGridProvider"/> stays assignable.
    /// </summary>
    private sealed class BeatGridRelay : IBeatGridProvider
    {
        private readonly DualDeckTransport _owner;

        public BeatGridRelay(DualDeckTransport owner) => _owner = owner;

        /// <summary>Null when no provider is set or the track has no grid yet.</summary>
        public BeatGrid? For(string? path)
        {
            try
            {
                return _owner.BeatGridProvider?.For(path);
            }
            catch (Exception ex)
            {
                // A failing analyser degrades to "no grid", never to a broken fade.
                Log.Warn($"beat grid lookup failed: {ex.Message}");
                return null;
            }
        }
    }
}
