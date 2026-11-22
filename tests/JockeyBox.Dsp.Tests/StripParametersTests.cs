namespace JockeyBox.Dsp.Tests;

public class StripParametersTests
{
    [Fact]
    public void Neutral_is_the_documented_transparent_strip()
    {
        StripParameters neutral = StripParameters.Neutral;

        Assert.True(neutral.Eq.IsFlat);
        Assert.True(neutral.HighPass.Enabled);
        Assert.Equal(HighPassFilter.TransparentFrequencyHz, neutral.HighPass.FrequencyHz);
        Assert.True(neutral.LowPass.Enabled);
        Assert.Equal(LowPassFilter.TransparentFrequencyHz, neutral.LowPass.FrequencyHz);

        // Dynamics are switched off: a gate or compressor that is armed cannot be transparent.
        Assert.False(neutral.Gate.Enabled);
        Assert.Equal(-40, neutral.Gate.ThresholdDb);
        Assert.Equal(-60, neutral.Gate.FloorDb);
        Assert.False(neutral.Compressor.Enabled);
        Assert.False(neutral.Limiter.Enabled);
        Assert.False(neutral.MasterBypass);
    }

    [Fact]
    public void Value_equality_compares_content_not_identity()
    {
        StripParameters a = StripParameters.Neutral;
        StripParameters b = StripParameters.Neutral with
        {
            Eq = StripParameters.Neutral.Eq with { Gain2kDb = 3 },
        };

        Assert.Equal(StripParameters.Neutral, a);
        Assert.NotEqual(a, b);
        Assert.Equal(a.GetHashCode(), StripParameters.Neutral.GetHashCode());
    }

    [Fact]
    public void Eq_indexer_reads_the_bands_in_iso_centre_order()
    {
        EqParameters eq = EqParameters.Flat with
        {
            Gain31Db = 1,
            Gain1kDb = 2,
            Gain16kDb = 3,
        };

        Assert.Equal(1, eq[0]);
        Assert.Equal(2, eq[5]);
        Assert.Equal(3, eq[9]);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq[10]);
    }

    [Fact]
    public void Clamp_brings_every_field_into_range_instead_of_throwing()
    {
        StripParameters wild = StripParameters.Neutral with
        {
            Eq = EqParameters.Flat with { Gain31Db = 99, Gain16kDb = -99 },
            HighPass = new FilterParameters(true, 1),
            LowPass = new FilterParameters(true, 1_000_000),
            Gate = new GateParameters(true, ThresholdDb: 40, HysteresisDb: -5, AttackMs: 0, ReleaseMs: 0,
                HoldMs: -100, FloorDb: -500),
            Compressor = new CompressorParameters(true, ThresholdDb: 12, Ratio: 0.5, AttackMs: -1,
                ReleaseMs: 0, KneeDb: 90, MakeupDb: 99),
            Limiter = new LimiterParameters(true, CeilingDb: 24, ReleaseMs: 0),
        };

        StripParameters safe = wild.Clamp();

        Assert.Equal(EqParameters.MaxGainDb, safe.Eq.Gain31Db);
        Assert.Equal(EqParameters.MinGainDb, safe.Eq.Gain16kDb);
        Assert.Equal(FilterParameters.MinFrequencyHz, safe.HighPass.FrequencyHz);
        Assert.Equal(FilterParameters.MaxFrequencyHz, safe.LowPass.FrequencyHz);
        Assert.Equal(0, safe.Gate.ThresholdDb);
        Assert.Equal(0, safe.Gate.HysteresisDb);
        Assert.Equal(0.01, safe.Gate.AttackMs);
        Assert.Equal(-120, safe.Gate.FloorDb);
        Assert.Equal(0, safe.Compressor.ThresholdDb);
        Assert.Equal(1, safe.Compressor.Ratio);
        Assert.Equal(24, safe.Compressor.KneeDb);
        Assert.Equal(24, safe.Compressor.MakeupDb);
        Assert.Equal(0, safe.Limiter.CeilingDb);
        Assert.Equal(1, safe.Limiter.ReleaseMs);
    }

    [Fact]
    public void Clamp_turns_nan_into_the_neutral_value()
    {
        StripParameters broken = StripParameters.Neutral with
        {
            Eq = EqParameters.Flat with { Gain1kDb = double.NaN },
            HighPass = new FilterParameters(true, double.NaN),
            Gate = new GateParameters(true, ThresholdDb: double.NaN, FloorDb: double.NaN),
            Compressor = new CompressorParameters(true, Ratio: double.NaN),
            Limiter = new LimiterParameters(true, CeilingDb: double.NaN),
        };

        StripParameters safe = broken.Normalise();

        Assert.Equal(0, safe.Eq.Gain1kDb);
        Assert.Equal(FilterParameters.MinFrequencyHz, safe.HighPass.FrequencyHz);
        Assert.Equal(-40, safe.Gate.ThresholdDb);
        Assert.Equal(-60, safe.Gate.FloorDb);
        Assert.Equal(4, safe.Compressor.Ratio);
        Assert.Equal(0, safe.Limiter.CeilingDb);
    }

    [Fact]
    public void Apply_clamps_what_it_is_given()
    {
        var strip = new MixerStrip(48000);
        strip.Apply(StripParameters.Neutral with { Eq = EqParameters.Flat with { Gain31Db = 99 } });

        Assert.Equal(EqParameters.MaxGainDb, strip.Parameters.Eq.Gain31Db);
        Assert.Equal(EqParameters.MaxGainDb, strip.Eq.GainDb(0));
    }
}
