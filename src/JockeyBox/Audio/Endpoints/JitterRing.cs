using System;
using NAudio.Wave;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// The buffer between the engine's render thread and one WASAPI device.
///
/// <para>Each sink is pulled by its own <c>WasapiOut</c> thread at the device's
/// clock; the engine pushes blocks at the engine's pace. The two clocks are not
/// the same (a USB interface runs tens of ppm off the system timer), so the
/// buffer slowly fills or empties. Left alone, one sink would eventually overrun
/// while the other starved.</para>
///
/// <para><b>Drift correction is local and explicit.</b> When the depth leaves
/// the band around the target, up to eight blocks are dropped or inserted in one
/// go (bounded by <c>blockFrames * 8</c> so a burst cannot empty the ring
/// audibly), at most once per <see cref="CorrectionIntervalMs"/>. That is
/// audible only as a 10 ms anomaly twice a minute at worst, and it is
/// debuggable — unlike a shared multi-consumer cache where the slowest sink
/// defines the read position for everyone and the buffer grows without bound.</para>
///
/// <para><b>Depth is an explicit count, not a pointer difference.</b> Write and
/// read cursors alone cannot tell "full" from "empty" (they are equal in both
/// states), so a completely full ring would report a depth of 0, serve silence
/// and count underruns while the audio was still buffered. <see cref="_count"/>
/// is the discriminator.</para>
///
/// <para><see cref="Read"/> never returns 0: underrun returns silence. A zero
/// read stops the WasapiOut thread for good.</para>
/// </summary>
public sealed class JitterRing : ISampleProvider
{
    public const int CorrectionIntervalMs = 2000;
    public const int BandLowMs = 40;
    public const int BandHighMs = 220;

    private readonly int _channels;
    private readonly int _rate;
    private readonly float[] _ring;
    private readonly int _ringFrames;
    private int _readFrame;
    private int _writeFrame;

    /// <summary>Frames actually buffered — the full/empty discriminator.</summary>
    private int _count;
    private long _lastCorrectionTicks;
    private long _droppedBlocks;
    private long _insertedBlocks;
    private long _underrunFrames;

    public JitterRing(int sampleRate, int channels, int capacityMs = 1000)
    {
        _rate = sampleRate;
        _channels = channels;
        _ringFrames = Math.Max(1024, sampleRate * capacityMs / 1000);
        _ring = new float[_ringFrames * channels];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Frames currently buffered (exact, including a completely full ring).</summary>
    public int DepthFrames => _count;

    public int TargetFrames { get; set; }
    public int LowFrames { get; set; }
    public int HighFrames { get; set; }

    public long DroppedBlocks => _droppedBlocks;
    public long InsertedBlocks => _insertedBlocks;
    public long UnderrunFrames => _underrunFrames;

    /// <summary>Append a rendered block, dropping the oldest audio if the ring is full.</summary>
    public void Write(float[] interleaved, int offset, int frames)
    {
        if (frames <= 0) return;
        int free = _ringFrames - _count;
        if (frames > free)
        {
            // Should not happen with a sane capacity; drop the oldest audio so the
            // newest (what the audience is about to hear) always survives.
            AdvanceRead(Math.Min(frames - free + 1, _count));
        }

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = ((_writeFrame + f) % _ringFrames) * _channels;
            int src = offset + f * _channels;
            for (int c = 0; c < _channels; c++) _ring[baseIdx + c] = interleaved[src + c];
        }
        _writeFrame = (_writeFrame + frames) % _ringFrames;
        _count += frames;
    }

    /// <summary>
    /// Keep the depth inside the band. Called by the engine about once a second;
    /// a single correction moves at most <c>blockFrames * 8</c> frames, and only
    /// once per <see cref="CorrectionIntervalMs"/>.
    /// </summary>
    public void Maintain(int blockFrames)
    {
        long now = Environment.TickCount64;
        if (now - _lastCorrectionTicks < CorrectionIntervalMs) return;

        int depth = _count;
        if (depth > HighFrames)
        {
            int drop = Math.Min(depth - TargetFrames, blockFrames * 8);
            if (drop > 0)
            {
                AdvanceRead(drop);
                _lastCorrectionTicks = now;
                _droppedBlocks++;
            }
        }
        else if (depth < LowFrames)
        {
            int pad = Math.Min(TargetFrames - depth, blockFrames * 8);
            if (pad > 0)
            {
                InsertSilence(pad);
                _lastCorrectionTicks = now;
                _insertedBlocks++;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count % _channels != 0) count -= count % _channels;
        int frames = count / _channels;
        int take = Math.Min(frames, _count);

        for (int f = 0; f < take; f++)
        {
            int baseIdx = ((_readFrame + f) % _ringFrames) * _channels;
            int dst = offset + f * _channels;
            for (int c = 0; c < _channels; c++) buffer[dst + c] = _ring[baseIdx + c];
        }

        if (take < frames)
        {
            Array.Clear(buffer, offset + take * _channels, (frames - take) * _channels);
            _underrunFrames += frames - take;
        }

        AdvanceRead(take);
        return count;
    }

    public void Clear()
    {
        _readFrame = 0;
        _writeFrame = 0;
        _count = 0;
        Array.Clear(_ring, 0, _ring.Length);
    }

    private void AdvanceRead(int frames)
    {
        if (frames <= 0) return;
        _readFrame = (_readFrame + frames) % _ringFrames;
        _count = Math.Max(0, _count - frames);
    }

    private void InsertSilence(int frames)
    {
        int free = _ringFrames - _count;
        if (frames > free) frames = free;
        if (frames <= 0) return;
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = ((_writeFrame + f) % _ringFrames) * _channels;
            for (int c = 0; c < _channels; c++) _ring[baseIdx + c] = 0f;
        }
        _writeFrame = (_writeFrame + frames) % _ringFrames;
        _count += frames;
    }
}

/// <summary>
/// Maps the engine's channel layout onto a device with a different one. Shared
/// mode guarantees the format we init with is the mix format, so this only ever
/// runs for an odd endpoint (a 5.1 receiver, a mono monitor).
/// </summary>
internal sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _in;
    private readonly int _out;
    private readonly bool _duplicateMono;
    private float[] _scratch = Array.Empty<float>();

    /// <summary>
    /// <paramref name="duplicateMono"/> sends a single-channel source to every
    /// output channel instead of leaving the extra channels silent. That is what a
    /// talk-over microphone needs: a mono headset mic must be audible in both
    /// speakers, not just the left one.
    /// </summary>
    public ChannelMapSampleProvider(ISampleProvider source, int outChannels, bool duplicateMono = false)
    {
        _source = source;
        _in = source.WaveFormat.Channels;
        _out = outChannels;
        _duplicateMono = duplicateMono && _in == 1;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _out;
        if (frames <= 0) return 0;
        int need = frames * _in;
        if (_scratch.Length < need) _scratch = new float[need];

        // Honour the returned count: a short final read from the source leaves the
        // tail of the scratch buffer holding the *previous* block's samples, which
        // would be audible garbage if we mapped the whole request.
        int got = _source.Read(_scratch, 0, need);
        if (got < 0) got = 0;
        else if (got > need) got = need;
        int gotFrames = got / _in;

        int copy = Math.Min(_in, _out);
        for (int f = 0; f < gotFrames; f++)
        {
            int dst = offset + f * _out;
            int src = f * _in;
            for (int c = 0; c < copy; c++) buffer[dst + c] = _scratch[src + c];
            for (int c = copy; c < _out; c++)
                buffer[dst + c] = _duplicateMono ? _scratch[src] : 0f;
        }

        // Silence, not stale audio, for the frames the source did not produce.
        int produced = gotFrames * _out;
        int written = frames * _out;
        if (produced < written) Array.Clear(buffer, offset + produced, written - produced);

        return produced;
    }
}
