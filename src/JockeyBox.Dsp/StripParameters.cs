using JockeyBox.Dsp.Reverb;

namespace JockeyBox.Dsp;

/// <summary>
/// Brings a knob value into range without throwing. Callers hand us values from the UI, a
/// preset file or a remote client, so NaN has to become something usable instead of
/// poisoning every ramp downstream.
/// </summary>
internal static class Ranges
{
    public static double Fix(double value, double min, double max, double fallback) =>
        double.IsNaN(value) ? fallback : Math.Clamp(value, min, max);
}

/// <summary>
/// The ten ISO octave band gains of one strip, in dB. Immutable value type: ten named
/// doubles, no array, so copying it allocates nothing and equality is structural.
/// </summary>
public readonly record struct EqParameters(
    double Gain31Db,
    double Gain63Db,
    double Gain125Db,
    double Gain250Db,
    double Gain500Db,
    double Gain1kDb,
    double Gain2kDb,
    double Gain4kDb,
    double Gain8kDb,
    double Gain16kDb)
{
    public const double MinGainDb = -15;
    public const double MaxGainDb = 15;

    /// <summary>Every band at 0 dB. Bit-transparent through <see cref="TenBandEq"/>.</summary>
    public static readonly EqParameters Flat = default;

    /// <summary>True when every band is exactly 0 dB.</summary>
    public bool IsFlat =>
        Gain31Db == 0 && Gain63Db == 0 && Gain125Db == 0 && Gain250Db == 0 && Gain500Db == 0 &&
        Gain1kDb == 0 && Gain2kDb == 0 && Gain4kDb == 0 && Gain8kDb == 0 && Gain16kDb == 0;

    /// <summary>Gain of one band, indexed 0..9 in <see cref="TenBandEq.CentresHz"/> order.</summary>
    public double this[int band] => band switch
    {
        0 => Gain31Db,
        1 => Gain63Db,
        2 => Gain125Db,
        3 => Gain250Db,
        4 => Gain500Db,
        5 => Gain1kDb,
        6 => Gain2kDb,
        7 => Gain4kDb,
        8 => Gain8kDb,
        9 => Gain16kDb,
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "band must be 0..9"),
    };

    public EqParameters Clamp() => new(
        Ranges.Fix(Gain31Db, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain63Db, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain125Db, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain250Db, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain500Db, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain1kDb, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain2kDb, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain4kDb, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain8kDb, MinGainDb, MaxGainDb, 0),
        Ranges.Fix(Gain16kDb, MinGainDb, MaxGainDb, 0));
}

/// <summary>On/off plus cutoff for one of the two band-limit filters.</summary>
public readonly record struct FilterParameters(bool Enabled, double FrequencyHz)
{
    public const double MinFrequencyHz = 20;
    public const double MaxFrequencyHz = 24000;

    public FilterParameters Clamp() =>
        this with { FrequencyHz = Ranges.Fix(FrequencyHz, MinFrequencyHz, MaxFrequencyHz, MinFrequencyHz) };
}

/// <summary>Downward expander settings. Disabled is the neutral state: the gate is off.</summary>
public readonly record struct GateParameters(
    bool Enabled,
    double ThresholdDb = -40,
    double HysteresisDb = 3,
    double AttackMs = 5,
    double ReleaseMs = 100,
    double HoldMs = 50,
    double FloorDb = -60)
{
    /// <summary>
    /// Gate off at the shipping defaults for threshold, timing and floor.
    /// <para>
    /// A gate cannot be transparent while it is armed: attenuating content below the threshold
    /// is its job, so a -40 dB threshold inevitably changes a quiet signal. The neutral strip
    /// therefore carries the gate switched off, exactly as it does the compressor.
    /// </para>
    /// </summary>
    public static readonly GateParameters Neutral = new(Enabled: false);

    public GateParameters Clamp() => new(
        Enabled,
        Ranges.Fix(ThresholdDb, -80, 0, -40),
        Ranges.Fix(HysteresisDb, 0, 24, 3),
        Ranges.Fix(AttackMs, 0.01, 1000, 5),
        Ranges.Fix(ReleaseMs, 0.01, 5000, 100),
        Ranges.Fix(HoldMs, 0, 2000, 50),
        Ranges.Fix(FloorDb, -120, 0, -60));
}

/// <summary>
/// De-esser settings. Disabled is the neutral state.
/// </summary>
public readonly record struct DeEsserParameters(
    bool Enabled,
    double FrequencyHz = 6000,
    double ThresholdDb = -28,
    double Ratio = 4,
    double AttackMs = 0.5,
    double ReleaseMs = 50,
    double MaxReductionDb = 12,
    double SidechainLowHz = 4500,
    double SidechainHighHz = 9000)
{
    public static readonly DeEsserParameters Neutral = new(Enabled: false);

    public DeEsserParameters Clamp() => new(
        Enabled,
        Ranges.Fix(FrequencyHz, 1000, 20000, 6000),
        Ranges.Fix(ThresholdDb, -60, 0, -28),
        Ranges.Fix(Ratio, 1, 20, 4),
        Ranges.Fix(AttackMs, 0.01, 1000, 0.5),
        Ranges.Fix(ReleaseMs, 0.01, 5000, 50),
        Ranges.Fix(MaxReductionDb, 0, 60, 12),
        Ranges.Fix(SidechainLowHz, 20, 20000, 4500),
        Ranges.Fix(SidechainHighHz, 20, 24000, 9000));
}

/// <summary>Compressor settings. Disabled is the neutral state.</summary>
public readonly record struct CompressorParameters(
    bool Enabled,
    double ThresholdDb = -20,
    double Ratio = 4,
    double AttackMs = 10,
    double ReleaseMs = 100,
    double KneeDb = 6,
    double MakeupDb = 0)
{
    public static readonly CompressorParameters Neutral = new(Enabled: false);

    public CompressorParameters Clamp() => new(
        Enabled,
        Ranges.Fix(ThresholdDb, -60, 0, -20),
        Ranges.Fix(Ratio, 1, 20, 4),
        Ranges.Fix(AttackMs, 0.01, 1000, 10),
        Ranges.Fix(ReleaseMs, 0.01, 5000, 100),
        Ranges.Fix(KneeDb, 0, 24, 6),
        Ranges.Fix(MakeupDb, -12, 24, 0));
}

/// <summary>Lookahead limiter settings. Disabled is the neutral state: no latency added.</summary>
public readonly record struct LimiterParameters(
    bool Enabled,
    double CeilingDb = 0,
    double ReleaseMs = 50)
{
    public static readonly LimiterParameters Neutral = new(Enabled: false);

    public LimiterParameters Clamp() => new(
        Enabled,
        Ranges.Fix(CeilingDb, -12, 0, 0),
        Ranges.Fix(ReleaseMs, 1, 1000, 50));
}

/// <summary>
/// One input device's whole channel strip as an immutable value: EQ, band limits, gate,
/// compressor, limiter and a master bypass. Every field is a *target* value; the strip ramps
/// towards it, so assigning a new instance never clicks.
/// </summary>
public readonly record struct StripParameters(
    EqParameters Eq,
    FilterParameters HighPass,
    FilterParameters LowPass,
    GateParameters Gate,
    CompressorParameters Compressor,
    LimiterParameters Limiter,
    DeEsserParameters DeEsser,
    ReverbParameters Reverb,
    bool MasterBypass = false)
{
    /// <summary>
    /// The transparent strip: flat EQ, band limits parked at their pass-through edges
    /// (20 Hz high-pass, 20 kHz low-pass), gate and compressor off, no limiter, no master
    /// bypass. Processing it leaves the buffer bit-identical and adds zero latency.
    /// </summary>
    public static readonly StripParameters Neutral = new(
        EqParameters.Flat,
        new FilterParameters(Enabled: true, HighPassFilter.TransparentFrequencyHz),
        new FilterParameters(Enabled: true, LowPassFilter.TransparentFrequencyHz),
        GateParameters.Neutral,
        CompressorParameters.Neutral,
        LimiterParameters.Neutral,
        DeEsserParameters.Neutral,
        ReverbParameters.Neutral);

    /// <summary>
    /// Returns this value with every field inside its valid range. Never throws: NaN becomes
    /// the field's neutral value, out-of-range values are clamped.
    /// </summary>
    public StripParameters Clamp() => this with
    {
        Eq = Eq.Clamp(),
        HighPass = HighPass.Clamp(),
        LowPass = LowPass.Clamp(),
        Gate = Gate.Clamp(),
        Compressor = Compressor.Clamp(),
        Limiter = Limiter.Clamp(),
        DeEsser = DeEsser.Clamp(),
        Reverb = Reverb.Clamp(),
    };

    /// <summary>Alias for <see cref="Clamp"/>: the spelling used by callers that mean "make this safe to apply".</summary>
    public StripParameters Normalise() => Clamp();
}
