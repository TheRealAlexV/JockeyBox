using System;
using JockeyBox.Audio.Graph;
using JockeyBox.Dsp;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The stretcher must never claim a latency it does not have, and must report the
/// one it does. The defect these pin down: an engaged stage reported zero while
/// SoundTouch held ~30 ms of buffered audio, so the deck's playhead and lyric
/// clock ran ahead of what the operator heard.
///
/// <para><b>Proven on Windows only.</b> This project targets net8.0-windows and
/// cannot execute on the Linux build host; the arithmetic behind the table is
/// covered locally by JockeyBox.Dsp.Tests and these assertions are proven by the
/// Windows gate. They have not been run here.</para>
/// </summary>
public class TempoStageLatencyTests
{
    private static readonly int Block = 2048;

    /// <summary>One block in, so the pending ratio is applied by the render path.</summary>
    private static void RenderOnce(TempoStage stage)
    {
        var buffer = new float[Block * 2];
        stage.Read(buffer, 0, buffer.Length);
    }

    [Fact]
    public void Injected_table_is_used_when_the_stretcher_is_engaged()
    {
        var table = new TempoLatencyTable(new (double, int)[] { (2.0, 1000) });
        var stage = new TempoStage(new TestSources.Endless(0.1f), table);

        stage.SetRatio(2.0);
        RenderOnce(stage);

        Assert.True(stage.Engaged);
        Assert.Equal(1000, stage.LatencyFrames);
        Assert.Same(table, stage.LatencyTable);
    }

    [Fact]
    public void Unity_bypass_still_reports_zero_with_a_calibrated_table()
    {
        var table = new TempoLatencyTable(new (double, int)[] { (1.0, 5000), (2.0, 9000) });
        var stage = new TempoStage(new TestSources.Endless(0.1f), table);

        stage.SetRatio(1.0);
        RenderOnce(stage);

        Assert.False(stage.Engaged);
        Assert.Equal(0, stage.LatencyFrames);
    }

    [Fact]
    public void Unity_bypass_is_sample_exact()
    {
        // No processor is constructed at unity, so the samples pass through
        // untouched - the calibrated table must not change that.
        var table = TempoLatencyTable.Measured;
        var stage = new TempoStage(new TestSources.Endless(0.25f), table);
        stage.SetRatio(1.0);

        var buffer = new float[Block * 2];
        int n = stage.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, n);
        foreach (float v in buffer) Assert.Equal(0.25f, v);
        Assert.Equal(0, stage.LatencyFrames);
    }

    [Fact]
    public void Without_an_injected_table_the_engaged_stage_still_reports_zero()
    {
        // Pre-existing behaviour must be byte-identical when nothing is injected.
        var stage = new TempoStage(new TestSources.Endless(0.1f));

        stage.SetRatio(1.5);
        RenderOnce(stage);

        Assert.True(stage.Engaged);
        Assert.Equal(0, stage.LatencyFrames);
        Assert.Same(TempoLatencyTable.Default, stage.LatencyTable);
    }

    [Fact]
    public void Ratio_is_still_clamped_to_the_supported_range()
    {
        var stage = new TempoStage(new TestSources.Endless(0.1f), TempoLatencyTable.Measured);

        stage.SetRatio(3.0);
        RenderOnce(stage);
        Assert.Equal(2.0, stage.Ratio);

        stage.SetRatio(0.1);
        RenderOnce(stage);
        Assert.Equal(0.5, stage.Ratio);
    }

    [Fact]
    public void Engaged_stage_with_the_measured_table_reports_a_positive_delay()
    {
        var stage = new TempoStage(new TestSources.Endless(0.1f), TempoLatencyTable.Measured);

        stage.SetRatio(1.5);
        RenderOnce(stage);

        // Measured ~28 ms at 1.5x; assert the order of magnitude, not the constant.
        Assert.InRange(stage.LatencyFrames, 480, 2880); // 10 ms .. 60 ms at 48 kHz
    }
}
