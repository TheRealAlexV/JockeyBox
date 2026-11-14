using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// MVSYNC: the link between the monitor bus's two deck sends. A binding that
/// silently fails looks exactly like a working slider, so these tests drive the
/// view model rather than the XAML: both directions, and both link states.
/// </summary>
[Collection(nameof(UiThreadCollection))]
public sealed class MonitorGainTests
{
    [Fact]
    public void SyncOn_MovingAGain_MovesBGain()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        vm.MonitorSyncEnabled = true;
        vm.MonitorAGain = 0.7;
        vm.MonitorBGain = 0.7;

        vm.MonitorAGain = 0.35;

        Assert.Equal(0.35, vm.MonitorBGain, 4);
        Assert.Equal(0.35, vm.MonitorAGain, 4);
    }

    [Fact]
    public void SyncOn_MovingBGain_MovesAGain()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        vm.MonitorSyncEnabled = true;
        vm.MonitorAGain = 0.7;
        vm.MonitorBGain = 0.7;

        vm.MonitorBGain = 1.05;

        Assert.Equal(1.05, vm.MonitorAGain, 4);
        Assert.Equal(1.05, vm.MonitorBGain, 4);
    }

    [Fact]
    public void SyncOff_GainsMoveIndependently()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        vm.MonitorSyncEnabled = false;
        vm.MonitorAGain = 0.7;
        vm.MonitorBGain = 0.7;

        vm.MonitorAGain = 0.2;
        Assert.Equal(0.7, vm.MonitorBGain, 4);

        vm.MonitorBGain = 1.1;
        Assert.Equal(0.2, vm.MonitorAGain, 4);
    }

    /// <summary>
    /// Switching MVSYNC on leaves both gains where they are instead of snapping
    /// them together. A link says how the two sliders respond to a hand; making
    /// it a value change would move one monitor send (audibly) at the moment the
    /// operator pressed a switch that promises no audio change at all.
    /// </summary>
    [Fact]
    public void SyncSwitchedOn_DoesNotJumpEitherGain()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        vm.MonitorSyncEnabled = false;
        vm.MonitorAGain = 0.3;
        vm.MonitorBGain = 0.9;

        vm.MonitorSyncEnabled = true;

        Assert.Equal(0.3, vm.MonitorAGain, 4);
        Assert.Equal(0.9, vm.MonitorBGain, 4);

        // The first hand to move becomes the leader, and everything follows it.
        vm.MonitorAGain = 0.55;
        Assert.Equal(0.55, vm.MonitorBGain, 4);
    }
}
