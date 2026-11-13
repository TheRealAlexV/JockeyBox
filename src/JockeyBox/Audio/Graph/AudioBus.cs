using System;
using JockeyBox.Audio.Endpoints;

namespace JockeyBox.Audio.Graph;

/// <summary>The five named buses of the 2.2 routing model.</summary>
public enum BusKind
{
    /// <summary>Deck A's output: the device the audience hears deck A on. No microphones, ever.</summary>
    DeckA,

    /// <summary>Deck B's output: its own device, or the same one (then both are summed into one session).</summary>
    DeckB,

    /// <summary>Sum of unmuted recording devices.</summary>
    Recording,

    /// <summary>Soundboard alone, when the soundboard has its own device.</summary>
    Soundboard,

    /// <summary>
    /// Operator cue: deck A × mon A + deck B × mon B + monitored recording devices.
    /// Not delayed, never on a deck output.
    /// </summary>
    Monitor,
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
    /// Deck A/B, Recording and Soundboard are delayed by
    /// <c>OutputDelayMs</c>; Monitor is the operator's cue and stays pre-delay
    /// (it is the only bus that is not part of what the audience hears).
    /// </summary>
    public bool Delayed => Kind is BusKind.DeckA or BusKind.DeckB or BusKind.Recording or BusKind.Soundboard;

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

    /// <summary>Release this bus's device reference (the engine owns the session).</summary>
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
