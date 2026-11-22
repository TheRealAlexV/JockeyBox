namespace JockeyBox.Dsp;

/// <summary>
/// Lookahead brickwall limiter. The input is delayed by <see cref="LatencyFrames"/> frames
/// while a monotonic deque tracks the peak over the lookahead window, so gain is reduced
/// before the peak reaches the output. Gain drops instantly (never overshoots the ceiling)
/// and recovers on an exponential release ramp, so it does not click.
/// </summary>
public sealed class Limiter : IAudioProcessor
{
    private readonly double _sampleRate;
    private readonly int _lookaheadFrames;

    // Retunable while running. The lookahead buffers below never change size, so latency is
    // fixed at construction no matter how often the ceiling or release changes.
    private double _thresholdDb;
    private double _ceilingLin;
    private double _releaseMs;
    private double _releaseCoef;

    private readonly float[] _delay;
    private int _delayWrite;

    // Monotonic deque over the lookahead window: index (frame number) and |sample|.
    private readonly long[] _dequeIndex;
    private readonly double[] _dequeValue;
    private readonly int _dequeCapacity;
    private int _dequeHead;
    private int _dequeTail;

    private double _gain = 1;
    private long _frame;

    public Limiter(double sampleRate, double thresholdDb = 0, double lookaheadMs = 5, double releaseMs = 50,
        string id = "dynamics.limiter")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;
        _lookaheadFrames = Math.Max(1, (int)Math.Round(Math.Max(0.01, lookaheadMs) * 0.001 * sampleRate));
        _thresholdDb = Ranges.Fix(thresholdDb, -60, 0, 0);
        _ceilingLin = Math.Pow(10, _thresholdDb / 20.0);
        _releaseMs = Math.Max(0.01, releaseMs);
        _releaseCoef = 1 - Math.Exp(-1.0 / (_releaseMs * 0.001 * sampleRate));

        _delay = new float[_lookaheadFrames];
        _dequeCapacity = _lookaheadFrames + 1;
        _dequeIndex = new long[_dequeCapacity];
        _dequeValue = new double[_dequeCapacity];
    }

    public string Id { get; }

    public int LatencyFrames => _lookaheadFrames;

    public bool Bypass { get; set; }

    public double ThresholdDb => _thresholdDb;

    /// <summary>
    /// Ceiling in dBFS. Setting it retunes a running limiter in place: the lookahead buffers
    /// are untouched, so <see cref="LatencyFrames"/> and the allocation footprint never change
    /// and the new ceiling applies to the very next sample (a lower ceiling clamps instantly,
    /// as the attack does). Non-finite input keeps the current value; the range is −60..0 dB.
    /// </summary>
    public double CeilingDb
    {
        get => _thresholdDb;
        set
        {
            _thresholdDb = Ranges.Fix(value, -60, 0, _thresholdDb);
            _ceilingLin = Math.Pow(10, _thresholdDb / 20.0);
        }
    }

    /// <summary>
    /// Release time in milliseconds. Setting it only recomputes the one-pole coefficient of the
    /// gain recovery ramp, so it takes effect without rebuilding anything and without allocating.
    /// </summary>
    public double ReleaseMs
    {
        get => _releaseMs;
        set
        {
            _releaseMs = Math.Max(0.01, value);
            _releaseCoef = 1 - Math.Exp(-1.0 / (_releaseMs * 0.001 * _sampleRate));
        }
    }

    public void Process(Span<float> samples)
    {
        if (Bypass) return;

        for (int i = 0; i < samples.Length; i++)
        {
            double input = samples[i];
            double magnitude = Math.Abs(input);

            // Keep the deque strictly decreasing: the front is the loudest sample in the window.
            while (_dequeTail > _dequeHead && _dequeValue[(_dequeTail - 1) % _dequeCapacity] <= magnitude)
            {
                _dequeTail--;
            }

            _dequeIndex[_dequeTail % _dequeCapacity] = _frame;
            _dequeValue[_dequeTail % _dequeCapacity] = magnitude;
            _dequeTail++;

            long oldest = _frame - _lookaheadFrames + 1;
            if (_dequeIndex[_dequeHead % _dequeCapacity] < oldest)
            {
                _dequeHead++;
            }

            double peak = _dequeValue[_dequeHead % _dequeCapacity];
            double target = peak > _ceilingLin ? _ceilingLin / peak : 1.0;

            if (target < _gain) _gain = target;                     // instant attack, no overshoot
            else _gain += (target - _gain) * _releaseCoef;          // smooth release, no click

            samples[i] = (float)(_delay[_delayWrite] * _gain);
            _delay[_delayWrite] = (float)input;
            _delayWrite++;
            if (_delayWrite == _lookaheadFrames) _delayWrite = 0;
            _frame++;
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _delayWrite = 0;
        _dequeHead = 0;
        _dequeTail = 0;
        _gain = 1;
        _frame = 0;
    }
}
