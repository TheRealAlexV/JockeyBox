using System;
using System.IO;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// Proof for the three-band mix EQ, in numbers rather than assertions of intent.
///
/// <para>The load-bearing facts: a centred band is <b>bit-identical</b> to the input
/// (topology 1: bypass, not a unity biquad); LOW is a <b>shelf</b>, so its attenuation
/// survives toward DC where a bell returns to unity; the range is the asymmetric
/// -26…+6 dB; and moving a band gain mid-stream produces no sample-to-sample step
/// larger than the signal's own slope.</para>
/// </summary>
public class EqTests
{
    private const int Rate = 48_000;
    private const int Block = 480;
    private const float Amp = 0.25f;

    private readonly Xunit.Abstractions.ITestOutputHelper _log;

    public EqTests(Xunit.Abstractions.ITestOutputHelper log) => _log = log;

    // ---------------------------------------------------------------- helpers

    private static float[] Sine(int frames, double hz, float amp = Amp, int startFrame = 0)
    {
        var block = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(amp * Math.Sin(2.0 * Math.PI * hz * (startFrame + f) / Rate));
            block[f * 2] = v;
            block[f * 2 + 1] = v;
        }
        return block;
    }

    /// <summary>Run BeginBlock + Process exactly as the engine does for one block.</summary>
    private static void Step(MixEq eq, float[] block, EqPath path = EqPath.DeckA)
    {
        eq.BeginBlock();
        eq.Process(block, 0, Block, path);
    }

    /// <summary>
    /// Steady-state magnitude response in dB at <paramref name="freq"/>, by RMS of a
    /// phase-continuous sine (RMS rather than peak, because 15 kHz at 48 kHz has only
    /// 3.2 samples per cycle and a peak reading would carry up to 1 dB of phase error).
    /// </summary>
    private static double ResponseDb(EqBand band, double gainDb, double freq,
                                     int warmBlocks = 30, int measureBlocks = 100)
    {
        var eq = new MixEq();
        eq.SetTargetDb(band, (float)gainDb);
        for (int i = 0; i < 400; i++) eq.BeginBlock(); // settle the one-pole fully

        double sum = 0.0;
        int n = 0;
        int frame = 0;
        for (int b = 0; b < warmBlocks + measureBlocks; b++)
        {
            var block = Sine(Block, freq, Amp, frame);
            Step(eq, block);
            if (b >= warmBlocks)
                for (int f = 0; f < Block; f++)
                {
                    double y = block[f * 2];
                    sum += y * y;
                    n++;
                }
            frame += Block;
        }

        double rms = Math.Sqrt(sum / n);
        double reference = Amp / Math.Sqrt(2.0);
        return 20.0 * Math.Log10(rms / reference);
    }

    // ------------------------------------------------- 1. bit-transparency

    [Fact]
    public void Centre_IsBitTransparent_NotUnityGainButPhaseShifted()
    {
        var rng = new Random(20261113);
        var input = new float[Block * 2];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        var output = (float[])input.Clone();
        var eq = new MixEq();

        // Ten blocks at the centre detent, both paths, exactly as the engine calls it.
        for (int b = 0; b < 10; b++)
        {
            eq.BeginBlock();
            eq.Process(output, 0, Block, EqPath.DeckA);
            eq.Process(output, 0, Block, EqPath.DeckB);
        }

        Assert.Equal(input, output); // element-wise, exact: no rounding, no phase shift
        Assert.False(eq.IsActive(EqBand.Low));
        Assert.False(eq.IsActive(EqBand.Mid));
        Assert.False(eq.IsActive(EqBand.High));

        // And the range really is the asymmetric -26…+6: a +26 dB request is clamped.
        eq.SetTargetDb(EqBand.Low, 26f);
        Assert.Equal(6f, eq.TargetDb(EqBand.Low));
        eq.SetTargetDb(EqBand.Low, -40f);
        Assert.Equal(-26f, eq.TargetDb(EqBand.Low));
    }

    // ------------------------------------------------- 2. low shelf

    [Fact]
    public void LowBand_IsAShelf_SoTheBassKillKillsTheBass()
    {
        // A peaking bell at 70 Hz with Q 0.6 measures about -12 dB at 30 Hz and about
        // -7 dB at 20 Hz: a bell always returns to unity toward DC. The shelf must hold
        // its attenuation down there or the "kill the bass" position does not exist.
        double at20 = ResponseDb(EqBand.Low, -26, 20);
        double at30 = ResponseDb(EqBand.Low, -26, 30);
        double at40 = ResponseDb(EqBand.Low, -26, 40);
        double at50 = ResponseDb(EqBand.Low, -26, 50);
        double at70 = ResponseDb(EqBand.Low, -26, 70);
        double at1k = ResponseDb(EqBand.Low, -26, 1_000);

        _log.WriteLine($"LOW shelf -26 dB: 20Hz={at20:F2} 30Hz={at30:F2} 40Hz={at40:F2} 50Hz={at50:F2} 70Hz={at70:F2} 1kHz={at1k:F2} dB");

        Assert.True(at20 < -18, $"20 Hz only reached {at20:F1} dB - that is a bell, not a shelf");        Assert.True(at30 < -15, $"30 Hz only reached {at30:F1} dB - that is a bell, not a shelf");
        Assert.True(at40 < -12, $"40 Hz only reached {at40:F1} dB");
        Assert.True(at20 < at30 && at30 < at40 && at40 < at50,
            "a low shelf must attenuate monotonically as frequency falls");
        Assert.True(Math.Abs(at1k) < 0.5, $"1 kHz moved by {at1k:F2} dB, it must be untouched");
    }

    // ------------------------------------------------- 3. mid bell

    [Fact]
    public void MidBand_IsABell_At1k()
    {
        double at70 = ResponseDb(EqBand.Mid, 6, 70);
        double at500 = ResponseDb(EqBand.Mid, 6, 500);
        double at1k = ResponseDb(EqBand.Mid, 6, 1_000);
        double at2k = ResponseDb(EqBand.Mid, 6, 2_000);
        double at8k = ResponseDb(EqBand.Mid, 6, 8_000);
        double at13k = ResponseDb(EqBand.Mid, 6, 13_000);

        _log.WriteLine($"MID bell +6 dB (Q 0.6): 70Hz={at70:F2} 500Hz={at500:F2} 1kHz={at1k:F2} 2kHz={at2k:F2} 8kHz={at8k:F2} 13kHz={at13k:F2} dB");

        Assert.InRange(at1k, 5.6, 6.4);          // the requested +6 dB, at the bell's tip
        Assert.True(Math.Abs(at500 - at1k) < 3.5, $"500 Hz sits {at500 - at1k:F2} dB below the tip");
        Assert.True(Math.Abs(at2k - at1k) < 3.5, $"2 kHz sits {at2k - at1k:F2} dB below the tip");
        Assert.True(Math.Abs(at70) < 0.5, $"70 Hz moved by {at70:F2} dB");
        Assert.True(Math.Abs(at13k) < 0.5, $"13 kHz moved by {at13k:F2} dB");
    }

    // ------------------------------------------------- 4. high shelf

    [Fact]
    public void HighBand_IsAShelf_At13k()
    {
        double at1k = ResponseDb(EqBand.High, -26, 1_000);
        double at5k = ResponseDb(EqBand.High, -26, 5_000);
        double at13k = ResponseDb(EqBand.High, -26, 13_000);
        double at15k = ResponseDb(EqBand.High, -26, 15_000);
        double at20k = ResponseDb(EqBand.High, -26, 20_000);

        _log.WriteLine($"HIGH shelf -26 dB: 1kHz={at1k:F2} 5kHz={at5k:F2} 13kHz={at13k:F2} 15kHz={at15k:F2} 20kHz={at20k:F2} dB");

        Assert.True(at15k < -15, $"15 kHz only reached {at15k:F1} dB");
        Assert.True(at20k < at15k, "a high shelf keeps falling toward Nyquist");
        Assert.True(Math.Abs(at1k) < 0.5, $"1 kHz moved by {at1k:F2} dB, it must be untouched");
        Assert.True(at5k > -12, $"5 kHz at {at5k:F1} dB - the shelf is cutting too far down");
    }

    // ------------------------------------------------- 5. no click

    [Fact]
    public void GainStep_DoesNotClick_AndNeverExceedsTheSignalsOwnSlope()
    {
        // 60 Hz (the band's own territory) riding on 1 kHz (bystander). The 1 kHz
        // component sets the signal's natural sample-to-sample delta; if a coefficient
        // jump added a discontinuity it would show up on top of that.
        var eq = new MixEq();
        double[] partials = { 60, 1_000 };
        const int blocks = 400;
        const int stepBlock = 80;
        const int steadyFrom = 200;

        float[] Mix(int blockIndex, int framesOffset)
        {
            var block = new float[Block * 2];
            for (int f = 0; f < Block; f++)
            {
                int frame = framesOffset + f;
                double v = 0.2 * Math.Sin(2 * Math.PI * partials[0] * frame / Rate)
                         + 0.2 * Math.Sin(2 * Math.PI * partials[1] * frame / Rate);
                block[f * 2] = (float)v;
                block[f * 2 + 1] = (float)v;
            }
            _ = blockIndex;
            return block;
        }

        double baseline = 0.0, transition = 0.0, steady = 0.0;
        float previous = 0f;

        for (int b = 0; b < blocks; b++)
        {
            if (b == stepBlock) eq.SetTargetDb(EqBand.Low, -26f); // the operator grabs the knob
            var block = Mix(b, b * Block);
            Step(eq, block);

            for (int f = 0; f < Block; f++)
            {
                float y = block[f * 2];
                double delta = Math.Abs(y - previous);
                previous = y;
                if (b >= 40 && b < stepBlock) baseline = Math.Max(baseline, delta);
                else if (b >= stepBlock && b < steadyFrom) transition = Math.Max(transition, delta);
                else if (b >= steadyFrom) steady = Math.Max(steady, delta);
            }
        }

        double ceiling = Math.Max(baseline, steady);
        _log.WriteLine($"CLICK: baseline(max delta at 0 dB, browsing)={baseline:F5} transition={transition:F5} steady(-26 dB)={steady:F5} ceiling={ceiling:F5}");
        Assert.True(transition <= ceiling * 1.5,
            $"the gain step produced a {transition:F5} sample step against a {ceiling:F5} steady-state maximum");
        // Absolute sanity: a real click on a 0.4-peak signal would be a visible step.
        Assert.True(transition < 0.05, $"transition step {transition:F5} is audible");
    }

    // ------------------------------------------------- 6. denormals

    [Fact]
    public void DeepCut_WithSilence_LeavesNoDenormalState()
    {
        var eq = new MixEq();
        eq.SetTargetDb(EqBand.Low, -26f);
        eq.SetTargetDb(EqBand.Mid, -26f);
        eq.SetTargetDb(EqBand.High, -26f);

        // Drive real signal through every band, so the states are non-zero.
        int frame = 0;
        for (int b = 0; b < 40; b++)
        {
            var block = Sine(Block, 100, 0.5f, frame);
            Step(eq, block);
            frame += Block;
        }
        Assert.True(eq.StateMagnitude(EqBand.Low, EqPath.DeckA) > 0f);

        // Then silence: a TDF2 state fed zero decays to zero, and anything that would
        // land in the float denormal range is flushed by the threshold.
        for (int b = 0; b < 200; b++)
        {
            Step(eq, new float[Block * 2]);
            Step(eq, new float[Block * 2], EqPath.DeckB);
        }

        foreach (EqBand band in new[] { EqBand.Low, EqBand.Mid, EqBand.High })
            foreach (EqPath path in new[] { EqPath.DeckA, EqPath.DeckB })
                Assert.Equal(0f, eq.StateMagnitude(band, path));

        // And the flush itself: a sub-threshold signal must not accumulate state.
        var tiny = new float[Block * 2];
        for (int i = 0; i < tiny.Length; i++) tiny[i] = 1e-18f;
        for (int b = 0; b < 10; b++) Step(eq, tiny);
        Assert.Equal(0f, eq.StateMagnitude(EqBand.Low, EqPath.DeckA));
    }

    // ------------------------------------------------- paths are independent

    [Fact]
    public void EachDeckPath_KeepsItsOwnState()
    {
        var eq = new MixEq();
        eq.SetTargetDb(EqBand.Low, -26f);
        for (int i = 0; i < 400; i++) eq.BeginBlock();

        var loud = Sine(Block, 60, 0.5f);
        Step(eq, loud, EqPath.DeckA);
        float deckA = eq.StateMagnitude(EqBand.Low, EqPath.DeckA);
        float deckB = eq.StateMagnitude(EqBand.Low, EqPath.DeckB);

        Assert.True(deckA > 0f);
        Assert.Equal(0f, deckB); // deck B never saw a sample
    }
}

/// <summary>
/// The mix EQ has to survive a restart: the operator sets a bass cut, closes the app,
/// and expects the cut still to be there. The keys are new, so an existing settings
/// file simply has no value and must load as the 0 dB bypass.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public class EqSettingsTests
{
    [Fact]
    public void EqSettings_ClampOnLoad_AndRoundTrip()
    {
        string home = Path.Combine(Path.GetTempPath(), "jb-eq-settings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            // A hand-edited file: two bands out of range, one inside it.
            File.WriteAllText(AppSettings.SettingsPath, """
                { "OutputDelayMs": 175, "EqLowDb": -60, "EqMidDb": 99, "EqHighDb": 3.5 }
                """);

            var settings = AppSettings.Load();
            Assert.Equal(-26.0, settings.EqLowDb);   // clamped to the cut limit
            Assert.Equal(6.0, settings.EqMidDb);     // clamped to the boost limit
            Assert.Equal(3.5, settings.EqHighDb);    // inside the window, untouched
            Assert.Equal(175, settings.OutputDelayMs); // neighbours survive

            settings.Save();
            var reloaded = AppSettings.Load();
            Assert.Equal(-26.0, reloaded.EqLowDb);
            Assert.Equal(6.0, reloaded.EqMidDb);
            Assert.Equal(3.5, reloaded.EqHighDb);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void MissingEqKeys_MeanCentreBypass()
    {
        string home = Path.Combine(Path.GetTempPath(), "jb-eq-settings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);
            File.WriteAllText(AppSettings.SettingsPath, """{ "OutputDelayMs": 40 }""");

            var settings = AppSettings.Load();
            Assert.Equal(0.0, settings.EqLowDb);
            Assert.Equal(0.0, settings.EqMidDb);
            Assert.Equal(0.0, settings.EqHighDb);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }
}
