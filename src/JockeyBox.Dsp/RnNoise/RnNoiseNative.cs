using System.Runtime.InteropServices;

namespace JockeyBox.Dsp.RnNoise;

/// <summary>
/// The exported surface of the vendored <c>rnnoise.dll</c> (xiph/rnnoise <c>70f1d256</c>,
/// Windows x64). See <c>docs/research/rnnoise-facts.md</c> for the measurements behind every
/// statement here.
/// <para>
/// <b>Only what the stage uses.</b> The DLL exports ten names; this binding declares four. The
/// rest are deliberately absent:
/// </para>
/// <list type="bullet">
/// <item><c>rnnoise_init</c> — redundant: <c>rnnoise_create(NULL)</c> already returns a ready
/// state (measured), and the stage never re-initialises an existing one (there is no reset
/// export; reset means destroy + create).</item>
/// <item><c>rnnoise_get_size</c> — a constant (32,688 bytes) that only a memory test would read.
/// Nothing in the audio path needs it.</item>
/// <item><c>rnnoise_model_from_file</c>, <c>rnnoise_model_from_filename</c>,
/// <c>rnnoise_model_from_buffer</c>, <c>rnnoise_model_free</c> — the stage always passes
/// <c>NULL</c> to select the weights compiled into the DLL. Binding a model loader would invite
/// a caller to load a file the vendored binary was never tested against, and
/// <c>GregorR/rnnoise-nu</c> models do not load here at all.</item>
/// </list>
/// <para>
/// <b>Library name.</b> Fixed, not resolved at run time: the csproj copies the DLL beside
/// <c>JockeyBox.Dsp.dll</c>, so the default probe finds it. <c>ExactSpelling</c> stops the
/// runtime from appending an <c>A</c>/<c>W</c> suffix that these plain C names do not have.
/// </para>
/// <para>
/// <b>Arrays, not pointers.</b> <c>float[]</c> parameters are blittable and pin in place on
/// .NET 8, so the per-frame call copies nothing and needs no <c>unsafe</c>. The stage owns the
/// buffers, so there is no lifetime to reason about beyond the call.
/// </para>
/// </summary>
internal static class RnNoiseNative
{
    /// <summary>The file name the csproj copies next to this assembly.</summary>
    internal const string Library = "rnnoise";

    /// <summary>
    /// <c>DenoiseState* rnnoise_create(RNNModel* model)</c>. Passing <see cref="IntPtr.Zero"/>
    /// selects the model compiled into the DLL (facts doc §5), which is the only model this
    /// build ships and the only one measured.
    /// </summary>
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rnnoise_create(IntPtr model);

    /// <summary><c>void rnnoise_destroy(DenoiseState* state)</c>.</summary>
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rnnoise_destroy(IntPtr state);

    /// <summary>
    /// <c>int rnnoise_get_frame_size(void)</c>. Measured 480 today, but the header says to ask,
    /// so the stage never hardcodes the number.
    /// </summary>
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rnnoise_get_frame_size();

    /// <summary>
    /// <c>float rnnoise_process_frame(DenoiseState* state, float* out, float* in)</c>.
    /// <para>
    /// <b>Input and output are int16 scale, not <c>[-1,1]</c>.</b> A <c>[-1,1]</c> frame makes the
    /// VAD read exactly 0.0000 and the signal pass through (+0.02 dB) — a silent no-op that still
    /// returns "success" (facts doc §4). The scaling lives in <see cref="RnNoiseStage"/>, not
    /// here: this binding is a faithful mirror of the C signature.
    /// </para>
    /// <para>
    /// <c>out</c> and <c>in</c> may alias (measured bit-identical, facts doc §5), which is what
    /// lets the stage process the scaled frame in place.
    /// </para>
    /// <para>The return value is the voice activity probability, 0..1.</para>
    /// </summary>
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float rnnoise_process_frame(IntPtr state, float[] output, float[] input);
}

/// <summary>
/// Whether this process can load the vendored denoiser. The DSP library ships and runs on
/// Linux (where the DLL cannot load), so this is a capability probe, not a platform assertion.
/// </summary>
public static class RnNoiseRuntime
{
    private static bool? _available;

    /// <summary>
    /// True when the DLL is present and its exports resolve. Probed once: a failed
    /// <see cref="DllNotFoundException"/> becomes <c>false</c> instead of throwing on the audio
    /// thread. On Linux this is always <c>false</c>.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            _available ??= Probe();
            return _available.Value;
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            // A pure query with no state to leak: proves the file loads and the entry point
            // exists. Any of these failures means "not usable here", never an exception.
            return RnNoiseNative.rnnoise_get_frame_size() > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
