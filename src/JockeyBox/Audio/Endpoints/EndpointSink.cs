using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// One physical output: a <see cref="JitterRing"/>, the format conversion the
/// device actually wants, and a <c>WasapiOut</c> pulling from it.
///
/// <para><b>Resampling belongs here, not in the graph.</b> Shared-mode WASAPI
/// does not set <c>AUTOCONVERTPCM</c>: if we hand it a format the engine rejects,
/// <c>Init</c> throws; where it does not throw, the engine resamples anyway and
/// the result is a silent quality loss nobody can trace. Each sink therefore
/// converts from the engine's format to its own device mix format.</para>
///
/// <para>A sink is optional by design: every consumer of this type treats a null
/// or failed sink as "this output is off" (settings value "None"), never as a
/// reason to stop playback.</para>
/// </summary>
public sealed class EndpointSink : IAudioSink
{
    private readonly MMDevice _device;
    private readonly WasapiOut _out;
    private readonly DrainMonitor _drain;
    private bool _disposed;

    private EndpointSink(MMDevice device, WasapiOut output, JitterRing ring, string deviceName,
        int deviceRate, int deviceChannels)
    {
        _device = device;
        _out = output;
        _drain = new DrainMonitor(Environment.TickCount64);
        Ring = ring;
        DeviceName = deviceName;
        DeviceRate = deviceRate;
        DeviceChannels = deviceChannels;
    }

    /// <summary>The ring the engine writes blocks into.</summary>
    public JitterRing Ring { get; }

    /// <summary>Friendly name of the endpoint (for the settings UI and diagnostics).</summary>
    public string DeviceName { get; }

    public int DeviceRate { get; }

    public int DeviceChannels { get; }

    /// <summary>True while the WASAPI session is running.</summary>
    public bool IsPlaying => !_disposed && _out.PlaybackState == PlaybackState.Playing;

    /// <summary>Buffer depth in milliseconds (diagnostics / drift display).</summary>
    public double DepthMs => Ring.DepthFrames * 1000.0 / Ring.WaveFormat.SampleRate;

    /// <summary>
    /// Open an endpoint. Returns null when the device cannot be opened — a missing
    /// or unplugged output must degrade to silence, never to a crash.
    /// </summary>
    public static EndpointSink? TryOpen(string? deviceId, int engineRate, int engineChannels,
        int targetDepthMs = 120)
    {
        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            var service = new DeviceService();
            device = service.ResolveDevice(deviceId);

            var mix = device.AudioClient.MixFormat;
            int deviceRate = mix.SampleRate;
            int deviceChannels = mix.Channels;

            var ring = new JitterRing(engineRate, engineChannels)
            {
                TargetFrames = engineRate * targetDepthMs / 1000,
                LowFrames = engineRate * JitterRing.BandLowMs / 1000,
                HighFrames = engineRate * JitterRing.BandHighMs / 1000,
            };

            ISampleProvider chain = ring;
            if (deviceRate != engineRate)
                chain = new WdlResamplingSampleProvider(chain, deviceRate);
            if (deviceChannels != engineChannels)
                chain = new ChannelMapSampleProvider(chain, deviceChannels);

            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(chain.ToWaveProvider());

            var sink = new EndpointSink(device, output, ring, device.FriendlyName, deviceRate, deviceChannels);
            output.Play();
            Log.Info($"output '{device.FriendlyName}': {deviceChannels}ch @ {deviceRate} Hz " +
                     $"(engine {engineChannels}ch @ {engineRate} Hz)");
            return sink;
        }
        catch (Exception ex)
        {
            Log.Warn($"output open failed for '{deviceId ?? "<default>"}': {ex.Message}");
            try { output?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>Append one rendered block.</summary>
    public void Write(float[] interleaved, int offset, int frames) => Ring.Write(interleaved, offset, frames);

    /// <summary>Drift correction; the engine calls this about once a second.</summary>
    public void Maintain(int blockFrames) => Ring.Maintain(blockFrames);

    /// <summary>
    /// How long a device may report Running while pulling nothing from its ring
    /// before it counts as failed. The pacing sink's target depth is 120 ms, so 250 ms
    /// clears a GC stop or late wake-up with 130 ms to spare, and the resulting hole
    /// in virtual publishing (at most ~25 blocks) stays inside the ASIO ring's
    /// ~52-block cushion.
    /// </summary>
    public const int StallTimeoutMs = 250;

    /// <summary>
    /// True when the device has stopped (unplugged, session lost) or reports Playing
    /// while its consumer pulls no audio at all. The second case is the one that used
    /// to be invisible: the session stays up, the ring fills, and pacing against it
    /// stalls every bus in the graph.
    /// </summary>
    public bool HasFailed => !_disposed && (Stopped() || NotDraining());

    /// <summary>Why the sink failed; null while healthy. Set once, on first detection.</summary>
    public string? FailureReason { get; private set; }

    private bool Stopped()
    {
        if (_out.PlaybackState != PlaybackState.Stopped) return false;
        FailureReason ??= "the device stopped (unplugged or session lost)";
        return true;
    }

    private bool NotDraining()
    {
        if (!_drain.Stalled(Ring.ReadCalls, Environment.TickCount64, StallTimeoutMs)) return false;
        FailureReason ??= _drain.Reason;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}

/// <summary>
/// "Is the consumer still pulling?" as a predicate over two numbers, so it can be
/// tested without WASAPI, a device, or a thread. It watches
/// <see cref="JitterRing.ReadCalls"/> — invocations, never frames — because an
/// underrunning ring pads silence and still returns: a healthy but starved device
/// consumes zero frames and would look dead if frames were the signal.
/// </summary>
internal sealed class DrainMonitor
{
    private long _lastCalls;
    private long _lastProgressTicks;

    public DrainMonitor(long now) => _lastProgressTicks = now;

    /// <summary>Why the last <see cref="Stalled"/> call reported a stall; null until one does.</summary>
    public string? Reason { get; private set; }

    public bool Stalled(long readCalls, long now, int stallMs)
    {
        if (readCalls != _lastCalls)
        {
            _lastCalls = readCalls;
            _lastProgressTicks = now;
            return false;
        }
        if (now - _lastProgressTicks < stallMs) return false;
        Reason ??= $"the device reported Playing but pulled no audio for {stallMs} ms";
        return true;
    }
}
