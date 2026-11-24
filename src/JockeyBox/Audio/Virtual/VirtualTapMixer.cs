using System;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Audio.Virtual;

/// <summary>
/// The render thread's side of <see cref="VirtualTapTable"/>: mixes one block of the
/// tap table into the interleaved ring payload, and crossfades over a single block
/// whenever the table is swapped by reference.
///
/// <para><b>No allocation, no locks, no LINQ.</b> The destination buffer is allocated
/// by whoever publishes the plan (control thread, once per geometry), the taps are a
/// prebuilt array, and the only state here is which table was rendered last.</para>
///
/// <para><b>Why a ramp.</b> A line's gain or mute can change between blocks. Applying
/// the new table cold steps every affected channel at a block boundary, which is an
/// audible click. One block of linear crossfade between the old and the new table is
/// short enough to be inaudible and long enough to cover the step.</para>
/// </summary>
public sealed class VirtualTapMixer
{
    private VirtualTapTable? _current;
    private VirtualTapTable? _fading;

    /// <summary>The table rendered on the previous block (tests and the trace read it).</summary>
    public VirtualTapTable? Current => _current;

    /// <summary>True while the block being rendered is the one-block crossfade.</summary>
    public bool IsRamping => _fading != null;

    /// <summary>
    /// Mix <paramref name="table"/> into <paramref name="destination"/>, which must hold
    /// <c>blockFrames * table.Channels</c> floats. Channels no tap feeds are silent.
    /// </summary>
    public void Render(VirtualTapTable table, AudioBus[] buses, int blockFrames, float[] destination)
    {
        var channels = table.Channels;
        var samples = blockFrames * channels;
        if (destination.Length < samples)
            throw new ArgumentException(
                $"tap buffer holds {destination.Length} samples, {samples} needed", nameof(destination));

        Array.Clear(destination, 0, samples);

        if (!ReferenceEquals(table, _current))
        {
            _fading = _current;
            _current = table;
        }

        var fading = _fading;
        _fading = null;   // the crossfade is exactly one block, whatever the next swap brings

        if (fading is null)
        {
            AddTaps(table.TapArray, buses, blockFrames, channels, destination, 1f, 0f);
            return;
        }

        var step = 1f / blockFrames;
        AddTaps(fading.TapArray, buses, blockFrames, channels, destination, 1f - step, -step);
        AddTaps(table.TapArray, buses, blockFrames, channels, destination, step, step);
    }

    /// <summary>
    /// Add one table's taps to the mix. The weight of frame <c>f</c> is
    /// <c>start + f * step</c>: constant 1 for a settled table, a one-block ramp up
    /// (<c>1/n .. 1</c>) for the incoming one and down (<c>1-1/n .. 0</c>) for the
    /// outgoing one.
    /// </summary>
    private static void AddTaps(VirtualTap[] taps, AudioBus[] buses, int blockFrames, int channels,
        float[] destination, float start, float step)
    {
        if (taps.Length == 0) return;

        for (var i = 0; i < taps.Length; i++)
        {
            var tap = taps[i];
            if (tap.DestinationChannel >= channels ||
                tap.SourceChannel >= VirtualTapTable.SourceChannels)
                continue;

            var source = buses[(int)tap.Bus].Output;
            var sourceBase = tap.SourceChannel;
            var destinationBase = tap.DestinationChannel;
            var gain = tap.Gain;

            for (var f = 0; f < blockFrames; f++)
            {
                var weight = start + f * step;
                destination[f * channels + destinationBase] +=
                    source[f * VirtualTapTable.SourceChannels + sourceBase] * gain * weight;
            }
        }
    }
}
