namespace JockeyBox.Dsp.RnNoise;

/// <summary>
/// One mono denoiser state: the seam that lets the frame, scaling and crossfade logic be tested
/// on Linux, where the vendored Windows DLL cannot load.
/// <para>
/// <b>Why an interface rather than a delegate.</b> The stage needs two things from the engine
/// that a single <c>Func&lt;float[], float&gt;</c> cannot express: the <see cref="FrameSize"/>
/// it was created with, and disposal, because a <c>DenoiseState</c> is unmanaged memory that has
/// to be released in a defined order. Both are part of the contract, so they belong in the type.
/// The interface is deliberately tiny — it mirrors the native call plus its lifetime, nothing
/// more — so it is not an abstraction layer, just the one substitution point the tests need.
/// </para>
/// <para>
/// <b>Samples are int16 scale</b> (roughly -32768..32767), exactly as
/// <c>rnnoise_process_frame</c> expects. Scaling from the strip's <c>[-1,1]</c> floats is the
/// stage's job, not the engine's: a binding that quietly rescales stops being a faithful mirror
/// of the C signature and hides the one thing a reviewer must see.
/// </para>
/// </summary>
public interface IDenoiseFrameProcessor : IDisposable
{
    /// <summary>Frame length this engine accepts, as queried from the library at construction.</summary>
    int FrameSize { get; }

    /// <summary>
    /// Algorithmic delay between input and output, in samples. The native engine is not
    /// zero-latency: a jittered formant vowel cross-correlates with its output at a unique
    /// 960-sample peak (corr 0.988 at 480-sample frames), so the stage must report and align to
    /// that delay rather than assume the frame callback is instantaneous.
    /// </summary>
    int LatencySamples { get; }

    /// <summary>
    /// Processes exactly <see cref="FrameSize"/> int16-scale samples in place and returns the
    /// voice activity probability (0..1).
    /// </summary>
    float ProcessFrame(float[] frame);
}

/// <summary>
/// The real engine: one <c>DenoiseState</c> over the vendored <c>rnnoise.dll</c>, created with a
/// <c>NULL</c> model so the weights compiled into the DLL are used.
/// <para>
/// <b>Why <c>NULL</c>.</b> The vendored binary has the default model baked in (facts doc §5), so
/// <c>NULL</c> removes an entire failure mode: no missing, mismatched or untested <c>.rnn</c>
/// file can reach the render path. The model loaders are not bound at all — see
/// <see cref="RnNoiseNative"/> — because nothing here would ever call them.
/// </para>
/// <para>
/// <b>One state per channel.</b> RNNoise is mono. Each <see cref="RnNoiseStage"/> owns one
/// engine, and a stereo strip owns two stages, so left and right cannot share a recurrent
/// state (and therefore cannot influence each other's output).
/// </para>
/// </summary>
public sealed class RnNoiseEngine : IDenoiseFrameProcessor
{
    /// <summary>
    /// Measured algorithmic delay of this build: two 480-sample frames. Jittered-vowel
    /// cross-correlation peaks sharply at 960 samples (corr 0.988; 959 and 961 are lower),
    /// and a burst onset emerges two frames after the input burst. See
    /// <c>docs/research/rnnoise-facts.md</c> and <c>RnNoiseInteropTests</c>.
    /// </summary>
    public const int MeasuredDelayFrames = 2;

    private IntPtr _state;

    public RnNoiseEngine()
    {
        FrameSize = RnNoiseNative.rnnoise_get_frame_size();
        if (FrameSize <= 0)
            throw new InvalidOperationException($"rnnoise_get_frame_size returned {FrameSize}.");

        // NULL selects the compiled-in model; documented above and in RnNoiseNative.
        _state = RnNoiseNative.rnnoise_create(IntPtr.Zero);
        if (_state == IntPtr.Zero)
            throw new InvalidOperationException("rnnoise_create(NULL) returned a null DenoiseState.");
    }

    /// <inheritdoc />
    public int FrameSize { get; }

    /// <inheritdoc />
    public int LatencySamples => FrameSize * MeasuredDelayFrames;

    /// <inheritdoc />
    public float ProcessFrame(float[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length != FrameSize)
            throw new ArgumentException(
                $"A frame is exactly {FrameSize} samples; got {frame.Length}.", nameof(frame));

        if (_state == IntPtr.Zero) throw new ObjectDisposedException(nameof(RnNoiseEngine));

        // In-place aliasing is measured bit-identical to separate buffers (facts doc §5), so the
        // same array on both sides of the call is the intended, not merely convenient, use.
        return RnNoiseNative.rnnoise_process_frame(_state, frame, frame);
    }

    /// <summary>
    /// Same export, separate input and output buffers. Only the aliasing equivalence test uses
    /// this; the stage always calls the in-place overload.
    /// </summary>
    public float ProcessFrame(float[] input, float[] output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (input.Length != FrameSize || output.Length != FrameSize)
            throw new ArgumentException($"Both frames must be exactly {FrameSize} samples.");

        if (_state == IntPtr.Zero) throw new ObjectDisposedException(nameof(RnNoiseEngine));

        return RnNoiseNative.rnnoise_process_frame(_state, output, input);
    }

    /// <summary>
    /// Destroys the state. A model, if one were ever loaded, would be freed after this — a model
    /// outlives every state that references it (facts doc §5). With the <c>NULL</c> model there
    /// is nothing to free, but <see cref="RnNoiseStage"/> keeps the same order so a future
    /// non-NULL model cannot be released under a live state.
    /// </summary>
    public void Dispose()
    {
        if (_state == IntPtr.Zero) return;
        RnNoiseNative.rnnoise_destroy(_state);
        _state = IntPtr.Zero;
    }
}
