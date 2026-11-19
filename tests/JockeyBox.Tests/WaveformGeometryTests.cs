using System;
using System.Collections.Generic;
using System.Linq;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The waveform strip's drag arithmetic (the pure half of drag-to-seek and of the
/// cue markers). The handlers only move pixels; these cases pin down the mapping,
/// the clamping at both edges, and the promise that a move never seeks.
/// </summary>
public sealed class WaveformGeometryTests
{
    private const double Width = 400;
    private const double Duration = 200; // 2 px per second, so the numbers read plainly

    // ---- seconds <-> x -----------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    [InlineData(400, 200)]
    public void SecondsAtXMapsTheStripEndToEnd(double x, double expected)
    {
        Assert.Equal(expected, WaveformGeometry.SecondsAtX(x, Width, Duration), 6);
    }

    [Fact]
    public void SecondsAtXClampsOutsideTheStripAndWithoutATrack()
    {
        Assert.Equal(0, WaveformGeometry.SecondsAtX(-50, Width, Duration));
        Assert.Equal(Duration, WaveformGeometry.SecondsAtX(9999, Width, Duration));
        Assert.Equal(0, WaveformGeometry.SecondsAtX(100, 0, Duration));
        Assert.Equal(0, WaveformGeometry.SecondsAtX(100, Width, 0));
        Assert.Equal(0, WaveformGeometry.SecondsAtX(double.NaN, Width, Duration));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 100)]
    [InlineData(200, 400)]
    public void XForSecondsIsTheInverse(double seconds, double expected)
    {
        Assert.Equal(expected, WaveformGeometry.XForSeconds(seconds, Width, Duration), 6);
    }

    [Fact]
    public void XForSecondsClampsOutsideTheTrack()
    {
        Assert.Equal(0, WaveformGeometry.XForSeconds(-1, Width, Duration));
        Assert.Equal(Width, WaveformGeometry.XForSeconds(Duration + 10, Width, Duration));
        Assert.Equal(0, WaveformGeometry.XForSeconds(50, 0, Duration));
        Assert.Equal(0, WaveformGeometry.XForSeconds(50, Width, 0));
    }

    [Fact]
    public void TheMappingRoundTrips()
    {
        foreach (var x in new[] { 0.0, 37.5, 200.0, 399.0, 400.0 })
            Assert.Equal(x, WaveformGeometry.XForSeconds(
                WaveformGeometry.SecondsAtX(x, Width, Duration), Width, Duration), 6);
    }

    // ---- drag release ------------------------------------------------------

    [Fact]
    public void ADragReleaseSeeksOnceWithTheMappedPosition()
    {
        var seeks = new List<double>();
        var drag = new WaveformDrag();

        Assert.True(drag.Begin(50, Width, Duration));
        Assert.True(drag.Move(120));
        Assert.True(drag.Move(300));
        Assert.Empty(seeks); // nothing is seeked while the pointer moves

        if (drag.Release() is { } seconds) seeks.Add(seconds);
        Assert.Null(drag.Release()); // mouse-up followed by lost capture

        Assert.Single(seeks);
        Assert.Equal(150.0, seeks[0], 6); // x=300 of 400 over 200 s
    }

    [Fact]
    public void MoveNeverReturnsAPositionToSeekTo()
    {
        var drag = new WaveformDrag();
        Assert.True(drag.Begin(10, Width, Duration));

        // Move's contract is a bool (did the preview move), not seconds: a caller
        // cannot accidentally seek mid-drag because there is nothing to seek with.
        Assert.True(drag.Move(250));
        Assert.Equal(250, drag.X, 6);
        Assert.True(drag.IsActive);
    }

    [Fact]
    public void ReleaseClampsToTheEdgesAndUsesTheLastPointerXWhenGivenNone()
    {
        var drag = new WaveformDrag();
        Assert.True(drag.Begin(0, Width, Duration));
        drag.Move(Width + 60); // pointer dragged past the right edge
        Assert.Equal(Duration, drag.Release()!.Value, 6);

        var lost = new WaveformDrag();
        Assert.True(lost.Begin(-20, Width, Duration));
        Assert.Equal(0, lost.X);
        Assert.Equal(0, lost.Release()!.Value, 6); // lost capture: no position supplied
    }

    [Fact]
    public void AnEmptyStripOrASecondBeginStartsNoDrag()
    {
        var drag = new WaveformDrag();
        Assert.False(drag.Begin(10, 0, Duration));
        Assert.False(drag.Begin(10, Width, 0));
        Assert.False(drag.IsActive);
        Assert.Null(drag.Release());
        Assert.False(drag.Move(10));

        Assert.True(drag.Begin(10, Width, Duration));
        Assert.False(drag.Begin(20, Width, Duration)); // one drag at a time
        Assert.Equal(10, drag.X);
    }

    // ---- cue markers -------------------------------------------------------

    private static CueBankViewModel Bank(Func<int, long> positionMs, Func<int, bool> occupied)
    {
        // A real bank: the marker layout is defined over what the pads actually
        // carry, not over a parallel test-only structure.
        var bank = new CueBankViewModel(1, () => 0, _ => { }, () => null);
        for (int i = 1; i <= 8; i++)
        {
            bank.Slots[i - 1].HasPosition = occupied(i);
            bank.Slots[i - 1].PositionMs = positionMs(i);
        }
        return bank;
    }

    [Fact]
    public void EightOccupiedSlotsProduceEightLabelledMarkers()
    {
        var bank = Bank(i => i * 25_000, _ => true); // 25 s apart over a 200 s track

        var markers = WaveformGeometry.CueMarkers(bank.Slots, Width, Duration);

        Assert.Equal(8, markers.Count);
        Assert.Equal(new[] { "1", "2", "3", "4", "5", "6", "7", "8" }, markers.Select(m => m.Label));
        for (int i = 0; i < 8; i++) Assert.Equal((i + 1) * 50.0, markers[i].X, 6);
    }

    [Fact]
    public void AnEmptyBankDrawsNothing()
    {
        var bank = Bank(_ => 0, _ => false);

        Assert.Empty(WaveformGeometry.CueMarkers(bank.Slots, Width, Duration));
        Assert.Empty(WaveformGeometry.CueMarkers(null, Width, Duration));
    }

    [Fact]
    public void OnlyOccupiedPadsDrawAndPositionsAreClampedToTheStrip()
    {
        var bank = Bank(i => i == 1 ? 500_000 : 40_000, i => i is 1 or 3 or 5);

        var markers = WaveformGeometry.CueMarkers(bank.Slots, Width, Duration);

        Assert.Equal(new[] { "1", "3", "5" }, markers.Select(m => m.Label));
        Assert.Equal(Width, markers[0].X, 6);   // 500 s on a 200 s track: right edge
        Assert.Equal(80.0, markers[1].X, 6);    // 40 s
        Assert.Equal(80.0, markers[2].X, 6);
    }
}
