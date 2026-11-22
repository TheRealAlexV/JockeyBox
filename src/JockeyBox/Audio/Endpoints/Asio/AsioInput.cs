using System;
using System.Threading;
using JockeyBox.Audio.Graph;
using JockeyBox.Dsp;
using NAudio.Wave;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// One mixing strip's view of a multi-channel ASIO interface: the driver is shared through
/// <see cref="AsioDriverRegistry"/>, and each strip extracts its own channel(s) from the
/// driver's interleaved block into a stereo jitter ring the render thread pulls from.
///
/// <para><b>Why a ring and not a callback into the mix.</b> The ASIO driver thread and the
/// engine's render thread are two clocks. <see cref="Read"/> takes whatever the ring holds and
/// pads with silence, so a late, stalled or unplugged interface is a quiet gap in this strip,
/// never a stutter in the music. <see cref="JitterRing.Maintain"/> trims or pads the depth on
/// the schedule the output sinks use.</para>
///
/// <para><b>No allocation per block.</b> The interleaved staging block and the stereo block are
/// sized from the driver's reported block size in the constructor. The callback only maps and
/// copies; the one exception is a driver that raises its block size after init, which reallocates
/// once and then stays put.</para>
/// </summary>
public sealed class AsioInput : IMicSource, IDisposable
{
    /// <summary>Ring capacity: about 200 ms, so a scheduling hiccup of that size is absorbed.</summary>
    private const int RingCapacityMs = 200;

    /// <summary>Depth the ring is steered to; comfortably inside the capacity.</summary>
    private const int TargetMs = 120;

    /// <summary>Below this the ring is topped up; above <see cref="HighMs"/> it is trimmed.</summary>
    private const int LowMs = 80;

    /// <summary>Upper edge of the drift band.</summary>
    private const int HighMs = 180;

    private readonly string _driverName;
    private readonly int _offset;
    private readonly AsioChannelMode _mode;
    private readonly AsioOut _device;
    private readonly JitterRing _ring;
    private readonly WaveFormat _waveFormat;

    /// <summary>Interleaved staging block: frames × <see cref="SourceChannels"/>.</summary>
    private float[] _interleaved;

    /// <summary>Interleaved stereo block: frames × 2.</summary>
    private float[] _stereo;

    private long _droppedFrames;
    private int _disposed;

    /// <summary>
    /// Opens (or joins) the driver and starts feeding this strip from channel
    /// <paramref name="channelOffset"/>.
    /// </summary>
    /// <param name="driverName">The ASIO driver name, for example <c>Focusrite USB ASIO</c>.</param>
    /// <param name="channelOffset">First source channel this strip reads.</param>
    /// <param name="mode">One channel (duplicated to both sides) or a stereo pair.</param>
    public AsioInput(string driverName, int channelOffset, AsioChannelMode mode)
    {
        if (string.IsNullOrWhiteSpace(driverName))
        {
            throw new ArgumentException("an ASIO driver name is required", nameof(driverName));
        }

        if (channelOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelOffset), channelOffset,
                "ASIO channel offsets start at 0");
        }

        _driverName = driverName;
        _offset = channelOffset;
        _mode = mode;
        _ring = new JitterRing(AudioEngine.Rate, AudioEngine.Channels, RingCapacityMs)
        {
            TargetFrames = AudioEngine.Rate * TargetMs / 1000,
            LowFrames = AudioEngine.Rate * LowMs / 1000,
            HighFrames = AudioEngine.Rate * HighMs / 1000,
        };
        _waveFormat = _ring.WaveFormat;

        _device = AsioDriverRegistry.Shared.Acquire(driverName, out int sourceChannels, out int framesPerBuffer);
        try
        {
            // Fail at construction, not on the driver thread where a throw has no catcher.
            AsioChannelMap.Validate(sourceChannels, channelOffset, mode);

            SourceChannels = sourceChannels;
            DeviceLatencyFrames = framesPerBuffer;
            int block = Math.Max(framesPerBuffer, AudioEngine.BlockFrames);
            _interleaved = new float[block * sourceChannels];
            _stereo = new float[block * AudioEngine.Channels];
            _device.AudioAvailable += OnAudioAvailable;
        }
        catch
        {
            AsioDriverRegistry.Shared.Release(driverName);
            throw;
        }
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _waveFormat;

    /// <summary>Input channels the shared driver exposes.</summary>
    public int SourceChannels { get; }

    /// <summary>
    /// Frames the interface adds before this strip sees a sample. ASIO publishes no input
    /// latency, so the driver's buffer size is the honest conservative figure: one full block
    /// sits in the driver's buffer before the callback runs. Reporting 0 would under-pad this
    /// strip against a WASAPI mic and the two would comb-filter.
    /// </summary>
    public int DeviceLatencyFrames { get; }

    /// <summary>Audio dropped because the ring overflowed, in frames (drop-oldest).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>Silence substituted because the ring was starved, in frames.</summary>
    public long UnderrunFrames => _ring.UnderrunFrames;

    /// <summary>Frames currently buffered in the jitter ring.</summary>
    public int DepthFrames => _ring.DepthFrames;

    /// <summary>
    /// Pulls one block from the ring. <see cref="JitterRing.Read"/> substitutes silence on a
    /// starved ring and counts the frames, so this always returns <paramref name="count"/>
    /// samples — exactly one engine block when the engine asks for one.
    /// </summary>
    public int Read(float[] buffer, int offset, int count) => _ring.Read(buffer, offset, count);

    /// <summary>Drift correction, called by the engine roughly once a second.</summary>
    public void Maintain(int blockFrames) => _ring.Maintain(blockFrames);

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        int channels = e.InputBuffers.Length;
        int frames = e.SamplesPerBuffer;
        if (channels <= 0 || frames <= 0) return;

        int needed = frames * channels;
        if (needed > _interleaved.Length)
        {
            // The driver raised its block size after init: one allocation, then steady state.
            _interleaved = new float[needed];
        }

        int stereoNeeded = frames * AudioEngine.Channels;
        if (stereoNeeded > _stereo.Length)
        {
            _stereo = new float[stereoNeeded];
        }

        // A driver that reports fewer channels than this strip asked for must not read past the
        // block. Count it instead of throwing on the driver thread, where nothing can catch it.
        if (_offset + AsioChannelMap.RequiredChannels(_mode) > channels)
        {
            Interlocked.Add(ref _droppedFrames, frames);
            return;
        }

        e.GetAsInterleavedSamples(_interleaved);
        AsioChannelMap.Map(
            _interleaved.AsSpan(0, needed),
            frames,
            channels,
            _offset,
            _mode,
            _stereo.AsSpan(0, stereoNeeded));

        int before = _ring.DepthFrames;
        _ring.Write(_stereo, 0, frames);
        int dropped = before + frames - _ring.DepthFrames;
        if (dropped > 0) Interlocked.Add(ref _droppedFrames, dropped);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _device.AudioAvailable -= OnAudioAvailable; } catch { }
        AsioDriverRegistry.Shared.Release(_driverName);
    }
}
