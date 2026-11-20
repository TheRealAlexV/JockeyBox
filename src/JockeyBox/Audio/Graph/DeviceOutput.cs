using System;
using System.Collections.Generic;
using JockeyBox.Audio.Endpoints;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// One physical output device: exactly one <c>WasapiOut</c> session, fed by the
/// sum of every bus routed to it.
///
/// <para><b>Why this exists.</b> The 2.1 model gave every <see cref="AudioBus"/>
/// its own <see cref="EndpointSink"/>. With four buses pointing at one endpoint
/// that opened four shared-mode sessions on one device, each carrying a
/// <i>different</i> signal, and Windows summed them: two buses that happened to
/// carry the same thing produced echo, and the device saw four independent
/// clocks where it should have seen one. Grouping by the resolved device id
/// means a device is opened once and hears one sum, which is what the operator
/// asked for ("one output session per physical device").</para>
///
/// <para>The sum happens here, in the engine's format, before the device's ring:
/// each bus still keeps its own delay line, so per-bus alignment is unchanged.</para>
/// </summary>
public sealed class DeviceOutput : IDisposable
{
    private readonly List<AudioBus> _buses = new();
    private readonly float[] _staging;
    private readonly float[] _delayed;
    private readonly DelayBuffer _delay;
    private readonly Action<float[], int, int> _write;
    private bool _disposed;

    public DeviceOutput(string deviceId, EndpointSink sink, int blockFrames, int channels,
        int sampleRate, int initialDelayMs = 0)
        : this(deviceId, (buffer, offset, frames) => sink.Write(buffer, offset, frames),
            blockFrames, channels, sampleRate, initialDelayMs)
    {
        Sink = sink;
    }

    /// <summary>
    /// The write seam the tests use: the delay sits after the sum, so it is
    /// asserted on what reaches the device, not on any one bus.
    /// </summary>
    public DeviceOutput(string deviceId, Action<float[], int, int> write, int blockFrames,
        int channels, int sampleRate, int initialDelayMs = 0)
    {
        DeviceId = deviceId;
        _write = write;
        _staging = new float[blockFrames * channels];
        _delayed = new float[blockFrames * channels];
        _delay = new DelayBuffer(sampleRate, channels, initialDelayMs);
    }

    /// <summary>The resolved endpoint id every bus here shares.</summary>
    public string DeviceId { get; }

    /// <summary>The session, or null for a sinkless (test) output.</summary>
    public EndpointSink? Sink { get; }

    /// <summary>
    /// This device's output delay in ms. Applied to the sum of every bus routed
    /// here, so all buses on the device share one offset: delay the wired path and
    /// the Bluetooth path lands with it. 0 is a true bypass.
    /// </summary>
    public int DelayMs => _delay.DelayMs;

    /// <summary>True while a delay change is crossfading (50 ms, equal power).</summary>
    public bool IsDelayCrossfading => _delay.IsCrossfading;

    /// <summary>Change the delay live; the old and new taps crossfade, never step.</summary>
    public void SetDelayMs(int milliseconds) => _delay.SetDelayMs(milliseconds);

    /// <summary>The buses summed into this device. Read-only for diagnostics.</summary>
    public IReadOnlyList<AudioBus> Buses => _buses;

    /// <summary>Peak of the last block actually written to the device.</summary>
    public float LastPeak { get; private set; }

    /// <summary>Per-channel levels and L/R correlation since the last <see cref="TakeStats"/>.</summary>
    public readonly record struct LevelStats(
        float PeakL, float PeakR, double RmsL, double RmsR, double Correlation, int Frames);

    private double _sumL2, _sumR2, _sumLR;
    private float _peakL, _peakR;
    private int _statFrames;

    /// <summary>
    /// This window's levels, then reset. An absolute-value peak cannot tell real
    /// audio from a stream whose right channel is the left one negated: both show
    /// full scale and one of them is silent on any speaker. Per-channel RMS plus
    /// the normalised correlation between the channels can tell them apart - a
    /// strongly negative correlation is inverted wiring, not a device fault.
    /// </summary>
    public LevelStats TakeStats()
    {
        double denom = Math.Sqrt(_sumL2 * _sumR2);
        var stats = new LevelStats(
            _peakL,
            _peakR,
            _statFrames > 0 ? Math.Sqrt(_sumL2 / _statFrames) : 0,
            _statFrames > 0 ? Math.Sqrt(_sumR2 / _statFrames) : 0,
            denom > 1e-12 ? _sumLR / denom : 0,
            _statFrames);

        _sumL2 = _sumR2 = _sumLR = 0;
        _peakL = _peakR = 0;
        _statFrames = 0;
        return stats;
    }

    public void Attach(AudioBus bus)
    {
        if (!_buses.Contains(bus)) _buses.Add(bus);
    }

    /// <summary>Sum every attached bus into one block, delay the sum, hand it to the device once.</summary>
    public void Write(int frames)
    {
        int count = _staging.Length;
        Array.Clear(_staging, 0, count);
        foreach (var bus in _buses)
            Samples.AddScaled(_staging, bus.Output, 0, count, 1f);

        // Stage the sum into the delay line even at 0 ms: the ring must already hold
        // history when the operator raises the delay, or the far tap would be
        // silence and the crossfade would fade the device out. The output is still
        // read straight from the staging buffer at 0, so it stays bit-identical.
        _delay.Write(_staging, 0, frames);

        var rendered = _staging;
        if (_delay.DelayMs != 0 || _delay.IsCrossfading)
        {
            _delay.ReadDelayed(_delayed, 0, frames);
            rendered = _delayed;
        }

        LastPeak = Samples.Peak(rendered, 0, count);
        Accumulate(rendered, count);
        _write(rendered, 0, frames);
    }

    /// <summary>Fold this block into the window's per-channel levels and L/R cross-product.</summary>
    private void Accumulate(float[] block, int count)
    {
        const int channels = 2;
        for (int i = 0; i + channels - 1 < count; i += channels)
        {
            float l = block[i];
            float r = block[i + 1];
            float al = Math.Abs(l), ar = Math.Abs(r);
            if (al > _peakL) _peakL = al;
            if (ar > _peakR) _peakR = ar;
            _sumL2 += (double)l * l;
            _sumR2 += (double)r * r;
            _sumLR += (double)l * r;
            _statFrames++;
        }
    }

    public void Maintain(int blockFrames) => Sink?.Maintain(blockFrames);

    public bool HasFailed => Sink?.HasFailed ?? false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Sink?.Dispose(); } catch { }
    }
}
