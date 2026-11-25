using System;
using System.Collections.Generic;
using System.Text.Json;
using JockeyBox.Audio.Graph;
using JockeyBox.Dsp;
using JockeyBox.Dsp.Reverb;
using JockeyBox.Tools;

namespace JockeyBox.Api;

/// <summary>
/// Routes the mixer half of the loopback API as plain data: method + path in, status + JSON out,
/// with the same shape as <see cref="CueApiRouter"/> so the listener treats both alike.
///
/// <para><b>The one rule.</b> A request never writes DSP state. The HTTP thread parses, checks the
/// parameter against its descriptor, clamps the value and posts exactly one
/// <see cref="RenderCommands"/> action; <see cref="MicMixer.Render"/> drains it at the top of the
/// next block, so a filter, envelope or gain stage is only ever written by the render thread. A
/// preset is one action covering every device, so it either lands whole or not at all.</para>
///
/// <para><b>Routes</b> (all loopback-only; the listener is the boundary):</para>
/// <list type="bullet">
/// <item><c>GET /mixer/devices</c> — every attached capture device</item>
/// <item><c>GET /mixer/controls</c> — every control id with its descriptor and value</item>
/// <item><c>GET /mixer/strips/{slug}</c> — one device's parameter set</item>
/// <item><c>PUT /mixer/strips/{slug}</c> — write parameters on one device</item>
/// <item><c>POST /mixer/preset</c> — the same write fanned out to every connected device</item>
/// <item><c>PUT /mixer/enabled</c> — the master gate</item>
/// </list>
/// </summary>
public sealed class MixerApiRouter
{
    /// <summary>Persist at most this often; a slider drag is many requests, not many writes.</summary>
    private const int PersistIntervalMs = 500;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One control: the id suffix, how it is presented, and how it reads and writes the
    /// immutable <see cref="StripParameters"/>. Toggles carry 0/1 and cross the API as booleans.
    /// </summary>
    private sealed record ParamDef(
        string Name,
        string Label,
        string Group,
        ControlKind Kind,
        double Min,
        double Max,
        double Step,
        double Default,
        string Unit,
        Func<StripParameters, double> Get,
        Func<StripParameters, double, StripParameters> Set);

    private static readonly ParamDef[] Params = BuildParams();

    private readonly MicMixer _mixer;
    private readonly string _directory;
    private readonly Action<bool>? _persistEnabled;
    private readonly Action<string>? _warn;

    private readonly object _persistGate = new();
    private IReadOnlyDictionary<string, StripParameters>? _pending;
    private bool _persistRunning;
    private long _lastPersistTicks;

    public MixerApiRouter(
        MicMixer mixer,
        string directory,
        Action<bool>? persistEnabled = null,
        Action<string>? warn = null)
    {
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        _directory = directory ?? "";
        _persistEnabled = persistEnabled;
        _warn = warn;
    }

    /// <summary>True when this path belongs to the mixer surface (the listener uses it as a filter).</summary>
    public static bool Handles(string path) =>
        path.StartsWith("/mixer/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Route one request. Never throws; every failure is a 4xx outcome.</summary>
    public RouteOutcome Handle(string method, string path, string? body)
    {
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        string verb = (method ?? "").ToUpperInvariant();

        try
        {
            if (verb == "GET" && segments.Length == 2 && Is(segments[1], "devices"))
                return Ok(Devices());

            if (verb == "GET" && segments.Length == 2 && Is(segments[1], "controls"))
                return Ok(Controls());

            if (segments.Length == 3 && Is(segments[1], "strips"))
            {
                if (verb == "GET") return GetStrip(segments[2]);
                if (verb == "PUT") return PutStrip(segments[2], body);
            }

            if (verb == "POST" && segments.Length == 2 && Is(segments[1], "preset"))
                return Preset(body);

            if (verb == "PUT" && segments.Length == 2 && Is(segments[1], "enabled"))
                return SetEnabled(body);

            return RouteOutcome.Reject(404, $"unknown action for {verb} {path}");
        }
        catch (Exception ex)
        {
            Warn($"mixer api: {verb} {path} failed: {ex.Message}");
            return RouteOutcome.Reject(500, "the mixer refused the request");
        }
    }

    private object Devices()
    {
        var snapshot = _mixer.SnapshotStrips();
        var devices = new List<object>();
        foreach (var strip in _mixer.Strips)
        {
            string id = strip.DeviceId ?? "";
            StripParameters parameters = snapshot.TryGetValue(id, out var stored) ? stored : StripParameters.Neutral;
            devices.Add(new
            {
                slug = SlugOrEmpty(id),
                endpointId = id,
                name = strip.DeviceName,
                connected = strip.IsOpen,
                bypassed = parameters.MasterBypass,
            });
        }

        return new { devices };
    }

    private object Controls()
    {
        var snapshot = _mixer.SnapshotStrips();
        var controls = new List<object>();
        foreach (var strip in _mixer.Strips)
        {
            string id = strip.DeviceId ?? "";
            StripParameters parameters = snapshot.TryGetValue(id, out var stored) ? stored : StripParameters.Neutral;
            foreach (ParamDef def in Params)
            {
                controls.Add(new
                {
                    id = DeviceControlId.For(id, def.Name),
                    deviceSlug = SlugOrEmpty(id),
                    label = def.Label,
                    group = def.Group,
                    kind = def.Kind.ToString().ToLowerInvariant(),
                    min = def.Min,
                    max = def.Max,
                    step = def.Step,
                    unit = def.Unit,
                    value = ValueOf(def, parameters),
                });
            }
        }

        return new { controls };
    }

    private RouteOutcome GetStrip(string slug)
    {
        if (!TryFindStrip(slug, out MicStrip? strip, out string? endpointId))
            return RouteOutcome.Reject(404, $"unknown device '{slug}'");

        var snapshot = _mixer.SnapshotStrips();
        StripParameters parameters = snapshot.TryGetValue(endpointId!, out var stored) ? stored : StripParameters.Neutral;

        return Ok(new
        {
            slug = SlugOrEmpty(endpointId!),
            endpointId = endpointId!,
            name = strip!.DeviceName,
            connected = strip.IsOpen,
            parameters = ParameterMap(parameters),
        });
    }

    private RouteOutcome PutStrip(string slug, string? body)
    {
        if (!TryFindStrip(slug, out _, out string? endpointId))
            return RouteOutcome.Reject(404, $"unknown device '{slug}'");

        if (!TryParseWrites(body, out List<(ParamDef Def, double Value)> writes, out string? error))
            return RouteOutcome.Reject(400, error!);

        var updates = new List<KeyValuePair<string, StripParameters>>(1);
        var applied = new Dictionary<string, object?>(StringComparer.Ordinal);
        var clamped = new List<object>();

        StripParameters before = _mixer.SnapshotStrips().TryGetValue(endpointId!, out var stored)
            ? stored
            : StripParameters.Neutral;
        StripParameters after = Apply(before, writes, applied, clamped);
        updates.Add(new KeyValuePair<string, StripParameters>(endpointId!, after));

        // One action, one device: the render thread applies it between blocks.
        _mixer.Commands.Post(() => _mixer.ApplyAll(updates));
        PersistSoon(endpointId!, after);

        return Accepted(new { ok = true, queued = 1, applied, clamped });
    }

    private RouteOutcome Preset(string? body)
    {
        if (!TryParseWrites(body, out List<(ParamDef Def, double Value)> writes, out string? error))
            return RouteOutcome.Reject(400, error!);

        var snapshot = _mixer.SnapshotStrips();
        var updates = new List<KeyValuePair<string, StripParameters>>();
        var applied = new Dictionary<string, object?>(StringComparer.Ordinal);
        var clamped = new List<object>();

        foreach (var strip in _mixer.Strips)
        {
            if (!strip.IsOpen) continue; // connected devices only: a dead capture has nothing to hear
            string id = strip.DeviceId ?? "";
            StripParameters before = snapshot.TryGetValue(id, out var stored) ? stored : StripParameters.Neutral;
            updates.Add(new KeyValuePair<string, StripParameters>(id, Apply(before, writes, applied, clamped)));
        }

        if (updates.Count == 0) return RouteOutcome.Reject(409, "no connected device to apply the preset to");

        // One action for every device: all of them change in the same block, latency re-padded
        // before that block renders, never device by device across blocks.
        _mixer.Commands.Post(() => _mixer.ApplyAll(updates));
        PersistSoon(updates);

        return Accepted(new { ok = true, devices = updates.Count, applied, clamped });
    }

    private RouteOutcome SetEnabled(string? body)
    {
        bool enabled;
        try
        {
            using var document = JsonDocument.Parse(body ?? "");
            var root = document.RootElement;
            enabled = root.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Object when root.TryGetProperty("enabled", out var value) => value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new FormatException("'enabled' must be a boolean"),
                },
                _ => throw new FormatException("body must be {\"enabled\": true|false}"),
            };
        }
        catch (Exception ex)
        {
            return RouteOutcome.Reject(400, $"invalid body: {ex.Message}");
        }

        _mixer.Commands.Post(() => _mixer.MixerEnabled = enabled);

        try
        {
            _persistEnabled?.Invoke(enabled);
        }
        catch (Exception ex)
        {
            Warn($"mixer api: the enabled flag was applied but not saved: {ex.Message}");
        }

        return Accepted(new { ok = true, enabled });
    }

    /// <summary>
    /// Applies parsed writes to an immutable strip, recording what landed and what had to be
    /// clamped. Clamp-not-reject is deliberate: a client that asks for +30 dB of 4 kHz gets
    /// +15 dB and is told so, rather than losing the whole request.
    /// </summary>
    private static StripParameters Apply(
        StripParameters source,
        List<(ParamDef Def, double Value)> writes,
        Dictionary<string, object?> applied,
        List<object> clamped)
    {
        StripParameters parameters = source;
        foreach (var (def, requested) in writes)
        {
            double value = double.IsNaN(requested)
                ? def.Default
                : Math.Clamp(requested, def.Min, def.Max);

            parameters = def.Set(parameters, value);
            applied[def.Name] = ValueOf(def, parameters);

            if (!value.Equals(requested))
            {
                clamped.Add(new { id = def.Name, requested, applied = value });
            }
        }

        return parameters.Clamp();
    }

    /// <summary>
    /// Validates the whole body before anything is applied, so a request that names one bad
    /// parameter changes nothing at all. Unknown ids are a 400 that names the id.
    /// </summary>
    private static bool TryParseWrites(
        string? body,
        out List<(ParamDef Def, double Value)> writes,
        out string? error)
    {
        writes = new List<(ParamDef, double)>();
        error = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            error = "a body is required";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            error = $"body is not JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "body must be a JSON object of parameter id to value";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                ParamDef? def = FindParam(property.Name);
                if (def == null)
                {
                    error = $"unknown parameter '{property.Name}'";
                    return false;
                }

                if (!TryReadValue(def, property.Value, out double value, out string? valueError))
                {
                    error = $"parameter '{property.Name}': {valueError}";
                    return false;
                }

                writes.Add((def, value));
            }
        }

        if (writes.Count == 0)
        {
            error = "the body names no parameter";
            return false;
        }

        return true;
    }

    private static bool TryReadValue(ParamDef def, JsonElement element, out double value, out string? error)
    {
        value = def.Default;
        error = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out double number):
                value = number;
                return true;

            case JsonValueKind.True when def.Kind == ControlKind.Toggle:
                value = 1;
                return true;

            case JsonValueKind.False when def.Kind == ControlKind.Toggle:
                value = 0;
                return true;

            case JsonValueKind.True:
            case JsonValueKind.False:
                error = "expected a number";
                return false;

            default:
                error = def.Kind == ControlKind.Toggle ? "expected true, false or 0/1" : "expected a number";
                return false;
        }
    }

    private static ParamDef? FindParam(string name)
    {
        foreach (ParamDef def in Params)
        {
            if (Is(def.Name, name)) return def;
        }

        // "eq.band3.gain" is the same control as "eq.band3": the short form is what a
        // preset file and the docs use, the long form is what a full id suggests.
        const string GainSuffix = ".gain";
        if (name.EndsWith(GainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            string shortName = name[..^GainSuffix.Length];
            foreach (ParamDef def in Params)
            {
                if (Is(def.Name, shortName)) return def;
            }
        }

        return null;
    }

    private static object ValueOf(ParamDef def, StripParameters parameters)
    {
        double value = def.Get(parameters);
        return def.Kind == ControlKind.Toggle ? value > 0.5 : value;
    }

    private static Dictionary<string, object?> ParameterMap(StripParameters parameters)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (ParamDef def in Params) map[def.Name] = ValueOf(def, parameters);
        return map;
    }

    private bool TryFindStrip(string slug, out MicStrip? strip, out string? endpointId)
    {
        foreach (var candidate in _mixer.Strips)
        {
            string id = candidate.DeviceId ?? "";
            if (string.Equals(SlugOrEmpty(id), slug, StringComparison.OrdinalIgnoreCase))
            {
                strip = candidate;
                endpointId = id;
                return true;
            }
        }

        strip = null;
        endpointId = null;
        return false;
    }

    private static string SlugOrEmpty(string endpointId) =>
        string.IsNullOrWhiteSpace(endpointId) ? "" : DeviceControlId.Slug(endpointId);

    /// <summary>
    /// Coalesces writes to at most one every <see cref="PersistIntervalMs"/>, with a trailing
    /// flush so the last value of a slider drag is never lost. Runs off both the HTTP and the
    /// render thread: the map handed over is already a copy.
    /// </summary>
    private void PersistSoon(string deviceId, StripParameters parameters)
    {
        var map = new Dictionary<string, StripParameters>(_mixer.SnapshotStrips(), StringComparer.Ordinal);
        map[deviceId] = parameters;
        PersistSoon(map);
    }

    private void PersistSoon(IReadOnlyList<KeyValuePair<string, StripParameters>> updates)
    {
        var map = new Dictionary<string, StripParameters>(_mixer.SnapshotStrips(), StringComparer.Ordinal);
        foreach (var (id, parameters) in updates) map[id] = parameters;
        PersistSoon(map);
    }

    private void PersistSoon(IReadOnlyDictionary<string, StripParameters> map)
    {
        lock (_persistGate)
        {
            _pending = map;
            if (_persistRunning) return;
            _persistRunning = true;
        }

        _ = System.Threading.Tasks.Task.Run(PersistLoopAsync);
    }

    private async System.Threading.Tasks.Task PersistLoopAsync()
    {
        try
        {
            while (true)
            {
                long elapsed = Environment.TickCount64 - _lastPersistTicks;
                if (elapsed < PersistIntervalMs)
                {
                    await System.Threading.Tasks.Task.Delay((int)(PersistIntervalMs - elapsed)).ConfigureAwait(false);
                }

                IReadOnlyDictionary<string, StripParameters>? map;
                lock (_persistGate)
                {
                    map = _pending;
                    _pending = null;
                    if (map == null)
                    {
                        _persistRunning = false;
                        return;
                    }
                }

                _lastPersistTicks = Environment.TickCount64;
                try
                {
                    new MixerSettingsStore(_directory).Save(new Dictionary<string, StripParameters>(map, StringComparer.Ordinal));
                }
                catch (Exception ex)
                {
                    // A failed save is worth a line, never a failed request: the change is live.
                    Warn($"mixer settings not saved: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            lock (_persistGate) _persistRunning = false;
            Warn($"mixer settings writer stopped: {ex.Message}");
        }
    }

    private void Warn(string message) => _warn?.Invoke(message);

    private static bool Is(string text, string expected) =>
        string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);

    private static RouteOutcome Ok(object payload) => new(200, JsonSerializer.Serialize(payload, Options));

    /// <summary>202: the change is queued for the next block, not yet applied.</summary>
    private static RouteOutcome Accepted(object payload) => new(202, JsonSerializer.Serialize(payload, Options));

    private static ParamDef[] BuildParams()
    {
        var definitions = new List<ParamDef>
        {
            new("bypass", "Bypass", "Strip", ControlKind.Toggle, 0, 1, 1, 0, "",
                p => p.MasterBypass ? 1 : 0,
                (p, v) => p with { MasterBypass = v > 0.5 }),

            new("hp.on", "High-pass", "Filters", ControlKind.Toggle, 0, 1, 1, 1, "",
                p => p.HighPass.Enabled ? 1 : 0,
                (p, v) => p with { HighPass = p.HighPass with { Enabled = v > 0.5 } }),

            new("hp.freq", "High-pass corner", "Filters", ControlKind.Continuous,
                FilterParameters.MinFrequencyHz, FilterParameters.MaxFrequencyHz, 1,
                HighPassFilter.TransparentFrequencyHz, "Hz",
                p => p.HighPass.FrequencyHz,
                (p, v) => p with { HighPass = p.HighPass with { FrequencyHz = v } }),

            new("lp.on", "Low-pass", "Filters", ControlKind.Toggle, 0, 1, 1, 1, "",
                p => p.LowPass.Enabled ? 1 : 0,
                (p, v) => p with { LowPass = p.LowPass with { Enabled = v > 0.5 } }),

            new("lp.freq", "Low-pass corner", "Filters", ControlKind.Continuous,
                FilterParameters.MinFrequencyHz, FilterParameters.MaxFrequencyHz, 1,
                LowPassFilter.TransparentFrequencyHz, "Hz",
                p => p.LowPass.FrequencyHz,
                (p, v) => p with { LowPass = p.LowPass with { FrequencyHz = v } }),

            new("gate.on", "Gate", "Dynamics", ControlKind.Toggle, 0, 1, 1, 0, "",
                p => p.Gate.Enabled ? 1 : 0,
                (p, v) => p with { Gate = p.Gate with { Enabled = v > 0.5 } }),

            new("gate.threshold", "Threshold", "Dynamics", ControlKind.Continuous, -80, 0, 0.5, -40, "dB",
                p => p.Gate.ThresholdDb,
                (p, v) => p with { Gate = p.Gate with { ThresholdDb = v } }),

            new("gate.hysteresis", "Hysteresis", "Dynamics", ControlKind.Continuous, 0, 24, 0.5, 3, "dB",
                p => p.Gate.HysteresisDb,
                (p, v) => p with { Gate = p.Gate with { HysteresisDb = v } }),

            new("gate.attack", "Attack", "Dynamics", ControlKind.Continuous, 0.01, 1000, 0.01, 5, "ms",
                p => p.Gate.AttackMs,
                (p, v) => p with { Gate = p.Gate with { AttackMs = v } }),

            new("gate.release", "Release", "Dynamics", ControlKind.Continuous, 0.01, 5000, 1, 100, "ms",
                p => p.Gate.ReleaseMs,
                (p, v) => p with { Gate = p.Gate with { ReleaseMs = v } }),

            new("gate.hold", "Hold", "Dynamics", ControlKind.Continuous, 0, 2000, 1, 50, "ms",
                p => p.Gate.HoldMs,
                (p, v) => p with { Gate = p.Gate with { HoldMs = v } }),

            new("gate.floor", "Floor", "Dynamics", ControlKind.Continuous, -120, 0, 1, -60, "dB",
                p => p.Gate.FloorDb,
                (p, v) => p with { Gate = p.Gate with { FloorDb = v } }),

            new("comp.on", "Compressor", "Dynamics", ControlKind.Toggle, 0, 1, 1, 0, "",
                p => p.Compressor.Enabled ? 1 : 0,
                (p, v) => p with { Compressor = p.Compressor with { Enabled = v > 0.5 } }),

            new("comp.threshold", "Threshold", "Dynamics", ControlKind.Continuous, -60, 0, 0.5, -20, "dB",
                p => p.Compressor.ThresholdDb,
                (p, v) => p with { Compressor = p.Compressor with { ThresholdDb = v } }),

            new("comp.ratio", "Ratio", "Dynamics", ControlKind.Continuous, 1, 20, 0.1, 4, ":1",
                p => p.Compressor.Ratio,
                (p, v) => p with { Compressor = p.Compressor with { Ratio = v } }),

            new("comp.attack", "Attack", "Dynamics", ControlKind.Continuous, 0.01, 1000, 0.01, 10, "ms",
                p => p.Compressor.AttackMs,
                (p, v) => p with { Compressor = p.Compressor with { AttackMs = v } }),

            new("comp.release", "Release", "Dynamics", ControlKind.Continuous, 0.01, 5000, 1, 100, "ms",
                p => p.Compressor.ReleaseMs,
                (p, v) => p with { Compressor = p.Compressor with { ReleaseMs = v } }),

            new("comp.knee", "Knee", "Dynamics", ControlKind.Continuous, 0, 24, 0.5, 6, "dB",
                p => p.Compressor.KneeDb,
                (p, v) => p with { Compressor = p.Compressor with { KneeDb = v } }),

            new("comp.makeup", "Makeup", "Dynamics", ControlKind.Continuous, -12, 24, 0.5, 0, "dB",
                p => p.Compressor.MakeupDb,
                (p, v) => p with { Compressor = p.Compressor with { MakeupDb = v } }),

            new("deess.on", "De-esser", "DeEsser", ControlKind.Toggle, 0, 1, 1, 0, "",
                p => p.DeEsser.Enabled ? 1 : 0,
                (p, v) => p with { DeEsser = p.DeEsser with { Enabled = v > 0.5 } }),

            new("deess.threshold", "Threshold", "DeEsser", ControlKind.Continuous, -60, 0, 0.5, -28, "dB",
                p => p.DeEsser.ThresholdDb,
                (p, v) => p with { DeEsser = p.DeEsser with { ThresholdDb = v } }),

            new("deess.amount", "Amount", "DeEsser", ControlKind.Continuous, 1, 20, 0.1, 4, ":1",
                p => p.DeEsser.Ratio,
                (p, v) => p with { DeEsser = p.DeEsser with { Ratio = v } }),

            new("deess.split", "Split", "DeEsser", ControlKind.Continuous, 1000, 20000, 10, 6000, "Hz",
                p => p.DeEsser.FrequencyHz,
                (p, v) => p with { DeEsser = p.DeEsser with { FrequencyHz = v } }),

            new("rev.on", "Reverb", "Reverb", ControlKind.Toggle, 0, 1, 1, 0, "",
                p => p.Reverb.On ? 1 : 0,
                (p, v) => p with { Reverb = p.Reverb with { On = v > 0.5 } }),

            new("rev.size", "Size", "Reverb", ControlKind.Continuous,
                ReverbParameters.MinSize, ReverbParameters.MaxSize, 0.01,
                ReverbParameters.Neutral.Size, "",
                p => p.Reverb.Size,
                (p, v) => p with { Reverb = p.Reverb with { Size = v } }),

            new("rev.wet", "Wet", "Reverb", ControlKind.Continuous,
                ReverbParameters.MinWet, ReverbParameters.MaxWet, 0.01,
                ReverbParameters.Neutral.Wet, "",
                p => p.Reverb.Wet,
                (p, v) => p with { Reverb = p.Reverb with { Wet = v } }),

            new("limiter.on", "Limiter", "Dynamics", ControlKind.Toggle, 0, 1, 1, 0, "",
                p => p.Limiter.Enabled ? 1 : 0,
                (p, v) => p with { Limiter = p.Limiter with { Enabled = v > 0.5 } }),

            new("limiter.ceiling", "Ceiling", "Dynamics", ControlKind.Continuous, -12, 0, 0.1, 0, "dB",
                p => p.Limiter.CeilingDb,
                (p, v) => p with { Limiter = p.Limiter with { CeilingDb = v } }),

            new("limiter.release", "Release", "Dynamics", ControlKind.Continuous, 1, 1000, 1, 50, "ms",
                p => p.Limiter.ReleaseMs,
                (p, v) => p with { Limiter = p.Limiter with { ReleaseMs = v } }),
        };

        // Ten ISO octave bands, indexed as TenBandEq expects: band0 is 31 Hz.
        for (int band = 0; band < 10; band++)
        {
            int index = band;
            double centre = TenBandEq.CentresHz[index];
            string label = centre >= 1000
                ? (centre / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz"
                : centre.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " Hz";

            definitions.Add(new ParamDef(
                $"eq.band{index}", label, "EQ", ControlKind.Continuous,
                EqParameters.MinGainDb, EqParameters.MaxGainDb, 0.5, 0, "dB",
                p => p.Eq[index],
                (p, v) => p with { Eq = SetBand(p.Eq, index, v) }));
        }

        return definitions.ToArray();
    }

    private static EqParameters SetBand(EqParameters eq, int band, double gain) => band switch
    {
        0 => eq with { Gain31Db = gain },
        1 => eq with { Gain63Db = gain },
        2 => eq with { Gain125Db = gain },
        3 => eq with { Gain250Db = gain },
        4 => eq with { Gain500Db = gain },
        5 => eq with { Gain1kDb = gain },
        6 => eq with { Gain2kDb = gain },
        7 => eq with { Gain4kDb = gain },
        8 => eq with { Gain8kDb = gain },
        _ => eq with { Gain16kDb = gain },
    };
}
