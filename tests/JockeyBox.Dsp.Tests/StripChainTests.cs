namespace JockeyBox.Dsp.Tests;

public class StripChainTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Empty_chain_is_bit_exact()
    {
        var chain = new StripChain();
        Assert.True(chain.IsPassthrough);
        Assert.Equal(0, chain.TotalLatencyFrames);

        var input = TestSignals.WhiteNoise(4096, seed: 11);
        var output = (float[])input.Clone();

        chain.Process(output);

        TestSignals.AssertBitExact(input, output, "an empty chain must leave the buffer untouched");
    }

    [Fact]
    public void All_bypassed_is_bit_exact()
    {
        var chain = new StripChain()
            .Add(new GainStage { GainDb = -6, Bypass = true })
            .Add(new Limiter(SampleRate) { Bypass = true })
            .Add(new TenBandEq(SampleRate) { Bypass = true });

        Assert.True(chain.IsPassthrough);
        Assert.Equal(0, chain.TotalLatencyFrames);

        var input = TestSignals.WhiteNoise(4096, seed: 12);
        var output = (float[])input.Clone();

        chain.Process(output);

        TestSignals.AssertBitExact(input, output, "a fully bypassed chain must leave the buffer untouched");
    }

    [Fact]
    public void Total_latency_skips_bypassed_processors()
    {
        var limiter = new Limiter(SampleRate, thresholdDb: 0, lookaheadMs: 5);
        var chain = new StripChain().Add(limiter);
        Assert.Equal(limiter.LatencyFrames, chain.TotalLatencyFrames);

        limiter.Bypass = true;
        Assert.Equal(0, chain.TotalLatencyFrames);
    }
}
