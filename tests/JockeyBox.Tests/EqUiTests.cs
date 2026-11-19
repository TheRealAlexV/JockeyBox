using System;
using System.IO;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The mix EQ's control surface, not its filters — the shelves, the clamp and the
/// centre-detent transparency are pinned in <see cref="EqTests"/>. What is pinned
/// here is the half a filter test cannot see: that a saved band reaches the view
/// model at start-up, and that a band the operator moves reaches the file. The last
/// link, the view model to the engine, is measured end to end by <c>--audiotrace</c>
/// rather than asserted against a double.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class EqUiTests : IDisposable
{
    private readonly string _home;
    private readonly HomeLease _lease;

    public EqUiTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "jb-eq-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _lease = HomeLease.Enter(_home, nameof(EqUiTests));
    }

    public void Dispose()
    {
        _lease.Dispose();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A render thread still holding a log file is not a test failure.
        }
    }

    [Fact]
    public void InitializeEq_AdoptsTheSavedBands()
    {
        var saved = AppSettings.Load();
        saved.EqLowDb = -14;
        saved.EqMidDb = 3.5;
        saved.EqHighDb = -26;
        saved.Save();

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() => vm.InitializeEq());

            Assert.Equal(-14, vm.EqLowDb);
            Assert.Equal(3.5, vm.EqMidDb);
            Assert.Equal(-26, vm.EqHighDb);
            Assert.Equal("−14.0 dB", vm.EqLowText);
            Assert.False(vm.IsEqLowBypassed);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    [Fact]
    public void MovingABand_WritesItToTheFile()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() => vm.InitializeEq());

            UiThread.Run(() => vm.EqMidDb = -18);

            // The file is the point: a restart reloads this and nothing else.
            var reloaded = AppSettings.Load();
            Assert.Equal(0, reloaded.EqLowDb);
            Assert.Equal(-18, reloaded.EqMidDb);
            Assert.Equal(0, reloaded.EqHighDb);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    [Fact]
    public void TheReadout_ShowsBypassOnlyAtZero()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() => vm.InitializeEq());

            Assert.Equal("0.0 dB", vm.EqLowText);
            Assert.True(vm.IsEqLowBypassed);

            UiThread.Run(() => vm.EqLowDb = 6);
            Assert.Equal("+6.0 dB", vm.EqLowText);
            Assert.False(vm.IsEqLowBypassed);

            UiThread.Run(() => vm.EqLowDb = -26);
            Assert.Equal("−26.0 dB", vm.EqLowText);
            Assert.False(vm.IsEqLowBypassed);

            // A drag can land a hair off the detent; the readout must not print "-0.0".
            UiThread.Run(() => vm.EqHighDb = -0.02);
            Assert.Equal("0.0 dB", vm.EqHighText);
            Assert.True(vm.IsEqHighBypassed);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    [Fact]
    public void TheFullBoostAndTheFullCut_ReachTheEngineAndTheFileUnclamped()
    {
        // The widened range is only real if every door agrees: the view model takes
        // ±50, the engine's own clamp keeps it, and the file reloads it as ±50 rather
        // than as the old -26/+6. A slider that offers ±50 while the engine still
        // held +6 would be a slider that lies.
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() => vm.InitializeEq());

            UiThread.Run(() => vm.EqLowDb = 50);
            UiThread.Run(() => vm.EqMidDb = -50);

            Assert.Equal(50, vm.EqLowDb);
            Assert.Equal("+50.0 dB", vm.EqLowText);
            Assert.Equal(50f, vm.Transport.Engine.Eq.TargetDb(EqBand.Low));

            Assert.Equal(-50, vm.EqMidDb);
            Assert.Equal("−50.0 dB", vm.EqMidText);
            Assert.Equal(-50f, vm.Transport.Engine.Eq.TargetDb(EqBand.Mid));

            var reloaded = AppSettings.Load();
            Assert.Equal(50, reloaded.EqLowDb);
            Assert.Equal(-50, reloaded.EqMidDb);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }
}
