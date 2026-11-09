using System;
using System.Collections.Generic;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// One recording device in the mixer: its capture, its app-domain gain, its mute
/// and its monitor send. Volume, mute and monitor are all app-domain; the Windows
/// recording level (<c>IAudioEndpointVolume</c>) is never touched.
///
/// <para><b>Mute vs Monitor.</b> Mute removes the device from everywhere the
/// audience can hear (Recording, Bridge): a muted device is silent. Monitor is an
/// <i>additional</i> send to the operator's Monitor bus, and it survives mute -
/// muting your own vocal must not remove your own cue. A device with Monitor off
/// (the default) never reaches Monitor.</para>
/// </summary>
public sealed class MicStrip : IDisposable
{
    private readonly IMicSource? _source;
    private readonly MicCapture? _capture;
    private int _started;
    private int _failedLogged;
    private volatile float _volume = 1f;
    private volatile bool _muted;
    private volatile bool _monitor;
    private volatile float _peak;

    /// <param name="deviceId">Capture endpoint id; empty means "system default".</param>
    public MicStrip(string deviceId)
    {
        DeviceId = deviceId ?? "";
        _capture = new MicCapture(DeviceId);
        _source = _capture;
    }

    /// <summary>Test hook: a synthetic source (no hardware, no thread).</summary>
    internal MicStrip(string deviceId, IMicSource source)
    {
        DeviceId = deviceId ?? "";
        _source = source;
    }

    /// <summary>The capture endpoint id this strip is bound to.</summary>
    public string DeviceId { get; }

    /// <summary>The endpoint's friendly name once opened (the device list's label otherwise).</summary>
    public string DeviceName => _capture?.DeviceName ?? DeviceId;

    /// <summary>The device's native mix rate, for the mixer's rate label (0 when unknown).</summary>
    public int MixRate => _capture?.DeviceRate ?? 0;

    /// <summary>True when the capture actually opened; false means "failed, skipped".</summary>
    public bool IsOpen => _capture == null || _capture.IsCapturing;

    /// <summary>The capture this strip owns (null for a synthetic test strip).</summary>
    public MicCapture? Capture => _capture;

    /// <summary>App-domain gain, 0..2 (0..200%).</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 2f);
    }

    /// <summary>Silences this device everywhere the audience hears it. Monitor is unaffected.</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Send this device to the Monitor bus (operator only). Default false.</summary>
    public bool Monitor
    {
        get => _monitor;
        set => _monitor = value;
    }

    /// <summary>Peak of the last block, post-gain (0..1+), for the mixer meter.</summary>
    public float Peak => _peak;

    /// <summary>Open the capture. Never throws; a device that fails is logged once and reads silence.</summary>
    public void Start()
    {
        if (_capture == null) return;
        if (System.Threading.Interlocked.Exchange(ref _started, 1) != 0) return;

        try
        {
            if (!_capture.Start())
            {
                if (VolatileFlag.First(ref _failedLogged))
                    Log.Warn($"mic: device '{DeviceId}' is not available, skipped");
            }
        }
        catch (Exception ex)
        {
            if (VolatileFlag.First(ref _failedLogged))
                Log.Warn($"mic: device '{DeviceId}' failed to open ({ex.Message}), skipped");
        }
    }

    internal void SetPeak(float peak) => _peak = peak;

    internal int Read(float[] buffer, int offset, int count)
    {
        if (_source == null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        Samples.ReadFull(_source, buffer, offset, count);
        return count;
    }

    internal void Maintain(int blockFrames) => _source?.Maintain(blockFrames);

    public void Dispose()
    {
        try { (_capture as IDisposable)?.Dispose(); } catch { }
    }
}

/// <summary>
/// The recording-device collection: one capture and one ring per enabled device,
/// summed into three destinations on the render thread.
///
/// <para>Devices may run at different native rates (a USB interface at 44.1 kHz
/// next to a 48 kHz codec); each <see cref="MicCapture"/> converts to the engine
/// format on its own thread, so the mixer only ever sums 48 kHz stereo.</para>
///
/// <para><b>Sums.</b> <c>recording = Σ(unmuted)</c>, <c>monitor = Σ(Monitor)</c>
/// (muted devices included: mute silences the audience, not the operator's cue),
/// and the Bridge's mic part is the same as recording. Main never receives any of
/// these.</para>
/// </summary>
public sealed class MicMixer : IDisposable
{
    private readonly object _gate = new();
    private MicStrip[] _strips = Array.Empty<MicStrip>();
    private float[] _scratch = new float[AudioEngine.BlockFrames * AudioEngine.Channels];

    /// <summary>The strips, in settings order. Replaced wholesale on route changes.</summary>
    public IReadOnlyList<MicStrip> Strips => _strips;

    public int Count => _strips.Length;

    /// <summary>
    /// Replace the collection. Old strips are disposed after the swap; new strips
    /// are started before this returns (opening a device is the caller's background
    /// work already, this is just the start call).
    /// </summary>
    public void SetStrips(IEnumerable<MicStrip> strips)
    {
        var next = new List<MicStrip>(strips).ToArray();
        foreach (var strip in next) strip.Start();

        MicStrip[] old;
        lock (_gate)
        {
            old = _strips;
            _strips = next;
            _scratch = new float[AudioEngine.BlockFrames * AudioEngine.Channels];
        }

        foreach (var strip in old) strip.Dispose();
    }

    /// <summary>Replace the collection from device ids (all freshly opened).</summary>
    public void SetDevices(IEnumerable<string> deviceIds)
    {
        var strips = new List<MicStrip>();
        foreach (var id in deviceIds) strips.Add(new MicStrip(id));
        SetStrips(strips);
    }

    /// <summary>Add one device to the end of the collection (returns the strip).</summary>
    public MicStrip Add(string deviceId)
    {
        var strip = new MicStrip(deviceId);
        strip.Start();

        lock (_gate)
        {
            var next = new MicStrip[_strips.Length + 1];
            Array.Copy(_strips, next, _strips.Length);
            next[^1] = strip;
            _strips = next;
        }

        return strip;
    }

    /// <summary>Peak of strip <paramref name="index"/>, or 0 when out of range.</summary>
    public float PeakAt(int index)
    {
        var strips = _strips;
        return index >= 0 && index < strips.Length ? strips[index].Peak : 0f;
    }

    /// <summary>
    /// Read every strip once and sum it into <paramref name="recording"/> and
    /// <paramref name="monitor"/>. Both destinations are cleared first. Called only
    /// from the render thread.
    /// </summary>
    public void Render(int frames, float[] recording, float[] monitor, int channels)
    {
        int samples = frames * channels;
        Array.Clear(recording, 0, samples);
        Array.Clear(monitor, 0, samples);

        var strips = _strips;
        if (strips.Length == 0) return;

        if (_scratch.Length < samples) _scratch = new float[samples];

        foreach (var strip in strips)
        {
            strip.Read(_scratch, 0, samples);

            float gain = strip.Volume;
            if (gain != 1f)
                for (int i = 0; i < samples; i++) _scratch[i] *= gain;

            // The meter is post-gain and independent of mute: muting must not make
            // the operator think the device went dead.
            strip.SetPeak(Samples.Peak(_scratch, 0, samples));

            if (!strip.Muted)
            {
                for (int i = 0; i < samples; i++) recording[i] += _scratch[i];
            }

            if (strip.Monitor)
            {
                // Monitor wins over mute: this is the operator's own cue.
                for (int i = 0; i < samples; i++) monitor[i] += _scratch[i];
            }
        }
    }

    /// <summary>Drift correction for every device ring, once per maintain tick.</summary>
    public void Maintain(int blockFrames)
    {
        foreach (var strip in _strips) strip.Maintain(blockFrames);
    }

    public void Dispose()
    {
        MicStrip[] strips;
        lock (_gate)
        {
            strips = _strips;
            _strips = Array.Empty<MicStrip>();
        }

        foreach (var strip in strips) strip.Dispose();
    }
}

internal static class VolatileFlag
{
    /// <summary>Set once; returns true only for the first caller (used for one-shot logs).</summary>
    public static bool First(ref int flag) => System.Threading.Interlocked.Exchange(ref flag, 1) == 0;
}
