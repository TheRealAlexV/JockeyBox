using System;
using System.Collections.Generic;
using System.IO;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Virtual;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The virtual device's geometry has one owner: the ordered line list. These tests pin
/// the three things that used to be decided in three places - how wide the ring is,
/// which bus each channel comes from, and what an untouched install publishes.
/// </summary>
public sealed class VirtualTapTableTests
{
    private const int Block = 256;
    private const int Rate = 48_000;

    private static VirtualLine Line(string id, VirtualLineKind kind, string source,
        double gain = 1.0, bool mute = false) => new()
    {
        Id = id,
        Name = id,
        Kind = kind,
        Source = source,
        Gain = gain,
        Mute = mute,
    };

    /// <summary>The committed fixture: 3 stereo + 2 mono = 8 channels.</summary>
    private static List<VirtualLine> Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "src/JockeyBox/Audio/Virtual/virtual-device.golden.json");
        if (!File.Exists(path))
            path = Path.Combine(LocateRoot(), "src/JockeyBox/Audio/Virtual/virtual-device.golden.json");
        return new List<VirtualLine>(VirtualDeviceConfig.Parse(File.ReadAllText(path)));
    }

    private static string LocateRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JockeyBox.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public void An_empty_line_list_is_the_legacy_stereo_pair()
    {
        var table = VirtualTapTable.Legacy(BusKind.DeckA);

        Assert.True(table.IsLegacy);
        Assert.Equal(VirtualDeviceConfig.MinChannels, table.Channels);
        Assert.Equal(2, table.TapArray.Length);
        Assert.Equal(BusKind.DeckA, table.TapArray[0].Bus);
        Assert.Equal(0, table.TapArray[0].DestinationChannel);
        Assert.Equal(1, table.TapArray[1].SourceChannel);
        Assert.Equal(1, table.TapArray[1].DestinationChannel);
        Assert.Equal(1f, table.TapArray[0].Gain);
    }

    [Fact]
    public void The_fixture_is_eight_channels_with_one_deck_tap_pair_per_line()
    {
        var table = VirtualTapTable.FromLines(Fixture());

        Assert.Equal(8, table.Channels);
        Assert.False(table.IsLegacy);

        // Deck A on pair 1, deck B on pair 2, deck C silent (no third deck bus),
        // then mic 1 and mic 2 as the last two mono channels.
        Assert.Equal(BusKind.DeckA, table.TapArray[0].Bus);
        Assert.Equal(0, table.TapArray[0].DestinationChannel);
        Assert.Equal(1, table.TapArray[1].DestinationChannel);
        Assert.Equal(BusKind.DeckB, table.TapArray[2].Bus);
        Assert.Equal(2, table.TapArray[2].DestinationChannel);
        Assert.Equal(3, table.TapArray[3].DestinationChannel);

        // deck:c resolves to nothing, so channels 4 and 5 are simply not tapped.
        Assert.DoesNotContain(table.TapArray, t => t.DestinationChannel is 4 or 5);

        Assert.Equal(BusKind.Recording, table.TapArray[4].Bus);
        Assert.Equal(6, table.TapArray[4].DestinationChannel);
        Assert.Equal(0, table.TapArray[4].SourceChannel);
        Assert.Equal(7, table.TapArray[5].DestinationChannel);
        Assert.Equal(1, table.TapArray[5].SourceChannel);
        Assert.Equal(6, table.TapArray.Length);
    }

    [Fact]
    public void A_muted_line_keeps_its_channels_so_the_lines_after_it_do_not_shift()
    {
        var lines = new List<VirtualLine>
        {
            Line("a", VirtualLineKind.Stereo, "deck:a"),
            Line("b", VirtualLineKind.Mono, "deck:b", mute: true),
            Line("c", VirtualLineKind.Mono, "mic:1"),
        };

        var table = VirtualTapTable.FromLines(lines);

        Assert.Equal(4, table.Channels);
        Assert.Equal(3, table.TapArray.Length);
        Assert.Equal(0, table.TapArray[0].DestinationChannel);
        Assert.Equal(1, table.TapArray[1].DestinationChannel);
        Assert.Equal(3, table.TapArray[2].DestinationChannel);   // channel 2 belongs to the muted line
    }

    [Fact]
    public void The_line_gain_is_folded_into_the_tap()
    {
        var table = VirtualTapTable.FromLines(new List<VirtualLine>
        {
            Line("a", VirtualLineKind.Stereo, "deck:a", gain: 0.25),
        });

        Assert.Equal(0.25f, table.TapArray[0].Gain);
        Assert.Equal(0.25f, table.TapArray[1].Gain);
    }

    [Fact]
    public void A_list_wider_than_the_device_is_truncated_not_shifted()
    {
        var lines = new List<VirtualLine>();
        for (var i = 0; i < 40; i++) lines.Add(Line($"l{i}", VirtualLineKind.Mono, "mic:1"));

        var table = VirtualTapTable.FromLines(lines);

        Assert.Equal(VirtualDeviceConfig.MaxChannels, table.Channels);
        Assert.Equal(VirtualDeviceConfig.MaxChannels, table.TapArray.Length);
        Assert.Equal(VirtualDeviceConfig.MaxChannels - 1,
            table.TapArray[VirtualDeviceConfig.MaxChannels - 1].DestinationChannel);
    }

    [Fact]
    public void An_unknown_source_is_silence_and_is_reported_once()
    {
        var reported = new List<string>();
        var table = VirtualTapTable.FromLines(new List<VirtualLine>
        {
            Line("x", VirtualLineKind.Stereo, "deck:c", gain: 1.0),
            Line("y", VirtualLineKind.Stereo, "nonsense"),
        }, reported.Add);

        Assert.Equal(4, table.Channels);
        Assert.Empty(table.TapArray);
        Assert.Equal(2, reported.Count);
    }

    [Theory]
    [InlineData("deck:a", BusKind.DeckA, 0)]
    [InlineData("deck-b", BusKind.DeckB, 0)]
    [InlineData("deck:b", BusKind.DeckB, 0)]
    [InlineData("MIC:2", BusKind.Recording, 1)]
    [InlineData("mic:1", BusKind.Recording, 0)]
    [InlineData("soundboard", BusKind.Soundboard, 0)]
    [InlineData("monitor", BusKind.Monitor, 0)]
    [InlineData("cue", BusKind.Monitor, 0)]
    [InlineData("bus:deck-a", BusKind.DeckA, 0)]   // "bus:" is stripped before matching
    public void Source_bindings_resolve_to_a_bus_and_channel(string source, BusKind bus, int channel)
    {
        var resolved = VirtualTapTable.ResolveSource(source);

        Assert.NotNull(resolved);
        Assert.Equal(bus, resolved!.Value.Bus);
        Assert.Equal(channel, resolved.Value.Channel);
    }

    // ---------------------------------------------------------------- the mixer

    private static AudioBus[] Buses(float deckA, float deckB)
    {
        var a = new AudioBus(BusKind.DeckA, Rate, 2, Block);
        var b = new AudioBus(BusKind.DeckB, Rate, 2, Block);
        Array.Fill(a.Output, deckA);
        Array.Fill(b.Output, deckB);
        return new[] { a, b };
    }

    [Fact]
    public void Each_destination_channel_gets_its_own_source_not_a_copy_of_the_first()
    {
        var buses = Buses(0.5f, -0.25f);
        var table = VirtualTapTable.FromLines(new List<VirtualLine>
        {
            Line("a", VirtualLineKind.Stereo, "deck:a"),
            Line("b", VirtualLineKind.Stereo, "deck:b"),
        });
        var mix = new float[Block * table.Channels];
        var mixer = new VirtualTapMixer();

        mixer.Render(table, buses, Block, mix);

        for (var f = 0; f < Block; f++)
        {
            Assert.Equal(0.5f, mix[f * 4 + 0]);
            Assert.Equal(0.5f, mix[f * 4 + 1]);
            Assert.Equal(-0.25f, mix[f * 4 + 2]);
            Assert.Equal(-0.25f, mix[f * 4 + 3]);
        }
    }

    [Fact]
    public void A_channel_no_tap_feeds_is_silent()
    {
        var buses = Buses(0.5f, 0.5f);
        var table = VirtualTapTable.FromLines(new List<VirtualLine>
        {
            Line("a", VirtualLineKind.Mono, "deck:a"),
            Line("x", VirtualLineKind.Mono, "deck:c"),
            Line("b", VirtualLineKind.Mono, "deck:b"),
        });
        var mix = new float[Block * table.Channels];
        var mixer = new VirtualTapMixer();

        mixer.Render(table, buses, Block, mix);

        Assert.Equal(0.5f, mix[0]);
        Assert.Equal(0f, mix[1]);
        Assert.Equal(0.5f, mix[2]);
        Assert.Equal(3, table.Channels);
    }

    [Fact]
    public void Swapping_the_table_ramps_over_one_block_instead_of_stepping()
    {
        var buses = Buses(1f, 0f);
        var loud = VirtualTapTable.FromLines(new List<VirtualLine>
        {
            Line("a", VirtualLineKind.Mono, "deck:a", gain: 1.0),
            Line("pad", VirtualLineKind.Mono, "deck:b"),
        });
        var quiet = VirtualTapTable.FromLines(new List<VirtualLine>
        {
            Line("a", VirtualLineKind.Mono, "deck:a", gain: 0.0),
            Line("pad", VirtualLineKind.Mono, "deck:b"),
        });
        var mix = new float[Block * loud.Channels];
        var mixer = new VirtualTapMixer();

        mixer.Render(loud, buses, Block, mix);
        Assert.Equal(1f, mix[0]);
        Assert.Equal(1f, mix[(Block - 1) * 2]);
        Assert.False(mixer.IsRamping);

        // The swap block fades the old table out sample by sample instead of stepping
        // from 1 to 0 at the block boundary.
        mixer.Render(quiet, buses, Block, mix);
        Assert.Equal(1f - (1f / Block), mix[0], 4);
        Assert.True(mix[(Block - 1) * 2] <= 1f / Block);
        for (var f = 1; f < Block; f++)
            Assert.True(mix[f * 2] <= mix[(f - 1) * 2], $"channel rose at frame {f}");

        // The block after the swap is the new table, exactly.
        mixer.Render(quiet, buses, Block, mix);
        Assert.Equal(0f, mix[0]);
        Assert.Equal(0f, mix[(Block - 1) * 2]);
        Assert.False(mixer.IsRamping);
    }
}
