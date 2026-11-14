using System;
using System.Threading;

namespace JockeyBox.Audio.Graph;

/// <summary>Which of the three mix-EQ bands a gain applies to.</summary>
public enum EqBand
{
    /// <summary>Low shelf at 70 Hz, -26…+6 dB. A shelf, not a bell: a bell at 70 Hz
    /// leaves 30-40 Hz untouched and the bass kill would not kill the bass.</summary>
    Low = 0,

    /// <summary>Peaking bell at 1 kHz, Q 0.6, -26…+6 dB.</summary>
    Mid = 1,

    /// <summary>High shelf at 13 kHz, -26…+6 dB.</summary>
    High = 2,
}

/// <summary>Which deck block is being filtered. Each stream keeps its own state.</summary>
public enum EqPath
{
    DeckA = 0,
    DeckB = 1,
}

/// <summary>
/// The operator's three-band mix EQ: low shelf, mid bell, high shelf.
///
/// <para><b>Where it sits.</b> It is applied to the two deck blocks before the
/// crossfader fan-out in <see cref="AudioEngine.RenderBlock"/>. Everything derived
/// from those blocks - the Deck A and Deck B buses, the monitor tap and the program
/// tap - therefore hears the same equalisation, which is what a "mix EQ with three
/// controls" means here. Nothing in the bus model special-cases it.</para>
///
/// <para><b>Why the shapes.</b> The two shelves use RBJ's shelf slope <c>S</c>
/// (= 1, i.e. 12 dB/octave), not a free Q, because that is the parameter a hardware
/// shelf actually has. The mid band is a peaking bell with Q 0.6.</para>
///
/// <para><b>Why a centre band is bit-transparent.</b> A biquad at 0 dB is a unity
/// filter mathematically but not bit-exactly: <c>b0 ≈ 1</c> rounds, and a centred
/// band would still shift phase. So a band whose smoothed gain is under
/// <see cref="BypassDb"/> is skipped outright and its state is zeroed. The output
/// buffer is then untouched, byte for byte, at the centre detent.</para>
///
/// <para><b>Why coefficients change once per block and not per sample.</b>
/// Interpolating <c>z1</c>/<c>z2</c> or the coefficients per sample makes the filter
/// time-varying and audibly zippy. Instead the UI thread writes a <i>target</i>, the
/// render thread smooths it with a one-pole (τ ≈ 15 ms) once per block, and the
/// coefficients are redesigned only when the smoothed gain moved more than
/// <see cref="CoeffStepDb"/>. State is carried across those steps, so a slider move
/// is a sequence of small, state-continuous filter changes - no click.</para>
///
/// <para>All coefficients and state are <see cref="float"/>. Transposed direct form II
/// is used because its state does not blow up when coefficients move.</para>
/// </summary>
public sealed class MixEq
{
    /// <summary>Lowest band gain, dB. Deliberately asymmetric with the boost side.</summary>
    public const float MinDb = -26f;

    /// <summary>Highest band gain, dB.</summary>
    public const float MaxDb = 6f;

    /// <summary>Below this magnitude a band is bypassed and its state zeroed.</summary>
    public const float BypassDb = 0.05f;

    /// <summary>Redesign the coefficients only once the smoothed gain moved this far.</summary>
    public const float CoeffStepDb = 0.05f;

    /// <summary>Smoothed-gain time constant, seconds.</summary>
    public const float SmoothSeconds = 0.015f;

    /// <summary>State below this is flushed to zero so denormals never reach the CPU.</summary>
    private const float DenormalFloor = 1e-15f;

    private const int Bands = 3;
    private const int Paths = 2;
    private const int MaxChannels = 2;

    // Band centres / shapes: fixed hardware convention, not user settings.
    private const double LowShelfHz = 70.0;
    private const double MidBellHz = 1_000.0;
    private const double MidQ = 0.6;
    private const double HighShelfHz = 13_000.0;
    private const double ShelfSlope = 1.0; // S = 1 → 12 dB/octave

    private readonly int _rate;
    private readonly int _blockFrames;

    /// <summary>Per-block one-pole coefficient, from the block duration and τ.</summary>
    private readonly float _smoothAlpha;

    // Targets: written by the UI thread, read by the render thread.
    private readonly float[] _targetDb = new float[Bands];

    // Render-thread state.
    private readonly float[] _gainDb = new float[Bands];
    private readonly float[] _coeffDb = new float[Bands];
    private readonly bool[] _active = new bool[Bands];
    private readonly bool[] _coeffValid = new bool[Bands];

    // [band][b0,b1,b2,a1,a2]
    private readonly float[,] _coeff = new float[Bands, 5];

    // Transposed direct form II state: [path][band][channel][z1,z2]
    private readonly float[] _z1 = new float[Paths * Bands * MaxChannels];
    private readonly float[] _z2 = new float[Paths * Bands * MaxChannels];

    public MixEq(int sampleRate = AudioEngine.Rate, int blockFrames = AudioEngine.BlockFrames)
    {
        _rate = sampleRate;
        _blockFrames = blockFrames;
        double blockSeconds = blockFrames / (double)sampleRate;
        _smoothAlpha = (float)(1.0 - Math.Exp(-blockSeconds / SmoothSeconds));
    }

    /// <summary>The band's target gain in dB, as last set (clamped). Safe to read from the UI thread.</summary>
    public float TargetDb(EqBand band) => Volatile.Read(ref _targetDb[(int)band]);

    /// <summary>The smoothed gain actually in use, in dB. Diagnostics only.</summary>
    public float CurrentDb(EqBand band) => _gainDb[(int)band];

    /// <summary>True when the band is filtering (not bypassed). Diagnostics only.</summary>
    public bool IsActive(EqBand band) => _active[(int)band];

    /// <summary>
    /// Largest |state| across both channels of a band on one path. Tests use it to
    /// prove the denormal flush; nothing in the render path reads it.
    /// </summary>
    internal float StateMagnitude(EqBand band, EqPath path)
    {
        float max = 0f;
        for (int ch = 0; ch < MaxChannels; ch++)
        {
            int i = Index(path, band, ch);
            max = MathF.Max(max, MathF.Abs(_z1[i]));
            max = MathF.Max(max, MathF.Abs(_z2[i]));
        }
        return max;
    }

    /// <summary>Set a band's gain from the UI thread. Clamped to -26…+6 dB.</summary>
    public void SetTargetDb(EqBand band, float db)
    {
        if (float.IsNaN(db)) db = 0f;
        Volatile.Write(ref _targetDb[(int)band], Math.Clamp(db, MinDb, MaxDb));
    }

    /// <summary>Set all three bands at once (settings load, preset recall).</summary>
    public void SetAll(double lowDb, double midDb, double highDb)
    {
        SetTargetDb(EqBand.Low, (float)lowDb);
        SetTargetDb(EqBand.Mid, (float)midDb);
        SetTargetDb(EqBand.High, (float)highDb);
    }

    /// <summary>
    /// Advance the gain smoothing and (only if a gain moved more than
    /// <see cref="CoeffStepDb"/>) redesign the coefficients. Called once per render
    /// block, before the block's deck paths are filtered.
    /// </summary>
    public void BeginBlock()
    {
        for (int band = 0; band < Bands; band++)
        {
            float target = Volatile.Read(ref _targetDb[band]);
            float gain = _gainDb[band];

            gain += (target - gain) * _smoothAlpha;
            // Settle exactly, so a deadband can converge instead of creeping.
            if (MathF.Abs(target - gain) < 0.001f) gain = target;
            _gainDb[band] = gain;

            if (MathF.Abs(gain) < BypassDb)
            {
                // Centre detent: skip the band entirely and drop its state, so the
                // signal is bit-transparent rather than unity-gain-but-phase-shifted.
                if (_active[band]) _active[band] = false;
                ZeroState((EqBand)band);
                continue;
            }

            _active[band] = true;
            if (!_coeffValid[band] || MathF.Abs(gain - _coeffDb[band]) > CoeffStepDb)
            {
                Design((EqBand)band, gain);
                _coeffDb[band] = gain;
                _coeffValid[band] = true;
            }
        }
    }

    /// <summary>
    /// Filter one interleaved stereo deck block in place. <see cref="BeginBlock"/>
    /// must have run for this block already; state is per path and per channel, so
    /// deck A and deck B never share filter history.
    /// </summary>
    public void Process(float[] buffer, int offset, int frames, EqPath path)
    {
        if (frames <= 0) return;
        for (int band = 0; band < Bands; band++)
        {
            if (!_active[band]) continue;

            float b0 = _coeff[band, 0], b1 = _coeff[band, 1], b2 = _coeff[band, 2];
            float a1 = _coeff[band, 3], a2 = _coeff[band, 4];

            int i0 = Index(path, (EqBand)band, 0);
            int i1 = Index(path, (EqBand)band, 1);
            float l1 = _z1[i0], l2 = _z2[i0];
            float r1 = _z1[i1], r2 = _z2[i1];

            int end = offset + frames * MaxChannels;
            for (int i = offset; i < end; i += MaxChannels)
            {
                // Transposed direct form II: y = b0·x + z1.
                float xl = buffer[i];
                float yl = b0 * xl + l1;
                l1 = b1 * xl - a1 * yl + l2;
                l2 = b2 * xl - a2 * yl;
                buffer[i] = yl;

                float xr = buffer[i + 1];
                float yr = b0 * xr + r1;
                r1 = b1 * xr - a1 * yr + r2;
                r2 = b2 * xr - a2 * yr;
                buffer[i + 1] = yr;

                // A deep cut with silence in decays the state toward zero; below the
                // float normal range the multiply slows down by orders of magnitude.
                if (MathF.Abs(l1) < DenormalFloor && MathF.Abs(l2) < DenormalFloor)
                {
                    l1 = 0f; l2 = 0f;
                }
                if (MathF.Abs(r1) < DenormalFloor && MathF.Abs(r2) < DenormalFloor)
                {
                    r1 = 0f; r2 = 0f;
                }
            }

            _z1[i0] = l1; _z2[i0] = l2;
            _z1[i1] = r1; _z2[i1] = r2;
        }
    }

    /// <summary>Drop every filter's state (device change, route rebuild).</summary>
    public void Reset()
    {
        Array.Clear(_z1, 0, _z1.Length);
        Array.Clear(_z2, 0, _z2.Length);
    }

    private void ZeroState(EqBand band)
    {
        for (int path = 0; path < Paths; path++)
            for (int ch = 0; ch < MaxChannels; ch++)
            {
                int i = Index((EqPath)path, band, ch);
                _z1[i] = 0f;
                _z2[i] = 0f;
            }
    }

    private static int Index(EqPath path, EqBand band, int channel)
        => ((int)path * Bands + (int)band) * MaxChannels + channel;

    /// <summary>RBJ cookbook designs, normalised by a0, stored as float.</summary>
    private void Design(EqBand band, float gainDb)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0;
        double alpha;

        switch (band)
        {
            case EqBand.Low:
                w0 = 2.0 * Math.PI * LowShelfHz / _rate;
                // S=1: sqrt((A + 1/A)·(1/S - 1) + 2) collapses to sqrt(2).
                alpha = Math.Sin(w0) * 0.5 * Math.Sqrt(2.0);
                Shelf(band, a, w0, alpha, low: true);
                break;

            case EqBand.Mid:
                w0 = 2.0 * Math.PI * MidBellHz / _rate;
                alpha = Math.Sin(w0) / (2.0 * MidQ);
                Bell(band, a, w0, alpha);
                break;

            default:
                w0 = 2.0 * Math.PI * HighShelfHz / _rate;
                alpha = Math.Sin(w0) * 0.5 * Math.Sqrt(2.0);
                Shelf(band, a, w0, alpha, low: false);
                break;
        }
    }

    private void Shelf(EqBand band, double a, double w0, double alpha, bool low)
    {
        double cos = Math.Cos(w0);
        double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        double b0, b1, b2, a0, a1, a2;
        if (low)
        {
            b0 = a * ((a + 1.0) - (a - 1.0) * cos + twoSqrtAAlpha);
            b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cos);
            b2 = a * ((a + 1.0) - (a - 1.0) * cos - twoSqrtAAlpha);
            a0 = (a + 1.0) + (a - 1.0) * cos + twoSqrtAAlpha;
            a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cos);
            a2 = (a + 1.0) + (a - 1.0) * cos - twoSqrtAAlpha;
        }
        else
        {
            b0 = a * ((a + 1.0) + (a - 1.0) * cos + twoSqrtAAlpha);
            b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cos);
            b2 = a * ((a + 1.0) + (a - 1.0) * cos - twoSqrtAAlpha);
            a0 = (a + 1.0) - (a - 1.0) * cos + twoSqrtAAlpha;
            a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cos);
            a2 = (a + 1.0) - (a - 1.0) * cos - twoSqrtAAlpha;
        }

        Store(band, b0, b1, b2, a0, a1, a2);
    }

    private void Bell(EqBand band, double a, double w0, double alpha)
    {
        double cos = Math.Cos(w0);
        double b0 = 1.0 + alpha * a;
        double b1 = -2.0 * cos;
        double b2 = 1.0 - alpha * a;
        double a0 = 1.0 + alpha / a;
        double a1 = -2.0 * cos;
        double a2 = 1.0 - alpha / a;
        Store(band, b0, b1, b2, a0, a1, a2);
    }

    private void Store(EqBand band, double b0, double b1, double b2, double a0, double a1, double a2)
    {
        int b = (int)band;
        _coeff[b, 0] = (float)(b0 / a0);
        _coeff[b, 1] = (float)(b1 / a0);
        _coeff[b, 2] = (float)(b2 / a0);
        _coeff[b, 3] = (float)(a1 / a0);
        _coeff[b, 4] = (float)(a2 / a0);
    }
}
