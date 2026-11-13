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
    private bool _disposed;

    public DeviceOutput(string deviceId, EndpointSink sink, int blockFrames, int channels)
    {
        DeviceId = deviceId;
        Sink = sink;
        _staging = new float[blockFrames * channels];
    }

    /// <summary>The resolved endpoint id every bus here shares.</summary>
    public string DeviceId { get; }

    public EndpointSink Sink { get; }

    /// <summary>The buses summed into this device. Read-only for diagnostics.</summary>
    public IReadOnlyList<AudioBus> Buses => _buses;

    /// <summary>Peak of the last block actually written to the device.</summary>
    public float LastPeak { get; private set; }

    public void Attach(AudioBus bus)
    {
        if (!_buses.Contains(bus)) _buses.Add(bus);
    }

    /// <summary>Sum every attached bus into one block and hand it to the device once.</summary>
    public void Write(int frames)
    {
        int count = _staging.Length;
        Array.Clear(_staging, 0, count);
        foreach (var bus in _buses)
            Samples.AddScaled(_staging, bus.Output, 0, count, 1f);

        LastPeak = Samples.Peak(_staging, 0, count);
        Sink.Write(_staging, 0, frames);
    }

    public void Maintain(int blockFrames) => Sink.Maintain(blockFrames);

    public bool HasFailed => Sink.HasFailed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Sink.Dispose(); } catch { }
    }
}
