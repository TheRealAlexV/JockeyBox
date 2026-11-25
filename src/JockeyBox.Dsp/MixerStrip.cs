namespace JockeyBox.Dsp;

/// <summary>
/// A live channel strip for one capture endpoint: one mono chain of
/// <see cref="HighPassFilter"/>, <see cref="LowPassFilter"/>, <see cref="TenBandEq"/>,
/// <see cref="NoiseGate"/>, <see cref="Compressor"/>, <see cref="DeEsser"/> and an optional
/// <see cref="Limiter"/>.
/// <para>
/// <b>Latency:</b> the limiter is the only stage here that carries latency (240 frames at
/// 48 kHz, 5 ms of lookahead). The operator's requirement is zero added latency by default, so
/// the limiter is constructed lazily, only when it is switched on, and <see cref="Apply"/>
/// is the only place that allocates it. In practice Apply runs when the operator changes a
/// setting, which happens off the render thread; <see cref="Process(Span{float})"/> never
/// allocates and never builds or drops the limiter. Once allocated the limiter is kept and
/// simply bypassed when switched off, so toggling does not churn the heap.
/// </para>
/// <para>
/// <b>Allocation:</b> <see cref="Process(Span{float})"/>, <see cref="Process(float[], int, int)"/>
/// and <see cref="Apply"/> are all allocation-free on the heap. The parameters are a value type,
/// so passing them never allocates.
/// </para>
/// <para>
/// <b>Mono:</b> a strip is one channel. For a stereo endpoint build two strips. The interleaved
/// overload takes one channel's contiguous slice of a larger mixer buffer; the caller
/// de-interleaves first, as <see cref="IAudioProcessor"/> already requires.
/// </para>
/// </summary>
public sealed class MixerStrip
{
    private readonly double _sampleRate;

    private readonly HighPassFilter _highPass;
    private readonly LowPassFilter _lowPass;
    private readonly TenBandEq _eq;
    private readonly NoiseGate _gate;
    private readonly Compressor _compressor;
    private readonly DeEsser _deEsser;

    private Limiter? _limiter;

    private StripParameters _current;

    public MixerStrip(double sampleRate, string id = "strip")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Strip id is required.", nameof(id));

        _sampleRate = sampleRate;
        Id = id;

        // 10 ms of ramp for cutoff moves: smooth enough to hide a knob sweep, short enough to
        // feel immediate. The processors run their own internal smoothers for the rest.
        int rampFrames = Math.Max(1, (int)Math.Round(sampleRate * 0.010));

        _highPass = new HighPassFilter(sampleRate, HighPassFilter.TransparentFrequencyHz,
            id: $"{id}.highpass", rampFrames: rampFrames);
        _lowPass = new LowPassFilter(sampleRate, LowPassFilter.TransparentFrequencyHz,
            id: $"{id}.lowpass", rampFrames: rampFrames);
        _eq = new TenBandEq(sampleRate, id: $"{id}.eq");
        _gate = new NoiseGate(sampleRate, id: $"{id}.gate");
        _compressor = new Compressor(sampleRate, id: $"{id}.compressor");
        _deEsser = new DeEsser(sampleRate, id: $"{id}.deesser");

        ApplyInternal(StripParameters.Neutral);
    }

    public string Id { get; }

    public double SampleRate => _sampleRate;

    /// <summary>The parameters currently applied, already clamped.</summary>
    public StripParameters Parameters => _current;

    public HighPassFilter HighPass => _highPass;

    public LowPassFilter LowPass => _lowPass;

    public TenBandEq Eq => _eq;

    public NoiseGate Gate => _gate;

    public Compressor Compressor => _compressor;

    public DeEsser DeEsser => _deEsser;

    /// <summary>The limiter, or null while it has never been switched on.</summary>
    public Limiter? Limiter => _limiter;

    /// <summary>Processors in the chain, limiter included only once it exists.</summary>
    public int ProcessorCount => _limiter is null ? 6 : 7;

    /// <summary>Latency this strip adds, in frames: the limiter's 240, or 0 when it is off.</summary>
    public int LatencyFrames => _limiter is not null && !_limiter.Bypass ? _limiter.LatencyFrames : 0;

    /// <summary>
    /// True when the strip cannot change the buffer at all: master bypass, or every stage is
    /// either bypassed or sitting in its bit-transparent neutral state. Callers keep their
    /// existing <c>if (!strip.IsPassthrough)</c> fast path.
    /// </summary>
    public bool IsPassthrough =>
        _current.MasterBypass ||
        ((_highPass.Bypass || _highPass.IsNeutral) &&
         (_lowPass.Bypass || _lowPass.IsNeutral) &&
         (_eq.Bypass || _eq.IsFlat) &&
         (_gate.Bypass || _gate.IsNeutral) &&
         _compressor.Bypass &&
         (_deEsser.Bypass || _deEsser.IsNeutral) &&
         (_limiter is null || _limiter.Bypass));

    /// <summary>
    /// Moves the strip to new parameters in place. Continuous values ramp (about 10 ms for the
    /// cutoff filters, the processors' own smoothers for EQ, gate and compressor); on/off
    /// switches take effect at the call boundary. The limiter is built here on first enable.
    /// <para>
    /// A ceiling or release change on an already-built limiter is not applied: <see cref="Limiter"/>
    /// takes those at construction. Rebuild the strip to change them.
    /// </para>
    /// </summary>
    public void Apply(StripParameters next) => ApplyInternal(next.Clamp());

    private void ApplyInternal(StripParameters p)
    {
        _current = p;
        bool bypassAll = p.MasterBypass;

        _highPass.Bypass = bypassAll || !p.HighPass.Enabled;
        _highPass.FrequencyHz = p.HighPass.FrequencyHz;

        _lowPass.Bypass = bypassAll || !p.LowPass.Enabled;
        _lowPass.FrequencyHz = p.LowPass.FrequencyHz;

        _eq.Bypass = bypassAll;
        for (int band = 0; band < TenBandEq.CentresHz.Length; band++)
        {
            _eq.SetGainDb(band, p.Eq[band]);
        }

        _gate.Bypass = bypassAll || !p.Gate.Enabled;
        _gate.ThresholdDb = p.Gate.ThresholdDb;
        _gate.HysteresisDb = p.Gate.HysteresisDb;
        _gate.AttackMs = p.Gate.AttackMs;
        _gate.ReleaseMs = p.Gate.ReleaseMs;
        _gate.HoldMs = p.Gate.HoldMs;
        _gate.FloorDb = p.Gate.FloorDb;

        _compressor.Bypass = bypassAll || !p.Compressor.Enabled;
        _compressor.ThresholdDb = p.Compressor.ThresholdDb;
        _compressor.Ratio = p.Compressor.Ratio;
        _compressor.AttackMs = p.Compressor.AttackMs;
        _compressor.ReleaseMs = p.Compressor.ReleaseMs;
        _compressor.KneeDb = p.Compressor.KneeDb;
        _compressor.MakeupDb = p.Compressor.MakeupDb;

        _deEsser.Bypass = bypassAll || !p.DeEsser.Enabled;
        _deEsser.Parameters = p.DeEsser;

        // Lazy by design: a neutral strip must not carry the limiter's 240 frames of latency.
        if (p.Limiter.Enabled && _limiter is null)
        {
            _limiter = new Limiter(_sampleRate, thresholdDb: p.Limiter.CeilingDb,
                releaseMs: p.Limiter.ReleaseMs, id: $"{Id}.limiter");
        }

        if (_limiter is not null)
        {
            _limiter.Bypass = bypassAll || !p.Limiter.Enabled;

            // Retune the live limiter in place. Rebuilding it would churn the heap and lose the
            // lookahead buffer; the setters keep latency and allocation flat.
            _limiter.CeilingDb = p.Limiter.CeilingDb;
            _limiter.ReleaseMs = p.Limiter.ReleaseMs;
        }
    }

    /// <summary>Processes one channel in place. Allocation-free; no-op when the strip is transparent.</summary>
    public void Process(Span<float> samples)
    {
        if (IsPassthrough) return;

        _highPass.Process(samples);
        _lowPass.Process(samples);
        _eq.Process(samples);
        _gate.Process(samples);
        _compressor.Process(samples);
        _deEsser.Process(samples);

        if (_limiter is not null && !_limiter.Bypass)
        {
            _limiter.Process(samples);
        }
    }

    /// <summary>
    /// Processes <paramref name="frames"/> samples of one channel, starting at
    /// <paramref name="offset"/> in a larger (typically interleaved) buffer. Allocation-free.
    /// </summary>
    public void Process(float[] buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (offset + frames > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frames),
                $"{frames} frames from offset {offset} run past the {buffer.Length}-sample buffer.");
        }

        if (frames == 0 || IsPassthrough) return;

        Process(buffer.AsSpan(offset, frames));
    }

    /// <summary>Clears every filter and envelope state, limiter lookahead included.</summary>
    public void Reset()
    {
        _highPass.Reset();
        _lowPass.Reset();
        _eq.Reset();
        _gate.Reset();
        _compressor.Reset();
        _deEsser.Reset();
        _limiter?.Reset();
    }
}
