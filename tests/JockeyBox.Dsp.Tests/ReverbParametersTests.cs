using JockeyBox.Dsp.Reverb;

namespace JockeyBox.Dsp.Tests;

public class ReverbParametersTests
{
    [Fact]
    public void Wet_is_capped_at_MaxWet_from_a_default_struct_and_from_an_overshoot()
    {
        // A default struct has its backing field at zero; the setter is the only gate that
        // matters, so it must clamp even when the struct never saw the three-argument ctor.
        var fromDefault = default(ReverbParameters);
        fromDefault.Wet = 1.0;
        Assert.Equal(0.4, fromDefault.Wet);

        var fromConstructor = new ReverbParameters(true, 0.75, 1.0);
        Assert.Equal(0.4, fromConstructor.Wet);

        var overshoot = new ReverbParameters(true, 0.5, ReverbParameters.MaxWet) { Wet = 4.2 };
        Assert.Equal(ReverbParameters.MaxWet, overshoot.Wet);
        Assert.Equal(0.4, overshoot.Wet);
    }

    [Fact]
    public void Clamp_is_idempotent()
    {
        var once = new ReverbParameters(true, 1.7, 2.5).Clamp();

        Assert.Equal(1.0, once.Size);
        Assert.Equal(0.4, once.Wet);
        Assert.Equal(once, once.Clamp());
        Assert.Equal(once, once.Clamp().Clamp());
    }
}
