namespace JockeyBox.Dsp;

/// <summary>
/// Split-band de-esser. A 4th-order Butterworth low band is subtracted from the input to
/// form the high band, so the two halves sum back to the input exactly and no allpass is
/// needed for phase alignment. Only the high band is multiplied by the gain, which turns the
/// stage into a variable-depth high shelf: below the crossover the signal is never scaled.
/// <para>
/// Detector: the input band-passed to 4.5-9 kHz, then a one-pole RMS with a 1 ms attack and
/// a 50 ms release. Gain follows <see cref="Compressor"/>'s law (no knee, hard max reduction)
/// and moves on per-sample linear ramps, so it cannot click.
/// </para>
/// </summary>
public sealed class DeEsser : IAudioProcessor
{
    private const double ButterworthQ = 0.7071067811865476;
    private const double DetectorAttackMs = 1;
    private const double DetectorReleaseMs = 50;

    private readonly double _sampleRate;

    private readonly Biquad _lowA = new();
    private readonly Biquad _lowB = new();
    private readonly Biquad _sidechainHighPass = new();
    private readonly Biquad _sidechainLowPass = new();

    private double _detectorAttackCoef;
    private double _detectorReleaseCoef;
    private double _gainStepDown;
    private double _gainStepUp;

    private double _meanSquare;
    private double _gain = 1;

    private DeEsserParameters _parameters;

    public DeEsser(double sampleRate, DeEsserParameters? parameters = null, string id = "dynamics.deesser")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _parameters = (parameters ?? DeEsserParameters.Neutral).Clamp();
        Retune();
    }

    public string Id { get; }

    public int LatencyFrames => 0;

    public bool Bypass { get; set; }

    public bool IsNeutral => !_parameters.Enabled;

    /// <summary>Current parameters. Assigning retunes the filters and the ramps.</summary>
    public DeEsserParameters Parameters
    {
        get => _parameters;
        set
        {
            _parameters = value.Clamp();
            Retune();
        }
    }

    /// <summary>Current high-band gain in dB. 0 or negative, never positive.</summary>
    public double GainDb => 20 * Math.Log10(_gain);

    public void Process(Span<float> samples)
    {
        if (Bypass || IsNeutral) return;

        double thresholdDb = _parameters.ThresholdDb;
        double slope = 1.0 - 1.0 / _parameters.Ratio;
        double maxReductionDb = _parameters.MaxReductionDb;

        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i];

            // Split by complementary subtraction: high = x - low, so low + high == x.
            double low = _lowB.ProcessSample(_lowA.ProcessSample(samples[i]));

            // Sidechain: only the sibilant band drives the gain.
            double side = _sidechainLowPass.ProcessSample(_sidechainHighPass.ProcessSample(samples[i]));
            double square = side * side;
            double coef = square > _meanSquare ? _detectorAttackCoef : _detectorReleaseCoef;
            _meanSquare += (square - _meanSquare) * coef;

            double levelDb = _meanSquare > 1e-12 ? 10 * Math.Log10(_meanSquare) : -120;
            double overDb = levelDb - thresholdDb;
            double targetDb = overDb <= 0 ? 0 : -slope * overDb;
            if (targetDb < -maxReductionDb) targetDb = -maxReductionDb;
            double targetGain = Math.Pow(10, targetDb / 20.0);

            if (targetGain < _gain)
            {
                _gain -= _gainStepDown;
                if (_gain < targetGain) _gain = targetGain;
            }
            else
            {
                _gain += _gainStepUp;
                if (_gain > targetGain) _gain = targetGain;
            }

            samples[i] = (float)(low + _gain * (x - low));
        }
    }

    public void Reset()
    {
        _lowA.Reset();
        _lowB.Reset();
        _sidechainHighPass.Reset();
        _sidechainLowPass.Reset();
        _meanSquare = 0;
        _gain = 1;
    }

    private void Retune()
    {
        _lowA.SetLowPass(_sampleRate, _parameters.FrequencyHz, ButterworthQ);
        _lowB.SetLowPass(_sampleRate, _parameters.FrequencyHz, ButterworthQ);
        _sidechainHighPass.SetHighPass(_sampleRate, _parameters.SidechainLowHz, ButterworthQ);
        _sidechainLowPass.SetLowPass(_sampleRate, _parameters.SidechainHighHz, ButterworthQ);

        _detectorAttackCoef = 1 - Math.Exp(-1.0 / (DetectorAttackMs * 0.001 * _sampleRate));
        _detectorReleaseCoef = 1 - Math.Exp(-1.0 / (DetectorReleaseMs * 0.001 * _sampleRate));
        _gainStepDown = 1.0 / Math.Max(1.0, _parameters.AttackMs * 0.001 * _sampleRate);
        _gainStepUp = 1.0 / Math.Max(1.0, _parameters.ReleaseMs * 0.001 * _sampleRate);
    }
}
