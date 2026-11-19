using System;
using System.Collections.Generic;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>A cue pad placed on the waveform strip: a tick at <paramref name="X"/>, labelled with its slot.</summary>
public readonly record struct CueMarker(double X, string Label);

/// <summary>
/// The arithmetic behind the deck waveform strip, kept free of WPF so it can be
/// pinned down by tests: pointer x to media seconds, seconds back to x, and where
/// a bank's cue pads land. Every result is clamped to the track.
/// </summary>
public static class WaveformGeometry
{
    /// <summary>Media seconds under a pointer, clamped to <c>[0, duration]</c>.</summary>
    public static double SecondsAtX(double x, double width, double duration)
    {
        if (width <= 0 || duration <= 0 || double.IsNaN(x)) return 0;
        return Math.Clamp(x, 0, width) / width * duration;
    }

    /// <summary>Pixel x for a media time, clamped to <c>[0, width]</c>.</summary>
    public static double XForSeconds(double seconds, double width, double duration)
    {
        if (width <= 0 || duration <= 0 || double.IsNaN(seconds)) return 0;
        return Math.Clamp(seconds, 0, duration) / duration * width;
    }

    /// <summary>
    /// One marker per occupied pad, in slot order. Empty pads draw nothing: a pad
    /// with no position has nowhere on the track to point at.
    /// </summary>
    public static IReadOnlyList<CueMarker> CueMarkers(IEnumerable<CueSlotViewModel>? slots,
                                                      double width, double duration)
    {
        var markers = new List<CueMarker>();
        if (slots == null || width <= 0 || duration <= 0) return markers;

        foreach (var slot in slots)
        {
            if (!slot.HasPosition) continue;
            var x = XForSeconds(slot.PositionMs / 1000.0, width, duration);
            markers.Add(new CueMarker(x, slot.SlotText));
        }
        return markers;
    }
}

/// <summary>
/// The state of one drag on a waveform strip: started on press, moved with the
/// pointer without ever seeking, and released exactly once. The view keeps the
/// preview in step with <see cref="X"/>; only <see cref="Release"/> yields a
/// position to seek to, and only the first release does.
/// </summary>
public sealed class WaveformDrag
{
    private double _width;
    private double _duration;

    /// <summary>True between a successful <see cref="Begin"/> and its release.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The clamped pointer x the preview should sit at.</summary>
    public double X { get; private set; }

    /// <summary>
    /// Start a drag. False when the strip has nothing to seek across (no size, no
    /// loaded track); the caller must then leave the deck alone.
    /// </summary>
    public bool Begin(double x, double width, double duration)
    {
        if (IsActive || width <= 0 || duration <= 0) return false;
        _width = width;
        _duration = duration;
        X = Math.Clamp(x, 0, width);
        IsActive = true;
        return true;
    }

    /// <summary>Track the pointer. Never seeks; false once the drag has ended.</summary>
    public bool Move(double x)
    {
        if (!IsActive) return false;
        X = Math.Clamp(x, 0, _width);
        return true;
    }

    /// <summary>
    /// End the drag and return the media seconds to seek to, or null when there
    /// was nothing to end. A second call returns null, so a release that arrives
    /// twice (mouse-up and lost capture) still seeks once.
    /// </summary>
    public double? Release(double? x = null)
    {
        if (!IsActive) return null;
        if (x is { } last) X = Math.Clamp(last, 0, _width);
        IsActive = false;
        return WaveformGeometry.SecondsAtX(X, _width, _duration);
    }
}
