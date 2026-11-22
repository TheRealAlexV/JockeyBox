namespace JockeyBox.Dsp.Tests;

public class GainStageTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Unity_gain_leaves_the_buffer_bit_exact()
    {
        var stage = new GainStage();

        var input = TestSignals.WhiteNoise(4096, seed: 31);
        var output = (float[])input.Clone();

        stage.Process(output);

        TestSignals.AssertBitExact(input, output, "unity gain must not change a single bit");
    }

    [Fact]
    public void Half_gain_halves_amplitude_after_the_ramp()
    {
        var stage = new GainStage { GainDb = 20 * Math.Log10(0.5) };

        var input = TestSignals.Sine(4096, 1000, SampleRate, 0.5);
        var output = (float[])input.Clone();

        stage.Process(output);

        // The 64-frame ramp finishes long before sample 128.
        for (int i = 128; i < output.Length; i++)
        {
            double expected = input[i] * 0.5;
            Assert.True(Math.Abs(output[i] - expected) <= 1e-6,
                $"sample {i}: {output[i]:R} != {expected:R} (half of {input[i]:R})");
        }
    }

    [Fact]
    public void Reset_clears_the_ramp_and_keeps_the_configured_gain()
    {
        // A 480-frame ramp, warmed up for only 100 frames, is still mid-flight.
        var stage = new GainStage(rampFrames: 480) { GainDb = -6 };
        var warmUp = TestSignals.Sine(100, 1000, SampleRate, 0.5);
        stage.Process(warmUp);

        stage.Reset();

        var block = TestSignals.Sine(64, 1000, SampleRate, 0.5);
        var output = (float[])block.Clone();
        stage.Process(output);

        double target = Math.Pow(10, -6 / 20.0);
        Assert.Equal(-6.0, stage.GainDb);
        Assert.True(Math.Abs(output[0] - block[0] * target) <= 1e-6,
            $"the first sample after Reset should already carry the target gain: {output[0]:R} vs {block[0] * target:R}");
    }
}
