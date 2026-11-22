namespace JockeyBox.Dsp.Tests;

/// <summary>
/// Rebuilding the registry is how the mixer follows device changes. The failure this guards
/// against is a control that outlives its strip and keeps writing to a device that is gone.
/// </summary>
public class ControlRegistryRebuildTests
{
    private static ControlDescriptor GateThreshold(string captureEndpointId) =>
        new(DeviceControlId.For(captureEndpointId, "gate.threshold"), "Threshold", "Gate",
            ControlScope.PerChannel, ControlKind.Continuous, Min: -80, Max: 0, Step: 0.5, Default: -40,
            Unit: "dB");

    [Fact]
    public void Clear_drops_every_registration()
    {
        var registry = new ControlRegistry();
        registry.Register(GateThreshold("capture:usb-mic-1"));

        registry.Clear();

        Assert.Empty(registry.All);
        Assert.False(registry.TryGet(DeviceControlId.For("capture:usb-mic-1", "gate.threshold"), out _));
    }

    [Fact]
    public void A_descriptor_that_is_not_reregistered_cannot_survive_a_rebuild()
    {
        const string removedDevice = "capture:usb-mic-1";
        const string presentDevice = "capture:usb-mic-2";

        var registry = new ControlRegistry();
        registry.Register(GateThreshold(removedDevice));
        string staleId = DeviceControlId.For(removedDevice, "gate.threshold");
        Assert.True(registry.TryGet(staleId, out _));

        // The device is unplugged: the mixer rebuilds the descriptors for what is still there.
        registry.Replace(new[] { GateThreshold(presentDevice) });

        Assert.False(registry.TryGet(staleId, out _), "a control for a removed device must not survive the rebuild");
        Assert.Single(registry.All);
        Assert.Equal(DeviceControlId.For(presentDevice, "gate.threshold"), registry.All[0].Id);
    }

    [Fact]
    public void Replace_rejects_null()
    {
        var registry = new ControlRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Replace(null!));
    }

    [Fact]
    public void Replace_can_register_the_same_ids_again()
    {
        var registry = new ControlRegistry();
        registry.Register(GateThreshold("capture:usb-mic-1"));

        // Re-registering after a rebuild must not throw as a duplicate.
        registry.Replace(new[] { GateThreshold("capture:usb-mic-1") });

        Assert.Single(registry.All);
    }
}
