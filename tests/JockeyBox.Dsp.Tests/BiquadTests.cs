namespace JockeyBox.Dsp.Tests;

public class BiquadTests
{
    private const double SampleRate = 48000;

    [Fact]
    public void Low_pass_gain_at_dc_is_unity_within_0_01_db()
    {
        var biquad = new Biquad();
        biquad.SetLowPass(SampleRate, 1000, 0.7071067811865476);

        var buffer = new float[(int)SampleRate];
        Array.Fill(buffer, 1f);
        biquad.Process(buffer);

        double gainDb = TestSignals.Db(buffer[^1]);
        Assert.True(Math.Abs(gainDb) <= 0.01, $"DC gain was {gainDb:F4} dB");
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(-6.0)]
    [InlineData(12.0)]
    public void Peaking_gain_at_centre_matches_requested_db_within_0_1_db(double gainDb)
    {
        var biquad = new Biquad();
        biquad.SetPeaking(SampleRate, 1000, 1.0, gainDb);

        var input = TestSignals.Sine((int)SampleRate * 2, 1000, SampleRate, 0.25);
        var output = (float[])input.Clone();
        biquad.Process(output);

        // Measure the second second, after the filter has settled.
        int start = (int)SampleRate;
        double measuredDb = TestSignals.RmsDb(output.AsSpan(start, (int)SampleRate))
                            - TestSignals.RmsDb(input.AsSpan(start, (int)SampleRate));

        Assert.True(Math.Abs(measuredDb - gainDb) <= 0.1,
            $"Requested {gainDb:F2} dB, measured {measuredDb:F4} dB");
    }

    [Fact]
    public void Ten_seconds_of_a_full_scale_sweep_produce_no_nan_or_inf()
    {
        const int length = (int)SampleRate * 10;
        var sweep = TestSignals.Chirp(length, 20, 20000, SampleRate, 1.0);

        var filters = new Biquad[5];
        filters[0] = new Biquad();
        filters[0].SetLowPass(SampleRate, 20000, 0.7071067811865476);
        filters[1] = new Biquad();
        filters[1].SetHighPass(SampleRate, 20, 0.7071067811865476);
        filters[2] = new Biquad();
        filters[2].SetPeaking(SampleRate, 1000, 1.41, 12);
        filters[3] = new Biquad();
        filters[3].SetLowShelf(SampleRate, 200, 1.0, -12);
        filters[4] = new Biquad();
        filters[4].SetHighShelf(SampleRate, 8000, 1.0, 12);

        foreach (Biquad filter in filters)
        {
            var buffer = (float[])sweep.Clone();
            filter.Process(buffer);
            for (int i = 0; i < buffer.Length; i++)
            {
                Assert.True(float.IsFinite(buffer[i]), $"Sample {i} was {buffer[i]}");
            }
        }
    }
}
