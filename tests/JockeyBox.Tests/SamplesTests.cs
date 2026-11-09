using JockeyBox.Audio.Graph;

namespace JockeyBox.Tests;

/// <summary>
/// The render-path helpers encode two rules the 2.0 bus depends on: a short read
/// must never reach the mixer (NAudio drops a provider that returns 0), and the
/// single bus must never clip now that both decks are summed into it.
/// </summary>
public class SamplesTests
{
    private const int Rate = 48_000;

    // -------------------------------------------------------------- ReadFull

    [Fact]
    public void ReadFull_PadsAShortSourceWithSilence_AndUsesExactlyCount()
    {
        // 6 real samples asked for as 10: the tail must be silence, not garbage.
        var source = new TestSources.Finite(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f });
        var buffer = new float[16];
        Array.Fill(buffer, 9f); // poison: any untouched slot is a bug

        Samples.ReadFull(source, buffer, 2, 10);

        Assert.Equal(0.1f, buffer[2]);
        Assert.Equal(0.6f, buffer[7]);
        for (int i = 8; i < 12; i++) Assert.Equal(0f, buffer[i]);
        // Nothing outside [offset, offset+count) may be touched.
        Assert.Equal(9f, buffer[0]);
        Assert.Equal(9f, buffer[1]);
        for (int i = 12; i < buffer.Length; i++) Assert.Equal(9f, buffer[i]);
    }

    [Fact]
    public void ReadFull_ReassemblesAProviderThatOnlyReturnsShortReads()
    {
        var data = new float[100];
        for (int i = 0; i < data.Length; i++) data[i] = i;
        var source = new TestSources.Finite(data, maxPerRead: 7); // 15 short reads
        var buffer = new float[100];

        Samples.ReadFull(source, buffer, 0, 100);

        Assert.Equal(data, buffer);
        Assert.True(source.Reads >= 15, "provider was not drained to completion");
    }

    [Fact]
    public void ReadFull_OnAnExhaustedProvider_ReturnsSilenceRatherThanZeroCount()
    {
        var source = new TestSources.Finite(Array.Empty<float>());
        var buffer = new float[8];
        Array.Fill(buffer, 1f);

        Samples.ReadFull(source, buffer, 0, 8);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void ReadFull_WithZeroCount_IsANoOp()
    {
        var source = new TestSources.Endless(0.5f);
        var buffer = new float[4];
        Array.Fill(buffer, 3f);

        Samples.ReadFull(source, buffer, 1, 0);

        Assert.All(buffer, sample => Assert.Equal(3f, sample));
    }

    [Fact]
    public void FullReadProvider_AlwaysReportsCount_EvenAtTheEndOfTheFile()
    {
        var provider = new FullReadProvider(new TestSources.Finite(new float[5], maxPerRead: 2));
        var buffer = new float[12];
        Array.Fill(buffer, 7f);

        int read = provider.Read(buffer, 0, 12);

        Assert.Equal(12, read); // NAudio would drop a 0-length read, killing the deck
        Assert.Equal(new float[5], buffer.Take(5));
        for (int i = 5; i < 12; i++) Assert.Equal(0f, buffer[i]);

        // Still full-length once the source is exhausted.
        Assert.Equal(12, provider.Read(buffer, 0, 12));
        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void FullReadProvider_PreservesTheSourceWaveFormat()
    {
        var source = new TestSources.Endless(0f, TestSources.Stereo);
        var provider = new FullReadProvider(source);

        Assert.Same(source.WaveFormat, provider.WaveFormat);
        Assert.Equal(2, provider.WaveFormat.Channels);
        Assert.Equal(Rate, provider.WaveFormat.SampleRate);
    }

    // ------------------------------------------------------------ AddScaled

    [Fact]
    public void AddScaled_AccumulatesInPlace_OnlyOverCount()
    {
        var dst = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
        var src = new[] { 0.0f, 1.0f, 2.0f, 3.0f, 4.0f };

        Samples.AddScaled(dst, src, 1, 3, 0.5f);

        Assert.Equal(0.1f, dst[0], 6);
        Assert.Equal(0.7f, dst[1], 6); // 0.2 + 1.0*0.5
        Assert.Equal(1.3f, dst[2], 6); // 0.3 + 2.0*0.5
        Assert.Equal(1.9f, dst[3], 6); // 0.4 + 3.0*0.5
        Assert.Equal(0.5f, dst[4], 6); // untouched
    }

    [Fact]
    public void AddScaled_WithZeroGain_LeavesTheDestinationBitIdentical()
    {
        var dst = new[] { 0.1f, -0.2f, 1e-30f, float.Epsilon };
        var original = (float[])dst.Clone();
        var src = new[] { 1f, 1f, 1f, 1f };

        Samples.AddScaled(dst, src, 0, dst.Length, 0f);

        Assert.Equal(original, dst);
    }

    [Fact]
    public void Clear_ZeroesOnlyTheRequestedSpan()
    {
        var buffer = new[] { 1f, 2f, 3f, 4f };

        Samples.Clear(buffer, 1, 2);

        Assert.Equal(new[] { 1f, 0f, 0f, 4f }, buffer);
    }

    // ------------------------------------------------------------ SoftLimit

    [Fact]
    public void SoftLimit_BelowTheThreshold_IsBitIdentical()
    {
        var buffer = new[] { 0f, 0.1f, -0.25f, 0.5f, -0.707f, 0.707f, float.Epsilon };
        var original = (float[])buffer.Clone();

        Samples.SoftLimit(buffer, 0, buffer.Length);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void SoftLimit_AboveTheThreshold_IsMonotonicAndNeverExceedsUnity()
    {
        // Sweep well past full scale: an overdriven bus must keep rising, flatten
        // out, and stop at 1.0 instead of wrapping or exceeding it.
        var buffer = new float[2_001];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (i - 1_000) / 100f; // -10 .. +10

        Samples.SoftLimit(buffer, 0, buffer.Length);

        for (int i = 1; i < buffer.Length; i++)
            Assert.True(buffer[i] >= buffer[i - 1], $"not monotonic at {i}");
        for (int i = 0; i < buffer.Length; i++)
            Assert.InRange(buffer[i], -1f, 1f);

        // Symmetry: +x and -x must be shaped identically.
        Assert.Equal(buffer[1_500], -buffer[500], 6);
        Assert.Equal(1f, buffer[2_000], 6);
        Assert.Equal(-1f, buffer[0], 6);
    }

    [Fact]
    public void SoftLimit_IsContinuousAtTheKnee()
    {
        // No discontinuity at the threshold: stepping across 0.707 must not jump.
        // Beyond it the curve only ever compresses (out <= in), which is the
        // point of a knee rather than a hard clip.
        var buffer = new[] { 0.7069f, 0.707f, 0.707001f, 0.708f };
        var original = (float[])buffer.Clone();

        Samples.SoftLimit(buffer, 0, buffer.Length);

        for (int i = 0; i < buffer.Length; i++)
            Assert.InRange(buffer[i] - original[i], -1e-6, 1e-6);
        for (int i = 1; i < buffer.Length; i++)
        {
            // Every output step is an input step or smaller: the knee only
            // compresses, it never adds motion of its own.
            double inputStep = original[i] - original[i - 1];
            Assert.InRange(buffer[i] - buffer[i - 1], 0f, (float)inputStep + 1e-6f);
        }

        // Higher up the knee the compression is real but small at 0.72.
        var higher = new[] { 0.72f, 0.8f, 1.0f };
        Samples.SoftLimit(higher, 0, higher.Length);
        Assert.InRange(higher[0] - 0.72f, -1e-4, 0f);
        Assert.InRange(higher[1] - 0.8f, -5e-3, 0f);
        Assert.InRange(higher[2] - 1.0f, -0.07f, 0f);
        Assert.True(higher[0] < higher[1] && higher[1] < higher[2], "knee is not monotonic");
    }

    [Fact]
    public void SoftLimit_KeepsAFullScaleSineInsideUnityOnBothChannels()
    {
        var buffer = new float[Rate * 2]; // 1 s stereo, interleaved
        for (int f = 0; f < Rate; f++)
        {
            float sample = (float)Math.Sin(2.0 * Math.PI * 440.0 * f / Rate);
            buffer[f * 2] = sample;
            buffer[f * 2 + 1] = -sample;
        }

        Samples.SoftLimit(buffer, 0, buffer.Length);

        // The 1.0 peak is compressed into the knee (0.707 + 0.293*tanh(1) ~ 0.93)
        // but stays loud, and nothing anywhere may exceed full scale.
        float peak = Samples.Peak(buffer, 0, buffer.Length);
        Assert.InRange(peak, 0.9f, 1.0f);
        for (int i = 0; i < buffer.Length; i++) Assert.InRange(buffer[i], -1f, 1f);
    }

    [Fact]
    public void SoftLimit_RespectsOffsetAndCount()
    {
        var buffer = new[] { 0.9f, 5f, 5f, 0.9f };
        var original = (float[])buffer.Clone();

        Samples.SoftLimit(buffer, 1, 2);

        Assert.Equal(original[0], buffer[0]);
        Assert.Equal(original[3], buffer[3]);
        Assert.InRange(buffer[1], -1f, 1f);
        Assert.InRange(buffer[2], -1f, 1f);
        Assert.InRange(buffer[1], 0.99f, 1f);
    }

    [Fact]
    public void SoftLimit_WithACustomThreshold_IsTransparentBelowIt()
    {
        var buffer = new[] { 0f, 0.4f, -0.4f, 0.5f, -0.5f };
        var original = (float[])buffer.Clone();

        Samples.SoftLimit(buffer, 0, buffer.Length, threshold: 0.5f);

        Assert.Equal(original[0], buffer[0]);
        Assert.Equal(original[1], buffer[1]);
        Assert.Equal(original[2], buffer[2]);
        Assert.Equal(original[3], buffer[3]); // exactly on the knee: untouched
        Assert.Equal(original[4], buffer[4]);
    }

    // ---------------------------------------------------------- Peak / PeakDb

    [Fact]
    public void Peak_ReturnsTheLargestMagnitude_IgnoringSign()
    {
        var buffer = new[] { 0.1f, -0.9f, 0.3f, 0.45f, -0.2f };
        Assert.Equal(0.9f, Samples.Peak(buffer, 0, buffer.Length), 6);

        // Offset/count are honoured.
        Assert.Equal(0.45f, Samples.Peak(buffer, 2, 2), 6);
        Assert.Equal(0f, Samples.Peak(buffer, 0, 0));
    }

    [Fact]
    public void PeakDb_HalfScaleIsMinusSixPointZeroTwoDbfs()
    {
        var buffer = new[] { 0.5f };
        Assert.InRange(Samples.PeakDb(buffer, 0, 1), -6.0206 - 1e-4, -6.0206 + 1e-4);

        // Full scale is 0 dBFS.
        var full = new[] { -1f };
        Assert.InRange(Samples.PeakDb(full, 0, 1), -1e-9, 1e-9);
    }

    [Fact]
    public void PeakDb_SilenceIsFlooredAtMinusOneHundredTwenty()
    {
        var silence = new float[256];
        Assert.Equal(-120.0, Samples.PeakDb(silence, 0, silence.Length), 12);
        Assert.Equal(-120.0, Samples.PeakDb(silence, 0, 0), 12); // empty buffer too
    }

    // -------------------------------------------------------- RenderCommands

    [Fact]
    public void RenderCommands_DrainRunsInFifoOrder()
    {
        var commands = new RenderCommands();
        var order = new List<int>();

        for (int i = 0; i < 10; i++)
        {
            int captured = i;
            commands.Post(() => order.Add(captured));
        }

        Assert.Equal(10, commands.Count);

        commands.Drain();

        Assert.Equal(Enumerable.Range(0, 10), order);
        Assert.Equal(0, commands.Count);

        // Draining again is a no-op: commands run exactly once.
        commands.Drain();
        Assert.Equal(10, order.Count);
    }

    [Fact]
    public void RenderCommands_AThrowingCommand_DoesNotBlockLaterOnes()
    {
        var commands = new RenderCommands();
        var order = new List<int>();

        commands.Post(() => order.Add(1));
        commands.Post(() => throw new InvalidOperationException("boom"));
        commands.Post(() => throw new ArgumentException("also boom"));
        commands.Post(() => order.Add(4));

        commands.Drain(); // must not throw: the render thread cannot die of a UI bug

        Assert.Equal(new[] { 1, 4 }, order);
        Assert.Equal(0, commands.Count);
    }

    [Fact]
    public void RenderCommands_PostedDuringDrain_RunsInTheSameDrain()
    {
        // Commands are drained to exhaustion, so a command that posts another
        // still runs before the render block ends (no one-frame stall).
        var commands = new RenderCommands();
        var order = new List<int>();

        commands.Post(() =>
        {
            order.Add(1);
            commands.Post(() => order.Add(2));
        });
        commands.Post(() => order.Add(3));

        commands.Drain();

        Assert.Equal(new[] { 1, 3, 2 }, order);
        Assert.Equal(0, commands.Count);
    }

    [Fact]
    public void RenderCommands_CountsPendingWork()
    {
        var commands = new RenderCommands();
        Assert.Equal(0, commands.Count);

        commands.Post(() => { });
        commands.Post(() => { });
        Assert.Equal(2, commands.Count);

        commands.Drain();
        Assert.Equal(0, commands.Count);
    }
}
