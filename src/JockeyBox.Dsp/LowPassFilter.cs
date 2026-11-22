namespace JockeyBox.Dsp;

/// <summary>
/// 12 dB/oct low-pass. Cutoff and Q ramp linearly over <c>rampFrames</c> frames (64 by
/// default) and coefficients are refreshed every 16 frames while the ramp runs.
/// </summary>
public sealed class LowPassFilter : IAudioProcessor
{
    /// <summary>
    /// Cutoff at or above this is the neutral ceiling. An 18 kHz+ biquad near Nyquist is not
    /// unity, so this frequency is defined as a pass-through; that keeps the neutral strip
    /// bit-transparent.
    /// </summary>
    public const double TransparentFrequencyHz = 20000;

    /// <summary>Butterworth Q: the only Q at which <see cref="TransparentFrequencyHz"/> is neutral.</summary>
    public const double DefaultQ = 0.7071067811865476;

    private readonly double _sampleRate;
    private readonly int _rampFrames;
    private readonly Biquad _biquad = new();
    private Smoother _frequency;
    private Smoother _q;
    private double _freqHz;
    private double _qValue;
    private bool _wasNeutral;

    public LowPassFilter(double sampleRate, double frequencyHz = 20000, double q = DefaultQ,
        string id = "filter.lowpass", int rampFrames = 64)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _rampFrames = Math.Max(0, rampFrames);
        _freqHz = ClampFrequency(frequencyHz, sampleRate);
        _qValue = q <= 0 ? DefaultQ : q;
        _frequency = new Smoother(_freqHz);
        _q = new Smoother(_qValue);
        _biquad.SetLowPass(_sampleRate, _freqHz, _qValue);
    }

    public string Id { get; }

    public int LatencyFrames => 0;

    public bool Bypass { get; set; }

    /// <summary>
    /// True when the filter cannot change the signal: cutoff at the neutral ceiling, no ramp
    /// in flight, default Q. <see cref="Process"/> then leaves the buffer untouched.
    /// </summary>
    public bool IsNeutral =>
        _frequency.Remaining == 0 && _q.Remaining == 0 &&
        _freqHz >= TransparentFrequencyHz && _qValue == DefaultQ;

    public double FrequencyHz
    {
        get => _freqHz;
        set
        {
            _freqHz = ClampFrequency(value, _sampleRate);
            _frequency.SetTarget(_freqHz, _rampFrames);
        }
    }

    public double Q
    {
        get => _qValue;
        set
        {
            _qValue = value <= 0 ? DefaultQ : value;
            _q.SetTarget(_qValue, _rampFrames);
        }
    }

    public void Process(Span<float> samples)
    {
        if (Bypass) return;

        if (IsNeutral)
        {
            // Drop any tail left by earlier non-neutral processing so the buffer is untouched.
            if (!_wasNeutral) _biquad.Reset();
            _wasNeutral = true;
            return;
        }

        _wasNeutral = false;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 15) == 0 && (_frequency.Remaining > 0 || _q.Remaining > 0))
            {
                _biquad.SetLowPass(_sampleRate, _frequency.Value, _q.Value);
            }

            _frequency.Next();
            _q.Next();
            samples[i] = _biquad.ProcessSample(samples[i]);
        }
    }

    public void Reset()
    {
        _biquad.Reset();
        _frequency.Snap();
        _q.Snap();
    }

    private static double ClampFrequency(double frequency, double sampleRate) =>
        Math.Clamp(frequency, 5, sampleRate * 0.49);
}
