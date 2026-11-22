namespace JockeyBox.Dsp;

/// <summary>
/// 10-band graphic EQ built from ISO octave centres. Hard rule: when every band gain is
/// exactly 0 dB (<see cref="IsFlat"/>) or <see cref="Bypass"/> is set, <see cref="Process"/>
/// does not read or write the buffer at all.
/// </summary>
public sealed class TenBandEq : IAudioProcessor
{
    /// <summary>ISO octave band centres in Hz.</summary>
    public static readonly double[] CentresHz = { 31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    /// <summary>Per-band Q for one-octave spacing.</summary>
    public const double BandQ = 1.41;

    private readonly double _sampleRate;
    private readonly Biquad[] _bands = new Biquad[CentresHz.Length];
    private readonly double[] _gainsDb = new double[CentresHz.Length];

    public TenBandEq(double sampleRate, string id = "eq.tenband")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        for (int i = 0; i < _bands.Length; i++)
        {
            _bands[i] = new Biquad();
            _bands[i].SetPeaking(_sampleRate, CentresHz[i], BandQ, 0);
        }
    }

    public string Id { get; }

    public int LatencyFrames => 0;

    public bool Bypass { get; set; }

    /// <summary>True when every band gain is exactly 0 dB.</summary>
    public bool IsFlat
    {
        get
        {
            for (int i = 0; i < _gainsDb.Length; i++)
            {
                if (_gainsDb[i] != 0.0) return false;
            }

            return true;
        }
    }

    public double GainDb(int band) => _gainsDb[Index(band)];

    /// <summary>Sets one band's gain in dB, clamped to +/-15.</summary>
    public void SetGainDb(int band, double gainDb)
    {
        int i = Index(band);
        double clamped = Math.Clamp(gainDb, -15, 15);
        if (_gainsDb[i] == clamped) return;
        _gainsDb[i] = clamped;
        _bands[i].SetPeaking(_sampleRate, CentresHz[i], BandQ, clamped);
    }

    public void Process(Span<float> samples)
    {
        if (Bypass || IsFlat) return;

        for (int i = 0; i < _bands.Length; i++)
        {
            if (_gainsDb[i] == 0.0) continue;
            _bands[i].Process(samples);
        }
    }

    public void Reset()
    {
        for (int i = 0; i < _bands.Length; i++)
        {
            _bands[i].Reset();
        }
    }

    private static int Index(int band)
    {
        if ((uint)band >= (uint)CentresHz.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, $"Band index must be 0..{CentresHz.Length - 1}.");
        }

        return band;
    }
}
