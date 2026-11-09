using System;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// Sample-exact delay on the program path — the global A/V offset
/// (<c>AppSettings.OutputDelayMs</c>).
///
/// <para><b>Why.</b> OBS captures JockeyBox as an application audio source and
/// the camera path adds its own latency, so the offset that makes lip-sync right
/// belongs on the audio the program bus emits. The monitor output is deliberately
/// <i>not</i> delayed (a performer must hear themselves in time) and the UI clock
/// reports the delayed position, so the waveform, lyrics and time readout all
/// describe what the audience hears.</para>
///
/// <para><b>Push/pull, not a sample provider.</b> The engine renders blocks on
/// its own thread and hands the same block to several sinks; a pull-based node
/// would have to be drained exactly once per block and would break the moment a
/// second consumer appeared. <see cref="Write"/> stages the block,
/// <see cref="ReadDelayed"/> reads it back N frames later.</para>
///
/// <para><b>Live changes.</b> Moving the tap would click, so the old and new taps
/// are crossfaded over <see cref="CrossfadeMs"/>, per frame and not per block:
/// the fraction is re-evaluated inside the frame loop, so the fade is continuous
/// however small the engine's blocks are (a block at the 10 ms render size is
/// 480 frames — a once-per-block coefficient would be a 5-step staircase for the
/// 50 ms fade, i.e. ~20 % jumps in gain).</para>
///
/// <para><b>The crossfade law.</b> A delay change is the <i>same</i> signal taken
/// twice, so the two taps are correlated and must sum to unity: the gains are the
/// equal-power pair <see cref="Crossfade.GainA"/>/<see cref="Crossfade.GainB"/>,
/// normalized by their own sum. That makes a correlated crossfade transparent
/// (no 3 dB dip, no 3 dB boost) and keeps the pair constant-power-shaped where the
/// taps are decorrelated — where the midpoint is up to 3 dB down, the price of not
/// pumping a correlated tap change. The progress is a smootherstep shaped by the
/// integral of a Hann window, whose slope is zero at both ends, so the blend never
/// steps at the start or the end of the fade.</para>
///
/// <para>The ring is preallocated for the maximum delay, so changing it never
/// allocates on the audio thread.</para>
/// </summary>
public sealed class DelayBuffer
{
    public const int MaxDelayMs = 2000;
    private const int CrossfadeMs = 50;

    private readonly int _rate;
    private readonly int _channels;
    private readonly int _maxDelayFrames;
    private readonly int _xfadeFrames;
    private readonly float[] _ring;
    private readonly int _ringFrames;
    private int _writeFrame;

    private int _delayFrames;
    private int _fromDelayFrames;
    private int _xfadeRemaining;

    public DelayBuffer(int sampleRate, int channels, int initialDelayMs = 0)
    {
        _rate = sampleRate;
        _channels = channels;
        _maxDelayFrames = sampleRate * MaxDelayMs / 1000;
        _xfadeFrames = Math.Max(1, sampleRate * CrossfadeMs / 1000);
        _ringFrames = _maxDelayFrames + 4096;
        _ring = new float[_ringFrames * channels];
        _delayFrames = Math.Clamp(initialDelayMs, 0, MaxDelayMs) * sampleRate / 1000;
        _fromDelayFrames = _delayFrames;
    }

    /// <summary>Delay currently applied, in milliseconds.</summary>
    public int DelayMs => _delayFrames * 1000 / _rate;

    /// <summary>Delay currently applied, in frames.</summary>
    public int DelayFrames => _delayFrames;

    /// <summary>True while a delay change is being crossfaded in.</summary>
    public bool IsCrossfading => _xfadeRemaining > 0;

    /// <summary>Request a new delay; the change is applied by the next <see cref="ReadDelayed"/>.</summary>
    public void SetDelayMs(int milliseconds)
    {
        int frames = Math.Clamp(milliseconds, 0, MaxDelayMs) * _rate / 1000;
        if (frames == _delayFrames) return;
        _fromDelayFrames = _delayFrames;
        _delayFrames = frames;
        _xfadeRemaining = _xfadeFrames;
    }

    /// <summary>Stage one block. <paramref name="frames"/> is a frame count, not a sample count.</summary>
    public void Write(float[] interleaved, int offset, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = (_writeFrame + f) % _ringFrames * _channels;
            int src = offset + f * _channels;
            for (int c = 0; c < _channels; c++) _ring[baseIdx + c] = interleaved[src + c];
        }
        _writeFrame = (_writeFrame + frames) % _ringFrames;
    }

    /// <summary>
    /// Read the block staged <see cref="_delayFrames"/> ago into
    /// <paramref name="dst"/>. Must be called once per <see cref="Write"/>.
    /// </summary>
    public void ReadDelayed(float[] dst, int offset, int frames)
    {
        // The fade fraction is recomputed per frame: a per-block constant turns a
        // 50 ms fade into a handful of audible gain steps at the render block size.
        int total = _xfadeFrames;
        for (int f = 0; f < frames; f++)
        {
            double t = _xfadeRemaining > 0
                ? Math.Clamp((total - _xfadeRemaining + f) / (double)total, 0.0, 1.0)
                : 1.0;

            int write = _writeFrame; // one past the newest frame written

            // The block being read was written `frames` ago, so the newest
            // readable frame for this output frame is write - frames + f.
            int newest = write - frames + f;
            int tapA = Mod(newest - _fromDelayFrames);
            int tapB = Mod(newest - _delayFrames);
            int aBase = tapA * _channels;
            int bBase = tapB * _channels;
            int dBase = offset + f * _channels;

            if (t >= 1.0)
            {
                // Settled: exactly the new tap, bit for bit.
                for (int c = 0; c < _channels; c++) dst[dBase + c] = _ring[bBase + c];
            }
            else
            {
                double p = Shape(t);
                double gainA = Crossfade.GainA(p);
                double gainB = Crossfade.GainB(p);
                double sum = gainA + gainB;
                for (int c = 0; c < _channels; c++)
                {
                    double a = _ring[aBase + c];
                    double b = _ring[bBase + c];
                    dst[dBase + c] = (float)((a * gainA + b * gainB) / sum);
                }
            }
        }

        if (_xfadeRemaining > 0)
        {
            _xfadeRemaining -= frames;
            if (_xfadeRemaining <= 0)
            {
                _xfadeRemaining = 0;
                _fromDelayFrames = _delayFrames;
            }
        }
    }

    /// <summary>Forget the staged history (e.g. after an output device is re-opened).</summary>
    public void Reset()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _writeFrame = 0;
        _fromDelayFrames = _delayFrames;
        _xfadeRemaining = 0;
    }

    /// <summary>
    /// Gain progress for a linear fade fraction: smootherstep (zero slope at both
    /// ends, no overshoot) composed with the integral of a Hann window
    /// (<c>p - sin(2πp)/2π</c>, also zero-slope at both ends). Both are monotone
    /// 0..1, so the composition is too, and the blend cannot step in or out.
    /// </summary>
    internal static double Shape(double t)
    {
        double p = t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        return p - Math.Sin(2.0 * Math.PI * p) / (2.0 * Math.PI);
    }

    private int Mod(int frame)
    {
        int i = frame % _ringFrames;
        return i < 0 ? i + _ringFrames : i;
    }
}
