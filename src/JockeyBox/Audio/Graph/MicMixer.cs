using System;
using System.Collections.Generic;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Dsp;
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
    /// <summary>The limiter lookahead is the only chain latency (5 ms at 48 kHz).</summary>
    private const int PadRingFrames = 240;

    /// <summary>A pad change ramps over this many frames, so a device never steps.</summary>
    private const int PadFadeFrames = 480;

    private readonly IMicSource? _source;
    private readonly MicCapture? _capture;

    /// <summary>
    /// This device's channel strip. Built in the constructor, so the render path never
    /// allocates; it processes the device block once, before both sends.
    /// </summary>
    private readonly InterleavedMixerStrip _strip;

    /// <summary>Fixed alignment delay ring: <see cref="PadRingFrames"/> frames, never grown.</summary>
    private readonly float[] _padRing = new float[PadRingFrames * AudioEngine.Channels];
    private int _padWrite;
    private int _padTarget;
    private float _padCurrent;
    private float _padStep;
    private volatile bool _mixerEnabled;
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
        _strip = new InterleavedMixerStrip(AudioEngine.Rate, AudioEngine.Channels, AudioEngine.BlockFrames);
    }

    /// <summary>Test hook: a synthetic source (no hardware, no thread).</summary>
    internal MicStrip(string deviceId, IMicSource source)
    {
        DeviceId = deviceId ?? "";
        _source = source;
        _strip = new InterleavedMixerStrip(AudioEngine.Rate, AudioEngine.Channels, AudioEngine.BlockFrames);
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

    /// <summary>Chain latency this device declares, in frames (limiter lookahead or 0).</summary>
    internal int ChainLatencyFrames => _strip.LatencyFrames;

    /// <summary>
    /// Master gate. When false the strip and its pad are skipped entirely, so the device
    /// block reaches the sums byte-identical to a build without a mixer.
    /// </summary>
    internal bool MixerEnabled
    {
        get => _mixerEnabled;
        set => _mixerEnabled = value;
    }

    /// <summary>Apply this device's stored parameters. <see cref="StripParameters.Neutral"/> is a passthrough.</summary>
    internal void ApplyStripParameters(StripParameters parameters) => _strip.Apply(parameters);

    /// <summary>
    /// Sets this device's alignment pad. The mix pads every device to the slowest chain in
    /// the collection, so two devices cannot comb-filter when only one has a limiter. The
    /// change ramps in over <see cref="PadFadeFrames"/> frames rather than stepping.
    /// </summary>
    internal void SetPadding(int frames)
    {
        frames = Math.Clamp(frames, 0, PadRingFrames);
        if (frames == _padTarget) return;
        _padTarget = frames;
        _padStep = (frames - _padCurrent) / PadFadeFrames;
    }

    /// <summary>
    /// Processes one contiguous device block in place: alignment pad first, then the channel
    /// strip. Called between the device read and the app gain, once per block, so the
    /// recording and monitor sends both carry the processed block.
    /// <para>
    /// The strip sits <b>before</b> the sends on purpose: a console channel strip is not
    /// per-destination, so hearing an un-EQ'd version in the operator's own cue would be a
    /// bug. The consequence is that the gate (and every other stage) now shapes the
    /// operator's monitor cue too, not just the audience feed.
    /// </para>
    /// </summary>
    internal void Process(float[] buffer, int offset, int channels, int frames)
    {
        if (!_mixerEnabled) return;
        if (channels != AudioEngine.Channels) return;
        if (_padTarget > 0 || _padCurrent > 0f) Pad(buffer, offset, channels, frames);
        _strip.Process(buffer, offset, frames);
    }

    /// <summary>
    /// Fractional-delay crossfade through the fixed ring. Reads the tap at the ramp's floor
    /// and ceiling so a pad change slides instead of clicking; reads before the write, so a
    /// 240-frame pad reads the oldest sample the ring holds.
    /// </summary>
    private void Pad(float[] buffer, int offset, int channels, int frames)
    {
        float current = _padCurrent;
        float step = _padStep;
        int write = _padWrite;

        for (int frame = 0; frame < frames; frame++)
        {
            int d0 = (int)current;
            float frac = current - d0;
            int d1 = frac > 0f ? d0 + 1 : d0;

            int baseIndex = offset + frame * channels;
            for (int c = 0; c < channels; c++)
            {
                int bi = baseIndex + c;
                float input = buffer[bi];

                float s0;
                if (d0 == 0)
                {
                    s0 = input;
                }
                else
                {
                    int i0 = write - d0;
                    if (i0 < 0) i0 += PadRingFrames;
                    s0 = _padRing[i0 * channels + c];
                }

                float outSample;
                if (d1 == d0 || d1 > PadRingFrames)
                {
                    outSample = s0;
                }
                else
                {
                    int i1 = write - d1;
                    if (i1 < 0) i1 += PadRingFrames;
                    outSample = s0 + (_padRing[i1 * channels + c] - s0) * frac;
                }

                _padRing[write * channels + c] = input;
                buffer[bi] = outSample;
            }

            write++;
            if (write == PadRingFrames) write = 0;

            if (step > 0f)
            {
                current += step;
                if (current >= _padTarget) { current = _padTarget; step = 0f; }
            }
            else if (step < 0f)
            {
                current += step;
                if (current <= _padTarget) { current = _padTarget; step = 0f; }
            }
        }

        _padWrite = write;
        _padCurrent = current;
        _padStep = step;
    }

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

    /// <summary>One ledger for the whole mix: every device pads to the slowest chain.</summary>
    private readonly LatencyLedger _latency = new();

    /// <summary>Applied strip settings keyed by capture endpoint id; reloaded on route changes.</summary>
    private readonly Dictionary<string, StripParameters> _stored = new(StringComparer.Ordinal);

    private volatile bool _mixerEnabled;

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
        foreach (var strip in next)
        {
            strip.MixerEnabled = _mixerEnabled;
            strip.ApplyStripParameters(ParametersFor(strip.DeviceId));
            strip.Start();
        }

        MicStrip[] old;
        lock (_gate)
        {
            old = _strips;
            _strips = next;
            _scratch = new float[AudioEngine.BlockFrames * AudioEngine.Channels];
        }

        RecalculateLatency(next);

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
        strip.MixerEnabled = _mixerEnabled;
        strip.ApplyStripParameters(ParametersFor(strip.DeviceId));
        strip.Start();

        MicStrip[] next;
        lock (_gate)
        {
            next = new MicStrip[_strips.Length + 1];
            Array.Copy(_strips, next, _strips.Length);
            next[^1] = strip;
            _strips = next;
        }

        RecalculateLatency(next);
        return strip;
    }

    /// <summary>Master gate for every strip; false leaves the render path untouched.</summary>
    public bool MixerEnabled
    {
        get => _mixerEnabled;
        set
        {
            _mixerEnabled = value;
            foreach (var strip in _strips) strip.MixerEnabled = value;
        }
    }

    /// <summary>
    /// Loads the stored per-device strips and the master gate, then re-aligns once. Never
    /// throws: a missing or broken file leaves every device neutral. Does not save.
    /// </summary>
    public MixerSettingsLoad LoadStoredSettings(string directory, bool mixerEnabled)
    {
        MixerSettingsLoad loaded;
        try
        {
            loaded = new MixerSettingsStore(directory).Load();
        }
        catch (Exception ex)
        {
            loaded = new MixerSettingsLoad(
                new Dictionary<string, StripParameters>(StringComparer.Ordinal), ex.Message);
        }

        _mixerEnabled = mixerEnabled;
        _stored.Clear();
        foreach (var pair in loaded.Devices) _stored[pair.Key] = pair.Value;

        var strips = _strips;
        foreach (var strip in strips)
        {
            strip.MixerEnabled = mixerEnabled;
            strip.ApplyStripParameters(ParametersFor(strip.DeviceId));
        }

        RecalculateLatency(strips);
        return loaded;
    }

    /// <summary>
    /// Applies one device's strip and persists the whole map: the entry point the settings
    /// API lane calls. The render path itself never writes the file.
    /// </summary>
    public void ApplyAndPersist(string directory, string deviceId, StripParameters parameters)
    {
        parameters = parameters.Clamp();
        _stored[deviceId ?? ""] = parameters;

        var strip = Array.Find(_strips, s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal));
        strip?.ApplyStripParameters(parameters);
        RecalculateLatency(_strips);

        try
        {
            new MixerSettingsStore(directory).Save(_stored);
        }
        catch (Exception ex)
        {
            Log.Warn($"mixer settings not saved: {ex.Message}");
        }
    }

    private StripParameters ParametersFor(string deviceId) =>
        _stored.TryGetValue(deviceId ?? "", out var parameters) ? parameters : StripParameters.Neutral;

    /// <summary>
    /// Registers every strip in the ledger and pads all of them to the slowest chain, in one
    /// pass. Runs on the settings path, never per device: one device cannot know what its
    /// peers carry. With no limiter engaged anywhere every pad is 0.
    /// </summary>
    private void RecalculateLatency(MicStrip[] strips)
    {
        for (int i = 0; i < strips.Length; i++)
        {
            string id = string.IsNullOrWhiteSpace(strips[i].DeviceId)
                ? "device." + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : strips[i].DeviceId;
            _latency.Set(id, strips[i].ChainLatencyFrames, 0, 0);
        }

        int max = _latency.MaxLatencyFrames;
        for (int i = 0; i < strips.Length; i++)
            strips[i].SetPadding(max - strips[i].ChainLatencyFrames);
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

            // The one point where this device's block exists contiguously and has not been
            // summed anywhere yet. Pad + channel strip run here, once per device, so both
            // sends below carry the processed block.
            strip.Process(_scratch, 0, channels, frames);

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
