namespace JockeyBox.Dsp.Reverb;

/// <summary>
/// One mono Freeverb chain: 8 parallel lowpass-feedback combs summed into 4 series allpasses.
/// Public-domain Jezar at Dreampoint topology, with the delay lengths sampled for 48 kHz.
/// <para>
/// Stereo comes from running two of these side by side and offsetting the right one's delays by
/// <see cref="StereoSpreadFrames"/>, not from cross-mixing, so each device keeps its own space.
/// </para>
/// <para>
/// No feedback matrix, no latency: every delay line is a flat array allocated once in the
/// constructor, so <see cref="ProcessSample"/> never touches the heap.
/// </para>
/// </summary>
public sealed class Freeverb
{
    /// <summary>Comb and allpass counts, fixed by the topology.</summary>
    public const int CombCount = 8;
    public const int AllPassCount = 4;

    /// <summary>Extra frames added to every line so a length can be trimmed or scaled without reallocating.</summary>
    public const int BufferPadding = 4;

    /// <summary>Right channel offset, in 48 kHz frames.</summary>
    public const int StereoSpreadFrames = 25;

    private const double ReferenceSampleRate = 48000;
    private const double InputGain = 0.015;
    private const double OutputGain = 3.0;

    private const double FeedbackScale = 0.28;
    private const double FeedbackOffset = 0.7;
    private const double DampingScale = 0.4;

    private const double DefaultDamping = 0.5;

    /// <summary>Comb delay lengths at 48 kHz, in samples.</summary>
    private static readonly int[] CombFramesAt48k = { 1215, 1293, 1390, 1476, 1548, 1623, 1695, 1760 };

    /// <summary>Allpass delay lengths at 48 kHz, in samples.</summary>
    private static readonly int[] AllPassFramesAt48k = { 605, 480, 371, 245 };

    private readonly Comb[] _combs;
    private readonly AllPass[] _allPasses;
    private readonly int[] _combFrames;
    private readonly int[] _allPassFrames;

    private float _feedback;
    private float _damp1;
    private float _damp2;

    /// <param name="sampleRate">Rate the delays are scaled to.</param>
    /// <param name="extraDelayFrames">
    /// Shift added to every line, used for the stereo spread on the right channel.
    /// </param>
    public Freeverb(double sampleRate, int extraDelayFrames = 0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (extraDelayFrames < 0) throw new ArgumentOutOfRangeException(nameof(extraDelayFrames));

        SampleRate = sampleRate;

        _combFrames = new int[CombCount];
        _combs = new Comb[CombCount];
        for (int i = 0; i < CombCount; i++)
        {
            int frames = Scale(CombFramesAt48k[i], sampleRate) + extraDelayFrames;
            _combFrames[i] = frames;
            _combs[i] = new Comb(frames);
        }

        _allPassFrames = new int[AllPassCount];
        _allPasses = new AllPass[AllPassCount];
        for (int i = 0; i < AllPassCount; i++)
        {
            int frames = Scale(AllPassFramesAt48k[i], sampleRate) + extraDelayFrames;
            _allPassFrames[i] = frames;
            _allPasses[i] = new AllPass(frames);
        }

        SetDamping(DefaultDamping);
        SetRoomSize(0.5);
    }

    public double SampleRate { get; }

    /// <summary>Resolved comb lengths in frames, after rate scaling and the spread offset.</summary>
    public ReadOnlySpan<int> CombFrames => _combFrames;

    /// <summary>Resolved allpass lengths in frames, after rate scaling and the spread offset.</summary>
    public ReadOnlySpan<int> AllPassFrames => _allPassFrames;

    /// <summary>Comb feedback, 0.7..0.98.</summary>
    public double Feedback => _feedback;

    /// <summary>Sets the room size, 0..1, which maps onto comb feedback 0.7..0.98.</summary>
    public void SetRoomSize(double size)
    {
        double clamped = Ranges.Fix(size, 0, 1, 0.5);
        _feedback = (float)(clamped * FeedbackScale + FeedbackOffset);
    }

    /// <summary>Sets the damping, 0..1, which maps onto the one-pole loss in each comb.</summary>
    public void SetDamping(double damping)
    {
        double clamped = Ranges.Fix(damping, 0, 1, DefaultDamping);
        _damp1 = (float)(clamped * DampingScale);
        _damp2 = 1 - _damp1;
    }

    /// <summary>One mono sample in, one mono sample out. Allocation-free.</summary>
    public float ProcessSample(float input)
    {
        float scaled = input * (float)InputGain;

        float summed = 0;
        for (int i = 0; i < _combs.Length; i++)
        {
            summed += _combs[i].Process(scaled, _feedback, _damp1, _damp2);
        }

        for (int i = 0; i < _allPasses.Length; i++)
        {
            summed = _allPasses[i].Process(summed);
        }

        return summed * (float)OutputGain;
    }

    /// <summary>Clears every delay line and the comb loss state.</summary>
    public void Reset()
    {
        for (int i = 0; i < _combs.Length; i++) _combs[i].Reset();
        for (int i = 0; i < _allPasses.Length; i++) _allPasses[i].Reset();
    }

    private static int Scale(int framesAt48k, double sampleRate) =>
        Math.Max(1, (int)Math.Round(framesAt48k * sampleRate / ReferenceSampleRate));

    /// <summary>Lowpass-feedback comb: one delay line plus a one-pole lowpass inside the loop.</summary>
    private sealed class Comb
    {
        private readonly float[] _buffer;
        private readonly int _length;
        private int _index;
        private float _store;

        public Comb(int frames)
        {
            _length = frames;
            _buffer = new float[frames + BufferPadding];
        }

        public float Process(float input, float feedback, float damp1, float damp2)
        {
            float output = _buffer[_index];
            _store = output * damp2 + _store * damp1;
            _buffer[_index] = input + _store * feedback;
            if (++_index >= _length) _index = 0;
            return output;
        }

        public void Reset()
        {
            Array.Clear(_buffer);
            _index = 0;
            _store = 0;
        }
    }

    /// <summary>Schroeder allpass with the conventional 0.5 coefficient.</summary>
    private sealed class AllPass
    {
        private readonly float[] _buffer;
        private readonly int _length;
        private int _index;

        public AllPass(int frames)
        {
            _length = frames;
            _buffer = new float[frames + BufferPadding];
        }

        public float Process(float input)
        {
            float buffered = _buffer[_index];
            _buffer[_index] = input + buffered * 0.5f;
            if (++_index >= _length) _index = 0;
            return buffered - input;
        }

        public void Reset()
        {
            Array.Clear(_buffer);
            _index = 0;
        }
    }
}
