namespace JockeyBox.Dsp.Tests;

public class ControlRegistryTests
{
    private static ControlRegistry SampleRegistry()
    {
        var registry = new ControlRegistry();
        registry.Register(new ControlDescriptor("eq.band1.gain", "31 Hz", "EQ", ControlScope.PerChannel,
            ControlKind.Continuous, Min: -15, Max: 15, Step: 0.1, Default: 0, Unit: "dB"));
        registry.Register(new ControlDescriptor("eq.band1.freq", "Centre", "EQ", ControlScope.PerChannel,
            ControlKind.Continuous, Min: 20, Max: 20000, Step: 1, Default: 31.5, Unit: "Hz"));
        registry.Register(new ControlDescriptor("gate.enabled", "Gate", "Dynamics", ControlScope.PerChannel,
            ControlKind.Toggle, Default: 1));
        registry.Register(new ControlDescriptor("limiter.mode", "Mode", "Dynamics", ControlScope.Global,
            ControlKind.Choice, Default: 0, Choices: new[] { "Peak", "RMS" }));
        return registry;
    }

    [Fact]
    public void Duplicate_id_throws_with_the_id_in_the_message()
    {
        var registry = SampleRegistry();
        var duplicate = new ControlDescriptor("eq.band1.gain", "Dup", "EQ", ControlScope.PerChannel,
            ControlKind.Continuous);

        var exception = Assert.Throws<ArgumentException>(() => registry.Register(duplicate));

        Assert.Contains("eq.band1.gain", exception.Message);
    }

    [Fact]
    public void Every_continuous_control_has_a_range_and_a_unit()
    {
        var registry = SampleRegistry();
        int continuous = 0;

        foreach (ControlDescriptor control in registry.All)
        {
            if (control.Kind != ControlKind.Continuous) continue;
            continuous++;

            Assert.True(control.Min < control.Max,
                $"{control.Id}: Min {control.Min} is not below Max {control.Max}");
            Assert.False(string.IsNullOrWhiteSpace(control.Unit), $"{control.Id}: Unit is empty");
        }

        Assert.Equal(2, continuous);
    }

    [Fact]
    public void Try_get_finds_a_registered_control_and_misses_an_unknown_one()
    {
        var registry = SampleRegistry();

        Assert.True(registry.TryGet("gate.enabled", out ControlDescriptor? found));
        Assert.NotNull(found);
        Assert.Equal(ControlKind.Toggle, found.Kind);

        Assert.False(registry.TryGet("nope", out ControlDescriptor? missing));
        Assert.Null(missing);
    }

    [Fact]
    public void All_preserves_registration_order()
    {
        var registry = SampleRegistry();
        Assert.Equal(new[] { "eq.band1.gain", "eq.band1.freq", "gate.enabled", "limiter.mode" },
            registry.All.Select(control => control.Id).ToArray());
    }
}
