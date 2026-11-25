namespace JockeyBox.Dsp.Tests;

/// <summary>
/// The tempo stretcher's ratio → group-delay curve. Pure arithmetic, so it runs
/// anywhere; the measurement that fills the table is a separate concern (see
/// <see cref="TempoLatencyTable.Measured"/>), and nothing here re-derives it.
/// </summary>
public class TempoLatencyTableTests
{
    private const int Rate = 48000;

    [Fact]
    public void Empty_table_reports_zero_at_every_ratio()
    {
        var table = TempoLatencyTable.Default;

        Assert.False(table.IsCalibrated);
        foreach (double ratio in new[] { 0.5, 0.75, 1.0, 1.4, 2.0 })
            Assert.Equal(0, table.FramesFor(ratio, Rate));
    }

    [Fact]
    public void Empty_table_at_an_odd_rate_still_reports_zero()
    {
        Assert.Equal(0, TempoLatencyTable.Default.FramesFor(1.33, 44100));
    }

    [Fact]
    public void Interpolates_linearly_between_two_points()
    {
        var table = new TempoLatencyTable(new (double, int)[] { (1.0, 100), (2.0, 300) });

        Assert.Equal(100, table.FramesFor(1.0, Rate));
        Assert.Equal(200, table.FramesFor(1.5, Rate));
        Assert.Equal(300, table.FramesFor(2.0, Rate));
        // 1.25 -> 100 + 200*0.25 = 150
        Assert.Equal(150, table.FramesFor(1.25, Rate));
    }

    [Fact]
    public void Clamps_outside_the_measured_range()
    {
        var table = new TempoLatencyTable(new (double, int)[] { (0.8, 200), (1.5, 900) });

        Assert.Equal(200, table.FramesFor(0.5, Rate));
        Assert.Equal(900, table.FramesFor(2.0, Rate));
    }

    [Fact]
    public void Scales_with_sample_rate()
    {
        var table = new TempoLatencyTable(new (double, int)[] { (1.0, 480) });

        Assert.Equal(480, table.FramesFor(1.0, 48000));
        Assert.Equal(441, table.FramesFor(1.0, 44100)); // 480 * 44100/48000 = 441
        Assert.Equal(960, table.FramesFor(1.0, 96000));
    }

    [Fact]
    public void FromMeasurements_sorts_by_ratio()
    {
        var table = TempoLatencyTable.FromMeasurements(new (double, int)[]
        {
            (2.0, 300), (0.5, 10), (1.0, 100),
        });

        Assert.Equal(10, table.FramesFor(0.4, Rate));
        Assert.Equal(300, table.FramesFor(2.5, Rate));
        Assert.Equal(200, table.FramesFor(1.5, Rate)); // halfway 1.0..2.0
    }

    [Fact]
    public void Measured_table_is_calibrated_and_covers_the_clamp_range()
    {
        var table = TempoLatencyTable.Measured;

        Assert.True(table.IsCalibrated);
        // Both ends of TempoStage's 0.5..2.0 clamp have a real point.
        Assert.True(table.FramesFor(0.5, Rate) >= 0);
        Assert.True(table.FramesFor(2.0, Rate) > 0);
    }

    [Fact]
    public void Measured_table_never_returns_negative_delay()
    {
        var table = TempoLatencyTable.Measured;

        for (double ratio = 0.5; ratio <= 2.0; ratio += 0.01)
            Assert.True(table.FramesFor(ratio, Rate) >= 0, $"ratio {ratio:0.00}");
    }

    [Fact]
    public void Measured_delay_grows_with_ratio()
    {
        // Measured points jitter by a frame or two, so this is a coarse check
        // that the curve is the right shape, not a monotonicity proof.
        var table = TempoLatencyTable.Measured;

        Assert.True(table.FramesFor(2.0, Rate) > table.FramesFor(1.5, Rate));
        Assert.True(table.FramesFor(1.5, Rate) > table.FramesFor(1.05, Rate));
        Assert.True(table.FramesFor(1.05, Rate) > table.FramesFor(0.6, Rate));
    }
}
