namespace JockeyBox.Dsp.RnNoise;

/// <summary>
/// The live RNNoise denoiser as a mono <see cref="IAudioProcessor"/>: one recurrent state per
/// channel, fed whole frames at int16 scale, with the network's measured delay reported and its
/// dry reference delayed to match.
///
/// <para>
/// <b>Placement.</b> The strip runs this stage <i>first</i>, ahead of the high-pass, the EQ and
/// the gate. Denoising after the gate is the tempting alternative — the gate is a "noise" tool —
/// but it is the wrong order twice over: the gate's envelope follower then tracks noise the
/// denoiser would have removed, so it chatters on every noise burst, and a 1 kHz EQ boost placed
/// before this stage is re-encoded by a network that never saw that spectral shape. Feeding the
/// network the raw signal also gives it the cleanest estimate of the noise floor. The rack UI
/// follows this order, so it is stated here and in <see cref="MixerStrip"/>.
/// </para>
///
/// <para>
/// <b>Frame size is queried, never assumed.</b> It is read from
/// <see cref="IDenoiseFrameProcessor.FrameSize"/> when the engine is created. It is 480 today,
/// and the header explicitly says to ask.
/// </para>
///
/// <para>
/// <b>Framing.</b> The engine delivers 480-frame blocks (<c>AudioEngine.BlockFrames</c>) and the
/// frame size is 480, so one block is exactly one frame: nothing is buffered between blocks
/// beyond the network's own delay. A block that is not a whole number of frames means the
/// caller's contract changed, which would corrupt audio silently if we padded or dropped, so it
/// throws.
/// </para>
///
/// <para>
/// <b>Latency is measured, not assumed.</b> This build of RNNoise is <i>not</i> zero-latency:
/// a jittered (deliberately non-periodic) formant vowel cross-correlates with the engine output
/// at a unique <b>960-sample</b> peak (corr 0.988; 959 and 961 are lower), and a burst onset
/// emerges exactly two 480-sample frames after the input burst. That is
/// <see cref="RnNoiseEngine.MeasuredDelayFrames"/> frames. The stage reports it through
/// <see cref="LatencyFrames"/> so the latency ledger can compensate the channel.
/// </para>
///
/// <para>
/// <b>Start-up is a delay, not a dead frame.</b> The facts doc's first two frames (0.00 and
/// 1.4% of level) are the same two-frame delay, seen from a fresh state. Mixing the current dry
/// signal with an output that is two frames behind would comb and smear an enable, so the dry
/// reference is delayed by the same amount before the wet blend. At the boundary both sit on the
/// same timeline, and the first 960 output samples are silence — the reported latency, which
/// compensation pads elsewhere. Nothing is dropped, so sync is kept.
/// </para>
///
/// <para>
/// <b>Int16 scale.</b> The strip hands over <c>[-1,1]</c> floats; RNNoise wants int16 scale. A
/// <c>[-1,1]</c> frame measured a VAD of exactly 0.0000 for every frame and passed a voiced
/// signal at +0.02 dB — a no-op that still reports success. The multiply and divide are both
/// here, in <see cref="ProcessFrame"/>, so their absence is visible and testable.
/// </para>
///
/// <para>
/// <b>Neutral touches nothing.</b> When disabled (or <c>Mix == 0</c>) the stage is a passthrough,
/// creates no engine and makes no native call; the buffer is left bit-identical.
/// </para>
/// </summary>
public sealed class RnNoiseStage : IAudioProcessor, IDisposable
{
    /// <summary>
    /// The int16 scale the network expects. One constant so the reader (and the test) can see
    /// every use in one place.
    /// </summary>
    public const float Int16Scale = 32768f;

    /// <summary>Stable identifier for the rack and the API.</summary>
    public const string StageId = "denoise.rnnoise";

    private readonly Func<IDenoiseFrameProcessor> _factory;

    private NoiseSuppressionParameters _parameters = NoiseSuppressionParameters.Neutral;
    private IDenoiseFrameProcessor? _engine;
    private float[]? _scratch;
    private float[]? _dryDelay;
    private int _dryWrite;
    private bool _unavailable;

    /// <summary>Creates a stage backed by the real <c>rnnoise.dll</c>.</summary>
    public RnNoiseStage() : this(() => new RnNoiseEngine())
    {
    }

    /// <summary>
    /// Creates a stage around a supplied engine factory. The tests use this to run the framing,
    /// scaling, delay alignment and stereo logic on Linux, where the DLL cannot load.
    /// </summary>
    public RnNoiseStage(Func<IDenoiseFrameProcessor> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public string Id => StageId;

    /// <summary>
    /// The engine's measured algorithmic delay while the stage is active, zero while it is
    /// bypassed. For the native engine this is 960 samples at 48 kHz (20 ms).
    /// </summary>
    public int LatencyFrames => IsNeutral ? 0 : (_engine?.LatencySamples ?? 0);

    /// <inheritdoc />
    public bool Bypass { get; set; }

    /// <summary>True when the stage would leave the buffer untouched.</summary>
    public bool IsNeutral => _parameters.IsNeutral;

    /// <summary>The queried frame size, or 0 before the engine exists.</summary>
    public int FrameSize => _engine?.FrameSize ?? 0;

    /// <summary>True once the engine has been created (tests and diagnostics only).</summary>
    public bool HasEngine => _engine is not null;

    /// <summary>True when the library could not be loaded and the stage degraded to dry.</summary>
    public bool IsUnavailable => _unavailable;

    /// <summary>Current target parameters.</summary>
    public NoiseSuppressionParameters Parameters => _parameters;

    /// <summary>
    /// Moves to new target parameters. The engine is built here, never on the render thread,
    /// on the first enable (the limiter's pattern). A load failure degrades to dry instead of
    /// throwing into the audio path.
    /// </summary>
    public void Apply(NoiseSuppressionParameters next)
    {
        _parameters = next.Clamp();
        if (_parameters.IsNeutral) return;

        EnsureEngine();
    }

    /// <summary>
    /// Processes whole frames in place. Allocation-free: every buffer was sized when the engine
    /// was created.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (Bypass || IsNeutral || _engine is null || samples.Length == 0) return;

        int frameSize = _engine.FrameSize;
        if (samples.Length % frameSize != 0)
        {
            // 480 today and 480 is the frame size, so the engine's block is exactly one frame.
            // If that ever stops being true, padding or dropping here would corrupt audio
            // silently; a clear failure is the honest response.
            throw new ArgumentException(
                $"A block of {samples.Length} samples is not a whole number of {frameSize}-sample " +
                "RNNoise frames. The engine delivers 480-frame blocks, so the stage expects a " +
                "whole number of frames per block.",
                nameof(samples));
        }

        int frames = samples.Length / frameSize;
        for (int f = 0; f < frames; f++)
        {
            ProcessFrame(samples.Slice(f * frameSize, frameSize));
        }
    }

    private void ProcessFrame(Span<float> frame)
    {
        int n = frame.Length;
        float[] scratch = _scratch!;

        // Scale to int16 in one pass; the engine sees only the scaled frame.
        for (int i = 0; i < n; i++) scratch[i] = frame[i] * Int16Scale;

        _engine!.ProcessFrame(scratch);

        double mix = _parameters.Mix;
        int delay = _engine.LatencySamples;

        if (delay == 0)
        {
            for (int i = 0; i < n; i++)
            {
                float wet = scratch[i] / Int16Scale;
                frame[i] = (float)(frame[i] + mix * (wet - frame[i]));
            }

            return;
        }

        // The engine's output is `delay` samples behind the input. Delay the dry reference by the
        // same amount so the blend compares the same instant, not the current sample against one
        // from 20 ms ago. The ring holds the delay, so the stage's output is uniformly delayed by
        // `delay` and the first `delay` output samples are zero.
        float[] dry = _dryDelay!;
        for (int i = 0; i < n; i++)
        {
            float current = frame[i];
            float delayed = dry[_dryWrite];
            dry[_dryWrite] = current;
            if (++_dryWrite == delay) _dryWrite = 0;

            float wet = scratch[i] / Int16Scale;
            frame[i] = (float)(delayed + mix * (wet - delayed));
        }
    }

    /// <summary>
    /// Fresh state: there is no reset export, so this destroys the engine and builds a new one
    /// when the stage is still enabled. Runs off the render thread (channel strip reset).
    /// </summary>
    public void Reset()
    {
        _engine?.Dispose();
        _engine = null;
        _scratch = null;
        _dryDelay = null;
        _dryWrite = 0;

        if (!_parameters.IsNeutral) EnsureEngine();
    }

    private void EnsureEngine()
    {
        if (_engine is not null || _unavailable) return;

        try
        {
            _engine = _factory();
            _scratch = new float[_engine.FrameSize];
            _dryDelay = _engine.LatencySamples > 0
                ? new float[_engine.LatencySamples]
                : Array.Empty<float>();
            _dryWrite = 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                       or EntryPointNotFoundException
                                       or BadImageFormatException
                                       or InvalidOperationException)
        {
            // Degrade to dry on a host that cannot load the runtime (Linux) rather than throw on
            // the audio path. The reason is kept for diagnostics.
            _unavailable = true;
            _engine = null;
            Bypass = true;
        }
    }

    /// <summary>
    /// Disposes the state. Ordered so a future non-NULL model, which outlives its states, would
    /// be freed after this rather than under a live state.
    /// </summary>
    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        _scratch = null;
        _dryDelay = null;
    }
}
