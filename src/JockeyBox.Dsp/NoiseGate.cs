namespace JockeyBox.Dsp;

/// <summary>
/// Downward expander with hysteresis, hold and a floor. The gate opens when the detected
/// envelope reaches <see cref="ThresholdDb"/> and closes once it drops
/// <see cref="HysteresisDb"/> below that and the hold time has elapsed, so it cannot
/// chatter around the threshold. Gain moves on linear ramps (attack when opening, release
/// when closing) which keeps the transition click-free.
/// </summary>
public sealed class NoiseGate : IAudioProcessor
{
    private readonly double _sampleRate;
    private double _thresholdDb = -40;
    private double _hysteresisDb = 3;
    private double _attackMs = 5;
    private double _releaseMs = 100;
    private double _holdMs = 50;
    private double _floorDb = -80;

    private double _envAttackCoef;
    private double _envReleaseCoef;
    private double _attackSamples;
    private double _releaseSamples;
    private int _holdSamples;
    private double _floorLin;
    private double _gainStepUp;
    private double _gainStepDown;

    private double _env;
    private double _gain;
    private int _holdRemaining;
    private bool _open;

    public NoiseGate(double sampleRate, double thresholdDb = -40, double hysteresisDb = 3,
        double attackMs = 5, double releaseMs = 100, double holdMs = 50, double floorDb = -80,
        string id = "dynamics.gate")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _thresholdDb = thresholdDb;
        _hysteresisDb = Math.Max(0, hysteresisDb);
        _attackMs = Math.Max(0.01, attackMs);
        _releaseMs = Math.Max(0.01, releaseMs);
        _holdMs = Math.Max(0, holdMs);
        _floorDb = Math.Min(0, floorDb);
        Recompute();
        _gain = _floorLin;
        _env = 0;
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

    public double HysteresisDb
    {
        get => _hysteresisDb;
        set
        {
            _hysteresisDb = Math.Max(0, value);
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

    public double HoldMs
    {
        get => _holdMs;
        set
        {
            _holdMs = Math.Max(0, value);
            Recompute();
        }
    }

    public double FloorDb
    {
        get => _floorDb;
        set
        {
            _floorDb = Math.Min(0, value);
            Recompute();
        }
    }

    /// <summary>Current attenuation in dB; 0 when fully open, <see cref="FloorDb"/> when closed.</summary>
    public double GainReductionDb => 20 * Math.Log10(Math.Max(_gain, 1e-9));

    /// <summary>True while the gate is passing signal.</summary>
    public bool IsOpen => _open;

    public void Process(Span<float> samples)
    {
        if (Bypass) return;

        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i];

            // Peak-ish envelope with fast attack and slower release, in linear amplitude.
            double magnitude = Math.Abs(x);
            double envCoef = magnitude > _env ? _envAttackCoef : _envReleaseCoef;
            _env += (magnitude - _env) * envCoef;
            double envDb = _env > 1e-9 ? 20 * Math.Log10(_env) : -200;

            if (!_open)
            {
                if (envDb >= _thresholdDb)
                {
                    _open = true;
                    _holdRemaining = _holdSamples;
                }
            }
            else if (envDb < _thresholdDb - _hysteresisDb)
            {
                if (_holdRemaining > 0) _holdRemaining--;
                else _open = false;
            }
            else
            {
                _holdRemaining = _holdSamples;
            }

            double target = _open ? 1.0 : _floorLin;
            if (target > _gain) _gain = Math.Min(target, _gain + _gainStepUp);
            else _gain = Math.Max(target, _gain - _gainStepDown);

            samples[i] = (float)(x * _gain);
        }
    }

    public void Reset()
    {
        _env = 0;
        _gain = _floorLin;
        _holdRemaining = 0;
        _open = false;
    }

    private void Recompute()
    {
        double sr = _sampleRate;
        _envAttackCoef = 1 - Math.Exp(-1.0 / (0.001 * sr));
        _envReleaseCoef = 1 - Math.Exp(-1.0 / (0.020 * sr));
        _attackSamples = Math.Max(1, _attackMs * 0.001 * sr);
        _releaseSamples = Math.Max(1, _releaseMs * 0.001 * sr);
        _holdSamples = (int)Math.Round(_holdMs * 0.001 * sr);
        _floorLin = Math.Pow(10, _floorDb / 20.0);
        _gainStepUp = (1.0 - _floorLin) / _attackSamples;
        _gainStepDown = (1.0 - _floorLin) / _releaseSamples;
    }
}
