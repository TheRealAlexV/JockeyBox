using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// Which device each of the five buses is routed to. A null id means "None"
/// (the bus is silent and costs one block of copies).
///
/// <para>Two buses naming the same device are summed into <b>one</b> session for
/// that device - never two sessions carrying the same signal.</para>
/// </summary>
public sealed record AudioRoute(
    string? DeckADeviceId = null,
    string? DeckBDeviceId = null,
    string? RecordingDeviceId = null,
    string? SoundboardDeviceId = null,
    string? MonitorDeviceId = null)
{
    /// <summary>The device id for a bus, or null for None.</summary>
    public string? DeviceFor(BusKind kind) => kind switch
    {
        BusKind.DeckA => DeckADeviceId,
        BusKind.DeckB => DeckBDeviceId,
        BusKind.Recording => RecordingDeviceId,
        BusKind.Soundboard => SoundboardDeviceId,
        BusKind.Monitor => MonitorDeviceId,
        _ => null,
    };

    /// <summary>True when at least one bus has a device.</summary>
    public bool AnyOutput =>
        !string.IsNullOrWhiteSpace(DeckADeviceId) ||
        !string.IsNullOrWhiteSpace(DeckBDeviceId) ||
        !string.IsNullOrWhiteSpace(RecordingDeviceId) ||
        !string.IsNullOrWhiteSpace(SoundboardDeviceId) ||
        !string.IsNullOrWhiteSpace(MonitorDeviceId);
}

/// <summary>
/// The 2.1 playback engine: one clock, one graph, six named buses.
///
/// <para><b>Why one clock.</b> The 1.x design opened a <c>WasapiOut</c> per stem —
/// up to four independent clocks per deck pair — and drove crossfades from a 30 Hz
/// dispatcher timer. The 2.0 design reduced that to one render thread and a fixed
/// set of sinks that all received the same block. The 2.1 routing model needs six
/// <i>different</i> signals, so the fixed sinks became <see cref="AudioBus"/>
/// objects: each owns its staging buffer, its delay line and its device ring, and
/// all of them are fed from the same 480-frame block on the same render thread.
/// Nothing here opens a second clock or a parallel render loop.</para>
///
/// <para><b>Bus content</b> (this is the whole routing model in one place):
/// <list type="bullet">
/// <item><b>Main</b> — decks (karaoke stem while Karaoke mode is on) plus the
/// soundboard when it has no device of its own. Never any microphone.</item>
/// <item><b>KaraokeAid</b> — the original stems while Karaoke mode is on, silence
/// otherwise. Not delayed: it is the operator's cue.</item>
/// <item><b>Recording</b> — the sum of unmuted recording devices.</item>
/// <item><b>Soundboard</b> — the soundboard alone, when it has a device.</item>
/// <item><b>Monitor</b> — recording devices whose <c>Monitor</c> flag is on,
/// including muted ones. Never delayed, never on Main.</item>
/// <item><b>Bridge</b> — Main's deck content + soundboard + unmuted microphones,
/// i.e. everything except monitor audio, on an ordinary <i>render</i> endpoint.</item>
/// </list></para>
///
/// <para><b>The Bridge caveat.</b> Windows has no user-mode API to expose a
/// <i>capture</i> endpoint, so the Bridge is a normal render device: the operator
/// points it at a virtual cable (VB-Audio Cable / Matrix) and other applications
/// select the cable's capture side as their microphone. JockeyBox does not create
/// a virtual device.</para>
///
/// <para><b>Signal order.</b> Decks → sum → soft limiter → master fader. The
/// microphones are summed after the master fader (the music fader must not duck a
/// voice) and never enter Main. Delay: Main, Recording, Soundboard and Bridge use
/// <c>OutputDelayMs</c>; KaraokeAid and Monitor are cue buses and are pre-delay.
/// The UI clock reports the delayed (audible) position.</para>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>Engine sample rate. Fixed so every deck shares one frame index; sinks convert.</summary>
    public const int Rate = 48_000;

    /// <summary>Engine channel count (stereo).</summary>
    public const int Channels = 2;

    /// <summary>Frames per render block (10 ms at 48 kHz).</summary>
    public const int BlockFrames = 480;

    private static readonly BusKind[] BusOrder =
    {
        BusKind.DeckA, BusKind.DeckB, BusKind.Recording,
        BusKind.Soundboard, BusKind.Monitor,
    };

    private readonly object _sinkLock = new();
    private readonly AudioBus[] _buses;

    private readonly float[] _deckABuffer = new float[BlockFrames * Channels];
    private readonly float[] _deckBBuffer = new float[BlockFrames * Channels];
    private readonly float[] _karaokeABuffer = new float[BlockFrames * Channels];
    private readonly float[] _karaokeBBuffer = new float[BlockFrames * Channels];
    private readonly float[] _deckMix = new float[BlockFrames * Channels];
    private readonly float[] _soundboardBlock = new float[BlockFrames * Channels];
    private readonly float[] _soundboardScaled = new float[BlockFrames * Channels];
    private readonly float[] _micRecording = new float[BlockFrames * Channels];
    private readonly float[] _micMonitor = new float[BlockFrames * Channels];
    private readonly float[] _programTap = new float[BlockFrames * Channels];

    private Thread? _thread;
    private volatile bool _running;
    private long _frameIndex;

    /// <summary>
    /// One entry per distinct physical device, in routing order. Each holds the
    /// single <c>WasapiOut</c> session opened for that device and every bus summed
    /// into it.
    /// </summary>
    private readonly List<DeviceOutput> _outputs = new();

    /// <summary>
    /// The first open device sink. Only used as the "is there any device at all?"
    /// sentinel for Start()/failure detection - it is no longer a program sink.
    /// </summary>
    private EndpointSink? _firstSink;
    private AudioRoute _route = new();

    private readonly ScheduledActions _scheduled = new();

    private volatile float _masterVolume = 1f;
    private float _soundboardVolume = 1f;
    private float _duck = 1f;
    private volatile float _deckAVolume = 1f;
    private volatile float _deckBVolume = 1f;
    private double _lastMaintainSeconds;

    private volatile bool _soundboardFollowsMain = true;

    private float _legacyMicVolume = 1f;

    // Monitor (operator cue) deck sends. Both decks default to -3 dB, i.e. audible
    // but not the same level as what the audience hears; MON is off by default so
    // an install with no headphones does not send deck audio to the cue device.
    private volatile bool _monitorEnabled;
    private float _monitorAGain = 0.7f;
    private float _monitorBGain = 0.7f;

    // ---- audio trace -------------------------------------------------------
    // "Nothing in the process ever proves a non-zero sample reached a device"
    // was the 2.1 blind spot. These counters are cheap (one pass per block),
    // always on, and reported once a second, so a silent player says where it
    // went silent instead of leaving the operator with a moving clock.
    private long _traceLastFrame;
    private float _traceDeckPre;
    private float _traceDeckPost;
    private float _traceDeckA;
    private float _traceDeckB;
    private readonly float[] _traceBusPeak = new float[5];

    public AudioEngine()
    {
        DeckA = new DeckSource(Rate, Channels);
        DeckB = new DeckSource(Rate, Channels);
        Soundboard = new SoundboardBus(Rate, Channels);
        Fader = new FaderEnvelope();
        Mics = new MicMixer();

        DeckA.GainSource = new CrossfaderGain(this, isDeckA: true);
        DeckB.GainSource = new CrossfaderGain(this, isDeckA: false);

        // Each deck is read once per block, pre-crossfader; the crossfade is applied
        // at fan-out so the monitor tap can hear a deck the fader has turned down.
        DeckA.PreFader = true;
        DeckB.PreFader = true;

        _buses = new AudioBus[BusOrder.Length];
        foreach (var kind in BusOrder) _buses[(int)kind] = new AudioBus(kind, Rate, Channels, BlockFrames);
    }

    public DeckSource DeckA { get; }

    public DeckSource DeckB { get; }

    /// <summary>Soundboard voices. Rendered into the Soundboard bus and/or Main/Bridge.</summary>
    public SoundboardBus Soundboard { get; }

    /// <summary>The crossfader: 0 = deck A, 1 = deck B. Ramped in engine frames.</summary>
    public FaderEnvelope Fader { get; }

    /// <summary>The recording-device collection: one strip per enabled device.</summary>
    public MicMixer Mics { get; }

    /// <summary>One deck plus which side of the crossfader it is on.</summary>
    public readonly record struct DeckSlot(DeckSource Deck, bool IsA);

    /// <summary>Deck A as a slot (for the transition scheduler).</summary>
    public DeckSlot SlotA => new(DeckA, true);

    /// <summary>Deck B as a slot.</summary>
    public DeckSlot SlotB => new(DeckB, false);

    /// <summary>Frame-scheduled actions owned by the render thread.</summary>
    public ScheduledActions ScheduledActions => _scheduled;

    /// <summary>Current engine frame; monotonic while the engine runs.</summary>
    public long FrameIndex => Interlocked.Read(ref _frameIndex);

    /// <summary>True while the render thread is alive.</summary>
    public bool IsRunning => _running;

    /// <summary>The route currently applied.</summary>
    public AudioRoute Route => _route;

    /// <summary>A bus by kind (its content is only meaningful on the render thread).</summary>
    public AudioBus Bus(BusKind kind) => _buses[(int)kind];

    /// <summary>
    /// When true (the default) the soundboard is mixed into Main if it has no
    /// device of its own; when it does have a device it goes there instead.
    /// </summary>
    public bool SoundboardFollowsMain
    {
        get => _soundboardFollowsMain;
        set => _soundboardFollowsMain = value;
    }

    /// <summary>Raised when an output disappears mid-session (unplugged device).</summary>
    public event Action<string>? OutputFault;

    // ------------------------------------------------------------------ levels

    /// <summary>Master fader (0..1.2). Applies to decks, soundboard and karaoke aid - never to voices.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1.2f);
    }

    /// <summary>Soundboard bus gain (0..1.2).</summary>
    public float SoundboardVolume
    {
        get => _soundboardVolume;
        set => _soundboardVolume = Math.Clamp(value, 0f, 1.2f);
    }

    /// <summary>End-of-track ducking factor applied to both decks (soundboard / monitor dim).</summary>
    public float Duck
    {
        get => _duck;
        set => _duck = Math.Clamp(value, 0f, 1f);
    }

    public float DeckAVolume
    {
        get => _deckAVolume;
        set => _deckAVolume = Math.Clamp(value, 0f, 1.2f);
    }

    public float DeckBVolume
    {
        get => _deckBVolume;
        set => _deckBVolume = Math.Clamp(value, 0f, 1.2f);
    }

    /// <summary>
    /// MON: when true the Monitor bus carries deck A × <see cref="MonitorAGain"/>
    /// plus deck B × <see cref="MonitorBGain"/> (and monitored recording devices);
    /// when false the cue device gets the monitored microphones only. A view model
    /// binds this and the two gains.
    /// </summary>
    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set => _monitorEnabled = value;
    }

    /// <summary>Deck A's send into the Monitor bus (0..1.2). Audible by default.</summary>
    public float MonitorAGain
    {
        get => _monitorAGain;
        set => _monitorAGain = Math.Clamp(value, 0f, 1.2f);
    }

    /// <summary>Deck B's send into the Monitor bus (0..1.2). Audible by default.</summary>
    public float MonitorBGain
    {
        get => _monitorBGain;
        set => _monitorBGain = Math.Clamp(value, 0f, 1.2f);
    }

    /// <summary>
    /// Global output delay in ms. Applies to Deck A, Deck B, Recording and
    /// Soundboard; Monitor is the operator's cue and stays pre-delay.
    /// </summary>
    public int OutputDelayMs
    {
        get => _buses[(int)BusKind.DeckA].DelayMs;
        set
        {
            foreach (var bus in _buses)
                if (bus.Delayed) bus.SetDelayMs(value);
        }
    }

    /// <summary>Peak of every recording device's last block, index-stable for the mixer meters.</summary>
    public IReadOnlyList<float> MicPeaks
    {
        get
        {
            var strips = Mics.Strips;
            var peaks = new float[strips.Count];
            for (int i = 0; i < strips.Count; i++) peaks[i] = strips[i].Peak;
            return peaks;
        }
    }

    /// <summary>
    /// Peak of one recording device (0 when the index is out of range). Named
    /// separately from the legacy aggregate <see cref="MicPeak"/> property because
    /// C# does not allow a property and a method to share a name.
    /// </summary>
    public float MicPeakAt(int index) => Mics.PeakAt(index);

    /// <summary>Legacy aggregate meter: the loudest recording device.</summary>
    public float MicPeak
    {
        get
        {
            float peak = 0f;
            foreach (var strip in Mics.Strips) if (strip.Peak > peak) peak = strip.Peak;
            return peak;
        }
    }

    /// <summary>
    /// Legacy single-strip gain: applies to every device. The 2.1 UI sets
    /// <see cref="MicStrip.Volume"/> per device instead.
    /// </summary>
    public float MicVolume
    {
        get => _legacyMicVolume;
        set
        {
            _legacyMicVolume = Math.Clamp(value, 0f, 2f);
            foreach (var strip in Mics.Strips) strip.Volume = _legacyMicVolume;
        }
    }

    /// <summary>Legacy single-strip mute: applies to every device.</summary>
    public bool MicMuted
    {
        get
        {
            foreach (var strip in Mics.Strips) if (!strip.Muted) return false;
            return Mics.Strips.Count > 0;
        }
        set
        {
            foreach (var strip in Mics.Strips) strip.Muted = value;
        }
    }

    /// <summary>Legacy single-strip monitor send: applies to every device.</summary>
    public bool MicMonitor
    {
        get
        {
            foreach (var strip in Mics.Strips) if (!strip.Monitor) return false;
            return Mics.Strips.Count > 0;
        }
        set
        {
            foreach (var strip in Mics.Strips) strip.Monitor = value;
        }
    }

    /// <summary>Run <paramref name="action"/> on the render thread at exactly <paramref name="frame"/>.</summary>
    public void ScheduleAt(long frame, Action action) => _scheduled.Add(frame, action);

    /// <summary>Run <paramref name="action"/> once the deck has consumed all its media.</summary>
    public void ScheduleAfterMedia(DeckSource deck, Action action) =>
        _scheduled.AddMediaEnd(deck, action);

    /// <summary>One-line bus description: every bus and its device (diagnostics / the route log).</summary>
    public string DescribeBuses()
    {
        string Fmt(BusKind kind)
        {
            var sink = _buses[(int)kind].Sink;
            return sink == null ? "None" : $"{sink.DeviceName}@{sink.DepthMs:0}ms";
        }

        return $"deckA={Fmt(BusKind.DeckA)} deckB={Fmt(BusKind.DeckB)} " +
               $"recording={Fmt(BusKind.Recording)} soundboard={Fmt(BusKind.Soundboard)} " +
               $"monitor={Fmt(BusKind.Monitor)}";
    }

    /// <summary>Alias kept for callers of the 2.0 API.</summary>
    public string DescribeOutputs() => DescribeBuses();

    // ------------------------------------------------------------------ offline rendering

    /// <summary>
    /// Render without a device. <paramref name="capture"/> receives the Main bus
    /// (delayed, i.e. exactly what the audience gets) and
    /// <paramref name="monitorCapture"/> the pre-delay Main content — the cue tap
    /// the 2.0 monitor sink used to expose, kept because it is what proves the
    /// delay is present without a sound card.
    ///
    /// <para><paramref name="busCapture"/>, when given, receives every bus's
    /// rendered block with its kind: the only way to assert the six-bus routing
    /// without opening six devices. It also runs in live mode, but is intended for
    /// offline tests.</para>
    /// </summary>
    public void StartOffline(Action<float[], int> capture, double seconds = 0,
        Action<float[], int>? monitorCapture = null,
        Action<BusKind, float[], int>? busCapture = null)
    {
        _capture = capture;
        _captureMonitor = monitorCapture;
        _busCapture = busCapture;
        _offlineSeconds = seconds;
        _offlineFrames = 0;
        _running = true;
        _thread = new Thread(OfflineLoop)
        {
            IsBackground = true,
            Name = "JockeyBox.Audio.Offline",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private Action<float[], int>? _capture;
    private Action<float[], int>? _captureMonitor;

    /// <summary>
    /// Optional listener for the post-fan-out program mix (deck A bus + deck B bus),
    /// one block per call. Used by <c>--audiotrace --wav</c> to write what the decks
    /// actually produce to a file the operator can play.
    /// </summary>
    public Action<float[], int>? ProgramCapture { get; set; }
    private Action<BusKind, float[], int>? _busCapture;
    private double _offlineSeconds;
    private long _offlineFrames;

    private void OfflineLoop()
    {
        long total = _offlineSeconds <= 0 ? long.MaxValue : FramesFor(_offlineSeconds);
        while (_running && _offlineFrames < total)
        {
            try
            {
                RenderBlock();
                _offlineFrames += BlockFrames;
            }
            catch (Exception ex)
            {
                Log.Error("offline render failed", ex);
                break;
            }
        }
        _running = false;
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Open the configured route: <b>one session per distinct device</b>. Devices
    /// are opened off the render thread and swapped in, so a slow USB interface
    /// cannot stall playback. Two buses naming the same endpoint share one
    /// <see cref="DeviceOutput"/> and are summed into it.
    /// </summary>
    public async Task SetRouteAsync(AudioRoute route)
    {
        var built = await Task.Run(() =>
        {
            var outputs = new List<DeviceOutput>();
            var byDevice = new Dictionary<string, DeviceOutput>(StringComparer.OrdinalIgnoreCase);

            foreach (var kind in BusOrder)
            {
                var id = route.DeviceFor(kind);
                if (string.IsNullOrWhiteSpace(id)) continue;

                if (!byDevice.TryGetValue(id!, out var output))
                {
                    EndpointSink? sink;
                    try { sink = EndpointSink.TryOpen(id, Rate, Channels); }
                    catch (Exception ex)
                    {
                        Log.Warn($"audio device '{id}' failed to open: {ex.Message}");
                        sink = null;
                    }
                    if (sink == null) continue;

                    output = new DeviceOutput(id!, sink, BlockFrames, Channels);
                    byDevice.Add(id!, output);
                    outputs.Add(output);
                }

                output.Attach(_buses[(int)kind]);
            }

            return (Outputs: outputs, ByDevice: byDevice);
        }).ConfigureAwait(false);

        List<DeviceOutput> old;
        lock (_sinkLock)
        {
            old = new List<DeviceOutput>(_outputs);
            _outputs.Clear();
            foreach (var output in built.Outputs)
            {
                _outputs.Add(output);
                foreach (var bus in output.Buses) bus.Sink = output.Sink;
            }

            // A bus with no device in the new route is off, not left pointing at a
            // sink that is about to be disposed.
            foreach (var bus in _buses)
            {
                var id = route.DeviceFor(bus.Kind);
                if (string.IsNullOrWhiteSpace(id) ||
                    !built.ByDevice.TryGetValue(id!, out var target) ||
                    !target.Buses.Contains(bus))
                    bus.Sink = null;
            }

            _route = route;
            _firstSink = FirstOpenLocked();
        }

        foreach (var output in old) DisposeQuietly(output);

        // A reopened device starts with an empty history; keeping the old tail
        // would replay up to OutputDelayMs of audio into the new device.
        foreach (var bus in _buses) bus.ResetDelay();

        if (built.Outputs.Count > 0)
            Log.Info($"audio route: {built.Outputs.Count} device session(s) for " +
                     $"{BusOrder.Count(k => !string.IsNullOrWhiteSpace(route.DeviceFor(k)))} routed bus(es)");

        // The route line lives in DualDeckTransport.BuildRouteFromAppSettings/LogRoute:
        // it is the only place that knows the friendly names.
    }

    /// <summary>Start the render thread. No-op when no bus has a device.</summary>
    public void Start()
    {
        if (_running) return;

        lock (_sinkLock)
        {
            _firstSink = FirstOpenLocked();
            if (_firstSink == null)
            {
                Log.Warn("audio engine not started: every bus is set to None");
                return;
            }
        }

        _running = true;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "JockeyBox.Audio",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        Log.Info("audio engine started");
    }

    /// <summary>Stop the render thread and close every device session.</summary>
    public void Stop()
    {
        _running = false;
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _thread = null;

        List<DeviceOutput> old;
        lock (_sinkLock)
        {
            old = new List<DeviceOutput>(_outputs);
            _outputs.Clear();
            foreach (var bus in _buses) bus.Sink = null;
            _firstSink = null;
        }

        foreach (var output in old) DisposeQuietly(output);
    }

    private EndpointSink? FirstOpenLocked()
    {
        // Deck A first: when it is present it should set the pace, so a paused deck
        // still gets pulled in time with the device the audience hears.
        foreach (var output in _outputs) return output.Sink;
        return null;
    }

    // ------------------------------------------------------------------ fader / tempo helpers

    /// <summary>Seconds → engine frames.</summary>
    public static long FramesFor(double seconds) => (long)Math.Round(seconds * Rate);

    /// <summary>Engine frames → seconds.</summary>
    public static double SecondsFor(long frames) => frames / (double)Rate;

    /// <summary>Jump the fader to a position at the next block (a snap, not a ramp).</summary>
    public void SetFader(double position) =>
        Fader.Snap(Crossfade.Clamp01(position), FrameIndex + BlockFrames);

    /// <summary>Ramp the fader over a duration, starting at the next block.</summary>
    public void RampFader(double to, double seconds, double? from = null)
    {
        long start = FrameIndex + BlockFrames;
        Fader.Ramp(from ?? Fader.FaderAt(start), Crossfade.Clamp01(to), start, FramesFor(Math.Max(0, seconds)));
    }

    /// <summary>Frame at which a ramp starting now would finish.</summary>
    public long FrameForRamp(double seconds) => FrameIndex + FramesFor(seconds);

    /// <summary>
    /// What the audience hears: the deck position minus the global output delay.
    /// Every user-facing readout uses this, never the deck's own position, or the
    /// lyrics and waveform would lead the OBS video by exactly the delay.
    /// </summary>
    public double AudibleSeconds(DeckSource deck)
    {
        double delay = _buses[(int)BusKind.DeckA].DelayMs / 1000.0;
        return Math.Max(0, deck.PositionSeconds - delay);
    }

    // ------------------------------------------------------------------ render loop

    private void RenderLoop()
    {
        Log.Info($"audio render thread up: {Rate} Hz, {Channels} ch, block {BlockFrames} frames");
        while (_running)
        {
            try
            {
                var firstSink = _firstSink;
                if (firstSink == null)
                {
                    Thread.Sleep(50);
                    continue;
                }

                if (firstSink.HasFailed)
                {
                    OutputFault?.Invoke(firstSink.DeviceName);
                    lock (_sinkLock)
                    {
                        var dead = _outputs.FirstOrDefault(o => ReferenceEquals(o.Sink, firstSink));
                        if (dead != null)
                        {
                            _outputs.Remove(dead);
                            foreach (var bus in dead.Buses) bus.Sink = null;
                            DisposeQuietly(dead);
                        }
                        else
                        {
                            DisposeQuietly(firstSink);
                        }
                        _firstSink = FirstOpenLocked();
                    }
                    continue;
                }

                // Only render when the first device's clock wants data. Every other
                // bus is corrected for drift against this one clock, never the other way.
                if (firstSink.Ring.DepthFrames >= firstSink.Ring.TargetFrames)
                {
                    Thread.Sleep(2);
                    continue;
                }

                RenderBlock();
                MaintainSinks();
            }
            catch (Exception ex)
            {
                Log.Error("audio render failed", ex);
                Thread.Sleep(50);
            }
        }
        Log.Info("audio render thread down");
    }

    private void RenderBlock()
    {
        long frame = Interlocked.Read(ref _frameIndex);

        // 0. Anything scheduled for this frame runs before the decks are pulled, so
        //    a transition that starts "on the beat" starts on that block.
        _scheduled.Drain(frame, DeckA, DeckB);

        bool karaoke = false; // the global karaoke mode is gone; decks render one file
        DeckA.FrameCursor = frame;
        DeckB.FrameCursor = frame;
        DeckA.KaraokeSplit = karaoke;
        DeckB.KaraokeSplit = karaoke;

        // 1. Decks. In karaoke mode a deck is read twice (karaoke stem to Main,
        //    original to KaraokeAid) but its clock advances once, inside ReadKaraoke.
        RenderDeck(DeckA, _deckABuffer, _karaokeABuffer, karaoke);
        RenderDeck(DeckB, _deckBBuffer, _karaokeBBuffer, karaoke);

        // 2. Soundboard, always rendered so its own gain and meter stay live.
        Array.Clear(_soundboardBlock, 0, _soundboardBlock.Length);
        Soundboard.Render(_soundboardBlock, 0, BlockFrames, _soundboardVolume);
        Samples.SoftLimit(_soundboardBlock, 0, _soundboardBlock.Length);

        // 3. Microphones: one read per device, three sums (recording / monitor /
        //    none). Main is deliberately not among them.
        Mics.Render(BlockFrames, _micRecording, _micMonitor, Channels);

        float master = _masterVolume;

        // 4. Program fan-out. Each deck was rendered ONCE, pre-crossfader (deck
        //    volume and duck are in the block, the crossfader is not). The crossfade
        //    is applied here, per frame, so the same block feeds the program bus and
        //    a monitor tap that ignores the fader - and no deck is clocked twice.
        var deckABus = _buses[(int)BusKind.DeckA];
        var deckBBus = _buses[(int)BusKind.DeckB];
        var deckAOut = deckABus.Input;
        var deckBOut = deckBBus.Input;
        ApplyCrossfade(_deckABuffer, deckAOut, frame, isDeckA: true);
        ApplyCrossfade(_deckBBuffer, deckBOut, frame, isDeckA: false);

        // The trace's deck mix is the program sum: both decks post-crossfade.
        for (int i = 0; i < _deckMix.Length; i++) _deckMix[i] = deckAOut[i] + deckBOut[i];
        // Two 0.707 centre decks would reach 1.414, so limit before the master fader.
        _traceDeckPre = Samples.Peak(_deckMix, 0, _deckMix.Length);
        _traceDeckA = Samples.Peak(_deckABuffer, 0, _deckABuffer.Length);
        _traceDeckB = Samples.Peak(_deckBBuffer, 0, _deckBBuffer.Length);
        Samples.SoftLimit(_deckMix, 0, _deckMix.Length);
        _traceDeckPost = Samples.Peak(_deckMix, 0, _deckMix.Length);
        if (master != 1f)
            for (int i = 0; i < _deckMix.Length; i++) _deckMix[i] *= master;

        for (int i = 0; i < _soundboardScaled.Length; i++)
            _soundboardScaled[i] = _soundboardBlock[i] * master;

        // 5. Deck A output: deck A, plus the soundboard when it has no device of its
        //    own. No microphones, ever. Deck B is its own bus; when both name one
        //    physical device the DeviceOutput sums them into one session.
        var soundboardBus = _buses[(int)BusKind.Soundboard];
        bool soundboardOnDeckA = _soundboardFollowsMain && !soundboardBus.IsOpen;
        if (soundboardOnDeckA)
        {
            for (int i = 0; i < deckAOut.Length; i++) deckAOut[i] += _soundboardScaled[i];
            // One limiter over the combined bus: the soundboard can push the sum
            // over the knee, exactly like a second deck would.
            Samples.SoftLimit(deckAOut, 0, deckAOut.Length);
        }

        // 6. Deck B output: deck B's render. This bus used to be the KaraokeAid cue
        //    and was cleared every block, because the global karaoke mode that fed
        //    it is gone. It is now deck B's real output and carries deck B.
        Samples.SoftLimit(deckBOut, 0, deckBOut.Length);

        if (master != 1f)
        {
            for (int i = 0; i < deckAOut.Length; i++) deckAOut[i] *= master;
            for (int i = 0; i < deckBOut.Length; i++) deckBOut[i] *= master;
        }

        // 7. Soundboard bus: the soundboard alone, when it has a device.
        var soundboardInput = soundboardBus.Input;
        if (soundboardBus.IsOpen) Array.Copy(_soundboardScaled, soundboardInput, soundboardInput.Length);
        else Array.Clear(soundboardInput, 0, soundboardInput.Length);

        // 8. Recording bus: unmuted microphones.
        var recording = _buses[(int)BusKind.Recording].Input;
        Array.Copy(_micRecording, recording, recording.Length);
        Samples.SoftLimit(recording, 0, recording.Length);

        // 9. Monitor bus: the operator's cue. Deck A × mon A + deck B × mon B while
        //    MON is on, plus monitored recording devices (muted or not - the mute
        //    silences the audience, not the operator's own cue).
        var monitor = _buses[(int)BusKind.Monitor].Input;
        if (_monitorEnabled)
        {
            for (int i = 0; i < monitor.Length; i++)
                monitor[i] = _deckABuffer[i] * _monitorAGain
                           + _deckBBuffer[i] * _monitorBGain
                           + _micMonitor[i];
            Samples.SoftLimit(monitor, 0, monitor.Length);
        }
        else
        {
            Array.Copy(_micMonitor, monitor, monitor.Length);
            Samples.SoftLimit(monitor, 0, monitor.Length);
        }

        // 10. Stage each bus through its own delay (cue buses bypass it), then feed
        //     the device rings from the same block.
        foreach (var bus in _buses)
        {
            bus.Render(BlockFrames);
            _traceBusPeak[(int)bus.Kind] = Samples.Peak(bus.Input, 0, BlockFrames * Channels);
        }

        if (_capture != null || ProgramCapture != null)
        {
            var program = ProgramTap(deckABus, deckBBus);
            _capture?.Invoke(program, BlockFrames);
            ProgramCapture?.Invoke(program, BlockFrames);
        }
        _captureMonitor?.Invoke(monitor, BlockFrames);
        if (_busCapture != null)
            foreach (var bus in _buses) _busCapture(bus.Kind, bus.Output, BlockFrames);

        // 11. One write per physical device: every bus routed there is summed first,
        //     so a device never receives two sessions carrying overlapping signal.
        foreach (var output in _outputs) output.Write(BlockFrames);

        TraceIfDue(frame);

        Interlocked.Exchange(ref _frameIndex, frame + BlockFrames);
    }

    /// <summary>
    /// Once a second, write one line that answers "is there signal, and did it get
    /// to a device?". Always on: the numbers are the only thing that distinguishes
    /// a device-side fault from a graph fault, and the render thread is the only
    /// place that knows both.
    /// </summary>
    private void TraceIfDue(long frame)
    {
        if (frame - _traceLastFrame < Rate) return;
        _traceLastFrame = frame;

        var sb = new System.Text.StringBuilder(320);
        sb.Append("audio trace: ")
          .Append($"A={DeckA.State}@{DeckA.PositionSeconds:0.00} ")
          .Append($"B={DeckB.State}@{DeckB.PositionSeconds:0.00} ")
          .Append($"deckApre={_traceDeckA:0.0000} deckBpre={_traceDeckB:0.0000} ")
          .Append($"deckMix pre={_traceDeckPre:0.0000} post={_traceDeckPost:0.0000} ")
          .Append($"master={_masterVolume:0.00} fader={Fader.FaderAt(frame):0.00} ");

        foreach (var kind in BusOrder)
        {
            var bus = _buses[(int)kind];
            sb.Append($"{kind}[bus={_traceBusPeak[(int)kind]:0.0000} dev={(bus.IsOpen ? "open" : "none")}] ");
        }

        foreach (var output in _outputs)
        {
            var sink = output.Sink;
            var stats = output.TakeStats();
            sb.Append($"device '{sink.DeviceName}' [sink={output.LastPeak:0.0000} " +
                      $"L={stats.PeakL:0.0000} R={stats.PeakR:0.0000} " +
                      $"rmsL={stats.RmsL:0.0000} rmsR={stats.RmsR:0.0000} " +
                      $"corr={stats.Correlation:+0.000;-0.000;0.000} " +
                      $"buses={string.Join('+', output.Buses.Select(b => b.Kind.ToString()))} " +
                      $"depth={sink.Ring.DepthFrames}f under={sink.Ring.UnderrunFrames} " +
                      $"dropped={sink.Ring.DroppedBlocks} playing={sink.IsPlaying}] ");
        }

        Log.Info(sb.ToString());
    }

    /// <summary>
    /// Copy one deck block to its program bus with the crossfader's per-frame gain
    /// applied. Deck volume and duck are already in the source block (the deck is
    /// rendered pre-crossfader), which is what lets the monitor tap read the same
    /// block without the fader.
    /// </summary>
    private void ApplyCrossfade(float[] source, float[] target, long firstFrame, bool isDeckA)
    {
        for (int f = 0; f < BlockFrames; f++)
        {
            double fader = Fader.FaderAt(firstFrame + f);
            float gain = (float)(isDeckA ? Crossfade.GainA(fader) : Crossfade.GainB(fader));
            int baseIdx = f * Channels;
            for (int c = 0; c < Channels; c++) target[baseIdx + c] = source[baseIdx + c] * gain;
        }
    }

    /// <summary>
    /// The offline verification tap's "program": both deck buses summed, which is
    /// what the audience hears as a whole now that each deck has its own bus and
    /// its own device session. Only built when a capture is attached.
    /// </summary>
    private float[] ProgramTap(AudioBus deckA, AudioBus deckB)
    {
        Array.Copy(deckA.Output, _programTap, _programTap.Length);
        for (int i = 0; i < _programTap.Length; i++) _programTap[i] += deckB.Output[i];
        return _programTap;
    }

    /// <summary>
    /// Read one deck into its Main content and (in karaoke mode, when it has a
    /// karaoke stem) its KaraokeAid content.
    /// </summary>
    private static void RenderDeck(DeckSource deck, float[] mainBuffer, float[] aidBuffer, bool karaoke)
    {
        if (karaoke && deck.HasKaraoke)
        {
            // Karaoke read first: it is the one that advances the clock.
            deck.ReadKaraoke(mainBuffer, 0, mainBuffer.Length);
            deck.ReadOriginal(aidBuffer, 0, aidBuffer.Length);
        }
        else
        {
            deck.Read(mainBuffer, 0, mainBuffer.Length);
            Array.Clear(aidBuffer, 0, aidBuffer.Length);
        }
    }

    private void MaintainSinks()
    {
        double now = SecondsFor(FrameIndex);
        if (now - _lastMaintainSeconds < 0.5) return;
        _lastMaintainSeconds = now;

        // One maintain per device session: two buses on one device must not each
        // count as a consumer of its ring, or the drift correction fights itself.
        foreach (var output in _outputs) output.Maintain(BlockFrames);

        // Capture devices run on their own clocks too; the same drift rule keeps
        // each ring inside its band (and pads with silence rather than stuttering).
        Mics.Maintain(BlockFrames);
    }

    private static void DisposeQuietly(EndpointSink? sink)
    {
        try { sink?.Dispose(); } catch { }
    }

    private static void DisposeQuietly(DeviceOutput? output)
    {
        try { output?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        foreach (var bus in _buses) bus.Dispose();
        Soundboard.Dispose();
        DeckA.Dispose();
        DeckB.Dispose();
    }

    /// <summary>Per-frame gain for one deck: crossfader envelope × deck volume × duck.</summary>
    private sealed class CrossfaderGain : IDeckGainSource
    {
        private readonly AudioEngine _engine;
        private readonly bool _isA;

        public CrossfaderGain(AudioEngine engine, bool isDeckA)
        {
            _engine = engine;
            _isA = isDeckA;
        }

        public float GainAt(long engineFrame)
        {
            double fader = _engine.Fader.FaderAt(engineFrame);
            double cross = _isA ? Crossfade.GainA(fader) : Crossfade.GainB(fader);
            return (float)(cross * BaseGainAt(engineFrame));
        }

        /// <summary>Deck volume × duck: everything the program gain has except the crossfader.</summary>
        public float BaseGainAt(long engineFrame)
        {
            double volume = _isA ? _engine._deckAVolume : _engine._deckBVolume;
            return (float)(volume * _engine._duck);
        }
    }
}
