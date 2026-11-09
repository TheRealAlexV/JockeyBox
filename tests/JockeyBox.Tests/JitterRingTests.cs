using JockeyBox.Audio.Endpoints;

namespace JockeyBox.Tests;

/// <summary>
/// The ring between the engine and one WASAPI device. Two rules matter more than
/// the buffering itself: a read must never return 0 (WasapiOut stops for good on
/// a zero read), and drift correction must move exactly one block and only once
/// per interval, because every correction is audible.
/// </summary>
public class JitterRingTests
{
    private const int Rate = 48_000;
    private const int Block = 480; // the engine's block size

    private static JitterRing Mono(int capacityMs = 100) => new(Rate, 1, capacityMs);

    /// <summary>Capacity for <see cref="Mono"/> in frames (100 ms).</summary>
    private const int CapacityFrames = Rate * 100 / 1000; // 4800

    private static float[] Values(int frames, int start = 0, int channels = 1)
    {
        var block = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                block[f * channels + c] = 1_000f + start + f;
        return block;
    }

    private static void Band(JitterRing ring, int target, int low, int high)
    {
        ring.TargetFrames = target;
        ring.LowFrames = low;
        ring.HighFrames = high;
    }

    // ------------------------------------------------------------ Empty read

    [Fact]
    public void Read_OnAnEmptyRing_ReturnsCountAndSilence_NeverZero()
    {
        // A zero-length read permanently stops the WASAPI thread, so this is the
        // single most important contract in this class.
        var ring = new JitterRing(Rate, 2);
        var buffer = new float[Block * 2];
        Array.Fill(buffer, 5f);

        int read = ring.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);
        Assert.NotEqual(0, read);
        Assert.All(buffer, sample => Assert.Equal(0f, sample));
        Assert.Equal(Block, ring.UnderrunFrames); // frames, not samples
    }

    [Fact]
    public void Read_ReturnsCountEvenForAnOddSampleCount()
    {
        var ring = new JitterRing(Rate, 2);
        var buffer = new float[5];

        int read = ring.Read(buffer, 0, 5);

        Assert.Equal(4, read); // truncated to whole frames: 2 frames
        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Read_ReportsTheProviderFormat()
    {
        var ring = new JitterRing(Rate, 2);
        Assert.Equal(Rate, ring.WaveFormat.SampleRate);
        Assert.Equal(2, ring.WaveFormat.Channels);
        Assert.Equal(32, ring.WaveFormat.BitsPerSample);
    }

    // ------------------------------------------------------- Depth accounting

    [Fact]
    public void DepthFrames_IsExactAcrossManyWrapArounds()
    {
        // 40 cycles of 480 frames walk the read and write pointers around a
        // 4800-frame ring three times. Depth must be exact at every step, not
        // just after a full cycle.
        var ring = Mono();
        var block = Values(Block);

        for (int cycle = 0; cycle < 40; cycle++)
        {
            ring.Write(block, 0, Block);
            Assert.Equal(Block, ring.DepthFrames);

            var output = new float[Block / 2];
            ring.Read(output, 0, Block / 2);
            Assert.Equal(Block / 2, ring.DepthFrames);

            ring.Read(output, 0, Block / 2);
            Assert.Equal(0, ring.DepthFrames);
        }
    }

    [Fact]
    public void WriteThenRead_PreservesOrderAcrossWrapAround()
    {
        var ring = Mono();
        float expected = 0f;

        for (int cycle = 0; cycle < 40; cycle++)
        {
            var block = new float[Block];
            for (int f = 0; f < Block; f++) block[f] = expected + f;
            expected += Block;

            ring.Write(block, 0, Block);

            var output = new float[Block];
            int read = ring.Read(output, 0, Block);

            Assert.Equal(Block, read);
            for (int f = 0; f < Block; f++)
                Assert.Equal(expected - Block + f, output[f]);
        }

        Assert.Equal(0, ring.UnderrunFrames);
        Assert.Equal(0, ring.DepthFrames);
    }

    // -------------------------------------------------------------- Underrun

    [Fact]
    public void Underrun_IsCountedInFrames_AndALaterWriteResumesCleanly()
    {
        var ring = Mono();
        var block = new float[Block];
        Array.Fill(block, 1f);
        ring.Write(block, 0, Block);

        // Ask for two blocks with one buffered: one block of audio, one of silence.
        var output = new float[Block * 2];
        Array.Fill(output, 7f);
        int read = ring.Read(output, 0, Block * 2);

        Assert.Equal(Block * 2, read);
        Assert.All(output.Take(Block), sample => Assert.Equal(1f, sample));
        Assert.All(output.Skip(Block), sample => Assert.Equal(0f, sample));
        Assert.Equal(Block, ring.UnderrunFrames);
        Assert.Equal(0, ring.DepthFrames);

        // The ring must not replay the stale block or leave zeros in front of
        // the new audio: the next write is heard immediately and intact.
        var fresh = new float[Block];
        Array.Fill(fresh, 2f);
        ring.Write(fresh, 0, Block);

        var next = new float[Block];
        Array.Fill(next, 9f);
        ring.Read(next, 0, Block);

        Assert.All(next, sample => Assert.Equal(2f, sample));
        Assert.Equal(0, ring.DepthFrames);
        Assert.Equal(Block, ring.UnderrunFrames); // no new underrun
    }

    [Fact]
    public void Underrun_IsNotCountedWhenTheRingCanFulfilTheRead()
    {
        var ring = Mono();
        ring.Write(Values(Block), 0, Block);

        var output = new float[Block];
        ring.Read(output, 0, Block);

        Assert.Equal(0, ring.UnderrunFrames);
    }

    // ------------------------------------------------------------ Maintain

    [Fact]
    public void Maintain_WhenTooDeep_DropsBackTowardTheTarget()
    {
        var ring = Mono();
        Band(ring, target: 1_000, low: 800, high: 2_000);
        ring.Write(Values(3_000), 0, 3_000); // depth 3000, above HighFrames

        ring.Maintain(Block);

        Assert.Equal(1_000, ring.DepthFrames);
        Assert.Equal(1, ring.DroppedBlocks);
        Assert.Equal(0, ring.InsertedBlocks);
    }

    [Fact]
    public void Maintain_MovesAudioAtMostOncePerCorrectionInterval()
    {
        var ring = Mono();
        Band(ring, target: 1_000, low: 800, high: 2_000);
        ring.Write(Values(3_000), 0, 3_000);

        ring.Maintain(Block); // first call is allowed to correct
        Assert.Equal(1_000, ring.DepthFrames);
        Assert.Equal(1, ring.DroppedBlocks);

        // Push it out of band again and call immediately: the time gate must
        // hold, so nothing may move even though the depth is above HighFrames.
        ring.Write(Values(3_000, start: 10_000), 0, 3_000);
        Assert.Equal(4_000, ring.DepthFrames);

        ring.Maintain(Block);
        Assert.Equal(4_000, ring.DepthFrames);
        Assert.Equal(1, ring.DroppedBlocks);

        ring.Maintain(Block);
        Assert.Equal(4_000, ring.DepthFrames);
        Assert.Equal(1, ring.DroppedBlocks);
    }

    [Fact]
    public void Maintain_WhenTooShallow_PadsTowardTheTarget()
    {
        var ring = Mono();
        Band(ring, target: 1_000, low: 800, high: 2_000);
        ring.Write(Values(100), 0, 100); // depth 100, below LowFrames

        ring.Maintain(Block);

        Assert.Equal(1_000, ring.DepthFrames);
        Assert.Equal(1, ring.InsertedBlocks);
        Assert.Equal(0, ring.DroppedBlocks);

        // The padding is silence appended after the real audio, not before it.
        var output = new float[1_000];
        ring.Read(output, 0, 1_000);
        for (int f = 0; f < 100; f++) Assert.Equal(1_000f + f, output[f]);
        for (int f = 100; f < 1_000; f++) Assert.Equal(0f, output[f]);
    }

    [Fact]
    public void Maintain_WithDepthInsideTheBand_DoesNothing()
    {
        var ring = Mono();
        Band(ring, target: 1_000, low: 800, high: 2_000);
        ring.Write(Values(1_500), 0, 1_500);

        ring.Maintain(Block);

        Assert.Equal(1_500, ring.DepthFrames);
        Assert.Equal(0, ring.DroppedBlocks);
        Assert.Equal(0, ring.InsertedBlocks);
    }

    [Fact]
    public void Maintain_NeverExceedsEightBlocksOfCorrectionAtOnce()
    {
        // A single correction is bounded to blockFrames * 8 so a burst cannot
        // empty the ring audibly; a second interval is needed to get the rest.
        var ring = Mono();
        Band(ring, target: 100, low: 50, high: 200);
        ring.Write(Values(4_000), 0, 4_000);

        ring.Maintain(Block);

        Assert.Equal(4_000 - Block * 8, ring.DepthFrames);
        Assert.Equal(1, ring.DroppedBlocks);
    }

    // ---------------------------------------------------------- Overflow drop

    [Fact]
    public void Write_LargerThanFreeSpace_DropsTheOldestAudio_NotTheNewest()
    {
        // One frame is deliberately left free: a *completely* full ring reports a
        // DepthFrames of 0 (see the report), so 4799/4800 is the deepest state the
        // API can actually describe.
        var ring = Mono();
        ring.Write(Values(CapacityFrames - 1, start: 0), 0, CapacityFrames - 1);
        Assert.Equal(CapacityFrames - 1, ring.DepthFrames);

        const int incoming = 100;
        var newest = Values(incoming, start: 5_000);
        ring.Write(newest, 0, incoming);

        var output = new float[ring.DepthFrames];
        int depthAfterDrop = ring.DepthFrames;
        ring.Read(output, 0, output.Length);

        // The newest block survives intact and contiguous at the tail...
        Assert.Equal(newest, output.Skip(output.Length - incoming).ToArray());

        // ...and everything before it is the oldest audio still resident, in
        // order, with the oldest frames gone.
        for (int i = 1; i < output.Length; i++)
            Assert.True(output[i] >= output[i - 1], $"order broken at {i}: {output[i - 1]} -> {output[i]}");
        Assert.True(output[0] >= 100f, $"kept audio older than expected: {output[0]}");

        Assert.InRange(depthAfterDrop, CapacityFrames - 2, CapacityFrames - 1);
        Assert.Equal(0, ring.DepthFrames); // fully drained by the read above
    }

    [Fact]
    public void Clear_DropsEverythingAndLeavesNoStaleAudio()
    {
        var ring = Mono();
        ring.Write(Values(2_000), 0, 2_000);

        ring.Clear();

        Assert.Equal(0, ring.DepthFrames);

        ring.Write(Values(Block, start: 7_000), 0, Block);
        var output = new float[Block];
        ring.Read(output, 0, Block);
        Assert.Equal(Values(Block, start: 7_000), output);
    }

    // ------------------------------------------------------------- Offsets

    [Fact]
    public void WriteAndRead_HonourOffsets_ForStereo()
    {
        var ring = new JitterRing(Rate, 2);
        var buffer = new float[4 * 2 + 2];
        for (int f = 0; f < 4; f++)
        {
            buffer[2 + f * 2] = 1f + f;
            buffer[2 + f * 2 + 1] = -(1f + f);
        }

        ring.Write(buffer, 2, 4); // 4 frames starting at sample offset 2

        var output = new float[4 * 2 + 2];
        Array.Fill(output, 9f);
        ring.Read(output, 2, 8);

        Assert.Equal(1f, output[2]);
        Assert.Equal(-1f, output[3]);
        Assert.Equal(4f, output[8]);
        Assert.Equal(-4f, output[9]);
        Assert.Equal(9f, output[0]);
        Assert.Equal(9f, output[1]);
    }
}
