namespace JockeyBox.Dsp;

/// <summary>
/// Write head shared by every channel's <see cref="PitchShiftStage"/>. Two stages on one stereo
/// pair must address their rings at the same offset each block, or the two read heads sweep
/// against different write positions and the stereo image wanders.
/// <para>
/// The owner advances it once per block, after every channel has processed. A stage constructed
/// without a shared clock owns a private clock that advances itself, which is what makes a
/// single <see cref="PitchShiftStage"/> usable on its own in tests and in a mono chain.
/// </para>
/// </summary>
public sealed class PitchShiftClock
{
    private readonly int _ringLength;
    private readonly bool _autoAdvance;
    private int _position;

    /// <param name="ringLength">Ring size in frames; must match every stage using this clock.</param>
    /// <param name="autoAdvance">
    /// True for a clock owned by one stage: the stage advances it after each block. False when an
    /// owner drives several stages and calls <see cref="Advance"/> once per block.
    /// </param>
    public PitchShiftClock(int ringLength, bool autoAdvance)
    {
        if (ringLength < 2) throw new ArgumentOutOfRangeException(nameof(ringLength));
        if ((ringLength & (ringLength - 1)) != 0)
            throw new ArgumentException("ring length must be a power of two", nameof(ringLength));

        _ringLength = ringLength;
        _autoAdvance = autoAdvance;
    }

    /// <summary>Ring size in frames, a power of two so indexing is a mask.</summary>
    public int RingLength => _ringLength;

    /// <summary>Current write position, always in <c>[0, RingLength)</c>.</summary>
    public int Position => _position;

    /// <summary>True when the single owning stage advances the clock itself.</summary>
    public bool AutoAdvance => _autoAdvance;

    /// <summary>Moves the write head forward by one block. Allocation-free.</summary>
    public void Advance(int frames) => _position = (_position + frames) % _ringLength;
}

/// <summary>
/// Monophonic delay-line pitch shifter with a WSOLA-style phase-aligned splice.
/// <para>
/// A read head sweeps a delay window at rate <c>1 - r</c> samples of delay per sample, so the
/// read position advances at <c>r</c> and the output pitch is <c>r</c> times the input. When the
/// head reaches the end of its window it must jump, and the jump is hidden by starting a second
/// head and crossfading.
/// </para>
/// <para>
/// <b>Why the splice is searched.</b> Starting the second head at a fixed offset leaves the two
/// heads a fixed delay apart, so every crossfade blends signals with a phase offset of
/// <c>f_in * offset / sr</c> and the pitch is pulled by an amount that depends on the input
/// frequency (period <c>sr / offset</c>, 200 Hz at a 240-sample offset). Before each crossfade
/// the stage correlates the outgoing head's continuation against candidate starting points and
/// starts the incoming head at the smallest candidate that correlates within
/// <see cref="SpliceMargin"/> of the best local peak, so the two heads are in phase when they
/// blend. Both heads then advance at the same rate and stay in phase for the whole sweep.
/// </para>
/// <para>
/// <b>Bias toward the smallest offset.</b> The correlation is periodic in the offset at the
/// input period, so many offsets align equally well. Taking the best anywhere in the range
/// picks one up to a full period away and inflates the mean read delay; taking the smallest
/// local peak within <see cref="SpliceMargin"/> of the best removes most of that delay, and the
/// margin is small enough that the residual phase error stays well inside 5 cents.
/// </para>
/// <para>
/// <b>Latency is the mean, not the floor.</b> The read delay sweeps, so the stage reports the
/// mean read delay while active, separately for up-shift and down-shift because the search
/// offset adds to the two directions by different amounts. <see cref="LatencyLedger"/> pads
/// other channels to this figure, so reporting the floor would let the pitched channel drag.
/// The mean is measured at <see cref="MeanReadDelayFrames"/>; the reported figure is the
/// band-average design constant, and the spread across the supported band is documented at the
/// constants.
/// </para>
/// <para>
/// <b>In-place and allocation-free.</b> The stage owns its ring and writes each incoming sample
/// before reading the delayed taps. Ring and search buffers are allocated once in the
/// constructor; <see cref="Process"/> allocates nothing.
/// </para>
/// </summary>
public sealed class PitchShiftStage : IAudioProcessor
{
    /// <summary>Delay lengths are quoted at this rate, like <c>Freeverb</c>'s tables.</summary>
    public const double ReferenceSampleRate = 48000;

    /// <summary>Crossfade length at 48 kHz: 5 ms, 240 frames. Linear gains that sum to one.</summary>
    public const int CrossfadeFramesAt48k = 240;

    /// <summary>Minimum delay at 48 kHz: 5 ms, 240 frames.</summary>
    public const int BaseDelayFramesAt48k = 240;

    /// <summary>
    /// Nominal sweep length at 48 kHz: 10 ms, 480 frames. The search extends the sweep upward
    /// rather than reserving room inside it, so this is the span at zero crossfade offset.
    /// </summary>
    public const int SweepFramesAt48k = 480;

    /// <summary>
    /// Search range at 48 kHz: 600 frames. One period at 600 frames is 80 Hz, so the search can
    /// align any input down to 80 Hz; below that the nearest correlation peak can fall outside
    /// the range and the splice degrades toward the old fixed-offset detune.
    /// </summary>
    public const int SearchFramesAt48k = 600;

    /// <summary>
    /// Correlation window at 48 kHz: 64 frames (1.3 ms). Long enough to reject noise, short
    /// enough that a period at 80 Hz (600 frames) still shows a clear peak.
    /// </summary>
    public const int CorrelationFramesAt48k = 128;

    /// <summary>Slack so the sweep, the correlation neighbour and the block write never collide.</summary>
    public const int BufferPadding = 4;

    /// <summary>
    /// Accepted correlation deficit, 2% of the best local peak. Chosen on a nine-point sweep:
    /// 1% and 2% both cut the mean read delay from ~590/630 to ~456/390 frames at the same
    /// 2.1-cent worst error; 5% and 10% bought no further delay and pushed the worst error to
    /// 3.5 cents, so 2% is the knee. The ratio is against the best local maximum, never a
    /// shoulder, or the chosen offset drifts off the peak and detunes by tens of cents.
    /// </summary>
    public const double SpliceMargin = 0.02;

    /// <summary>
    /// Mean read delay while up-shifting at 48 kHz, measured across 200–1000 Hz on a sine sweep.
    /// The spread is 360–513 frames: lower inputs need a larger correlation offset because the
    /// period is longer, so the mean rises with period. White noise, which has no single period,
    /// measured 581 frames; the constant is the tonal band mean because a microphone sees voiced
    /// material, not noise.
    /// </summary>
    public const int MeanUpDelayFramesAt48k = 448;

    /// <summary>
    /// Mean read delay while down-shifting at 48 kHz, measured across 200–1000 Hz on a sine sweep.
    /// The spread is 502–655 frames. It is higher than the up-shift mean because the sweep runs
    /// from the short delay to the long one and the crossfade advance subtracts less from it.
    /// White noise measured 829 frames; see the up-shift comment for why the constant is tonal.
    /// </summary>
    public const int MeanDownDelayFramesAt48k = 554;

    /// <summary>
    /// Fixed convention: linear interpolation between ring samples. The ring is a power of two
    /// and the read position is fractional, so the neighbour is one mask away. A higher-order
    /// interpolator would cost more than the 0.05 ms the spike budgeted; the ±12 semitone bound
    /// keeps the error acceptable.
    /// </summary>
    public const string Interpolation = "linear";

    private readonly PitchShiftClock _clock;
    private readonly float[] _ring;
    private readonly int _mask;
    private readonly int _baseDelay;
    private readonly int _nominalFar;
    private readonly int _sweepLength;
    private readonly int _searchLength;
    private readonly int _correlationLength;
    private readonly int _crossfadeLength;
    private readonly double _scale;

    private PitchShiftParameters _parameters;
    private double _ratio = 1;
    private double _delay;
    private double _incomingDelay;
    private double _sweepEnd;
    private double _pendingOffset;
    private int _xfadeRemaining;
    private int _direction = 1;
    private bool _bypass;

    private long _delaySum;
    private long _delayCount;
    private double _delayMin = double.MaxValue;
    private double _delayMax = double.MinValue;

    /// <summary>Ring size for a sample rate, rounded up to a power of two.</summary>
    public static int RingFramesFor(double sampleRate)
    {
        if (sampleRate <= 0 || double.IsNaN(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        double scale = sampleRate / ReferenceSampleRate;
        int baseDelay = Scale(BaseDelayFramesAt48k, scale);
        int sweep = Scale(SweepFramesAt48k, scale);
        int search = Scale(SearchFramesAt48k, scale);
        int correlation = Scale(CorrelationFramesAt48k, scale);
        int needed = baseDelay + sweep + search + correlation + BufferPadding;

        int ring = 2;
        while (ring < needed) ring <<= 1;
        return ring;
    }

    private static int Scale(int framesAt48k, double scale) =>
        Math.Max(1, (int)Math.Round(framesAt48k * scale));

    public PitchShiftStage(
        double sampleRate,
        PitchShiftClock? clock = null,
        PitchShiftParameters? parameters = null,
        string id = "pitch.shift")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Stage id is required.", nameof(id));

        _scale = sampleRate / ReferenceSampleRate;
        _baseDelay = Scale(BaseDelayFramesAt48k, _scale);
        _sweepLength = Scale(SweepFramesAt48k, _scale);
        _searchLength = Scale(SearchFramesAt48k, _scale);
        _correlationLength = Scale(CorrelationFramesAt48k, _scale);
        _crossfadeLength = Scale(CrossfadeFramesAt48k, _scale);
        _nominalFar = _baseDelay + _sweepLength;

        _clock = clock ?? new PitchShiftClock(RingFramesFor(sampleRate), autoAdvance: true);
        _ring = new float[_clock.RingLength];
        _mask = _clock.RingLength - 1;

        Id = id;
        SetParameters(parameters ?? PitchShiftParameters.Neutral);
    }

    public string Id { get; }

    /// <summary>
    /// Mean read delay the stage adds while active, 0 while neutral. This is a design average,
    /// measured over the supported band, not the instantaneous delay; see
    /// <see cref="MeanReadDelayFrames"/> for the value realised by the signal being processed.
    /// </summary>
    public int LatencyFrames => IsNeutral
        ? 0
        : Scale(_direction > 0 ? MeanUpDelayFramesAt48k : MeanDownDelayFramesAt48k, _scale);

    /// <summary>
    /// Mean of the read delay actually used since the last <see cref="Reset"/>, in frames.
    /// Diagnostic only: it is what the test compares <see cref="LatencyFrames"/> against.
    /// </summary>
    public int MeanReadDelayFrames => _delayCount == 0
        ? LatencyFrames
        : (int)Math.Round((double)_delaySum / _delayCount);

    /// <summary>Smallest read delay seen since the last <see cref="Reset"/>, in frames.</summary>
    public int MinReadDelayFrames => _delayCount == 0 ? LatencyFrames : (int)Math.Round(_delayMin);

    /// <summary>Largest read delay seen since the last <see cref="Reset"/>, in frames.</summary>
    public int MaxReadDelayFrames => _delayCount == 0 ? LatencyFrames : (int)Math.Round(_delayMax);

    /// <summary>Set by the strip's master bypass; behaves exactly like neutral.</summary>
    public bool Bypass
    {
        get => _bypass;
        set
        {
            if (_bypass == value) return;
            bool wasNeutral = IsNeutral;
            _bypass = value;
            if (wasNeutral && !IsNeutral) Reset();
        }
    }

    /// <summary>True when <see cref="Process"/> leaves the buffer untouched.</summary>
    public bool IsNeutral => _bypass || _parameters.IsNeutral;

    /// <summary>Parameters last applied, already clamped.</summary>
    public PitchShiftParameters Parameters => _parameters;

    /// <summary>
    /// Applies parameters. <see cref="Reset"/> runs only on the neutral → active transition: a
    /// slider tick must not clear the ring, and the reverb's earlier "reset on every write"
    /// defect is exactly what this guard avoids.
    /// </summary>
    public void SetParameters(PitchShiftParameters parameters)
    {
        bool wasNeutral = IsNeutral;
        _parameters = parameters.Clamp();
        _ratio = Math.Pow(2.0, _parameters.Semitones / 12.0);
        _direction = _ratio > 1 ? 1 : -1;
        if (wasNeutral && !IsNeutral) Reset();
    }

    public void Process(Span<float> samples)
    {
        int basePosition = _clock.Position;

        if (!IsNeutral && samples.Length > 0)
        {
            ProcessActive(samples, basePosition);
        }

        if (_clock.AutoAdvance) _clock.Advance(samples.Length);
    }

    /// <summary>Clears the ring, restarts the sweep and clears the delay diagnostics.</summary>
    public void Reset()
    {
        Array.Clear(_ring);
        _delay = _direction > 0 ? _nominalFar : _baseDelay;
        _sweepEnd = _direction > 0 ? _baseDelay : _nominalFar;
        _incomingDelay = 0;
        _pendingOffset = 0;
        _xfadeRemaining = 0;
        _delaySum = 0;
        _delayCount = 0;
        _delayMin = double.MaxValue;
        _delayMax = double.MinValue;
    }

    private void ProcessActive(Span<float> samples, int basePosition)
    {
        // Delay moves at (1 - r) samples per sample: the read position then advances at r.
        double delayDelta = 1.0 - _ratio;
        double absDelta = Math.Abs(delayDelta);
        int mask = _mask;
        float[] ring = _ring;
        int direction = _direction;
        int baseDelay = _baseDelay;
        int nominalFar = _nominalFar;
        int crossfade = _crossfadeLength;

        for (int i = 0; i < samples.Length; i++)
        {
            int write = (basePosition + i) & mask;
            ring[write] = samples[i];

            if (_xfadeRemaining == 0)
            {
                // Start the handover one crossfade's worth of samples before the boundary, so
                // the outgoing head reaches it exactly as the fade ends.
                double remaining = direction > 0
                    ? (_delay - _sweepEnd) / absDelta
                    : (_sweepEnd - _delay) / absDelta;

                if (remaining <= crossfade)
                {
                    double k = FindSpliceOffset(write, direction);
                    // Extend the sweep upward by k. k >= 0 only, so the minimum delay (and the
                    // floor of the reported latency) never moves.
                    _incomingDelay = direction > 0 ? nominalFar + k : baseDelay + k;
                    _pendingOffset = k;
                    _xfadeRemaining = crossfade;
                }
            }

            if (_xfadeRemaining == 0)
            {
                samples[i] = (float)ReadRing(ring, mask, write, _delay);
            }
            else
            {
                // The heads are phase-aligned by FindSpliceOffset, so the linear blend does not
                // pull the pitch.
                double u = (double)(crossfade - _xfadeRemaining) / crossfade;
                double a = ReadRing(ring, mask, write, _delay);
                double b = ReadRing(ring, mask, write, _incomingDelay);
                samples[i] = (float)(a + (b - a) * u);

                _incomingDelay += delayDelta;
                _xfadeRemaining--;
                if (_xfadeRemaining == 0)
                {
                    _delay = _incomingDelay;
                    _sweepEnd = direction > 0 ? baseDelay : nominalFar + _pendingOffset;
                }
            }

            _delay += delayDelta;

            double observed = _delay;
            _delaySum += (long)observed;
            _delayCount++;
            if (observed < _delayMin) _delayMin = observed;
            if (observed > _delayMax) _delayMax = observed;
        }
    }

    /// <summary>
    /// WSOLA splice search. Correlates the outgoing head's continuation against every candidate
    /// incoming start in <c>[0, searchLength]</c>, then returns the smallest local maximum whose
    /// score is within <see cref="SpliceMargin"/> of the best, refined to sub-sample precision by
    /// a parabola through its neighbours. Choosing a local maximum (not the first sample above a
    /// threshold) is essential: a shoulder of the correlation peak is within the margin but is
    /// several samples off the true alignment, which detunes by tens of cents.
    /// </summary>
    private double FindSpliceOffset(int write, int direction)
    {
        int search = _searchLength;
        double outgoing = write - _delay;

        Span<double> scores = search < 1024
            ? stackalloc double[search + 1]
            : new double[search + 1];

        double best = double.NegativeInfinity;
        for (int k = 0; k <= search; k++)
        {
            scores[k] = Correlate(write, direction, outgoing, k);
            if (scores[k] > best) best = scores[k];
        }

        double threshold = best * (1.0 - SpliceMargin);
        int chosen = -1;
        for (int k = 0; k <= search; k++)
        {
            bool localMaximum = (k == 0 || scores[k] >= scores[k - 1])
                && (k == search || scores[k] >= scores[k + 1]);
            if (localMaximum && scores[k] >= threshold)
            {
                chosen = k;
                break;
            }
        }

        if (chosen < 0) chosen = 0;

        if (chosen > 0 && chosen < search)
        {
            double previous = scores[chosen - 1];
            double centre = scores[chosen];
            double next = scores[chosen + 1];
            double denominator = previous - 2 * centre + next;

            if (denominator < 0)
            {
                double delta = 0.5 * (previous - next) / denominator;
                if (delta > -1 && delta < 1) return chosen + delta;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Correlates the outgoing head's continuation with an incoming head placed at
    /// <paramref name="k"/>. Both read positions advance at the pitch ratio <c>r</c> samples per
    /// output sample (the delay moves at <c>1 - r</c>), so the correlation must step by
    /// <c>r</c>, not by one. Stepping by one forces the wrong slope through the window and
    /// biases the alignment, which shows up as a frequency error that grows as the sweep shortens.
    /// </summary>
    private double Correlate(int write, int direction, double outgoing, int k)
    {
        int mask = _mask;
        float[] ring = _ring;
        int length = _correlationLength;
        int ringLength = ring.Length;
        double incoming = write - (direction > 0 ? _nominalFar + k : _baseDelay + k);
        double rate = _ratio;

        // Bias both positions by one ring length so they are always positive: the read position
        // can be a whole ring behind the write head, and truncation is only floor for positives.
        // This keeps Math.Floor out of a loop that runs once per candidate per correlation sample.
        double outPosition = outgoing + ringLength;
        double inPosition = incoming + ringLength;
        double score = 0;

        for (int m = 0; m < length; m++)
        {
            int outWhole = (int)outPosition;
            double outFraction = outPosition - outWhole;
            int outNext = (outWhole + 1) & mask;
            outWhole &= mask;
            double outgoingSample = ring[outWhole] + (ring[outNext] - ring[outWhole]) * outFraction;

            int inWhole = (int)inPosition;
            double inFraction = inPosition - inWhole;
            int inNext = (inWhole + 1) & mask;
            inWhole &= mask;
            double incomingSample = ring[inWhole] + (ring[inNext] - ring[inWhole]) * inFraction;

            score += outgoingSample * incomingSample;
            outPosition += rate;
            inPosition += rate;
        }

        return score;
    }

    private static double ReadAt(float[] ring, int mask, double position)
    {
        int whole = (int)Math.Floor(position);
        double fraction = position - whole;
        int a = whole & mask;
        int b = (whole + 1) & mask;
        return ring[a] + (ring[b] - ring[a]) * fraction;
    }

    private static double ReadRing(float[] ring, int mask, int write, double delay)
    {
        double readPosition = write - delay;
        int whole = (int)Math.Floor(readPosition);
        double fraction = readPosition - whole;
        int a = whole & mask;
        int b = (whole + 1) & mask;
        return ring[a] + (ring[b] - ring[a]) * fraction;
    }
}
