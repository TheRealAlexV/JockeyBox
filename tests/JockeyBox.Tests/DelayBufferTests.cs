using JockeyBox.Audio.Graph;

namespace JockeyBox.Tests;

/// <summary>
/// The program-path delay is the A/V offset the audience hears, so it must be
/// sample-exact (not "about right") at every delay including the ring edge, must
/// not mix channels, and must not click when the offset is changed live.
/// </summary>
public class DelayBufferTests
{
    private const int Rate = 48_000;
    private const int Block = 480;

    /// <summary>frame index -&gt; a unique, exactly representable float (1:1 for |v| &lt; 2^24).</summary>
    private static float FrameValue(int frame) => 1_000f + frame;

    private static float[] Ramp(int frames, int channels = 1, int start = 0)
    {
        var block = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                block[f * channels + c] = FrameValue(start + f) * (c == 0 ? 1f : -1f);
        return block;
    }

    // ----------------------------------------------------------- Zero delay

    [Fact]
    public void ReadDelayed_WithZeroDelay_ReturnsTheBlockThatWasJustWritten()
    {
        var delay = new DelayBuffer(Rate, 2);
        var block = Ramp(Block, 2);
        var output = new float[block.Length];

        delay.Write(block, 0, Block);
        delay.ReadDelayed(output, 0, Block);

        Assert.Equal(block, output);
        Assert.Equal(0, delay.DelayFrames);
        Assert.False(delay.IsCrossfading);
    }

    [Fact]
    public void ReadDelayed_ReturnsTheCurrentBlockAtEveryOffset_NotJustTheFirst()
    {
        var delay = new DelayBuffer(Rate, 2);

        for (int b = 0; b < 8; b++)
        {
            var block = Ramp(Block, 2, b * Block);

            delay.Write(block, 0, Block);
            var output = new float[block.Length];
            delay.ReadDelayed(output, 0, Block);

            Assert.Equal(block, output);
        }
    }

    // ------------------------------------------------------- Whole-frame delay

    [Fact]
    public void ReadDelayed_AtNDelay_ShiftsTheBlockByExactlyNFrames_PerChannel()
    {
        // 100 ms == 4800 frames at 48 kHz, an exact integer, so this is sample-exact.
        const int delayFrames = 4_800;
        var delay = new DelayBuffer(Rate, 2, initialDelayMs: 100);
        Assert.Equal(delayFrames, delay.DelayFrames);

        int blocks = 14; // 6720 frames: 4800 of silence, then real delayed audio
        for (int b = 0; b < blocks; b++)
        {
            var block = Ramp(Block, 2, b * Block);
            delay.Write(block, 0, Block);

            var output = new float[Block * 2];
            delay.ReadDelayed(output, 0, Block);

            for (int f = 0; f < Block; f++)
            {
                int source = b * Block + f - delayFrames;
                for (int c = 0; c < 2; c++)
                {
                    float expected = source >= 0 ? FrameValue(source) * (c == 0 ? 1f : -1f) : 0f;
                    Assert.Equal(expected, output[f * 2 + c]);
                }
            }
        }
    }

    [Fact]
    public void ReadDelayed_HonoursTheDstOffset()
    {
        var delay = new DelayBuffer(Rate, 1, initialDelayMs: 0);
        var block = Ramp(Block, 1);
        delay.Write(block, 0, Block);

        var output = new float[Block + 16];
        Array.Fill(output, 9f);
        delay.ReadDelayed(output, 8, Block);

        for (int f = 0; f < Block; f++) Assert.Equal(block[f], output[8 + f]);
        for (int i = 0; i < 8; i++) Assert.Equal(9f, output[i]);
        for (int i = 8 + Block; i < output.Length; i++) Assert.Equal(9f, output[i]);
    }

    // ----------------------------------------------------------- Ring edge

    [Fact]
    public void ReadDelayed_AtTheMaximumDelay_IsSampleExactForEveryFrame_NoWrapCorruption()
    {
        // 2001 ms of unique per-frame values read back at the 2000 ms tap: the
        // tap sits 96 000 frames behind the write pointer, which is the deepest
        // the ring is ever asked to hold.
        const int delayFrames = Rate * 2000 / 1000; // 96 000
        const int totalFrames = 2001 * Rate / 1000; // 96 048

        var delay = new DelayBuffer(Rate, 1, initialDelayMs: DelayBuffer.MaxDelayMs);
        Assert.Equal(delayFrames, delay.DelayFrames);

        int written = 0;
        while (written < totalFrames)
        {
            int frames = Math.Min(Block, totalFrames - written);
            var block = Ramp(frames, 1, written);
            delay.Write(block, 0, frames);
            written += frames;

            var output = new float[frames];
            delay.ReadDelayed(output, 0, frames);

            for (int f = 0; f < frames; f++)
            {
                int source = written - frames + f - delayFrames;
                float expected = source >= 0 ? FrameValue(source) : 0f;
                Assert.Equal(expected, output[f]);
            }
        }

        Assert.Equal(totalFrames, written);
    }

    // -------------------------------------------------------- Live changes

    [Fact]
    public void SetDelayMs_MidStream_DoesNotClick()
    {
        // PROTECTS: "moving the tap would click, so the old and new taps are
        // crossfaded". A live offset change must not introduce a step in the
        // output larger than the signal's own sample-to-sample step.
        //
        // KNOWN GAP (fails against current source): ReadDelayed computes the
        // crossfade coefficient once per *block*, outside the frame loop, so a
        // 50 ms fade at 480 frames/block is a 5-step staircase that jumps by
        // ~20 % of the tap difference at each block boundary. Measured here:
        // input max step 0.022, output max step ~0.107 (4.8x).
        var delay = new DelayBuffer(Rate, 1);
        const double amplitude = 0.5;
        const double frequency = 337.0;

        var input = new double[20 * Block];
        for (int n = 0; n < input.Length; n++)
            input[n] = amplitude * Math.Sin(2.0 * Math.PI * frequency * n / Rate);

        var output = new double[input.Length];
        const int changeAtBlock = 10;

        for (int b = 0; b < 20; b++)
        {
            var block = new float[Block];
            for (int f = 0; f < Block; f++) block[f] = (float)input[b * Block + f];

            delay.Write(block, 0, Block);
            if (b == changeAtBlock) delay.SetDelayMs(50);

            var delayed = new float[Block];
            delay.ReadDelayed(delayed, 0, Block);
            for (int f = 0; f < Block; f++) output[b * Block + f] = delayed[f];
        }

        double maxInputStep = 0;
        for (int n = 1; n < input.Length; n++)
            maxInputStep = Math.Max(maxInputStep, Math.Abs(input[n] - input[n - 1]));

        int changeAt = changeAtBlock * Block;
        int end = changeAt + 10 * Block; // well past the 50 ms crossfade
        double maxOutputStep = 0;
        int worst = -1;
        for (int n = changeAt + 1; n < end; n++)
        {
            double step = Math.Abs(output[n] - output[n - 1]);
            if (step > maxOutputStep) { maxOutputStep = step; worst = n; }
        }

        Assert.True(maxOutputStep <= maxInputStep + 1e-6,
            $"click: output step {maxOutputStep:F6} at frame {worst} exceeds the input's own {maxInputStep:F6} " +
            $"({maxOutputStep / maxInputStep:F1}x) - the crossfade is block-quantised");
    }

    [Fact]
    public void SetDelayMs_AfterFiftyMilliseconds_TheOutputIsExactlyTheNewTap()
    {
        // The crossfade is 50 ms == 2400 frames; from then on the old tap must be
        // gone from the maths, not merely attenuated.
        const int newDelayMs = 50;
        const int newDelayFrames = Rate * newDelayMs / 1000; // 2400
        const int crossfadeFrames = 2_400;

        var delay = new DelayBuffer(Rate, 1);
        int written = 0;
        bool changed = false;
        const int changeAtFrame = 4 * Block; // the block whose read starts the fade

        for (int b = 0; b < 14; b++)
        {
            var block = Ramp(Block, 1, written);
            delay.Write(block, 0, Block);
            written += Block;

            if (b == 4)
            {
                delay.SetDelayMs(newDelayMs);
                changed = true;
                Assert.True(delay.IsCrossfading);
            }

            var output = new float[Block];
            delay.ReadDelayed(output, 0, Block);

            for (int f = 0; f < Block; f++)
            {
                int g = b * Block + f;
                if (!changed || g < changeAtFrame + crossfadeFrames) continue;
                Assert.Equal(FrameValue(g - newDelayFrames), output[f]);
            }
        }

        Assert.True(changed, "the delay change never happened");
        Assert.False(delay.IsCrossfading, "crossfade did not finish after 50 ms");
        Assert.Equal(newDelayFrames, delay.DelayFrames);
        Assert.Equal(newDelayMs, delay.DelayMs);
    }

    [Fact]
    public void SetDelayMs_ToTheSameValue_IsANoOp()
    {
        var delay = new DelayBuffer(Rate, 2, initialDelayMs: 40);
        Assert.Equal(40, delay.DelayMs);

        delay.SetDelayMs(40);
        Assert.False(delay.IsCrossfading);

        delay.SetDelayMs(41);
        Assert.True(delay.IsCrossfading);
        Assert.Equal(41, delay.DelayMs);
    }

    [Fact]
    public void SetDelayMs_ClampsToTheSupportedRange()
    {
        var delay = new DelayBuffer(Rate, 1);
        delay.SetDelayMs(-500);
        Assert.Equal(0, delay.DelayFrames);

        delay.SetDelayMs(999_999);
        Assert.Equal(DelayBuffer.MaxDelayMs, delay.DelayMs);
        Assert.Equal(Rate * DelayBuffer.MaxDelayMs / 1000, delay.DelayFrames);
    }

    // ----------------------------------------------------- Stereo integrity

    [Fact]
    public void Channels_AreNeverMixed_LAndRDivergeIndependently()
    {
        const int delayFrames = 4_800; // 100 ms
        var delay = new DelayBuffer(Rate, 2, initialDelayMs: 100);

        for (int b = 0; b < 14; b++)
        {
            var block = new float[Block * 2];
            for (int f = 0; f < Block; f++)
            {
                int g = b * Block + f;
                block[f * 2] = FrameValue(g);        // L rises
                block[f * 2 + 1] = -3f * FrameValue(g); // R falls, different scale
            }

            delay.Write(block, 0, Block);
            var output = new float[Block * 2];
            delay.ReadDelayed(output, 0, Block);

            for (int f = 0; f < Block; f++)
            {
                int source = b * Block + f - delayFrames;
                float left = source >= 0 ? FrameValue(source) : 0f;
                float right = source >= 0 ? -3f * FrameValue(source) : 0f;

                Assert.Equal(left, output[f * 2]);
                Assert.Equal(right, output[f * 2 + 1]);
            }
        }
    }

    [Fact]
    public void Reset_ForgetsHistoryAndCancelsAnyCrossfade()
    {
        var delay = new DelayBuffer(Rate, 1);
        delay.Write(Ramp(Block, 1), 0, Block);
        delay.SetDelayMs(100);
        Assert.True(delay.IsCrossfading);

        delay.Reset();

        Assert.False(delay.IsCrossfading);

        // Everything read immediately after a reset is silence, not stale audio.
        var output = new float[Block];
        Array.Fill(output, 1f);
        delay.ReadDelayed(output, 0, Block);
        Assert.All(output, sample => Assert.Equal(0f, sample));

        // ...and the new tap is applied immediately rather than fading in from
        // the old one: with the new audio at the 100 ms tap and silence at the
        // old (0 ms) tap, a leftover crossfade would show up as a blend.
        var fresh = Ramp(Block, 1, start: 0);
        delay.Write(fresh, 0, Block);
        delay.Write(new float[4_800], 0, 4_800);

        var after = new float[Block];
        delay.ReadDelayed(after, 0, Block);
        Assert.Equal(fresh, after);
    }

    [Fact]
    public void WriteAndRead_BlockSizeIsAFrameCount_NotASampleCount()
    {
        // A stereo Write(.., frames: 2) consumes 4 floats; an off-by-channel bug
        // shows up immediately as the wrong frames landing in the ring.
        var delay = new DelayBuffer(Rate, 2);
        var block = new[] { 1f, -1f, 2f, -2f };
        delay.Write(block, 0, frames: 2);

        var output = new float[4];
        delay.ReadDelayed(output, 0, frames: 2);
        Assert.Equal(block, output);
    }
}
