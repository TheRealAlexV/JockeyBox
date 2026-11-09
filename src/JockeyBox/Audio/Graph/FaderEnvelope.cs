using System;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// A crossfader position ramp expressed in <b>engine frames</b>, not wall clock.
///
/// Everything time-critical in the 2.0 engine is scheduled against the render
/// thread's frame counter, so a fade cannot drift, cannot be starved by the UI
/// dispatcher, and is reproducible offline (see <c>--selftest</c>). The old
/// 16 ms <c>DispatcherTimer</c> crossfade is gone.
///
/// The envelope carries the <i>fader position</i> (0 = deck A, 1 = deck B);
/// equal-power gains are derived from it by <see cref="Crossfade.GainA"/> /
/// <see cref="Crossfade.GainB"/>, exactly as the 1.x transport did, so a linear
/// fader ramp still produces the familiar cos/sin equal-power fade.
/// </summary>
public sealed class FaderEnvelope
{
    /// <summary>Fader position before <see cref="StartFrame"/>.</summary>
    public double From { get; private set; } = 0.5;

    /// <summary>Fader position once the ramp has finished.</summary>
    public double To { get; private set; } = 0.5;

    /// <summary>First engine frame of the ramp.</summary>
    public long StartFrame { get; private set; }

    /// <summary>Length of the ramp in engine frames; 0 means "snap".</summary>
    public long FrameCount { get; private set; }

    /// <summary>True while a ramp is still in progress at <paramref name="frame"/>.</summary>
    public bool IsRunning(long frame) => FrameCount > 0 && frame >= StartFrame && frame < StartFrame + FrameCount;

    /// <summary>True once the ramp has reached its target (or was a snap).</summary>
    public bool IsComplete(long frame) => frame >= StartFrame + FrameCount;

    /// <summary>Ramp position 0..1 for the given frame, clamped outside the window.</summary>
    public double ProgressAt(long frame)
    {
        if (FrameCount <= 0) return frame >= StartFrame ? 1.0 : 0.0;
        if (frame <= StartFrame) return 0.0;
        if (frame >= StartFrame + FrameCount) return 1.0;
        return (frame - StartFrame) / (double)FrameCount;
    }

    /// <summary>Fader position at an engine frame.</summary>
    public double FaderAt(long frame)
    {
        if (FrameCount <= 0) return frame >= StartFrame ? To : From;
        if (frame <= StartFrame) return From;
        if (frame >= StartFrame + FrameCount) return To;
        return From + (To - From) * ProgressAt(frame);
    }

    /// <summary>Start a ramp at <paramref name="startFrame"/> lasting one frame (an immediate snap).</summary>
    public void Snap(double to, long startFrame)
    {
        From = to;
        To = to;
        StartFrame = startFrame;
        FrameCount = 1;
    }

    /// <summary>Start a ramp. A non-positive length is treated as a snap.</summary>
    public void Ramp(double from, double to, long startFrame, long frameCount)
    {
        From = from;
        To = to;
        StartFrame = startFrame;
        FrameCount = frameCount <= 0 ? 0 : frameCount;
    }

    /// <summary>Cancel a running ramp where it currently is (used when a hand takes the fader).</summary>
    public void FreezeAt(long frame)
    {
        if (!IsRunning(frame)) return;
        From = FaderAt(frame);
        To = From;
        StartFrame = frame;
        FrameCount = 0;
    }

    /// <summary>Put the envelope at rest without a ramp, as if it had always been there.</summary>
    public void Reset(double value)
    {
        From = value;
        To = value;
        StartFrame = 0;
        FrameCount = 0;
    }
}

/// <summary>Equal-power crossfade curves shared by the transport and the UI mirror.</summary>
public static class Crossfade
{
    /// <summary>Deck A gain for a fader position (equal power: cos).</summary>
    public static double GainA(double fader) => Math.Cos(Clamp01(fader) * Math.PI / 2.0);

    /// <summary>Deck B gain for a fader position (equal power: sin).</summary>
    public static double GainB(double fader) => Math.Sin(Clamp01(fader) * Math.PI / 2.0);

    /// <summary>Clamp helper used by every fader consumer.</summary>
    public static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
