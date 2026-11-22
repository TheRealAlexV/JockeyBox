namespace JockeyBox.Dsp;

/// <summary>
/// Stereo (or N-channel) wrapper around <see cref="MixerStrip"/>, which is mono.
/// <para>
/// One <see cref="MixerStrip"/> per channel, each with its own filter and envelope state, plus
/// one contiguous scratch block. Every buffer and strip is allocated in the constructor, so a
/// block costs no allocation and <see cref="Process(float[], int, int)"/> can run on the render
/// thread.
/// </para>
/// <para>
/// <b>Inert while neutral.</b> When every channel strip is a passthrough,
/// <see cref="IsPassthrough"/> is true and <see cref="Process(float[], int, int)"/> returns
/// before it reads, writes or de-interleaves anything, leaving the caller's block bit-identical
/// to a render with no strip at all.
/// </para>
/// <para>
/// <b>Latency mirrors channel 0.</b> A device block is one block: the engine compensates a
/// single delay for the whole interleaved pair, never one per channel, so a per-channel latency
/// split would drift the image. <see cref="LatencyFrames"/> therefore reports channel 0 and the
/// other channels are expected to match; if an operator enables the limiter on channel 0 only,
/// channel 1's own <see cref="MixerStrip.LatencyFrames"/> still reads 0 while this wrapper
/// reports the 240 frames the pair actually carries. Reporting channel 0 is the honest number
/// for the device block; reporting channel 1's 0 would under-compensate the pair.
/// </para>
/// </summary>
public sealed class InterleavedMixerStrip
{
    private readonly MixerStrip[] _strips;
    private readonly float[] _scratch;
    private readonly int _channels;

    /// <param name="sampleRate">Device rate, 48 kHz in the engine.</param>
    /// <param name="channels">Interleaved channel count, 2 for stereo.</param>
    /// <param name="blockFrames">Frames per block; the scratch block is this long.</param>
    public InterleavedMixerStrip(double sampleRate, int channels, int blockFrames)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (blockFrames < 1) throw new ArgumentOutOfRangeException(nameof(blockFrames));

        _channels = channels;
        _scratch = new float[blockFrames];
        _strips = new MixerStrip[channels];
        for (int c = 0; c < channels; c++) _strips[c] = new MixerStrip(sampleRate, $"strip.{c}");
    }

    /// <summary>Interleaved channel count.</summary>
    public int Channels => _channels;

    /// <summary>The mono strip for one channel, for adding processors or retuning it alone.</summary>
    public MixerStrip this[int channel] => _strips[channel];

    /// <summary>True when every channel strip would leave the block untouched.</summary>
    public bool IsPassthrough
    {
        get
        {
            for (int c = 0; c < _strips.Length; c++)
            {
                if (!_strips[c].IsPassthrough) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Processing latency of the device block, taken from channel 0. See the class remarks on
    /// why the pair mirrors channel 0 rather than reporting each channel separately.
    /// </summary>
    public int LatencyFrames => _strips[0].LatencyFrames;

    /// <summary>Processor count of channel 0, the same shape every channel is built from.</summary>
    public int ProcessorCount => _strips[0].ProcessorCount;

    /// <summary>Applies the same parameters to every channel.</summary>
    public void Apply(StripParameters parameters)
    {
        for (int c = 0; c < _strips.Length; c++) _strips[c].Apply(parameters);
    }

    /// <summary>
    /// Processes <paramref name="frames"/> interleaved frames in place, de-interleaving one
    /// channel at a time through the scratch block. Does nothing at all when
    /// <see cref="IsPassthrough"/>.
    /// </summary>
    public void Process(float[] buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (frames > _scratch.Length) throw new ArgumentOutOfRangeException(nameof(frames));
        if (offset + frames * _channels > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frames),
                $"{frames} interleaved frames from offset {offset} run past the {buffer.Length}-sample buffer.");
        }

        if (frames == 0 || IsPassthrough) return;

        Span<float> scratch = _scratch.AsSpan(0, frames);
        for (int c = 0; c < _channels; c++)
        {
            int index = offset + c;
            for (int i = 0; i < frames; i++, index += _channels) scratch[i] = buffer[index];

            _strips[c].Process(scratch);

            index = offset + c;
            for (int i = 0; i < frames; i++, index += _channels) buffer[index] = scratch[i];
        }
    }

    /// <summary>Clears filter and envelope state on every channel, limiter lookahead included.</summary>
    public void Reset()
    {
        for (int c = 0; c < _strips.Length; c++) _strips[c].Reset();
    }
}
