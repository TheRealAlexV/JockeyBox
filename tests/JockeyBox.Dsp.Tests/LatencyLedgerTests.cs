namespace JockeyBox.Dsp.Tests;

public class LatencyLedgerTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Two_channels_pad_to_null()
    {
        var chainA = new StripChain().Add(new Limiter(SampleRate, thresholdDb: 0, lookaheadMs: 5));
        var chainB = new StripChain();

        var ledger = new LatencyLedger();
        ledger.Set("A", chainA.TotalLatencyFrames, 0, 0);
        ledger.Set("B", chainB.TotalLatencyFrames, 0, 0);

        Assert.Equal(240, ledger.MaxLatencyFrames);
        Assert.Equal(0, ledger.PaddingFor("A"));
        Assert.Equal(240, ledger.PaddingFor("B"));

        var chirp = TestSignals.Chirp(SampleRate * 2, 100, 8000, SampleRate, 0.1);

        var a = (float[])chirp.Clone();
        chainA.Process(a);

        var b = (float[])chirp.Clone();
        chainB.Process(b);

        int padding = ledger.PaddingFor("B");
        var bPadded = new float[b.Length + padding];
        Array.Copy(b, 0, bPadded, padding, b.Length);

        Assert.Equal(0, TestSignals.BestLag(a, bPadded, maxLag: 8));
    }

    [Fact]
    public void Padding_is_never_negative()
    {
        var ledger = new LatencyLedger();
        ledger.Set("drums", 100, 50, 0);
        ledger.Set("vocal", 300, 0, 20);

        Assert.Equal(320, ledger.MaxLatencyFrames);
        Assert.Equal(170, ledger.PaddingFor("drums"));
        Assert.Equal(0, ledger.PaddingFor("vocal"));
        Assert.True(ledger.PaddingFor("vocal") >= 0);
    }

    [Fact]
    public void Unknown_channel_throws()
    {
        var ledger = new LatencyLedger();
        Assert.Throws<KeyNotFoundException>(() => ledger.PaddingFor("nope"));
    }

    [Fact]
    public void Channel_latency_totals_chain_device_and_user_offset()
    {
        var ledger = new LatencyLedger();
        ledger.Set("gtr", chainFrames: 240, deviceFrames: 64, userOffsetFrames: 12);

        Assert.True(ledger.TryGet("gtr", out ChannelLatency? entry), "the registered channel must be found");
        Assert.NotNull(entry);
        Assert.Equal(316, entry!.TotalFrames);
        Assert.Equal(240, entry.ChainFrames);
        Assert.Equal(64, entry.DeviceFrames);
        Assert.Equal(12, entry.UserOffsetFrames);
        Assert.Equal(entry.TotalFrames, ledger.MaxLatencyFrames);
    }

    [Fact]
    public void Entries_hold_exactly_what_was_registered_in_order()
    {
        var ledger = new LatencyLedger();
        ledger.Set("drums", 10, 0, 0);
        ledger.Set("bass", 20, 5, 0);
        ledger.Set("vox", 0, 0, 7);

        Assert.Equal(new[] { "drums", "bass", "vox" }, ledger.Entries.Select(entry => entry.ChannelId).ToArray());
        Assert.Equal(3, ledger.Entries.Count);
        Assert.Equal(10, ledger.Entries[0].TotalFrames);
        Assert.Equal(25, ledger.Entries[1].TotalFrames);
        Assert.Equal(7, ledger.Entries[2].TotalFrames);

        // Re-registering a channel replaces its entry in place instead of appending a second one.
        ledger.Set("drums", 11, 0, 0);
        Assert.Equal(3, ledger.Entries.Count);
        Assert.Equal(11, ledger.Entries[0].TotalFrames);
    }
}
