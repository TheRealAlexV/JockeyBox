using JockeyBox.Dsp.NvAfx;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless the NVIDIA runtime is reachable:
/// Windows, with <c>NVAFX_SDK_DIR</c> set and pointing at an existing directory.
/// <para>
/// The decision is made at discovery, not inside the test body, because xunit 2.5's VSTest
/// runner does not honour a runtime skip — <c>Xunit.Sdk.SkipException</c> arrives as a failure
/// with a <c>$XunitDynamicSkip$</c> prefix. Setting <see cref="FactAttribute.Skip"/> here is the
/// one form the runner reports as Skipped, and it needs no package the project lacks.
/// </para>
/// </summary>
public sealed class WindowsRuntimeFactAttribute : FactAttribute
{
    public WindowsRuntimeFactAttribute()
    {
        if (!NvAfxRuntime.IsAvailable)
            Skip = "NVAudioEffects.dll is a Windows x64 runtime and NVAFX_SDK_DIR is not set here; " +
                   "this probe only proves the binding on the rig.";
    }
}

/// <summary>
/// A probe against the installed NVIDIA Audio Effects runtime, not a unit test: it proves the
/// P/Invoke shapes in <see cref="NvAfxRuntime"/> match the DLL, which cannot be known from a
/// header because the SDK ships none.
/// <para>
/// Skipped unless the runtime is actually reachable (Windows with <c>NVAFX_SDK_DIR</c> set and
/// present), so the DSP suite stays green on Linux. A skip is neither a pass nor a failure: the
/// binding is unproven until this file runs on the rig.
/// </para>
/// </summary>
public class NvAfxInteropTests
{
    /// <summary>The simplest measured effect: 1 in, 1 out, 480-frame blocks at 48 kHz.</summary>
    private const string EffectName = "denoiser";

    private const uint SampleRate = 48000;

    /// <summary>The models' own readme says "10 milliseconds framesize": 48000 / 100.</summary>
    private const int BlockFrames = 480;

    [WindowsRuntimeFact]
    public void Effect_list_enumerates_the_installed_runtime()
    {
        var status = NvAfxRuntime.GetEffectList(out string[] effects);

        status.EnsureOk("NvAFX_GetEffectList");
        Assert.NotEmpty(effects);
        Assert.Contains(EffectName, effects);
    }

    [WindowsRuntimeFact]
    public void Denoiser_loads_the_requested_model_and_runs_a_480_frame_block()
    {
        string modelPath = Path.Combine(NvAfxRuntime.SdkDirectory!, "models", "denoiser_48k.trtpkg");
        Assert.True(File.Exists(modelPath), $"model not found at '{modelPath}'");

        using var effect = NvAfxRuntime.CreateEffect(EffectName);

        effect.SetU32("sample_rate", SampleRate).EnsureOk("SetU32(sample_rate)");

        // Channel count is derived from the effect name; aec returns 3 for this and is still
        // right at 2-in/1-out. The denoiser is 1-in/1-out and never needs it set.
        effect.SetString("model_path", modelPath).EnsureOk("SetString(model_path)");

        // LoadVerified is the only accepted proof of load: it reads model_path back through
        // GetString, which is the one call the runtime demonstrably answers with real data.
        effect.LoadVerified(modelPath);

        var frameStatus = effect.GetU32("num_samples_per_frame", out uint frameSamples);
        frameStatus.EnsureOk("GetU32(num_samples_per_frame)");
        Assert.Equal((uint)BlockFrames, frameSamples);

        var input = new float[BlockFrames];
        var output = new float[BlockFrames];

        // A status code alone is not proof that anything was processed: measured on the rig,
        // a 480-frame block of pure zeros returns 0, and so do the first blocks of a real
        // signal, because the runtime delays the stream by 3360 frames (70 ms at 48 kHz;
        // measured by impulse injection at block 256 index 240 emerging at block 263 index 240,
        // and confirmed by cross-correlation, which peaks at exactly 3360 frames).
        //
        // So the probe drives a 1 kHz tone at amplitude 0.5 and asserts the output carries
        // signal, not just a success code. 32 blocks is comfortably past the 7-block delay.
        //
        // Threshold 1e-3 per-block RMS, from the measured levels rather than taste: the rig
        // puts the transient peak at 0.0131 (block 9, i.e. after the 70 ms delay plus a block
        // of gate settling), and the deepest steady-state level at 1.4e-4 once the denoiser has
        // learned the tone is noise. 1e-3 is 13x below the measured peak, so a working model
        // clears it, and 7x above the deepest steady-state value, so leakage cannot reach it.
        // A block of zeros produces exactly 0, which fails by three orders of magnitude.
        const double LoudestBlockRmsCeiling = 1e-3;
        const int WarmBlocks = 32;

        double loudestBlockRms = 0;
        double phase = 0;
        double phaseStep = 2.0 * Math.PI * 1000.0 / SampleRate;

        for (int block = 0; block < WarmBlocks; block++)
        {
            for (int i = 0; i < BlockFrames; i++)
            {
                input[i] = (float)(0.5 * Math.Sin(phase));
                phase += phaseStep;
            }

            // The native call made by NvAfxEffect.Run has five arguments. The four-argument form
            // (handle, in[], out[], numSamples) is a trap, not an alternative: measured on a
            // loaded handle it returns 1 on the first call and 3 on the next four, while the
            // five-argument form returns 0 five times on the same handle afterwards. The error
            // code depends on call order, so neither "it always returns 1" nor "it always
            // returns 3" describes it. No four-argument overload exists and none must be added.
            var runStatus = effect.Run(new[] { input }, new[] { output }, BlockFrames);
            Assert.Equal(NvAfxStatus.Ok, runStatus);

            double sumSquares = 0;
            for (int i = 0; i < BlockFrames; i++)
                sumSquares += (double)output[i] * output[i];

            loudestBlockRms = Math.Max(loudestBlockRms, Math.Sqrt(sumSquares / BlockFrames));
        }

        Assert.True(
            loudestBlockRms >= LoudestBlockRmsCeiling,
            $"denoiser produced no signal on a {WarmBlocks}-block 1 kHz tone: loudest per-block RMS " +
            $"{loudestBlockRms:G4} is below {LoudestBlockRmsCeiling:G4}, so this is the silence case " +
            "that a status-code-only assertion cannot see.");
    }

}
