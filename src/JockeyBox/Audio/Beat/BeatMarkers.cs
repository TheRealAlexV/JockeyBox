using System;
using System.Collections.Generic;

namespace JockeyBox.Audio.Beat;

/// <summary>
/// Turns an analysed grid into the normalised tick positions the waveform draws
/// (requirement 9: beat indicators on the waveform).
///
/// <para>Kept out of the view model so the mapping is testable without a device:
/// the view model only supplies the track length and stores the result.</para>
/// </summary>
public static class BeatMarkers
{
    /// <summary>
    /// Ticks in 0..1 of the track, starting on the first measured beat. Capped at
    /// <see cref="MaxTicks"/>: a ten-minute set at 174 BPM would be 1740 ticks and
    /// they stop being visual information long before that.
    /// </summary>
    public const int MaxTicks = 1200;

    public static List<double> Build(BeatGrid? grid, double durationSeconds)
    {
        var markers = new List<double>();
        if (grid == null || durationSeconds <= 0) return markers;

        double beat = grid.BeatSeconds;
        if (beat <= 0) return markers;

        double t = grid.FirstBeatSeconds;
        while (t < durationSeconds && markers.Count < MaxTicks)
        {
            markers.Add(Math.Clamp(t / durationSeconds, 0, 1));
            t += beat;
        }

        return markers;
    }
}
