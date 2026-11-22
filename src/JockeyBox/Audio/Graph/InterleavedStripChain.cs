using System;
using JockeyBox.Dsp;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// Bridges the engine's interleaved deck blocks to <see cref="StripChain"/>, which is
/// mono.
///
/// <para>One <see cref="StripChain"/> per channel, each with its own processor
/// instances so filter and envelope state stay independent, plus one contiguous
/// scratch block. Every buffer and chain is allocated in the constructor: a block
/// costs no allocation.</para>
///
/// <para><b>Inert while empty.</b> When every channel chain is a passthrough,
/// <see cref="IsPassthrough"/> is true and <see cref="Process"/> returns before it
/// reads, writes or de-interleaves anything, so the caller's block is bit-identical
/// to a render with no chain at all.</para>
/// </summary>
public sealed class InterleavedStripChain
{
    private readonly StripChain[] _chains;
    private readonly float[] _scratch;
    private readonly int _channels;

    /// <param name="channels">Interleaved channel count, <see cref="AudioEngine.Channels"/>.</param>
    /// <param name="blockFrames">Frames per block, <see cref="AudioEngine.BlockFrames"/>.</param>
    public InterleavedStripChain(int channels, int blockFrames)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (blockFrames < 1) throw new ArgumentOutOfRangeException(nameof(blockFrames));

        _channels = channels;
        _scratch = new float[blockFrames];
        _chains = new StripChain[channels];
        for (int c = 0; c < channels; c++) _chains[c] = new StripChain();
    }

    /// <summary>The chain for one channel, for adding its processors.</summary>
    public StripChain this[int channel] => _chains[channel];

    /// <summary>True when every channel chain would leave the block untouched.</summary>
    public bool IsPassthrough
    {
        get
        {
            for (int c = 0; c < _chains.Length; c++)
            {
                if (!_chains[c].IsPassthrough) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Chain latency in frames. All channels carry the same processors, so the first
    /// channel speaks for the rest.
    /// </summary>
    public int LatencyFrames => _chains[0].TotalLatencyFrames;

    /// <summary>
    /// Processes <paramref name="frames"/> interleaved frames in place. Does nothing
    /// at all when <see cref="IsPassthrough"/>.
    /// </summary>
    public void Process(float[] interleaved, int offset, int frames)
    {
        if (IsPassthrough) return;
        if (frames > _scratch.Length) throw new ArgumentOutOfRangeException(nameof(frames));

        Span<float> scratch = _scratch.AsSpan(0, frames);
        for (int c = 0; c < _channels; c++)
        {
            int index = offset + c;
            for (int i = 0; i < frames; i++, index += _channels) scratch[i] = interleaved[index];

            _chains[c].Process(scratch);

            index = offset + c;
            for (int i = 0; i < frames; i++, index += _channels) interleaved[index] = scratch[i];
        }
    }

    /// <summary>Clears filter and envelope state on every channel.</summary>
    public void Reset()
    {
        for (int c = 0; c < _chains.Length; c++) _chains[c].Reset();
    }
}
