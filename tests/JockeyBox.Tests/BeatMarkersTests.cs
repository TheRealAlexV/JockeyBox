using System;
using System.Linq;
using JockeyBox.Audio.Beat;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The waveform ticks (requirement 9). The view model only hands over the track
/// length, so the mapping from an analysed grid to normalised positions is what
/// these cases pin down.
/// </summary>
public sealed class BeatMarkersTests
{
    private static BeatGrid Grid(double bpm, double firstBeat = 0.25, int beats = 64) => new(
        bpm, firstBeat,
        Enumerable.Range(0, beats).Select(i => firstBeat + i * 60.0 / bpm).ToArray(),
        bpmP10: bpm - 0.5, bpmP90: bpm + 0.5, confidence: 0.9);

    [Fact]
    public void AnAnalysedGridProducesTicksAcrossTheWholeTrack()
    {
        var markers = BeatMarkers.Build(Grid(120.0), durationSeconds: 180);

        Assert.Equal(360, markers.Count); // 120 BPM for 3 minutes
        Assert.All(markers, m => Assert.InRange(m, 0, 1));
        Assert.Equal(0.25 / 180.0, markers[0], 6);
        Assert.Equal(0.5 / 180.0, markers[1] - markers[0], 6); // half a second apart at 120 BPM
        Assert.True(markers[^1] < 1);
        for (var i = 1; i < markers.Count; i++) Assert.True(markers[i] > markers[i - 1]);
    }

    [Fact]
    public void TicksAreSpreadOverTheTrackAndScaleWithTempo()
    {
        double[] slow = BeatMarkers.Build(Grid(90.0), 120).ToArray();
        double[] fast = BeatMarkers.Build(Grid(180.0), 120).ToArray();

        Assert.Equal(180, slow.Length);
        Assert.Equal(360, fast.Length);
        // Same first beat, so the first tick of both sits at the same place.
        Assert.Equal(slow[0], fast[0], 9);
    }

    [Fact]
    public void NoGridMeansNoTicks()
    {
        Assert.Empty(BeatMarkers.Build(null, 200));
        Assert.Empty(BeatMarkers.Build(Grid(120), 0));
        Assert.Empty(BeatMarkers.Build(Grid(120), -1));
        Assert.Empty(BeatMarkers.Build(new BeatGrid(0, 0), 200));
    }

    [Fact]
    public void TickCountIsCappedForVeryLongTracks()
    {
        var markers = BeatMarkers.Build(Grid(174.0), durationSeconds: 3 * 3600);
        Assert.Equal(BeatMarkers.MaxTicks, markers.Count);
    }

    [Fact]
    public void FirstBeatBeyondTheTrackLengthProducesNoTicks()
    {
        var grid = new BeatGrid(120, 500, new[] { 500.0, 500.5 });
        Assert.Empty(BeatMarkers.Build(grid, 200));
    }
}
