using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace JockeyBox.Dsp.NvAfx;

/// <summary>
/// Status codes observed from the installed <c>NVAudioEffects.dll</c> (FileVersion 1.6.1.2).
/// <para>
/// The codes are deliberately named rather than bucketed. <see cref="UnknownParameter"/> (3)
/// and <see cref="Failure"/> (1) are different findings, and the runtime really does return
/// them differently for the same bogus name: <c>GetU32("no_such_param")</c> returns 1 while
/// <c>GetString("no_such_param")</c> returns 3. Treating both as "the effect failed" hides a
/// typo in a parameter name behind an inference failure, which is the most expensive kind of
/// wrong diagnosis to chase.
/// </para>
/// <para>
/// Only 0, 1 and 3 were measured. <see cref="Unrecognized"/> is synthetic, carries the raw
/// value through <see cref="NvAfxStatusText.RawCode"/>, and means "the runtime returned
/// something the rig has never seen" — never "some other failure".
/// </para>
/// </summary>
public enum NvAfxStatus
{
    /// <summary>The call succeeded (measured 0).</summary>
    Ok = 0,

    /// <summary>The effect failed (measured 1).</summary>
    Failure = 1,

    /// <summary>The runtime does not know that parameter name (measured 3).</summary>
    UnknownParameter = 3,

    /// <summary>A code outside {0, 1, 3}; see <see cref="NvAfxStatusText.RawCode"/>.</summary>
    Unrecognized = -1,
}

/// <summary>Reads and describes <see cref="NvAfxStatus"/> without collapsing the codes.</summary>
public static class NvAfxStatusText
{
    /// <summary>The int returned by the runtime, including for <see cref="NvAfxStatus.Unrecognized"/>.</summary>
    public static int RawCode(this NvAfxStatus status) => (int)status;

    /// <summary>True only for <see cref="NvAfxStatus.Ok"/>.</summary>
    public static bool IsOk(this NvAfxStatus status) => status == NvAfxStatus.Ok;

    /// <summary>A sentence that names which failure this is, for logs and exception messages.</summary>
    public static string Describe(this NvAfxStatus status, string context) => status switch
    {
        NvAfxStatus.Ok => context + ": ok",
        NvAfxStatus.Failure => context + ": the effect failed (status 1)",
        NvAfxStatus.UnknownParameter => context + ": the runtime does not know that parameter name (status 3)",
        _ => context + ": unrecognised status " + status.RawCode(),
    };

    /// <summary>
    /// Throws a status-specific exception. Callers that use this can never turn a misnamed
    /// parameter into "the effect failed", because the two produce different exception types.
    /// </summary>
    public static void EnsureOk(this NvAfxStatus status, string context)
    {
        if (status == NvAfxStatus.Ok) return;

        if (status == NvAfxStatus.UnknownParameter) throw new NvAfxUnknownParameterException(status.Describe(context));

        throw new NvAfxCallException(status.Describe(context), status);
    }
}

/// <summary>Base type for every failure this binding reports.</summary>
public class NvAfxException : Exception
{
    public NvAfxException(string message) : base(message) { }

    public NvAfxException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A native call returned a non-zero status other than <see cref="NvAfxStatus.UnknownParameter"/>.</summary>
public sealed class NvAfxCallException : NvAfxException
{
    public NvAfxCallException(string message, NvAfxStatus status) : base(message) => Status = status;

    /// <summary>The status that caused the throw, preserved so callers can inspect the raw code.</summary>
    public NvAfxStatus Status { get; }
}

/// <summary>
/// A parameter name the runtime does not know. Separate type on purpose: status 3 is a
/// programmer error in the call site, not an effect failure.
/// </summary>
public sealed class NvAfxUnknownParameterException : NvAfxException
{
    public NvAfxUnknownParameterException(string message) : base(message) { }
}

/// <summary>
/// After a successful load, <c>GetString("model_path")</c> did not report the path that was
/// requested. That means a different model than intended is loaded, or the load silently
/// fell back, so the caller must not run audio through it.
/// </summary>
public sealed class NvAfxModelPathMismatchException : NvAfxException
{
    public NvAfxModelPathMismatchException(string effect, string expected, string actual)
        : base($"NvAFX: effect '{effect}' reports model_path '{actual}' but '{expected}' was requested.")
    {
        Effect = effect;
        Expected = expected;
        Actual = actual;
    }

    public string Effect { get; }

    public string Expected { get; }

    public string Actual { get; }
}

/// <summary>
/// Raw P/Invoke surface of <c>NVAudioEffects.dll</c>.
/// <para>
/// There is no header, no <c>.lib</c> and no <c>.def</c> anywhere on the box — only
/// <c>version.h</c>, which holds version macros and no declarations. Every argument type and
/// order below is therefore an <b>inference from observed behaviour</b>, not a declaration
/// shipped by NVIDIA. Each member says what the inference rests on. Where the shape is
/// unmeasured it says so; do not promote a comment to truth without a probe run on the rig.
/// </para>
/// <para>
/// The library name is a fixed string because the file name is the only stable thing here:
/// the exports are plain undecorated C names, so there is nothing to append or decorate. The
/// <i>directory</i> is not fixed — <see cref="NvAfxRuntime.EnsureSearchPath"/> resolves it
/// from <c>NVAFX_SDK_DIR</c> at run time, which is how the SDK installs itself (Machine scope).
/// </para>
/// </summary>
internal static class NvAfxNative
{
    internal const string Library = "NVAudioEffects.dll";

    /// <summary>
    /// <c>int NvAFX_CreateEffect(const char* effect_name, NvAFX_Handle* handle)</c>.
    /// Inferred from the working sequence CreateEffect → SetU32 → SetString → Load → GetU32:
    /// the name is an input, the handle is an output this side owns afterwards.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_CreateEffect(string effectName, out IntPtr handle);

    /// <summary>
    /// <c>int NvAFX_DestroyEffect(NvAFX_Handle)</c>. Single handle argument, returns int:
    /// inferred from the export sitting beside CreateEffect with no other parameter names in
    /// the SDK to suggest a second argument.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_DestroyEffect(IntPtr handle);

    /// <summary>
    /// <c>int NvAFX_Load(NvAFX_Handle)</c>. One argument, inferred: the measured sequence
    /// configures sample rate and <c>model_path</c> through Set* calls <i>before</i> Load, and
    /// a 4-argument Load is not among the exports' observable shapes. The SDK has no
    /// <c>NvAFX_GetVersion</c> and no default model, so the model can only arrive by SetString.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_Load(IntPtr handle);

    /// <summary>
    /// <c>int NvAFX_Run(NvAFX_Handle, float** in, float** out, uint32 num_samples, uint32 num_channels)</c>.
    /// <para>
    /// <b>Five arguments. The four-argument form is a trap and must never be bound.</b>
    /// Measured on a loaded handle: 4-arg Run returns <c>1</c> on the first call then <c>3</c>
    /// on the next four; the same handle afterwards answers five 5-arg calls with <c>0</c>.
    /// The error code depends on call order, which is why both "always 1" and "always 3"
    /// reports can be honest. A function that can be called at two arities is not a signature
    /// this binding may leave open, so no 4-argument overload exists here at all.
    /// </para>
    /// <para>
    /// <c>num_channels</c> is inferred to be the count the effect writes: every model in this
    /// SDK writes 1 channel, including <c>aec</c>, which reads 2. NOT ESTABLISHED for an
    /// effect with more than one output channel; the SDK ships none.
    /// </para>
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_Run(IntPtr handle, IntPtr[] input, IntPtr[] output, uint numSamples, uint numChannels);

    /// <summary>
    /// <c>int NvAFX_SetU32(NvAFX_Handle, const char* name, uint32 value)</c>.
    /// Inferred: <c>SetU32("sample_rate", 48000)</c> takes effect (GetU32 reads it back), so
    /// the value is a 32-bit unsigned input passed by value.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_SetU32(IntPtr handle, string name, uint value);

    /// <summary>
    /// <c>int NvAFX_GetU32(NvAFX_Handle, const char* name, uint32* value)</c>.
    /// Inferred: the measured probes read five fields back, so the value is an out parameter;
    /// a return-value-only shape cannot carry both status and data.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_GetU32(IntPtr handle, string name, out uint value);

    /// <summary>
    /// <c>int NvAFX_SetString(NvAFX_Handle, const char* name, const char* value)</c>.
    /// Inferred: <c>SetString("model_path", &lt;abs path&gt;)</c> is what makes the model load.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_SetString(IntPtr handle, string name, string value);

    /// <summary>
    /// <c>int NvAFX_GetString(NvAFX_Handle, const char* name, char* buffer, uint32* size)</c>.
    /// <para>
    /// Measured to work: returns <c>0</c> and the real <c>model_path</c>. The size argument is
    /// bound as an in/out count because the neighbouring Get*List exports share that shape.
    /// The rig confirmed both plausible marshallings — <c>(byte[] buf, uint capacity)</c> and
    /// <c>(byte[] buf, ref uint size)</c> — return the same status and the same bytes, so this
    /// choice is not load-bearing; do not "fix" it to the other form.
    /// </para>
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_GetString(IntPtr handle, string name, byte[] buffer, ref uint size);

    /// <summary>
    /// <c>int NvAFX_GetEffectList(uint32* count, char** list)</c>. Established by measurement on
    /// the rig against SDK 1.6.1.2, not inferred: the runtime writes the effect count through
    /// the first argument and stores a pointer to its own array of NUL-terminated names through
    /// the second. It is an out-only call — the caller supplies no buffer. The earlier
    /// <c>GetString</c>-shaped guess <c>(byte[], ref uint)</c> returned <c>0</c> but landed
    /// <c>0x05</c> at the buffer's first byte and a pointer value where the size was expected,
    /// which is exactly this shape read the wrong way round.
    /// <para>The name array is owned by the runtime; read it, never free it.</para>
    /// </summary>
    [DllImport(Library, ExactSpelling = true)]
    internal static extern int NvAFX_GetEffectList(out uint count, out IntPtr list);

    /// <summary>
    /// <c>int NvAFX_Reset(NvAFX_Handle)</c>. Single handle, inferred: it sits beside Load/Run
    /// and its purpose is to clear the effect's state between blocks.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NvAFX_Reset(IntPtr handle);

    /// <summary>
    /// <c>void (*)(int level, const char* message)</c>. The message arrives as a pointer so the
    /// managed side owns the decode; the level is an int because the enum behind it is not
    /// declared anywhere on the box. NOT ESTABLISHED: both the arity and calling convention.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NvAfxLogCallback(int level, IntPtr message);

    /// <summary>
    /// <c>int NvAFX_InitializeLogger(void)</c>. No-argument form inferred: no export on the box
    /// takes a log level, and the logger is only useful for catching the text behind a bare
    /// <c>1</c>, so it must be usable before any handle exists.
    /// </summary>
    [DllImport(Library, ExactSpelling = true)]
    internal static extern int NvAFX_InitializeLogger();

    /// <summary>
    /// <c>int NvAFX_SetLoggingCallback(NvAFX_LogCallback)</c>. Callback shape inferred as
    /// above; unmeasured.
    /// </summary>
    [DllImport(Library, ExactSpelling = true)]
    internal static extern int NvAFX_SetLoggingCallback(NvAfxLogCallback callback);

    /// <summary><c>int NvAFX_UninitializeLogger(void)</c>. No-argument form, inferred as above.</summary>
    [DllImport(Library, ExactSpelling = true)]
    internal static extern int NvAFX_UninitializeLogger();

    /// <summary>
    /// Adds the SDK directory to the process DLL search path. <c>NVAudioEffects.dll</c> depends
    /// on <c>nvinfer_10.dll</c>, <c>cublas64_12.dll</c>, <c>cufft64_11.dll</c>,
    /// <c>nvrtc64_120_0.dll</c>, <c>libcrypto-3-x64.dll</c> and the MSVC runtime, all of which
    /// live beside it. Loading it from an arbitrary working directory fails outright rather
    /// than degrading, which is why the search path is set before the first P/Invoke.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDllDirectory(string path);
}

/// <summary>
/// Process-wide entry point: resolves the SDK directory, enumerates effects, and hands out
/// <see cref="NvAfxEffect"/> instances.
/// </summary>
public static class NvAfxRuntime
{
    private static readonly object Gate = new();

    private static bool _searchPathSet;

    private static NvAfxNative.NvAfxLogCallback? _logCallback;

    /// <summary>
    /// The SDK install directory, read from <c>NVAFX_SDK_DIR</c> (Machine scope on the rig).
    /// Null when unset — usually because this is not the Windows rig.
    /// </summary>
    public static string? SdkDirectory => Environment.GetEnvironmentVariable("NVAFX_SDK_DIR");

    /// <summary>True on a platform where the runtime can exist at all.</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// Whether this process could load the runtime: Windows, with <c>NVAFX_SDK_DIR</c> set to a
    /// directory that exists. Says nothing about whether the DLL itself will load.
    /// </summary>
    public static bool IsAvailable =>
        IsWindows && !string.IsNullOrWhiteSpace(SdkDirectory) && Directory.Exists(SdkDirectory);

    /// <summary>
    /// Points the process DLL search path at <c>NVAFX_SDK_DIR</c>. Idempotent; called by every
    /// entry point before the first P/Invoke so callers cannot forget it.
    /// </summary>
    public static void EnsureSearchPath()
    {
        lock (Gate)
        {
            if (_searchPathSet) return;

            if (!IsWindows) throw new NvAfxException("NvAFX: NVAudioEffects.dll is a Windows x64 runtime; this host cannot load it.");

            string? directory = SdkDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                throw new NvAfxException("NvAFX: NVAFX_SDK_DIR is not set, so NVAudioEffects.dll and its TensorRT/CUDA dependencies cannot be found.");

            if (!Directory.Exists(directory))
                throw new NvAfxException($"NvAFX: NVAFX_SDK_DIR points at '{directory}', which does not exist.");

            if (!NvAfxNative.SetDllDirectory(directory))
                throw new NvAfxException($"NvAFX: SetDllDirectory failed for '{directory}': {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            _searchPathSet = true;
        }
    }

    /// <summary>
    /// The effects this runtime offers (measured: five on SDK 1.6.1.2 — <c>denoiser</c>,
    /// <c>dereverb</c>, <c>dereverb_denoiser</c>, <c>aec</c>, <c>superres</c>).
    /// <para>
    /// The runtime hands back its own array of separately NUL-terminated names, so there is no
    /// separator to guess at: each pointer is one name.
    /// </para>
    /// </summary>
    public static NvAfxStatus GetEffectList(out string[] effects)
    {
        EnsureSearchPath();

        var status = (NvAfxStatus)NvAfxNative.NvAFX_GetEffectList(out uint count, out IntPtr list);
        if (!status.IsOk() || list == IntPtr.Zero)
        {
            effects = Array.Empty<string>();
            return status;
        }

        var names = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            IntPtr name = Marshal.ReadIntPtr(list, (int)i * IntPtr.Size);
            string? text = name == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(name);
            if (!string.IsNullOrWhiteSpace(text)) names.Add(text.Trim());
        }

        effects = names.ToArray();
        return status;
    }

    /// <summary>
    /// Creates an effect instance. The caller owns it and must dispose it.
    /// </summary>
    public static NvAfxEffect CreateEffect(string effectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectName);
        EnsureSearchPath();

        var status = (NvAfxStatus)NvAfxNative.NvAFX_CreateEffect(effectName, out IntPtr handle);
        status.EnsureOk($"NvAFX_CreateEffect('{effectName}')");

        if (handle == IntPtr.Zero) throw new NvAfxException($"NvAFX_CreateEffect('{effectName}') reported ok but returned a null handle.");

        return new NvAfxEffect(effectName, handle);
    }

    /// <summary>
    /// Turns on the SDK logger and forwards its text to <paramref name="sink"/>, so a bare
    /// status <c>1</c> can be explained instead of guessed at. The callback is rooted here
    /// because the runtime keeps the pointer after the call returns.
    /// </summary>
    public static NvAfxStatus AttachLogger(Action<int, string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        EnsureSearchPath();

        _logCallback = (level, message) => sink(level, Marshal.PtrToStringAnsi(message) ?? string.Empty);

        var status = (NvAfxStatus)NvAfxNative.NvAFX_InitializeLogger();
        if (!status.IsOk()) return status;

        return (NvAfxStatus)NvAfxNative.NvAFX_SetLoggingCallback(_logCallback);
    }

    /// <summary>Stops the logger. Safe to call when it was never started.</summary>
    public static NvAfxStatus DetachLogger()
    {
        _logCallback = null;
        return (NvAfxStatus)NvAfxNative.NvAFX_UninitializeLogger();
    }
}

/// <summary>
/// One effect instance: create, configure, load, run, release. Disposing is mandatory.
/// <para>
/// Every call returns <see cref="NvAfxStatus"/> instead of a bool, so a caller cannot answer
/// "did the parameter name exist?" with a yes/no. Use
/// <see cref="NvAfxStatusText.EnsureOk"/> when the difference does not matter, and inspect
/// the status when it does.
/// </para>
/// <para>
/// Threading: an instance is not thread-safe. The runtime takes one handle at a time.
/// </para>
/// </summary>
public sealed class NvAfxEffect : IDisposable
{
    private readonly string _effectName;
    private IntPtr _handle;

    internal NvAfxEffect(string effectName, IntPtr handle)
    {
        _effectName = effectName;
        _handle = handle;
    }

    /// <summary>The effect name this instance was created from, for messages.</summary>
    public string EffectName => _effectName;

    /// <summary><c>SetU32</c>. A writable knob, e.g. <c>sample_rate</c> before Load.</summary>
    /// <remarks>
    /// <c>num_channels</c> is not one of them: <c>SetU32("num_channels", 2)</c> on <c>aec</c>
    /// returns 3. The channel count is derived from the effect name, and the derived value is
    /// already right (<c>aec</c> reads 2). Do not retry it as a different type.
    /// </remarks>
    public NvAfxStatus SetU32(string name, uint value) => (NvAfxStatus)NvAfxNative.NvAFX_SetU32(Handle, name, value);

    /// <summary><c>SetString</c>. This is how <c>model_path</c> is set before Load.</summary>
    public NvAfxStatus SetString(string name, string value) => (NvAfxStatus)NvAfxNative.NvAFX_SetString(Handle, name, value);

    /// <summary><c>GetU32</c>. Returns the status; <paramref name="value"/> is 0 unless it is Ok.</summary>
    public NvAfxStatus GetU32(string name, out uint value)
    {
        var status = (NvAfxStatus)NvAfxNative.NvAFX_GetU32(Handle, name, out value);
        if (!status.IsOk()) value = 0;
        return status;
    }

    /// <summary>
    /// <c>GetString</c>. Returns the status and the text (empty unless Ok). This is the only
    /// proof the intended model loaded, so check it after every Load.
    /// </summary>
    public (NvAfxStatus Status, string Value) GetString(string name)
    {
        uint size = 1024;
        var buffer = new byte[size];
        var status = (NvAfxStatus)NvAfxNative.NvAFX_GetString(Handle, name, buffer, ref size);
        if (!status.IsOk()) return (status, string.Empty);

        int used = (int)Math.Min(size, (uint)buffer.Length);
        int end = Array.IndexOf(buffer, (byte)0, 0, Math.Max(used, 0));
        if (end < 0) end = used;

        return (status, Encoding.ASCII.GetString(buffer, 0, end));
    }

    /// <summary>Loads the configured model. Configure <c>sample_rate</c> and <c>model_path</c> first.</summary>
    public NvAfxStatus Load() => (NvAfxStatus)NvAfxNative.NvAFX_Load(Handle);

    /// <summary>
    /// Loads, then proves the runtime holds the model that was asked for: a successful load is
    /// not evidence by itself, but <c>GetString("model_path")</c> is.
    /// </summary>
    /// <exception cref="NvAfxUnknownParameterException">The runtime has no <c>model_path</c> parameter.</exception>
    /// <exception cref="NvAfxModelPathMismatchException">A different model is loaded.</exception>
    /// <exception cref="NvAfxCallException">Load or the read-back failed.</exception>
    public void LoadVerified(string expectedModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModelPath);

        Load().EnsureOk($"NvAFX_Load on '{_effectName}'");

        var (status, actual) = GetString("model_path");
        status.EnsureOk($"NvAFX_GetString(\"model_path\") on '{_effectName}'");

        // Windows paths are case-insensitive, so a case-only difference is not a mismatch.
        if (!string.Equals(actual, expectedModelPath, StringComparison.OrdinalIgnoreCase))
            throw new NvAfxModelPathMismatchException(_effectName, expectedModelPath, actual);
    }

    /// <summary>Clears the effect's state, e.g. between songs.</summary>
    public NvAfxStatus Reset() => (NvAfxStatus)NvAfxNative.NvAFX_Reset(Handle);

    /// <summary>
    /// Runs one block. <paramref name="frames"/> must equal the effect's
    /// <c>num_samples_per_frame</c> (480 at 48 kHz: the models are "10 milliseconds framesize"),
    /// and every buffer must hold at least that many samples.
    /// <para>
    /// The 5-argument native call is the only one made here; the 4-argument form returns 1 then
    /// 3 and is documented as a trap in <see cref="NvAfxNative.NvAFX_Run"/>. Channels are
    /// pinned per call — fine for a probe, and Phase 3 may hoist the pinning.
    /// </para>
    /// <para>
    /// Budget against the measured p99 of roughly 1.1-1.2 ms per 480-frame block, never the
    /// 0.8 us p50: the distribution is bimodal and the tail is what drops audio.
    /// </para>
    /// </summary>
    public NvAfxStatus Run(float[][] input, float[][] output, int frames)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames), frames, "frames must be positive.");
        if (input.Length == 0 || output.Length == 0)
            throw new ArgumentException("Run needs at least one input and one output channel.");

        for (int i = 0; i < input.Length; i++)
            if (input[i] is null || input[i].Length < frames)
                throw new ArgumentException($"input[{i}] must hold at least {frames} samples.", nameof(input));

        for (int i = 0; i < output.Length; i++)
            if (output[i] is null || output[i].Length < frames)
                throw new ArgumentException($"output[{i}] must hold at least {frames} samples.", nameof(output));

        var inputPins = new GCHandle[input.Length];
        var outputPins = new GCHandle[output.Length];
        var inputPointers = new IntPtr[input.Length];
        var outputPointers = new IntPtr[output.Length];

        try
        {
            for (int i = 0; i < input.Length; i++)
            {
                inputPins[i] = GCHandle.Alloc(input[i], GCHandleType.Pinned);
                inputPointers[i] = inputPins[i].AddrOfPinnedObject();
            }

            for (int i = 0; i < output.Length; i++)
            {
                outputPins[i] = GCHandle.Alloc(output[i], GCHandleType.Pinned);
                outputPointers[i] = outputPins[i].AddrOfPinnedObject();
            }

            return (NvAfxStatus)NvAfxNative.NvAFX_Run(Handle, inputPointers, outputPointers, (uint)frames, (uint)output.Length);
        }
        finally
        {
            for (int i = 0; i < inputPins.Length; i++)
                if (inputPins[i].IsAllocated) inputPins[i].Free();

            for (int i = 0; i < outputPins.Length; i++)
                if (outputPins[i].IsAllocated) outputPins[i].Free();
        }
    }

    /// <summary>Releases the native handle. Idempotent.</summary>
    public void Dispose()
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero) return;

        _handle = IntPtr.Zero;
        NvAfxNative.NvAFX_DestroyEffect(handle);
    }

    private IntPtr Handle => _handle != IntPtr.Zero
        ? _handle
        : throw new ObjectDisposedException(nameof(NvAfxEffect), $"Effect '{_effectName}' was already disposed.");
}
