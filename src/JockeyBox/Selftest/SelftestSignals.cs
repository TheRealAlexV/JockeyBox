using System;
using System.Collections.Generic;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Selftest;

/// <summary>
/// The measurements the self-test asserts on. Deliberately dependency-free:
/// peak, RMS, zero-crossing frequency, a single-bin Goertzel amplitude at a
/// known frequency, and run detection for locating bursts in the captured
/// programme. Every one of them is a plain loop over the captured buffer, so a
/// failing case can be reasoned about without a plotting library.
/// </summary>
public static class SelftestSignals
{
    /// <summary>Linear amplitude to dBFS (0 when silent).</summary>
    public static double Db(double amplitude) =>
        amplitude <= 0 ? -200.0 : 20.0 * Math.Log10(amplitude);

    /// <summary>dBFS to linear amplitude.</summary>
    public static double FromDb(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>Peak across every channel in <c>[offset, offset+count)</c> samples.</summary>
    public static double Peak(float[] buffer, int offset, int count)
    {
        double peak = 0;
        int end = Math.Min(buffer.Length, offset + count);
        for (int i = Math.Max(0, offset); i < end; i++)
        {
            double a = Math.Abs(buffer[i]);
            if (a > peak) peak = a;
        }
        return peak;
    }

    public static double PeakDb(float[] buffer) => Db(Peak(buffer, 0, buffer.Length));

    /// <summary>RMS across every channel of a frame range.</summary>
    public static double Rms(float[] buffer, int startFrame, int frames)
    {
        int first = Math.Max(0, startFrame) * AudioEngine.Channels;
        int last = Math.Min(buffer.Length, (startFrame + frames) * AudioEngine.Channels);
        if (last <= first) return 0;

        double sum = 0;
        for (int i = first; i < last; i++)
        {
            double x = buffer[i];
            sum += x * x;
        }
        return Math.Sqrt(sum / (last - first));
    }

    public static double RmsDb(float[] buffer, int startFrame, int frames) =>
        Db(Rms(buffer, startFrame, frames));

    /// <summary>Number of samples that are not exactly zero — the "is it silent" test.</summary>
    public static int CountNonZero(float[] buffer, int offset, int count)
    {
        int n = 0;
        int end = Math.Min(buffer.Length, offset + count);
        for (int i = Math.Max(0, offset); i < end; i++)
            if (buffer[i] != 0f) n++;
        return n;
    }

    /// <summary>Index of the first non-zero sample, or -1 (samples, not frames).</summary>
    public static int FirstNonZero(float[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            if (buffer[i] != 0f) return i;
        return -1;
    }

    /// <summary>
    /// Index of the first frame in which <i>any</i> channel is non-zero, or -1.
    /// This is the unit a delay measurement must use: <see cref="FirstNonZero"/>
    /// counts interleaved samples, so on a stereo bus an impulse at frame 12000
    /// reports as sample 24000 and looks exactly like a doubled delay.
    /// </summary>
    public static int FirstNonZeroFrame(float[] buffer)
    {
        int frames = buffer.Length / AudioEngine.Channels;
        for (int f = 0; f < frames; f++)
        {
            int i = f * AudioEngine.Channels;
            for (int c = 0; c < AudioEngine.Channels; c++)
                if (buffer[i + c] != 0f) return f;
        }
        return -1;
    }

    /// <summary>
    /// Dominant frequency from positive-going zero crossings over a frame range
    /// (channel 0). Interpolated, so it is not limited by the sample grid; the
    /// caller resolves a start-up transient by measuring a window well inside the
    /// steady state.
    /// </summary>
    public static double ZeroCrossFrequency(float[] buffer, int startFrame, int frames)
    {
        int first = Math.Max(1, startFrame);
        int last = Math.Min(buffer.Length / AudioEngine.Channels, startFrame + frames);
        if (last - first < 3) return 0;

        int crossings = 0;
        double firstSeconds = 0, lastSeconds = 0;
        float previous = buffer[(first - 1) * AudioEngine.Channels];

        for (int f = first; f < last; f++)
        {
            float current = buffer[f * AudioEngine.Channels];
            if (previous < 0f && current >= 0f)
            {
                // Linear interpolation of the crossing between the two samples.
                double frac = -previous / (current - previous);
                double seconds = (f - 1 + frac) / AudioEngine.Rate;
                if (crossings == 0) firstSeconds = seconds;
                lastSeconds = seconds;
                crossings++;
            }
            previous = current;
        }

        if (crossings < 2 || lastSeconds <= firstSeconds) return 0;
        return (crossings - 1) / (lastSeconds - firstSeconds);
    }

    /// <summary>
    /// Amplitude of a known tone: one Goertzel bin at <paramref name="hertz"/> over
    /// <c>[startFrame, startFrame+frames)</c> of channel 0. Exact when the window
    /// spans a whole number of periods, which is why the crossfade case measures
    /// 50 ms windows (22 periods of 440 Hz, 33 of 660 Hz).
    /// </summary>
    public static double ToneAmplitude(float[] buffer, int startFrame, int frames, double hertz)
    {
        int first = Math.Max(0, startFrame);
        int last = Math.Min(buffer.Length / AudioEngine.Channels, startFrame + frames);
        if (last <= first) return 0;

        double step = 2.0 * Math.PI * hertz / AudioEngine.Rate;
        double re = 0, im = 0;
        for (int f = first; f < last; f++)
        {
            double x = buffer[f * AudioEngine.Channels];
            double w = step * f;
            re += x * Math.Cos(w);
            im += x * Math.Sin(w);
        }

        return 2.0 * Math.Sqrt(re * re + im * im) / (last - first);
    }

    /// <summary>A contiguous stretch of audio above the gate.</summary>
    public readonly record struct AudioRun(double StartSeconds, double LengthSeconds,
        double PeakSeconds, double Peak);

    /// <summary>
    /// Locate audible runs (1 ms resolution, gate on channel 0) so a case can talk
    /// about "the first burst" instead of guessing where it landed. A run's
    /// <see cref="AudioRun.PeakSeconds"/> is the sample-exact position of its peak,
    /// which is what the tempo case measures: the <i>distance</i> between two
    /// bursts, which cancels the stretcher's constant group delay.
    /// </summary>
    public static List<AudioRun> Runs(float[] buffer, int frames, double gate = 0.15)
    {
        var runs = new List<AudioRun>();
        int hop = Math.Max(1, AudioEngine.Rate / 1000);
        int open = -1;
        double peak = 0, peakAt = 0;

        for (int f = 0; f < frames; f += hop)
        {
            int end = Math.Min(frames, f + hop);
            double local = 0;
            int localAt = f;

            for (int i = f; i < end; i++)
            {
                double a = Math.Abs(buffer[i * AudioEngine.Channels]);
                if (a > local) { local = a; localAt = i; }
            }

            if (local > gate)
            {
                if (open < 0) { open = f; peak = 0; peakAt = f; }
                if (local > peak) { peak = local; peakAt = localAt; }
            }
            else if (open >= 0)
            {
                runs.Add(new AudioRun(open / (double)AudioEngine.Rate,
                    (f - open) / (double)AudioEngine.Rate,
                    peakAt / (double)AudioEngine.Rate, peak));
                open = -1;
            }
        }

        if (open >= 0)
            runs.Add(new AudioRun(open / (double)AudioEngine.Rate,
                (frames - open) / (double)AudioEngine.Rate,
                peakAt / (double)AudioEngine.Rate, peak));

        return runs;
    }
}
