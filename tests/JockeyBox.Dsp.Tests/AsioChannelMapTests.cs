namespace JockeyBox.Dsp.Tests;

/// <summary>
/// <see cref="AsioChannelMap"/> is the only arithmetic between one ASIO client's interleaved
/// input and a strip. A wrong offset is a silent channel swap or a crash on the driver thread,
/// so every offset the 4i4 can expose is pinned, plus the bounds and the allocation contract.
/// </summary>
public class AsioChannelMapTests
{
    private static float[] Interleaved(int frames, int channels)
    {
        var buffer = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                buffer[f * channels + c] = f * 10 + c;
        return buffer;
    }

    [Fact]
    public void Mono_duplicates_the_chosen_channel_to_both_sides()
    {
        float[] source = Interleaved(frames: 3, channels: 4);
        var destination = new float[3 * 2];

        AsioChannelMap.Map(source, 3, 4, 1, AsioChannelMode.Mono, destination);

        Assert.Equal(new[] { 1f, 1f, 11f, 11f, 21f, 21f }, destination);
    }

    [Fact]
    public void Stereo_pair_takes_the_two_channels_at_the_offset()
    {
        float[] source = Interleaved(frames: 3, channels: 4);
        var destination = new float[3 * 2];

        AsioChannelMap.Map(source, 3, 4, 2, AsioChannelMode.StereoPair, destination);

        Assert.Equal(new[] { 2f, 3f, 12f, 13f, 22f, 23f }, destination);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_mono_offset_of_a_four_channel_source_maps_its_own_channel(int offset)
    {
        float[] source = Interleaved(frames: 2, channels: 4);
        var destination = new float[2 * 2];

        AsioChannelMap.Map(source, 2, 4, offset, AsioChannelMode.Mono, destination);

        Assert.Equal(new[] { (float)offset, (float)offset, 10f + offset, 10f + offset }, destination);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_stereo_pair_offset_of_a_four_channel_source_takes_its_two(int offset)
    {
        float[] source = Interleaved(frames: 2, channels: 4);
        var destination = new float[2 * 2];

        AsioChannelMap.Map(source, 2, 4, offset, AsioChannelMode.StereoPair, destination);

        Assert.Equal(
            new[] { (float)offset, offset + 1f, 10f + offset, 11f + offset },
            destination);
    }

    [Fact]
    public void Mono_offset_past_the_last_channel_names_the_offset_and_the_channel_count()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AsioChannelMap.Map(new float[16], 4, 4, 4, AsioChannelMode.Mono, new float[8]));

        Assert.Contains("4", ex.Message);
        Assert.Contains("4-channel", ex.Message);
    }

    [Fact]
    public void Stereo_pair_that_runs_off_the_end_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AsioChannelMap.Map(new float[16], 4, 4, 3, AsioChannelMode.StereoPair, new float[8]));

        Assert.Contains("3", ex.Message);
        Assert.Contains("4-channel", ex.Message);
    }

    [Fact]
    public void A_stereo_pair_needs_two_source_channels()
    {
        Assert.Throws<ArgumentException>(() =>
            AsioChannelMap.Map(new float[4], 2, 1, 0, AsioChannelMode.StereoPair, new float[4]));
    }

    [Fact]
    public void A_negative_offset_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            AsioChannelMap.Map(new float[16], 4, 4, -1, AsioChannelMode.Mono, new float[8]));
    }

    [Fact]
    public void A_source_shorter_than_the_frames_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            AsioChannelMap.Map(new float[7], 4, 4, 0, AsioChannelMode.Mono, new float[8]));
    }

    [Fact]
    public void A_destination_shorter_than_the_stereo_frames_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            AsioChannelMap.Map(new float[16], 4, 4, 0, AsioChannelMode.Mono, new float[7]));
    }

    [Fact]
    public void Mapping_allocates_nothing()
    {
        float[] source = Interleaved(frames: 480, channels: 4);
        var destination = new float[480 * 2];

        // The helper's first pass covers all four offsets and both modes, so every branch and
        // the JIT are warm before the counted pass.
        long allocated = AllocationAssert.Measure(() =>
        {
            for (int i = 0; i < 1_000; i++)
            {
                AsioChannelMap.Map(source, 480, 4, i % 4, AsioChannelMode.Mono, destination);
                AsioChannelMap.Map(source, 480, 4, i % 3, AsioChannelMode.StereoPair, destination);
            }
        });

        Assert.Equal(0, allocated);
    }
}
