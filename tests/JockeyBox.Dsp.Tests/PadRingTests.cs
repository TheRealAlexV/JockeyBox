namespace JockeyBox.Dsp.Tests;

public class PadRingTests
{
    private const int Channels = 2;

    [Fact]
    public void Pad_above_the_old_240_frame_ring_delays_by_the_full_amount()
    {
        // The defect: the ring held a fixed 240 frames (5 ms at 48 kHz) and SetPadding clamped to
        // it, so anything larger was silently discarded. 500 frames must arrive at 500, not 240.
        var pad = new PadRing(Channels);
        pad.SetPadding(500);

        Assert.Equal(500, pad.TargetFrames);
        Assert.False(pad.Clamped, "500 frames is below the 960-frame ceiling");
        Assert.True(pad.CapacityFrames >= 500, "the ring must grow to hold the pad it was given");

        // Let the ramp settle first: a pad change slides over 480 frames instead of stepping.
        pad.Process(new float[1024 * Channels], 0, Channels, 1024);

        var block = new float[1024 * Channels];
        block[0] = 1f;
        block[1] = 1f;
        pad.Process(block, 0, Channels, 1024);

        Assert.Equal(500, FirstNonZeroFrame(block));
    }

    [Fact]
    public void Compensation_uses_chain_plus_device_total()
    {
        // A limiter mic (240 of chain) against an ASIO mic that reports 500 of its own device
        // latency. The slowest total is 500, so the limiter needs 260 of pad: compensating the
        // chain alone would leave the pair 260 frames apart, which comb-filters.
        var ledger = new LatencyLedger();
        ledger.Set("limiter", chainFrames: 240, deviceFrames: 0, userOffsetFrames: 0);
        ledger.Set("asio", chainFrames: 0, deviceFrames: 500, userOffsetFrames: 0);

        Assert.Equal(500, ledger.MaxLatencyFrames);

        var limiterPad = new PadRing(Channels);
        limiterPad.SetPadding(ledger.PaddingFor("limiter"));
        Assert.Equal(260, limiterPad.TargetFrames);
        Assert.False(limiterPad.Clamped);

        var asioPad = new PadRing(Channels);
        asioPad.SetPadding(ledger.PaddingFor("asio"));
        Assert.Equal(0, asioPad.TargetFrames);
        Assert.False(asioPad.Active);

        limiterPad.Process(new float[1024 * Channels], 0, Channels, 1024);

        var block = new float[1024 * Channels];
        block[0] = 1f;
        block[1] = 1f;
        limiterPad.Process(block, 0, Channels, 1024);

        Assert.Equal(260, FirstNonZeroFrame(block));
    }

    [Fact]
    public void Pad_over_the_ceiling_clamps_and_raises_the_visible_flag()
    {
        var pad = new PadRing(Channels);
        pad.SetPadding(PadRing.MaxFrames + 400);

        Assert.True(pad.Clamped, "a clamped pad must be visible to diagnostics, never silent");
        Assert.Equal(PadRing.MaxFrames, pad.TargetFrames);

        pad.Process(new float[1024 * Channels], 0, Channels, 1024);

        var block = new float[1024 * Channels];
        block[0] = 1f;
        block[1] = 1f;
        pad.Process(block, 0, Channels, 1024);

        Assert.Equal(PadRing.MaxFrames, FirstNonZeroFrame(block));
    }

    [Fact]
    public void Exactly_the_ceiling_is_not_clamped()
    {
        var pad = new PadRing(Channels);
        pad.SetPadding(PadRing.MaxFrames);

        Assert.False(pad.Clamped);
        Assert.Equal(PadRing.MaxFrames, pad.TargetFrames);
    }

    [Fact]
    public void Zero_pad_is_bit_exact()
    {
        var pad = new PadRing(Channels);
        Assert.False(pad.Active);

        var input = TestSignals.WhiteNoise(4096, seed: 17);
        var output = (float[])input.Clone();

        pad.Process(output, 0, Channels, 2048);

        TestSignals.AssertBitExact(input, output, "a zero pad must leave the buffer untouched");
    }

    [Fact]
    public void Render_path_allocates_nothing_after_the_ring_grew()
    {
        // Growing the ring is the settings/route path's job, so SetPadding may allocate. The
        // render path (Process) must not.
        var pad = new PadRing(Channels);
        pad.SetPadding(500);

        var block = TestSignals.WhiteNoise(512 * Channels, seed: 23);
        for (int i = 0; i < 64; i++) pad.Process(block, 0, Channels, 512);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) pad.Process(block, 0, Channels, 512);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static int FirstNonZeroFrame(float[] block)
    {
        for (int frame = 0; frame < block.Length / Channels; frame++)
        {
            if (block[frame * Channels] != 0f || block[frame * Channels + 1] != 0f) return frame;
        }

        return -1;
    }
}
