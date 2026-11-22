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
    private readonly IMicSource? _source;
    private readonly MicCapture? _capture;

    /// <summary>
    /// This device's channel strip. Built in the constructor, so the render path never
    /// allocates; it processes the device block once, before both sends.
    /// </summary>
    private readonly InterleavedMixerStrip _strip;

    /// <summary>
    /// Alignment delay. Its ring is sized from the pad actually needed, and it grows only on
    /// the settings/route path (<see cref="SetPadding"/>), so the render path never allocates.
    /// </summary>
    private readonly PadRing _pad = new(AudioEngine.Channels);

    private int _padClampLogged;
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

        // A composite key names one channel of a multi-channel source. Every strip of an ASIO
        // interface shares one client (ASIO is single-client) and reads its own channel from it.
        if (DeviceSourceKey.TryParse(DeviceId, out string kind, out string endpoint, out int channel) &&
            channel >= 0 && string.Equals(kind, AsioDriverRegistry.Kind, StringComparison.Ordinal))
        {
            var asio = new AsioInput(endpoint, channel, AsioChannelMode.Mono);
            _source = asio;
            DeviceLatencyFrames = asio.DeviceLatencyFrames;
            _strip = new InterleavedMixerStrip(AudioEngine.Rate, AudioEngine.Channels, AudioEngine.BlockFrames);
            return;
        }

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
    /// Latency the capture device itself adds, in frames, before this strip ever sees a sample.
    /// Default 0: a WASAPI source does not report one and stays 0. The ASIO lane sets it from
    /// the driver's reported input latency, so a fast ASIO interface can be padded against a
    /// slower WASAPI mic instead of being aligned on chain latency alone.
    /// </summary>
    public int DeviceLatencyFrames { get; set; }

    /// <summary>Everything this device's signal carries before the mix: chain plus device.</summary>
    internal int TotalLatencyFrames => ChainLatencyFrames + DeviceLatencyFrames;

    /// <summary>
    /// True when this device's alignment pad hit the ceiling and was clamped: the mix is
    /// knowingly misaligned by whatever it could not absorb. Read by the diagnostics window.
    /// </summary>
    public bool LatencyClamped => _pad.Clamped;

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
    /// Sets this device's alignment pad. The mix pads every device to the slowest total (chain
    /// + device) latency in the collection, so two devices cannot comb-filter. The change ramps
    /// in over the ring's fade rather than stepping. A pad past the ring's ceiling is clamped
    /// and logged once: a misaligned mix is never silent.
    /// </summary>
    internal void SetPadding(int frames)
    {
        _pad.SetPadding(frames);

        if (_pad.Clamped && VolatileFlag.First(ref _padClampLogged))
        {
            Log.Warn(
                $"mic: '{DeviceId}' needs {frames} frames of alignment, more than the " +
                $"{PadRing.MaxFrames}-frame ceiling; clamped, the mix stays misaligned");
        }
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
        if (_pad.Active) _pad.Process(buffer, offset, channels, frames);
        _strip.Process(buffer, offset, frames);
    }

    public void Dispose()
    {
        try { (_capture as IDisposable)?.Dispose(); } catch { }

        // An ASIO source is not a MicCapture; releasing it gives the shared driver back.
        if (!ReferenceEquals(_source, _capture))
        {
            try { (_source as IDisposable)?.Dispose(); } catch { }
        }
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
    /// True when any device's pad hit the ceiling and was clamped, so the mix is misaligned by
    /// more than it can compensate. Read by the diagnostics window.
    /// </summary>
    public bool LatencyClamped
    {
        get
        {
            var strips = _strips;
            for (int i = 0; i < strips.Length; i++)
                if (strips[i].LatencyClamped) return true;

            return false;
        }
    }

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
    /// Render-thread queue. The HTTP API, hotkeys and presets post here and return at once;
    /// <see cref="Render"/> drains it at the top of the block, so nothing outside the render
    /// thread ever writes a filter, envelope or gain stage.
    /// </summary>
    public RenderCommands Commands { get; } = new();

    /// <summary>
    /// Applies a batch of strips on the render thread and re-aligns once, after the whole
    /// batch. Call only from a <see cref="Commands"/> action: one call per block is what keeps
    /// a preset atomic instead of staggering device by device across blocks.
    /// </summary>
    public void ApplyAll(IReadOnlyList<KeyValuePair<string, StripParameters>> updates)
    {
        if (updates == null || updates.Count == 0) return;

        var byId = new Dictionary<string, StripParameters>(updates.Count, StringComparer.Ordinal);
        for (int i = 0; i < updates.Count; i++)
        {
            byId[updates[i].Key ?? ""] = updates[i].Value.Clamp();
        }

        MicStrip[] strips = _strips;
        for (int i = 0; i < strips.Length; i++)
        {
            if (byId.TryGetValue(strips[i].DeviceId ?? "", out StripParameters parameters))
            {
                strips[i].ApplyStripParameters(parameters);
            }
        }

        lock (_gate)
        {
            foreach (var pair in byId) _stored[pair.Key] = pair.Value;
        }

        // After the batch, before the strips process in this same block: a preset that
        // switches a limiter on re-pads every peer in the block it arrives in.
        RecalculateLatency(strips);
    }

    /// <summary>
    /// Copy of the applied map for a caller on another thread (the HTTP read side and the
    /// debounced writer). Every attached device is present, neutral when it has no entry.
    /// </summary>
    public IReadOnlyDictionary<string, StripParameters> SnapshotStrips()
    {
        var copy = new Dictionary<string, StripParameters>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var pair in _stored) copy[pair.Key] = pair.Value;
            foreach (var strip in _strips)
            {
                string id = strip.DeviceId ?? "";
                if (!copy.ContainsKey(id)) copy[id] = StripParameters.Neutral;
            }
        }

        return copy;
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
    /// Registers every strip in the ledger and pads all of them to the slowest total latency
    /// (chain + device), in one pass. Runs on the settings path, never per block: one device
    /// cannot know what its peers carry. With no limiter engaged and no device latency
    /// reported, every pad is 0.
    /// </summary>
    private void RecalculateLatency(MicStrip[] strips)
    {
        for (int i = 0; i < strips.Length; i++)
        {
            string id = string.IsNullOrWhiteSpace(strips[i].DeviceId)
                ? "device." + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : strips[i].DeviceId;
            _latency.Set(id, strips[i].ChainLatencyFrames, strips[i].DeviceLatencyFrames, 0);
        }

        int max = _latency.MaxLatencyFrames;
        for (int i = 0; i < strips.Length; i++)
            strips[i].SetPadding(max - strips[i].TotalLatencyFrames);
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
        // Everything the API, a hotkey or a preset asked for lands here, at the top of the
        // block, before any strip processes: one block sees one consistent set of targets.
        Commands.Drain();

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
