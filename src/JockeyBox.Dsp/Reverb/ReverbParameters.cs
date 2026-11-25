namespace JockeyBox.Dsp.Reverb;

/// <summary>
/// Send parameters for one <see cref="ReverbSend"/>. Mutable value type: every setter pushes
/// the value through <see cref="Ranges"/> so a NaN from the UI, a preset or a remote client
/// arrives as something usable instead of poisoning the tail.
/// <para><see cref="Wet"/> is capped at <see cref="MaxWet"/>. That cap is a product decision:
/// a reverb that can be pushed to full wet is a different instrument, so the ceiling lives
/// here and the getter always reports the clamped value.</para>
/// </summary>
public record struct ReverbParameters
{
    public const double MinSize = 0;
    public const double MaxSize = 1;
    public const double MinWet = 0;
    public const double MaxWet = 0.4;

    private const double DefaultSize = 0.5;

    private bool _on;
    private double _size;
    private double _wet;

    public ReverbParameters(bool on, double size, double wet)
    {
        _on = on;
        _size = Ranges.Fix(size, MinSize, MaxSize, DefaultSize);
        _wet = Ranges.Fix(wet, MinWet, MaxWet, MinWet);
    }

    /// <summary>Off by default: the send costs nothing until someone asks for it.</summary>
    public static readonly ReverbParameters Neutral = new(false, DefaultSize, MinWet);

    public bool On
    {
        readonly get => _on;
        set => _on = value;
    }

    /// <summary>Room size, 0..1. Feedback is derived from this by <see cref="Freeverb"/>.</summary>
    public double Size
    {
        readonly get => _size;
        set => _size = Ranges.Fix(value, MinSize, MaxSize, DefaultSize);
    }

    /// <summary>Wet return level, 0..<see cref="MaxWet"/> inclusive.</summary>
    public double Wet
    {
        readonly get => _wet;
        set => _wet = Ranges.Fix(value, MinWet, MaxWet, MinWet);
    }

    /// <summary>Nothing to hear: switched off, or the wet level is zero.</summary>
    public bool IsNeutral => !_on || _wet <= MinWet;

    /// <summary>Returns a copy with every field brought into range.</summary>
    public readonly ReverbParameters Clamp() => new(_on, _size, _wet);
}
