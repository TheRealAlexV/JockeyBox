namespace JockeyBox.Dsp;

/// <summary>
/// Second-order IIR section. Coefficients follow the RBJ Audio EQ Cookbook and the state
/// is transposed direct form II, so a per-sample call allocates nothing and needs only
/// two doubles of state.
/// </summary>
public sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _s1, _s2;

    /// <summary>Clears the two state variables.</summary>
    public void Reset()
    {
        _s1 = 0;
        _s2 = 0;
    }

    /// <summary>Transposed direct form II, one frame, allocation-free.</summary>
    public float ProcessSample(float input)
    {
        double x = input;
        double y = _b0 * x + _s1;
        _s1 = _b1 * x - _a1 * y + _s2;
        _s2 = _b2 * x - _a2 * y;
        return (float)y;
    }

    /// <summary>Processes one channel in place.</summary>
    public void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = ProcessSample(samples[i]);
        }
    }

    /// <summary>Low-pass, RBJ cookbook.</summary>
    public void SetLowPass(double sampleRate, double frequency, double q = 0.7071067811865476)
    {
        (double _, double cosW, double sinW) = Normalised(sampleRate, frequency);
        double alpha = sinW / (2 * Q(q));
        double b0 = (1 - cosW) / 2;
        double b1 = 1 - cosW;
        double b2 = (1 - cosW) / 2;
        Set(b0, b1, b2, 1 + alpha, -2 * cosW, 1 - alpha);
    }

    /// <summary>High-pass, RBJ cookbook.</summary>
    public void SetHighPass(double sampleRate, double frequency, double q = 0.7071067811865476)
    {
        (double _, double cosW, double sinW) = Normalised(sampleRate, frequency);
        double alpha = sinW / (2 * Q(q));
        double b0 = (1 + cosW) / 2;
        double b1 = -(1 + cosW);
        double b2 = (1 + cosW) / 2;
        Set(b0, b1, b2, 1 + alpha, -2 * cosW, 1 - alpha);
    }

    /// <summary>Peaking EQ, RBJ cookbook. A = 10^(gainDb/40), so the response at the centre is gainDb.</summary>
    public void SetPeaking(double sampleRate, double frequency, double q, double gainDb)
    {
        (double _, double cosW, double sinW) = Normalised(sampleRate, frequency);
        double alpha = sinW / (2 * Q(q));
        double a = Math.Pow(10, gainDb / 40.0);
        double b0 = 1 + alpha * a;
        double b1 = -2 * cosW;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        Set(b0, b1, b2, a0, -2 * cosW, 1 - alpha / a);
    }

    /// <summary>Low shelf, RBJ cookbook. <paramref name="shelfSlope"/> is S (0.5..1, 1 = steepest).</summary>
    public void SetLowShelf(double sampleRate, double frequency, double shelfSlope, double gainDb)
    {
        (double _, double cosW, double sinW) = Normalised(sampleRate, frequency);
        double a = Math.Pow(10, gainDb / 40.0);
        double s = shelfSlope <= 0 ? 1 : shelfSlope;
        double alpha = sinW / 2 * Math.Sqrt((a + 1 / a) * (1 / s - 1) + 2);
        double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;
        double b0 = a * ((a + 1) - (a - 1) * cosW + twoSqrtAAlpha);
        double b1 = 2 * a * ((a - 1) - (a + 1) * cosW);
        double b2 = a * ((a + 1) - (a - 1) * cosW - twoSqrtAAlpha);
        double a0 = (a + 1) + (a - 1) * cosW + twoSqrtAAlpha;
        double a1 = -2 * ((a - 1) + (a + 1) * cosW);
        double a2 = (a + 1) + (a - 1) * cosW - twoSqrtAAlpha;
        Set(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>High shelf, RBJ cookbook. <paramref name="shelfSlope"/> is S (0.5..1, 1 = steepest).</summary>
    public void SetHighShelf(double sampleRate, double frequency, double shelfSlope, double gainDb)
    {
        (double _, double cosW, double sinW) = Normalised(sampleRate, frequency);
        double a = Math.Pow(10, gainDb / 40.0);
        double s = shelfSlope <= 0 ? 1 : shelfSlope;
        double alpha = sinW / 2 * Math.Sqrt((a + 1 / a) * (1 / s - 1) + 2);
        double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;
        double b0 = a * ((a + 1) + (a - 1) * cosW + twoSqrtAAlpha);
        double b1 = -2 * a * ((a - 1) + (a + 1) * cosW);
        double b2 = a * ((a + 1) + (a - 1) * cosW - twoSqrtAAlpha);
        double a0 = (a + 1) - (a - 1) * cosW + twoSqrtAAlpha;
        double a1 = 2 * ((a - 1) - (a + 1) * cosW);
        double a2 = (a + 1) - (a - 1) * cosW - twoSqrtAAlpha;
        Set(b0, b1, b2, a0, a1, a2);
    }

    private static (double W0, double Cos, double Sin) Normalised(double sampleRate, double frequency)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        double nyquist = sampleRate * 0.5;
        frequency = Math.Clamp(frequency, 1e-3, nyquist - 1e-3);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        return (w0, Math.Cos(w0), Math.Sin(w0));
    }

    private static double Q(double q) => q <= 0 ? 0.7071067811865476 : q;

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        double inv = 1.0 / a0;
        _b0 = b0 * inv;
        _b1 = b1 * inv;
        _b2 = b2 * inv;
        _a1 = a1 * inv;
        _a2 = a2 * inv;
    }
}
