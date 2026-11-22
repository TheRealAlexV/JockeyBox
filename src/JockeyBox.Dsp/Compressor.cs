namespace JockeyBox.Dsp;

/// <summary>
/// Feed-forward compressor with a soft knee.
/// <para>
/// Detector: RMS. The squared input is smoothed with a 10 ms one-pole, so the sidechain
/// follows average level rather than a single peak, then the gain is smoothed in dB with
/// the attack/release constants. <see cref="GainReductionDb"/> is the applied gain in dB
/// (0 when below threshold, negative while compressing).
/// </para>
/// </summary>
public sealed class Compressor : IAudioProcessor
{
    private readonly double _sampleRate;
    private double _thresholdDb = -20;
    private double _ratio = 4;
    private double _attackMs = 10;
    private double _releaseMs = 100;
    private double _kneeDb = 6;
    private double _makeupDb;

    private double _rmsCoef;
    private double _attackCoef;
    private double _releaseCoef;

    private double _rmsSquared;
    private double _gainDb;

    public Compressor(double sampleRate, double thresholdDb = -20, double ratio = 4, double attackMs = 10,
        double releaseMs = 100, double kneeDb = 6, double makeupDb = 0, string id = "dynamics.compressor")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _thresholdDb = thresholdDb;
        _ratio = Math.Max(1, ratio);
        _attackMs = Math.Max(0.01, attackMs);
        _releaseMs = Math.Max(0.01, releaseMs);
        _kneeDb = Math.Max(0, kneeDb);
        _makeupDb = makeupDb;
        Recompute();
    }

    public string Id { get; }

    public int LatencyFrames => 0;

    public bool Bypass { get; set; }

    public double ThresholdDb
    {
        get => _thresholdDb;
        set
        {
            _thresholdDb = value;
            Recompute();
        }
    }

    public double Ratio
    {
        get => _ratio;
        set
        {
            _ratio = Math.Max(1, value);
            Recompute();
        }
    }

    public double AttackMs
    {
        get => _attackMs;
        set
        {
            _attackMs = Math.Max(0.01, value);
            Recompute();
        }
    }

    public double ReleaseMs
    {
        get => _releaseMs;
        set
        {
            _releaseMs = Math.Max(0.01, value);
            Recompute();
        }
    }

    public double KneeDb
    {
        get => _kneeDb;
        set
        {
            _kneeDb = Math.Max(0, value);
            Recompute();
        }
    }

    public double MakeupDb
    {
        get => _makeupDb;
        set => _makeupDb = value;
    }

    /// <summary>Current gain change in dB, including makeup. 0 or negative while compressing.</summary>
    public double GainReductionDb => _gainDb;

    public void Process(Span<float> samples)
    {
        if (Bypass) return;

        double slope = 1.0 - 1.0 / _ratio;
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i];

            // RMS detector: one-pole average of x^2 with a 10 ms window.
            _rmsSquared += (x * x - _rmsSquared) * _rmsCoef;
            double rms = Math.Sqrt(_rmsSquared);
            double inputDb = rms > 1e-6 ? 20 * Math.Log10(rms) : -120;
            double overDb = inputDb - _thresholdDb;

            double targetGainDb;
            if (_kneeDb > 0 && Math.Abs(overDb) < _kneeDb / 2)
            {
                double t = overDb + _kneeDb / 2;
                targetGainDb = -slope * t * t / (2 * _kneeDb);
            }
            else if (overDb >= _kneeDb / 2)
            {
                targetGainDb = -slope * overDb;
            }
            else
            {
                targetGainDb = 0;
            }

            double coef = targetGainDb < _gainDb ? _attackCoef : _releaseCoef;
            _gainDb += (targetGainDb - _gainDb) * coef;

            samples[i] = (float)(x * Math.Pow(10, (_gainDb + _makeupDb) / 20.0));
        }
    }

    public void Reset()
    {
        _rmsSquared = 0;
        _gainDb = 0;
    }

    private void Recompute()
    {
        double sr = _sampleRate;
        _rmsCoef = 1 - Math.Exp(-1.0 / (0.010 * sr));
        _attackCoef = 1 - Math.Exp(-1.0 / (Math.Max(0.01, _attackMs) * 0.001 * sr));
        _releaseCoef = 1 - Math.Exp(-1.0 / (Math.Max(0.01, _releaseMs) * 0.001 * sr));
    }
}
