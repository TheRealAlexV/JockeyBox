using System.Runtime.InteropServices;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The bypass contract for a whole channel strip: a processor constructed at its neutral
/// value, and left un-bypassed, must leave the buffer bit-identical. Zero tolerance —
/// this is the property the mixer's <c>if (!chain.IsPassthrough)</c> fast path relies on.
/// </summary>
public class NeutralTransparencyTests
{
    private const int SampleRate = 48000;

    private static string Describe(float[] input, float[] output)
    {
        int differing = 0;
        int first = -1;
        double maxAbs = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i].Equals(output[i])) continue;
            differing++;
            if (first < 0) first = i;
            double delta = Math.Abs((double)output[i] - input[i]);
            if (delta > maxAbs) maxAbs = delta;
        }

        return differing == 0
            ? "bit-identical"
            : $"{differing}/{input.Length} samples differ, max |delta| = {maxAbs:E3}, " +
              $"first at index {first}: in {input[first]:G9} -> out {output[first]:G9}";
    }

    private static void AssertTransparent(IAudioProcessor processor, string what)
    {
        Assert.False(processor.Bypass, $"{what} is supposed to be un-bypassed for this check");

        var signals = new (string Name, float[] Samples)[]
        {
            ("white noise", TestSignals.WhiteNoise(8192, seed: 21)),
            ("1 kHz sine", TestSignals.Sine(8192, 1000, SampleRate, 0.5)),
            ("30 Hz sine", TestSignals.Sine(8192, 30, SampleRate, 0.5)),
            ("quiet sine (-60 dBFS)", TestSignals.Sine(8192, 440, SampleRate, 0.001)),
            ("silence", new float[512]),
        };

        foreach ((string name, float[] input) in signals)
        {
            float[] output = (float[])input.Clone();
            processor.Reset();
            processor.Process(output);

            Assert.True(
                MemoryMarshal.AsBytes(input.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(output.AsSpan())),
                $"{what} is not bit-transparent for {name}: {Describe(input, output)}");
        }
    }

    [Fact]
    public void Flat_ten_band_eq_is_bit_transparent() =>
        AssertTransparent(new TenBandEq(SampleRate), "flat 10-band EQ");

    [Fact]
    public void High_pass_at_20Hz_is_bit_transparent() =>
        AssertTransparent(new HighPassFilter(SampleRate, 20), "high-pass at 20 Hz");

    [Fact]
    public void Low_pass_at_20kHz_is_bit_transparent() =>
        AssertTransparent(new LowPassFilter(SampleRate, 20000), "low-pass at 20 kHz");

    [Fact]
    public void Gate_at_neutral_is_bit_transparent() =>
        // A downward expander at -40 dB / -60 dB is *not* unity below the threshold by
        // definition (that is the gate's job), so the neutral strip carries it switched off.
        // The transparent case for an un-bypassed gate is a unity floor, which is what this
        // checks: the guard in NoiseGate.Process must leave the buffer alone.
        AssertTransparent(new NoiseGate(SampleRate, thresholdDb: -40, floorDb: 0), "gate with a 0 dB floor");

    [Fact]
    public void De_esser_at_neutral_is_bit_transparent() =>
        // Unlike the gate, the de-esser cannot be transparent while armed: the split-band
        // reconstruction (low + gain * (x - low)) rounds in the last bit even at unity gain.
        // Its neutral state is therefore "disabled", and Process returns before touching the
        // buffer, which is the guard this checks.
        AssertTransparent(new DeEsser(SampleRate), "de-esser at neutral");

    [Fact]
    public void Whole_neutral_chain_is_bit_transparent()
    {
        // Every processor the milestone's neutral strip owns, constructed and left on.
        // The gate sits at a unity floor here for the reason above; StripParameters.Neutral
        // switches it off instead.
        var chain = new StripChain()
            .Add(new TenBandEq(SampleRate))
            .Add(new HighPassFilter(SampleRate, 20))
            .Add(new LowPassFilter(SampleRate, 20000))
            .Add(new NoiseGate(SampleRate, thresholdDb: -40, floorDb: 0))
            .Add(new DeEsser(SampleRate));

        var input = TestSignals.WhiteNoise(8192, seed: 22);
        var output = (float[])input.Clone();

        chain.Process(output);

        Assert.True(
            MemoryMarshal.AsBytes(input.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(output.AsSpan())),
            $"the neutral chain is not bit-transparent: {Describe(input, output)}");
    }
}
