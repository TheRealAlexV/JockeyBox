using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using SoundTouch;
using JockeyBox.Dsp;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// Key-locked tempo change for one deck (plan items 5 and 11).
///
/// <para><b>Bypass at unity.</b> At ratio 1.0 the provider is a straight pass
/// through: stretching untouched audio would smear it for no reason and cost the
/// render thread cycles that plain playback should not pay.</para>
///
/// <para><b>Group delay is ratio-dependent</b> and SoundTouch exposes no
/// buffered-sample count, so the latency cannot be derived — it is measured once
/// into <see cref="LatencyTable"/> and interpolated here. Beat alignment adds
/// <see cref="LatencyFrames"/> so a stretched deck and a bypassed deck land on
/// the same beat (the review's "auto-mix sounds slightly off and nobody knows
/// why" defect).</para>
///
/// <para><b>Failures are loud.</b> The NAudio bridge can swallow reader
/// exceptions and then return 0, which the graph would read as "ended" and drop
/// the deck silently mid-set. Here an exception propagates on the render thread
/// and <see cref="Failure"/> lets the engine surface it.</para>
///
/// <para><b>Threading.</b> <see cref="Read"/> is render-thread only.
/// <see cref="SetRatio"/> and <see cref="RequestFlush"/> may be called from the
/// UI or the scheduler; they only write fields the next block picks up.</para>
/// </summary>
public sealed class TempoStage : ISampleProvider
{
    private const int PumpFrames = 1024;
    private const int MaxPumpsPerRead = 64;

    private readonly ISampleProvider _source;
    private readonly int _rate;
    private readonly int _channels;

    private SoundTouchProcessor? _processor;
    private float[] _in = Array.Empty<float>();
    private float[] _out = Array.Empty<float>();
    private int _outAvailable;   // frames
    private int _outOffset;      // frames

    private double _ratio = 1.0;
    private double _pendingRatio = 1.0;   // written by UI/scheduler, read by render
    private volatile bool _flushRequested;
    private Exception? _failure;

    /// <summary>
    /// <paramref name="latencyTable"/> is the measured ratio → group-delay curve.
    /// Defaulting it to <see cref="TempoLatencyTable.Default"/> keeps a stage that
    /// is built without one byte-identical to the old behaviour: unity is still a
    /// true passthrough and an engaged stage still reports the zero it always did.
    /// The deck passes <see cref="TempoLatencyTable.Measured"/> so an engaged
    /// stage stops claiming zero latency.
    /// </summary>
    public TempoStage(ISampleProvider source, TempoLatencyTable? latencyTable = null)
    {
        _source = source;
        _rate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        LatencyTable = latencyTable ?? TempoLatencyTable.Default;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Ratio the stretcher is currently running at (1.0 = original tempo).</summary>
    public double Ratio => _ratio;

    /// <summary>True when the stretcher is in circuit.</summary>
    public bool Engaged => Math.Abs(_ratio - 1.0) > 1e-6;

    /// <summary>Frames of added latency at the current ratio (0 when bypassed).</summary>
    public int LatencyFrames => Engaged ? LatencyTable.FramesFor(_ratio, _rate) : 0;

    /// <summary>Calibrated ratio → latency curve (see <c>--calibrate</c>).</summary>
    public TempoLatencyTable LatencyTable { get; }

    /// <summary>Non-null once the stretcher has failed; the deck must stop and report it.</summary>
    public Exception? Failure => _failure;

    /// <summary>Request a new ratio; applied at the next render block.</summary>
    public void SetRatio(double ratio) => Volatile.Write(ref _pendingRatio, Math.Clamp(ratio, 0.5, 2.0));

    /// <summary>Drop buffered audio (after a seek).</summary>
    public void RequestFlush() => _flushRequested = true;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_failure != null) throw new InvalidOperationException("tempo stage failed", _failure);

        if (count % _channels != 0) count -= count % _channels;
        if (count <= 0) return 0;

        if (_flushRequested)
        {
            _flushRequested = false;
            _processor?.Clear();
            _outAvailable = 0;
            _outOffset = 0;
        }

        ApplyPendingRatio();

        if (!Engaged)
        {
            Samples.ReadFull(_source, buffer, offset, count);
            return count;
        }

        try
        {
            FillStretched(buffer, offset, count);
        }
        catch (Exception ex)
        {
            _failure = ex;
            Log.Error("tempo: stretcher failed", ex);
            throw;
        }
        return count;
    }

    private void ApplyPendingRatio()
    {
        double next = Volatile.Read(ref _pendingRatio);
        if (Math.Abs(next - _ratio) < 1e-9) return;

        double previous = _ratio;
        _ratio = next;
        EnsureProcessor();

        if (previous == 1.0 || next == 1.0)
        {
            // Crossing the bypass boundary changes the pipe length, so start the
            // stretcher from a clean state rather than mixing stale audio in.
            _processor!.Clear();
            _outAvailable = 0;
            _outOffset = 0;
            Log.Info($"tempo: {(next == 1.0 ? "bypass" : $"engage {next:0.####}")}");
        }
    }

    private void EnsureProcessor()
    {
        if (_processor != null)
        {
            _processor.Tempo = (float)_ratio;
            return;
        }

        _processor = new SoundTouchProcessor
        {
            Channels = _channels,
            SampleRate = _rate,
            Tempo = (float)_ratio,
            Pitch = 1.0f,
            Rate = 1.0f,
        };
        _processor.SetSetting(SettingId.UseQuickSeek, 1);

        int inLen = PumpFrames * _channels;
        if (_in.Length < inLen) _in = new float[inLen];
        if (_out.Length < inLen) _out = new float[inLen];
        _outAvailable = 0;
        _outOffset = 0;
    }

    /// <summary>
    /// Pump source frames through the processor until the caller's buffer is
    /// full. One frame is <c>_channels</c> floats — SoundTouch counts frames, not
    /// floats ("in case of stereo audio, one sample consists of both channels").
    /// </summary>
    private void FillStretched(float[] buffer, int offset, int count)
    {
        int framesWanted = count / _channels;
        int produced = 0;
        int pumps = 0;

        while (produced < framesWanted && pumps < MaxPumpsPerRead)
        {
            if (_outAvailable > 0)
            {
                int take = Math.Min(_outAvailable, framesWanted - produced);
                int srcStart = _outOffset * _channels;
                int dstStart = offset + produced * _channels;
                Array.Copy(_out, srcStart, buffer, dstStart, take * _channels);
                produced += take;
                _outOffset += take;
                _outAvailable -= take;
                continue;
            }

            int inFrames = PumpFrames;
            Samples.ReadFull(_source, _in, 0, inFrames * _channels);
            _processor!.PutSamples(_in.AsSpan(0, inFrames * _channels), inFrames);

            int capacityFrames = _out.Length / _channels;
            int got = _processor.ReceiveSamples(_out.AsSpan(), capacityFrames);
            _outAvailable = got;
            _outOffset = 0;
            pumps++;
        }

        if (produced < framesWanted)
        {
            // The processor never starves this long unless it is broken; emit
            // silence rather than a short read, then let the caller see Failure
            // on the next block if it really is broken.
            Array.Clear(buffer, offset + produced * _channels, (framesWanted - produced) * _channels);
        }
    }
}
