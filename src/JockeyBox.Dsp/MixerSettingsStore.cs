using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Dsp.Reverb;

namespace JockeyBox.Dsp;

/// <summary>
/// What <see cref="MixerSettingsStore.Load"/> found: the map of capture endpoint id to strip,
/// and a human-readable reason when the file could not be used. Never an exception: this runs
/// on the audio path, and a broken settings file must mute nothing.
/// </summary>
public sealed record MixerSettingsLoad(
    IReadOnlyDictionary<string, StripParameters> Devices,
    string? Reason)
{
    /// <summary>Nothing configured. Equal to every device being neutral.</summary>
    public static MixerSettingsLoad Nothing =>
        new(new Dictionary<string, StripParameters>(StringComparer.Ordinal), null);

    /// <summary>True when the file existed but could not be read, in whole or in part.</summary>
    public bool HasProblem => !string.IsNullOrEmpty(Reason);
}

/// <summary>
/// One JSON file holding the per-device channel strip settings, keyed by capture endpoint id.
/// <para>
/// The app's <c>settings.json</c> reserialises wholesale, so strip config living there would be
/// clobbered by a concurrent write and silently dropped by a rollback to an older schema. This
/// store is pure data, no WPF, so it can be tested anywhere.
/// </para>
/// <para>
/// An absent file loads as an empty map, not an error: an unconfigured device is neutral, and
/// neutral is bit-transparent, so absence and neutrality are the same state.
/// </para>
/// <para>
/// Writes are atomic (temp file in the same directory, then replace): a crash leaves either the
/// old document or the new one, never half of one. Two saves of equal content produce equal
/// bytes because device keys are written in ordinal order.
/// </para>
/// </summary>
public sealed class MixerSettingsStore
{
    public const string FileName = "mixer-settings.json";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _directory;
    private readonly string _path;

    public MixerSettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, FileName);
    }

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Reads the file. Missing ⇒ empty map and no reason. Unreadable or unparsable ⇒ empty map
    /// and a reason. Values outside their valid range are clamped, not rejected. A device entry
    /// that cannot be parsed keeps its id (mapped to neutral) so no setting is silently pruned;
    /// its problem goes into the reason. Unknown top-level keys are ignored, so a newer build's
    /// file is still readable.
    /// </summary>
    public MixerSettingsLoad Load()
    {
        if (!File.Exists(_path)) return MixerSettingsLoad.Nothing;

        var devices = new Dictionary<string, StripParameters>(StringComparer.Ordinal);
        var problems = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_path), DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new MixerSettingsLoad(devices, $"{FileName}: root is not a JSON object");

            // "Schema" and every other top-level key is ignored on purpose: only "Devices" matters,
            // and a newer schema must not make this build throw away the operator's settings.
            if (!root.TryGetProperty("Devices", out var entries)) return new MixerSettingsLoad(devices, null);
            if (entries.ValueKind != JsonValueKind.Object)
                return new MixerSettingsLoad(devices, $"{FileName}: \"Devices\" is not a JSON object");

            foreach (var entry in entries.EnumerateObject())
            {
                try
                {
                    devices[entry.Name] = ToParameters(entry.Value);
                }
                catch (Exception ex)
                {
                    // Keep the id: a neutral strip is recoverable by hand, a pruned one is lost.
                    devices[entry.Name] = StripParameters.Neutral;
                    problems.Add($"{entry.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            return new MixerSettingsLoad(devices, $"{FileName}: {ex.Message}");
        }

        return new MixerSettingsLoad(devices, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <summary>
    /// Writes the map atomically. Throws only after cleaning up its temp file; the caller decides
    /// whether a failed save is worth surfacing.
    /// </summary>
    public void Save(IDictionary<string, StripParameters> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var file = new FileDto
        {
            Schema = SchemaVersion,
            Devices = new SortedDictionary<string, DeviceDto>(StringComparer.Ordinal),
        };
        foreach (var (id, strip) in devices) file.Devices[id] = DeviceDto.From(strip);

        Directory.CreateDirectory(_directory);
        var temp = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            // Same directory, same volume: replace is atomic, so a crash keeps one whole document.
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    private static StripParameters ToParameters(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("device entry is not a JSON object");

        var dto = element.Deserialize<DeviceDto>(JsonOptions)
                  ?? throw new FormatException("device entry is null");

        var neutral = StripParameters.Neutral;
        return new StripParameters(
            Eq: EqFrom(dto.Eq) ?? neutral.Eq,
            HighPass: new FilterParameters(dto.HighPassOn ?? neutral.HighPass.Enabled, dto.HighPassHz ?? neutral.HighPass.FrequencyHz),
            LowPass: new FilterParameters(dto.LowPassOn ?? neutral.LowPass.Enabled, dto.LowPassHz ?? neutral.LowPass.FrequencyHz),
            Gate: dto.Gate?.ToParameters(neutral.Gate) ?? neutral.Gate,
            Compressor: dto.Comp?.ToParameters(neutral.Compressor) ?? neutral.Compressor,
            Limiter: dto.Limiter?.ToParameters(neutral.Limiter) ?? neutral.Limiter,
            DeEsser: dto.DeEsser?.ToParameters(neutral.DeEsser) ?? neutral.DeEsser,
            Reverb: dto.Reverb?.ToParameters(neutral.Reverb) ?? neutral.Reverb,
            PitchShift: dto.PitchShift?.ToParameters(neutral.PitchShift) ?? neutral.PitchShift,
            MasterBypass: dto.Bypass ?? false).Clamp();
    }

    /// <summary>Ten band gains in ISO centre order; a short array pads with flat, extra entries drop.</summary>
    private static EqParameters? EqFrom(double[]? gains)
    {
        if (gains is null) return null;
        double At(int band) => band < gains.Length ? gains[band] : 0;
        return new EqParameters(
            At(0), At(1), At(2), At(3), At(4), At(5), At(6), At(7), At(8), At(9));
    }

    // ---- on-disk shape, schema 1 -------------------------------------------------------------

    private sealed class FileDto
    {
        public int Schema { get; set; }
        public SortedDictionary<string, DeviceDto>? Devices { get; set; }
    }

    private sealed class DeviceDto
    {
        public double[]? Eq { get; set; }
        public double? HighPassHz { get; set; }
        public bool? HighPassOn { get; set; }
        public double? LowPassHz { get; set; }
        public bool? LowPassOn { get; set; }
        public GateDto? Gate { get; set; }
        public CompDto? Comp { get; set; }
        public LimiterDto? Limiter { get; set; }
        public DeEsserDto? DeEsser { get; set; }
        public ReverbDto? Reverb { get; set; }
        public PitchShiftDto? PitchShift { get; set; }
        public bool? Bypass { get; set; }

        public static DeviceDto From(StripParameters strip) => new()
        {
            Eq = new[]
            {
                strip.Eq[0], strip.Eq[1], strip.Eq[2], strip.Eq[3], strip.Eq[4],
                strip.Eq[5], strip.Eq[6], strip.Eq[7], strip.Eq[8], strip.Eq[9],
            },
            HighPassHz = strip.HighPass.FrequencyHz,
            HighPassOn = strip.HighPass.Enabled,
            LowPassHz = strip.LowPass.FrequencyHz,
            LowPassOn = strip.LowPass.Enabled,
            Gate = GateDto.From(strip.Gate),
            Comp = CompDto.From(strip.Compressor),
            Limiter = LimiterDto.From(strip.Limiter),
            DeEsser = DeEsserDto.From(strip.DeEsser),
            Reverb = ReverbDto.From(strip.Reverb),
            PitchShift = PitchShiftDto.From(strip.PitchShift),
            Bypass = strip.MasterBypass,
        };
    }

    private sealed class GateDto
    {
        public bool? On { get; set; }
        public double? ThresholdDb { get; set; }
        public double? HysteresisDb { get; set; }
        public double? AttackMs { get; set; }
        public double? ReleaseMs { get; set; }
        public double? HoldMs { get; set; }
        public double? FloorDb { get; set; }

        public static GateDto From(GateParameters gate) => new()
        {
            On = gate.Enabled,
            ThresholdDb = gate.ThresholdDb,
            HysteresisDb = gate.HysteresisDb,
            AttackMs = gate.AttackMs,
            ReleaseMs = gate.ReleaseMs,
            HoldMs = gate.HoldMs,
            FloorDb = gate.FloorDb,
        };

        public GateParameters ToParameters(GateParameters fallback) => new(
            On ?? fallback.Enabled,
            ThresholdDb ?? fallback.ThresholdDb,
            HysteresisDb ?? fallback.HysteresisDb,
            AttackMs ?? fallback.AttackMs,
            ReleaseMs ?? fallback.ReleaseMs,
            HoldMs ?? fallback.HoldMs,
            FloorDb ?? fallback.FloorDb);
    }

    private sealed class CompDto
    {
        public bool? On { get; set; }
        public double? ThresholdDb { get; set; }
        public double? Ratio { get; set; }
        public double? AttackMs { get; set; }
        public double? ReleaseMs { get; set; }
        public double? KneeDb { get; set; }
        public double? MakeupDb { get; set; }

        public static CompDto From(CompressorParameters comp) => new()
        {
            On = comp.Enabled,
            ThresholdDb = comp.ThresholdDb,
            Ratio = comp.Ratio,
            AttackMs = comp.AttackMs,
            ReleaseMs = comp.ReleaseMs,
            KneeDb = comp.KneeDb,
            MakeupDb = comp.MakeupDb,
        };

        public CompressorParameters ToParameters(CompressorParameters fallback) => new(
            On ?? fallback.Enabled,
            ThresholdDb ?? fallback.ThresholdDb,
            Ratio ?? fallback.Ratio,
            AttackMs ?? fallback.AttackMs,
            ReleaseMs ?? fallback.ReleaseMs,
            KneeDb ?? fallback.KneeDb,
            MakeupDb ?? fallback.MakeupDb);
    }

    private sealed class LimiterDto
    {
        public bool? On { get; set; }
        public double? CeilingDb { get; set; }
        public double? ReleaseMs { get; set; }

        public static LimiterDto From(LimiterParameters limiter) => new()
        {
            On = limiter.Enabled,
            CeilingDb = limiter.CeilingDb,
            ReleaseMs = limiter.ReleaseMs,
        };

        public LimiterParameters ToParameters(LimiterParameters fallback) => new(
            On ?? fallback.Enabled,
            CeilingDb ?? fallback.CeilingDb,
            ReleaseMs ?? fallback.ReleaseMs);
    }

    private sealed class DeEsserDto
    {
        public bool? On { get; set; }
        public double? FrequencyHz { get; set; }
        public double? ThresholdDb { get; set; }
        public double? Ratio { get; set; }
        public double? AttackMs { get; set; }
        public double? ReleaseMs { get; set; }
        public double? MaxReductionDb { get; set; }
        public double? SidechainLowHz { get; set; }
        public double? SidechainHighHz { get; set; }

        public static DeEsserDto From(DeEsserParameters deEsser) => new()
        {
            On = deEsser.Enabled,
            FrequencyHz = deEsser.FrequencyHz,
            ThresholdDb = deEsser.ThresholdDb,
            Ratio = deEsser.Ratio,
            AttackMs = deEsser.AttackMs,
            ReleaseMs = deEsser.ReleaseMs,
            MaxReductionDb = deEsser.MaxReductionDb,
            SidechainLowHz = deEsser.SidechainLowHz,
            SidechainHighHz = deEsser.SidechainHighHz,
        };

        public DeEsserParameters ToParameters(DeEsserParameters fallback) => new(
            On ?? fallback.Enabled,
            FrequencyHz ?? fallback.FrequencyHz,
            ThresholdDb ?? fallback.ThresholdDb,
            Ratio ?? fallback.Ratio,
            AttackMs ?? fallback.AttackMs,
            ReleaseMs ?? fallback.ReleaseMs,
            MaxReductionDb ?? fallback.MaxReductionDb,
            SidechainLowHz ?? fallback.SidechainLowHz,
            SidechainHighHz ?? fallback.SidechainHighHz);
    }

    private sealed class ReverbDto
    {
        public bool? On { get; set; }
        public double? Size { get; set; }
        public double? Wet { get; set; }

        public static ReverbDto From(ReverbParameters reverb) => new()
        {
            On = reverb.On,
            Size = reverb.Size,
            Wet = reverb.Wet,
        };

        public ReverbParameters ToParameters(ReverbParameters fallback) => new()
        {
            On = On ?? fallback.On,
            Size = Size ?? fallback.Size,
            Wet = Wet ?? fallback.Wet,
        };
    }

    private sealed class PitchShiftDto
    {
        public bool? On { get; set; }
        public double? Semitones { get; set; }

        public static PitchShiftDto From(PitchShiftParameters pitch) => new()
        {
            On = pitch.On,
            Semitones = pitch.Semitones,
        };

        public PitchShiftParameters ToParameters(PitchShiftParameters fallback) => new(
            On ?? fallback.On,
            Semitones ?? fallback.Semitones);
    }
}
