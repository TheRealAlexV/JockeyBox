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
    private bool _disposed;

    private EndpointSink(MMDevice device, WasapiOut output, JitterRing ring, string deviceName,
        int deviceRate, int deviceChannels)
    {
        _device = device;
        _out = output;
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

    /// <summary>True when the device has stopped (unplugged, session lost).</summary>
    public bool HasFailed => !_disposed && _out.PlaybackState == PlaybackState.Stopped;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
