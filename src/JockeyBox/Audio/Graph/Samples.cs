using System;
using NAudio.Wave;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// Small, allocation-free buffer helpers shared by the 2.0 render path.
///
/// They look trivial but each one encodes a rule that the 1.x engine did not
/// have and that the new graph depends on:
/// <list type="bullet">
/// <item><see cref="ReadFull"/> — a short or zero read must never reach the
/// mixer. NAudio treats a 0-length read as "input ended" and drops the provider,
/// so a paused deck would be silently removed mid-fade; and a short read from
/// one deck inserts a hole and desynchronises it from the shared frame index.</item>
/// <item><see cref="SoftLimit"/> — four independent WASAPI sessions never summed,
/// so the 1.x signal could not clip. One bus means both decks can be at 0.707
/// together and sum to 1.414, so the bus needs headroom.</item>
/// </list>
/// </summary>
public static class Samples
{
    /// <summary>
    /// Read exactly <paramref name="count"/> samples from a provider, padding the
    /// tail with silence if it runs dry. Returns the buffer for chaining.
    /// </summary>
    public static float[] ReadFull(ISampleProvider source, float[] buffer, int offset, int count)
    {
        int got = 0;
        while (got < count)
        {
            int read = source.Read(buffer, offset + got, count - got);
            if (read <= 0) break;
            got += read;
        }
        if (got < count) Array.Clear(buffer, offset + got, count - got);
        return buffer;
    }

    /// <summary>Zero a span of an interleaved buffer.</summary>
    public static void Clear(float[] buffer, int offset, int count) => Array.Clear(buffer, offset, count);

    /// <summary><c>dst += src * gain</c> over interleaved samples.</summary>
    public static void AddScaled(float[] dst, float[] src, int offset, int count, float gain)
    {
        if (gain == 0f) return;
        for (int i = 0; i < count; i++) dst[offset + i] += src[offset + i] * gain;
    }

    /// <summary><c>dst = src * gain</c> over interleaved samples.</summary>
    public static void CopyScaled(float[] src, int offset, int count, float gain, float[] dst)
    {
        for (int i = 0; i < count; i++) dst[offset + i] = src[offset + i] * gain;
    }

    /// <summary>
    /// Transparent below <paramref name="threshold"/>, then a tanh knee up to
    /// hard clipping at ±1. Cheap enough for the audio thread and it never
    /// changes the signal below -3 dBFS, so normal listening is untouched.
    /// </summary>
    public static void SoftLimit(float[] buffer, int offset, int count, float threshold = 0.707f)
    {
        float span = 1f - threshold;
        for (int i = 0; i < count; i++)
        {
            float x = buffer[offset + i];
            float a = x < 0 ? -x : x;
            if (a <= threshold) continue;
            float over = (a - threshold) / span;
            float shaped = threshold + span * (float)Math.Tanh(over);
            if (shaped > 1f) shaped = 1f;
            buffer[offset + i] = x < 0 ? -shaped : shaped;
        }
    }

    /// <summary>Peak absolute sample in a buffer (used by <c>--selftest</c> and the level meters).</summary>
    public static float Peak(float[] buffer, int offset, int count)
    {
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float a = buffer[offset + i] < 0 ? -buffer[offset + i] : buffer[offset + i];
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>Peak as dBFS (silence = -inf, floored at -120).</summary>
    public static double PeakDb(float[] buffer, int offset, int count)
    {
        float peak = Peak(buffer, offset, count);
        return peak <= 0f ? -120.0 : 20.0 * Math.Log10(peak);
    }
}

/// <summary>
/// A pull-based ISampleProvider that can turn a provider with an unknown or
/// variable tail into one that always fills the request.
/// </summary>
public sealed class FullReadProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public FullReadProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count) =>
        Samples.ReadFull(_source, buffer, offset, count) is var _ ? count : count;
}

/// <summary>
/// FIFO of actions for the render thread. The UI, the scheduler and hotkeys all
/// mutate deck state by posting here and then forgetting about it, which is the
/// only reason seeks/pauses/tempo changes can be safe without locks: the render
/// thread is the sole reader and writer of reader state.
/// </summary>
public sealed class RenderCommands
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

    /// <summary>Post an action; runs at the top of the next render block.</summary>
    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>Run everything queued so far (render thread only).</summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out Action? action))
        {
            try { action(); }
            catch (Exception ex) { Log.Error("render command failed", ex); }
        }
    }

    /// <summary>Number of pending actions (diagnostics only).</summary>
    public int Count => _queue.Count;
}
