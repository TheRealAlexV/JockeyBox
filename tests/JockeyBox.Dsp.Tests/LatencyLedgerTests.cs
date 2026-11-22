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
}
