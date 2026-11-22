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

    [Fact]
    public void Processors_exposes_what_was_added_in_order()
    {
        var gain = new GainStage();
        var eq = new TenBandEq(SampleRate);
        var limiter = new Limiter(SampleRate);

        var chain = new StripChain().Add(gain).Add(eq).Add(limiter);

        Assert.Equal(3, chain.Processors.Count);
        Assert.Same(gain, chain.Processors[0]);
        Assert.Same(eq, chain.Processors[1]);
        Assert.Same(limiter, chain.Processors[2]);
    }

    [Fact]
    public void Reset_clears_processor_state_so_a_silent_block_is_exactly_silent()
    {
        var chain = new StripChain().Add(new HighPassFilter(SampleRate, frequencyHz: 500));

        chain.Process(TestSignals.WhiteNoise(4096, seed: 41));

        // Without a reset the filter's state keeps ringing into the next, silent block.
        var tail = new float[64];
        chain.Process(tail);
        Assert.Contains(tail, sample => sample != 0f);

        chain.Reset();

        var silence = new float[4096];
        chain.Process(silence);

        for (int i = 0; i < silence.Length; i++)
        {
            Assert.True(silence[i] == 0.0f,
                $"frame {i} is {silence[i]:R} after Reset; the chain must not carry a decaying tail");
        }
    }
}
