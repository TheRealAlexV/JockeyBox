using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Beat;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Transition;
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

        Engine.KaraokeMode = settings.KaraokeModeEnabled;
        Engine.SoundboardFollowsMain = settings.SoundboardFollowsMain;

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

    /// <summary>Play the active deck.</summary>
    public void Play() => PlayDeck(ActiveDeck);

    /// <summary>Pause the active deck.</summary>
    public void Pause() => PauseDeck(ActiveDeck);

    /// <summary>Play/pause the active deck.</summary>
    public void TogglePlayPause() => TogglePlayPause(ActiveDeck);

    /// <summary>Stop the active deck (media stays loaded).</summary>
    public void Stop() => StopDeck(ActiveDeck);

    /// <summary>Seek the active deck.</summary>
    public void Seek(TimeSpan position) => SeekDeck(ActiveDeck, position);

    /// <summary>Nudge the active deck forward.</summary>
    public void SkipForward(double seconds = 10) => Active.SkipForward(seconds);

    /// <summary>Nudge the active deck backward.</summary>
    public void SkipBack(double seconds = 10) => Active.SkipBack(seconds);

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

    /// <summary>Load a track onto a deck (non-blocking; failures arrive via StateChanged).</summary>
    public void LoadOnDeck(PlaybackDeck deck, string? primaryPath, string? karaokePath)
    {
        Channel(deck).Load(primaryPath, karaokePath);
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

        RaiseStateChanged();
    }

    /// <summary>Pause a deck (load and playhead are kept).</summary>
    public void PauseDeck(PlaybackDeck deck)
    {
        Channel(deck).Pause();
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
        RaiseStateChanged();
    }

    /// <summary>Seek a deck.</summary>
    public void SeekDeck(PlaybackDeck deck, TimeSpan position)
    {
        Channel(deck).Seek(position);
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

        var route = BuildRoute(settings);
        if (!route.Equals(_appliedRoute))
        {
            _appliedRoute = route;
            changed = true;
            _ = ApplyRouteAsync(route);
        }

        if (changed) RaiseStateChanged();
    }

    /// <summary>
    /// Settings as one six-bus engine route.
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
    private AudioRoute BuildRoute(AppSettings settings)
    {
        string? main = NullIfBlank(settings.MainOutputDeviceId);
        string? karaokeAid = NullIfBlank(settings.KaraokeAidOutputDeviceId);
        string? recording = NullIfBlank(settings.RecordingOutputDeviceId);
        string? soundboard = NullIfBlank(settings.SoundboardOutputDeviceId);
        string? monitor = NullIfBlank(settings.MonitorOutputDeviceId);
        string? bridge = NullIfBlank(settings.BridgeOutputDeviceId);

        if (!settings.AudioRoutingConfigured &&
            main == null && karaokeAid == null && recording == null &&
            soundboard == null && monitor == null && bridge == null)
        {
            main = DefaultOutputDeviceId();
            if (main != null)
                Log.Info("no audio routing configured yet: routing Main to the default output");
        }

        return new AudioRoute(main, karaokeAid, recording, soundboard, monitor, bridge);
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
            var route = BuildRoute(settings);

            Engine.MasterVolume = Math.Clamp(settings.MasterVolume, 0f, 1.2f);
            Engine.OutputDelayMs = Math.Clamp(settings.OutputDelayMs, 0, 2000);
            _appliedMasterVolume = Engine.MasterVolume;
            _appliedDelayMs = Engine.OutputDelayMs;
            AdoptTransitions(settings);

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
    /// Announce the six-bus route. The Bridge deserves the extra line: Windows has
    /// no user-mode way to expose a capture endpoint, so the bridge is an ordinary
    /// render endpoint that the user must point at a virtual cable (VB-Audio
    /// Cable/Matrix, installed on this host); other applications then pick the
    /// cable's capture side as their microphone. No virtual capture device is, or
    /// can be, created here.
    /// </summary>
    private void LogRoute(AudioRoute route)
    {
        Log.Info($"audio route: main={Describe(route.MainDeviceId)} " +
                 $"karaokeAid={Describe(route.KaraokeAidDeviceId)} " +
                 $"recording={Describe(route.RecordingDeviceId)} " +
                 $"soundboard={Describe(route.SoundboardDeviceId)} " +
                 $"monitor={Describe(route.MonitorDeviceId)} " +
                 $"bridge={Describe(route.BridgeDeviceId)} mics={Engine.Mics.Count}");

        if (route.BridgeDeviceId != null)
            Log.Info("bridge: render endpoint only (Windows cannot create a capture endpoint); " +
                     "route it to VB-Audio Cable/Matrix and use that cable's capture side as a " +
                     "microphone in the other application");
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

    private void OnDeckStateChanged(object? sender, EventArgs e) =>
        RaiseStateChanged();

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
