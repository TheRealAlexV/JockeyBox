namespace JockeyBox.Dsp;

/// <summary>
/// Parameters for one <see cref="PitchShiftStage"/>. Mutable value type, same idiom as
/// <c>ReverbParameters</c>: every setter pushes the value through <see cref="Ranges"/> so a NaN
/// from the UI, a preset or a remote client arrives as something usable.
/// <para><b>Range: ±12 semitones.</b> One octave is the useful bound for a monitor pitch
/// effect. Past an octave the delay-line read head runs at more than 2x or less than 0.5x the
/// write rate, linear-interpolation error (a lowpass plus aliasing) dominates, and the voice is
/// no longer recognisable anyway: the spike's §2.4 notes that cheap hardware pedals top out
/// around an octave for the same reason. The bound is also what keeps the crossfade rate sane
/// (|ratio - 1| ≤ 1).</para>
/// </summary>
public record struct PitchShiftParameters
{
    public const double MinSemitones = -12;
    public const double MaxSemitones = 12;

    /// <summary>Zero semitones: the stage must be bit-transparent, so this is the fallback.</summary>
    public const double DefaultSemitones = 0;

    private bool _on;
    private double _semitones;

    public PitchShiftParameters(bool on, double semitones)
    {
        _on = on;
        _semitones = Ranges.Fix(semitones, MinSemitones, MaxSemitones, DefaultSemitones);
    }

    /// <summary>Off and centred by default: the stage costs nothing until someone asks for it.</summary>
    public static readonly PitchShiftParameters Neutral = new(false, DefaultSemitones);

    public bool On
    {
        readonly get => _on;
        set => _on = value;
    }

    /// <summary>Shift in semitones, ±<see cref="MaxSemitones"/>.</summary>
    public double Semitones
    {
        readonly get => _semitones;
        set => _semitones = Ranges.Fix(value, MinSemitones, MaxSemitones, DefaultSemitones);
    }

    /// <summary>
    /// Nothing to hear: switched off, or exactly zero semitones. Zero is the bit-exact fast
    /// path, so the test is exact rather than a near-zero tolerance.
    /// </summary>
    public bool IsNeutral => !_on || _semitones == 0;

    /// <summary>Returns a copy with every field brought into range.</summary>
    public readonly PitchShiftParameters Clamp() => new(_on, _semitones);
}
