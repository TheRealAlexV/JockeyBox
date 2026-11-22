namespace JockeyBox.Dsp;

/// <summary>How one mixing strip reads a multi-channel ASIO source.</summary>
public enum AsioChannelMode
{
    /// <summary>One source channel, duplicated to both sides of the stereo strip.</summary>
    Mono,

    /// <summary>Two adjacent source channels, one per side.</summary>
    StereoPair,
}

/// <summary>
/// Extracts one strip's channels from an interleaved ASIO input block.
///
/// <para><b>Why this lives outside the capture.</b> An ASIO driver hands one client every
/// input channel in a single interleaved buffer. A strip wants one channel (mono, on both
/// sides) or two (a stereo pair), so the mapping is pure index arithmetic over a caller-owned
/// block: no allocation, no state, and it runs on Linux in the tests while the capture side
/// stays Windows-only.</para>
///
/// <para><b>Validated once per block, cheaply.</b> The offset is checked against the
/// source's channel count on every call: a driver that reports fewer channels than the strip
/// asked for gets a named exception instead of reading another channel's samples or an
/// out-of-bounds slot.</para>
/// </summary>
public static class AsioChannelMap
{
    /// <summary>Channels a strip consumes: left and right.</summary>
    public const int StereoChannels = 2;

    /// <summary>How many adjacent source channels <paramref name="mode"/> occupies.</summary>
    public static int RequiredChannels(AsioChannelMode mode) =>
        mode == AsioChannelMode.StereoPair ? 2 : 1;

    /// <summary>
    /// Throws when <paramref name="offset"/> and <paramref name="mode"/> do not fit a source of
    /// <paramref name="sourceChannels"/> channels. The message names the offset and the count.
    /// </summary>
    public static void Validate(int sourceChannels, int offset, AsioChannelMode mode)
    {
        if (sourceChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannels), sourceChannels,
                "an ASIO source needs at least one channel");
        }

        if (offset < 0)
        {
            throw new ArgumentException(
                $"channel offset {offset} is negative; a {sourceChannels}-channel ASIO source accepts 0..{sourceChannels - RequiredChannels(mode)}",
                nameof(offset));
        }

        int needed = RequiredChannels(mode);
        if (offset + needed > sourceChannels)
        {
            throw new ArgumentException(
                $"channel offset {offset} does not fit a {sourceChannels}-channel ASIO source for {mode}: " +
                $"it needs channels {offset}..{offset + needed - 1}",
                nameof(offset));
        }
    }

    /// <summary>
    /// Maps <paramref name="frames"/> interleaved frames of <paramref name="sourceChannels"/>
    /// channels into interleaved stereo, taking channel <paramref name="offset"/> (mono, copied
    /// to L and R) or channels <paramref name="offset"/> and <c>offset + 1</c> (stereo pair).
    /// Allocates nothing.
    /// </summary>
    public static void Map(
        ReadOnlySpan<float> source,
        int frames,
        int sourceChannels,
        int offset,
        AsioChannelMode mode,
        Span<float> destination)
    {
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames));
        Validate(sourceChannels, offset, mode);

        int sourceNeeded = frames * sourceChannels;
        if (source.Length < sourceNeeded)
        {
            throw new ArgumentException(
                $"source holds {source.Length} samples but {frames} frames of {sourceChannels} channels need {sourceNeeded}",
                nameof(source));
        }

        int stereoNeeded = frames * StereoChannels;
        if (destination.Length < stereoNeeded)
        {
            throw new ArgumentException(
                $"destination holds {destination.Length} samples but {frames} stereo frames need {stereoNeeded}",
                nameof(destination));
        }

        if (mode == AsioChannelMode.Mono)
        {
            for (int f = 0; f < frames; f++)
            {
                float sample = source[f * sourceChannels + offset];
                int dst = f * StereoChannels;
                destination[dst] = sample;
                destination[dst + 1] = sample;
            }
            return;
        }

        for (int f = 0; f < frames; f++)
        {
            int src = f * sourceChannels + offset;
            int dst = f * StereoChannels;
            destination[dst] = source[src];
            destination[dst + 1] = source[src + 1];
        }
    }
}
