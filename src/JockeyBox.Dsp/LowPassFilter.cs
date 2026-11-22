namespace JockeyBox.Dsp;

/// <summary>
/// 12 dB/oct low-pass. Cutoff and Q ramp linearly over <c>rampFrames</c> frames (64 by
/// default) and coefficients are refreshed every 16 frames while the ramp runs.
/// </summary>
public sealed class LowPassFilter : IAudioProcessor
{
    private readonly double _sampleRate;
    private readonly int _rampFrames;
    private readonly Biquad _biquad = new();
    private Smoother _frequency;
    private Smoother _q;
    private double _freqHz;
    private double _qValue;

    public LowPassFilter(double sampleRate, double frequencyHz = 20000, double q = 0.7071067811865476,
        string id = "filter.lowpass", int rampFrames = 64)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _rampFrames = Math.Max(0, rampFrames);
        _freqHz = ClampFrequency(frequencyHz, sampleRate);
        _qValue = q <= 0 ? 0.7071067811865476 : q;
        _frequency = new Smoother(_freqHz);
        _q = new Smoother(_qValue);
        _biquad.SetLowPass(_sampleRate, _freqHz, _qValue);
    }

    public string Id { get; }

    public int LatencyFrames => 0;

    public bool Bypass { get; set; }

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
            _qValue = value <= 0 ? 0.7071067811865476 : value;
            _q.SetTarget(_qValue, _rampFrames);
        }
    }

    public void Process(Span<float> samples)
    {
        if (Bypass) return;
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
