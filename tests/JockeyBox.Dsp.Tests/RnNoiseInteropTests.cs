using JockeyBox.Dsp.RnNoise;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// Skips unless the vendored <c>rnnoise.dll</c> loads. Mirrors
/// <c>WindowsRuntimeFactAttribute</c> — the skip is set at discovery, which is the only form the
/// xunit 2.5 VSTest runner reports as Skipped — but keyed to this library's own capability
/// rather than to <c>NVAFX_SDK_DIR</c>, so the probe is <b>not</b> skipped on Windows just
/// because the NVIDIA SDK is absent.
/// </summary>
public sealed class RnNoiseRuntimeFactAttribute : FactAttribute
{
    public RnNoiseRuntimeFactAttribute()
    {
        if (!RnNoiseRuntime.IsAvailable)
            Skip = "rnnoise.dll is a Windows x64 runtime that cannot load here; " +
                   "this probe only proves the binding on the rig.";
    }
}

/// <summary>
/// Probes against the real vendored library.
/// <para>
/// <b>Behavioural probes drive <see cref="RnNoiseStage"/>, the shipping path.</b> Only
/// <see cref="Frame_size_is_queried_and_is_480"/>, which is the native contract itself, drives
/// the engine directly.
/// </para>
/// <para>
/// <b>Every energy assertion names its signal class from <see cref="RnNoiseSignals"/>, and the
/// class must be one the network is known to transform that way.</b> A noise-like input supports
/// an attenuation bound; a periodic/formant input is passed and must never carry one. Signals
/// that are wrong for the assertion, not code, caused three gate failures.
/// </para>
/// </summary>
public class RnNoiseInteropTests
{
    private const int FrameSize = 480;

    private static RnNoiseStage EnabledStage()
    {
        var stage = new RnNoiseStage();
        stage.Apply(new NoiseSuppressionParameters(Enabled: true, Mix: 1.0));
        return stage;
    }

    /// <summary>Runs whole 480-frame blocks, the way the engine drives the strip.</summary>
    private static float[] Run(RnNoiseStage stage, float[] input)
    {
        var output = (float[])input.Clone();
        for (int offset = 0; offset + FrameSize <= output.Length; offset += FrameSize)
        {
            stage.Process(output.AsSpan(offset, FrameSize));
        }

        return output;
    }

    private static double Energy(ReadOnlySpan<float> samples, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)samples[i] * samples[i];
        return sum;
    }

    private static (int Lag, double Correlation) BestLag(
        float[] output, float[] input, int maxLag)
    {
        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double num = 0, oo = 0, ii = 0;
            for (int i = 0; i < output.Length; i++)
            {
                int j = i - lag;
                if (j < 0 || j >= input.Length) continue;
                num += (double)output[i] * input[j];
                oo += (double)output[i] * output[i];
                ii += (double)input[j] * input[j];
            }

            double score = num / Math.Sqrt(oo * ii + 1e-30);
            if (score > best)
            {
                best = score;
                bestLag = lag;
            }
        }

        return (bestLag, best);
    }

    [RnNoiseRuntimeFact]
    public void Frame_size_is_queried_and_is_480()
    {
        // No signal: the one raw-engine probe tests the native contract, not stage behaviour.
        using var engine = new RnNoiseEngine();
        Assert.Equal(480, engine.FrameSize);
        Assert.Equal(960, engine.LatencySamples);
    }

    [RnNoiseRuntimeFact]
    public void Noise_only_input_is_attenuated_by_a_wide_margin()
    {
        // Signal class: Suppressive (noise-like). Supports the attenuation bound by measurement
        // (-68.94 dB at int16 scale); the stage takes [-1,1] and scales internally.
        const int blocks = 54;
        float[] input = RnNoiseSignals.Suppressive(blocks, seed: 100);
        float[] output = Run(EnabledStage(), input);

        // Skip the four frames that cover the delay and the network's start-up ramp.
        int from = 4 * FrameSize;
        double wetDb = 10 * Math.Log10(Energy(output, from, output.Length) / Energy(input, from, input.Length));

        // Facts doc §6 measured -32.15 dB on noise with no voice; this build measured -68.9 dB
        // at int16 scale. -20 dB is a wide margin a working denoiser clears and an inert one
        // cannot: a passthrough reads 0 dB.
        Assert.True(wetDb <= -20, $"noise-only attenuation was {wetDb:F2} dB, expected <= -20 dB");

        // Control: the same signal through a neutral stage is bit-identical. If the metric were
        // broken this would not read 0 dB, and the bound above would be vacuous.
        float[] dry = Run(new RnNoiseStage(), input);
        double dryDb = 10 * Math.Log10(Energy(dry, from, dry.Length) / Energy(input, from, input.Length));
        Assert.True(dryDb > -1, $"a neutral path should be ~0 dB, measured {dryDb:F2} dB");
    }

    [RnNoiseRuntimeFact]
    public void In_place_processing_matches_separate_buffers_and_is_not_inert()
    {
        // Signal class: Suppressive (noise-like), converted to int16 for the raw engine. The
        // class supports both halves: determinism for bit-exactness, and removed energy for the
        // inertness bound.
        float[] signal = RnNoiseSignals.ToInt16(RnNoiseSignals.Suppressive(20, seed: 500));

        using var inPlace = new RnNoiseEngine();
        using var separate = new RnNoiseEngine();

        var input = new float[inPlace.FrameSize];
        var output = new float[inPlace.FrameSize];
        double inSq = 0, outSq = 0;

        for (int block = 0; block < 20; block++)
        {
            Array.Copy(signal, block * FrameSize, input, 0, FrameSize);

            var a = (float[])input.Clone();
            inPlace.ProcessFrame(a);
            separate.ProcessFrame(input, output);

            TestSignals.AssertBitExact(a, output, $"frame {block}: in-place and separate buffers must agree");

            for (int i = 0; i < input.Length; i++)
            {
                inSq += (double)input[i] * input[i];
                outSq += (double)a[i] * a[i];
            }
        }

        // Two pass-throughs are also bit-identical; only the energy drop proves the network ran.
        Assert.True(outSq < 0.1 * inSq, $"the engine removed no energy (out/in {10 * Math.Log10(outSq / inSq):F2} dB)");
    }

    [RnNoiseRuntimeFact]
    public void Two_fresh_states_produce_identical_output_and_are_not_inert()
    {
        // Signal class: Suppressive (noise-like), converted to int16. Chosen over splitting the
        // test: the generator is deterministic, so bit-exactness stays meaningful, and the same
        // class supports the energy bound. The old periodic ramp read as voiced and passed at
        // 0.00 dB, which was correct behaviour and a wrong assertion.
        float[] signal = RnNoiseSignals.ToInt16(RnNoiseSignals.Suppressive(12, seed: 600));

        using var first = new RnNoiseEngine();
        using var second = new RnNoiseEngine();

        var a = new float[first.FrameSize];
        var b = new float[first.FrameSize];
        double inSq = 0, outSq = 0;

        for (int block = 0; block < 12; block++)
        {
            Array.Copy(signal, block * FrameSize, a, 0, FrameSize);
            Array.Copy(a, b, a.Length);

            // The input term comes from the source, before either state touches it: a and b are
            // both outputs once the frames below run, so summing either as "input" compares the
            // output with itself and reads 0.00 dB for any signal.
            for (int i = 0; i < FrameSize; i++)
            {
                inSq += (double)signal[block * FrameSize + i] * signal[block * FrameSize + i];
            }

            first.ProcessFrame(a);
            second.ProcessFrame(b);

            TestSignals.AssertBitExact(a, b, $"block {block}: two fresh states diverged");

            for (int i = 0; i < a.Length; i++)
            {
                outSq += (double)b[i] * b[i];
            }
        }

        Assert.True(outSq < 0.1 * inSq, $"the engine removed no energy (out/in {10 * Math.Log10(outSq / inSq):F2} dB)");
    }

    [RnNoiseRuntimeFact]
    public void Latency_is_measured_by_cross_correlation()
    {
        // Signal class: Voiced (periodic/formant). Supports the correlation because the network
        // passes it; no energy bound is asserted here, and none may be.
        const int blocks = 60;
        int total = blocks * FrameSize;
        float[] vowel = RnNoiseSignals.Voiced(total, amplitude: 0.6f);

        // Prime the stage's delay line from silence before the measured span.
        var input = new float[FrameSize * 6 + total];
        Array.Copy(vowel, 0, input, FrameSize * 6, total);

        using var stage = EnabledStage();
        float[] output = Run(stage, input);

        var (lag, correlation) = BestLag(output, input, maxLag: 2400);

        // The jittered vowel measures 0.988 at lag 960 on the rig.
        Assert.True(correlation > 0.9,
            $"the latency correlation is too weak to trust ({correlation:F3}); the signal was not passed");

        // The engine's measured delay, not a reasoned zero. An identity stage would report lag
        // 0; an extra buffered frame would report 1440. Both fail here.
        Assert.Equal(960, stage.LatencyFrames);
        Assert.Equal(stage.LatencyFrames, lag);
    }
}
