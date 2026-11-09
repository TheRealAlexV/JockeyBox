using System;
using JockeyBox.Audio.Endpoints;

namespace JockeyBox.Audio.Graph;

/// <summary>The six named buses of the 2.1 routing model.</summary>
public enum BusKind
{
    /// <summary>Decks (karaoke stem in karaoke mode) + soundboard when it follows Main. No microphones.</summary>
    Main,

    /// <summary>Original stems while karaoke mode is on, for the operator's headphones. Never delayed.</summary>
    KaraokeAid,

    /// <summary>Sum of unmuted recording devices.</summary>
    Recording,

    /// <summary>Soundboard alone, when the soundboard has its own device.</summary>
    Soundboard,

    /// <summary>Recording devices with Monitor on. Never delayed, never heard by the audience.</summary>
    Monitor,

    /// <summary>
    /// All sounds except monitor audio, on an ordinary render endpoint. Windows has
    /// no user-mode way to create a capture endpoint, so this must be routed to a
    /// virtual cable (VB-Audio Cable/Matrix); other apps use the cable's capture side.
    /// </summary>
    Bridge,
}

/// <summary>
/// One render bus: staging buffer → optional delay → device ring.
///
/// <para>All buses are fed from the same <see cref="AudioEngine"/> block on the one
/// render thread; each has its own <see cref="EndpointSink"/> and therefore its own
/// <see cref="JitterRing"/>, which absorbs device-clock drift per device instead of
/// letting one slow interface stall the graph.</para>
///
/// <para><see cref="Input"/> holds the signal before the bus delay (the cue buses
/// and the offline capture tap read it), <see cref="Output"/> after it — that is
/// what the device hears.</para>
/// </summary>
public sealed class AudioBus : IDisposable
{
    private readonly float[] _output;
    private readonly DelayBuffer? _delay;
    private readonly object _sinkLock = new();

    public AudioBus(BusKind kind, int sampleRate, int channels, int blockFrames)
    {
        Kind = kind;
        Input = new float[blockFrames * channels];
        _output = new float[blockFrames * channels];
        _delay = Delayed ? new DelayBuffer(sampleRate, channels) : null;
    }

    public BusKind Kind { get; }

    /// <summary>
    /// Main, Recording, Soundboard and Bridge are delayed by
    /// <c>OutputDelayMs</c>; KaraokeAid and Monitor are the operator's cue buses
    /// and stay pre-delay (exactly the rule the 2.0 monitor sink followed).
    /// </summary>
    public bool Delayed => Kind is BusKind.Main or BusKind.Recording or BusKind.Soundboard or BusKind.Bridge;

    /// <summary>Signal before the bus delay. Written by the render thread.</summary>
    public float[] Input { get; }

    /// <summary>Signal after the bus delay. Written by the render thread.</summary>
    public float[] Output => _output;

    /// <summary>The delay line for delayed buses (0 ms for cue buses, which have none).</summary>
    public int DelayMs => _delay?.DelayMs ?? 0;

    public void SetDelayMs(int milliseconds) => _delay?.SetDelayMs(milliseconds);

    /// <summary>Clear the delay history (called when a device is swapped).</summary>
    public void ResetDelay() => _delay?.Reset();

    /// <summary>The device sink for this bus, or null for "None".</summary>
    public EndpointSink? Sink
    {
        get
        {
            lock (_sinkLock) return _sink;
        }
        set
        {
            lock (_sinkLock) _sink = value;
        }
    }

    private EndpointSink? _sink;

    /// <summary>True when this bus currently has a device.</summary>
    public bool IsOpen => Sink != null;

    /// <summary>Stage the block: delay it (when the bus is delayed) and copy into Output.</summary>
    public void Render(int frames)
    {
        if (_delay == null)
        {
            Array.Copy(Input, _output, _output.Length);
            return;
        }

        _delay.Write(Input, 0, frames);
        _delay.ReadDelayed(_output, 0, frames);
    }

    /// <summary>Push the staged block to this bus's device ring, if it has one.</summary>
    public void WriteToSink(int frames) => Sink?.Write(_output, 0, frames);

    public void Dispose()
    {
        EndpointSink? sink;
        lock (_sinkLock)
        {
            sink = _sink;
            _sink = null;
        }

        try { sink?.Dispose(); } catch { }
    }
}
