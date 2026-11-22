namespace JockeyBox.Dsp;

/// <summary>
/// Single peaking band. Frequency, Q and gain ramp linearly over <c>rampFrames</c> frames
/// (64 by default); coefficients refresh every 16 frames while the ramp runs.
/// </summary>
public sealed class PeakingEq : IAudioProcessor
{
    private readonly double _sampleRate;
    private readonly int _rampFrames;
    private readonly Biquad _biquad = new();
    private Smoother _frequency;
    private Smoother _q;
    private Smoother _gain;
    private double _freqHz;
    private double _qValue;
    private double _gainDb;

    public PeakingEq(double sampleRate, double frequencyHz = 1000, double q = 1.41, double gainDb = 0,
        string id = "eq.peaking", int rampFrames = 64)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _rampFrames = Math.Max(0, rampFrames);
        _freqHz = ClampFrequency(frequencyHz, sampleRate);
        _qValue = q <= 0 ? 0.7071067811865476 : q;
        _gainDb = Math.Clamp(gainDb, -24, 24);
        _frequency = new Smoother(_freqHz);
        _q = new Smoother(_qValue);
        _gain = new Smoother(_gainDb);
        _biquad.SetPeaking(_sampleRate, _freqHz, _qValue, _gainDb);
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

    public double GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = Math.Clamp(value, -24, 24);
            _gain.SetTarget(_gainDb, _rampFrames);
        }
    }

    public void Process(Span<float> samples)
    {
        if (Bypass) return;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 15) == 0 && (_frequency.Remaining > 0 || _q.Remaining > 0 || _gain.Remaining > 0))
            {
                _biquad.SetPeaking(_sampleRate, _frequency.Value, _q.Value, _gain.Value);
            }

            _frequency.Next();
            _q.Next();
            _gain.Next();
            samples[i] = _biquad.ProcessSample(samples[i]);
        }
    }

    public void Reset()
    {
        _biquad.Reset();
        _frequency.Snap();
        _q.Snap();
        _gain.Snap();
    }

    private static double ClampFrequency(double frequency, double sampleRate) =>
        Math.Clamp(frequency, 5, sampleRate * 0.49);
}
