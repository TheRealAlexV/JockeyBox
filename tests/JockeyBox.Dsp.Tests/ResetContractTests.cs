namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The contract every insert depends on: after <see cref="IAudioProcessor.Reset"/> a silent
/// block must stay exactly silent, with no state left over to decay into the next block.
/// </summary>
public class ResetContractTests
{
    private const int SampleRate = 48000;

    public static readonly TheoryData<string, Func<IAudioProcessor>> Processors = new()
    {
        { "GainStage", () => new GainStage { GainDb = 12 } },
        { "HighPassFilter", () => new HighPassFilter(SampleRate, frequencyHz: 500) },
        { "LowPassFilter", () => new LowPassFilter(SampleRate, frequencyHz: 2000) },
        { "PeakingEq", () => new PeakingEq(SampleRate, frequencyHz: 1000, q: 1.41, gainDb: 9) },
        { "TenBandEq", TenBandEqWithBoost },
        { "Compressor", () => new Compressor(SampleRate, thresholdDb: -30, ratio: 4, attackMs: 5, releaseMs: 100, kneeDb: 6, makeupDb: 3) },
        { "DeEsser", () => new DeEsser(SampleRate, DeEsserParameters.Neutral with { Enabled = true, ThresholdDb = -40 }) },
        { "NoiseGate", () => new NoiseGate(SampleRate, thresholdDb: -30, hysteresisDb: 3, attackMs: 5, releaseMs: 100, holdMs: 200, floorDb: -60) },
        { "Limiter", () => new Limiter(SampleRate, thresholdDb: -6, lookaheadMs: 5, releaseMs: 50) },
    };

    private static IAudioProcessor TenBandEqWithBoost()
    {
        var eq = new TenBandEq(SampleRate);
        eq.SetGainDb(5, 12);
        return eq;
    }

    [Theory]
    [MemberData(nameof(Processors))]
    public void Reset_makes_the_next_silent_block_exactly_silent(string name, Func<IAudioProcessor> factory)
    {
        IAudioProcessor processor = factory();

        var burst = TestSignals.WhiteNoise(8192, seed: 51);
        processor.Process(burst);
        Assert.Contains(burst, sample => sample != 0f);

        processor.Reset();

        var silence = new float[8192];
        processor.Process(silence);

        for (int i = 0; i < silence.Length; i++)
        {
            Assert.True(silence[i] == 0.0f,
                $"{name} (id '{processor.Id}') emitted {silence[i]:R} at frame {i} after Reset; Reset must leave no decaying tail");
        }
    }
}
