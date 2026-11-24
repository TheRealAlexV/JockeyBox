using System;
using System.Collections.Generic;
using System.IO;
using JockeyBox.Audio.Virtual;

namespace JockeyBox.Tests;

/// <summary>
/// The app writes <c>virtual-device.json</c>; the native ASIO driver reads it and
/// derives its channel geometry from it. A disagreement between the two shows up
/// as silence, so the canonical bytes are pinned to a committed fixture that the
/// driver-side harness reads too: changing the writer without the fixture (or the
/// reader without the fixture) fails one side or the other.
/// </summary>
public sealed class VirtualDeviceConfigTests
{
    private const string GoldenRelativePath =
        "src/JockeyBox/Audio/Virtual/virtual-device.golden.json";

    /// <summary>Three Stereo Lines plus two Mono Lines: 3*2 + 2*1 = 8 channels.</summary>
    private static List<VirtualLine> Sample() => new()
    {
        new VirtualLine { Id = "deck-a", Name = "Deck A", Kind = VirtualLineKind.Stereo, Source = "deck:a" },
        new VirtualLine { Id = "deck-b", Name = "Deck B", Kind = VirtualLineKind.Stereo, Source = "deck:b" },
        new VirtualLine { Id = "deck-c", Name = "Deck C", Kind = VirtualLineKind.Stereo, Source = "deck:c" },
        new VirtualLine { Id = "mic-1", Name = "Mic 1", Kind = VirtualLineKind.Mono, Source = "mic:1" },
        new VirtualLine { Id = "mic-2", Name = "Mic 2", Kind = VirtualLineKind.Mono, Source = "mic:2" },
    };

    [Fact]
    public void Writer_Produces_Exactly_The_Fixture_The_Driver_Reads()
    {
        var golden = File.ReadAllText(Path.Combine(RepoRoot(), GoldenRelativePath));

        Assert.Equal(golden, VirtualDeviceConfig.Serialize(Sample()));
    }

    [Fact]
    public void Fixture_Parses_Back_To_The_Same_Model()
    {
        var golden = File.ReadAllText(Path.Combine(RepoRoot(), GoldenRelativePath));

        var parsed = VirtualDeviceConfig.Parse(golden);

        Assert.Equal(VirtualDeviceConfig.Serialize(Sample()), VirtualDeviceConfig.Serialize(parsed));
        Assert.Equal(new[] { "deck-a", "deck-b", "deck-c", "mic-1", "mic-2" },
            parsed.Select(line => line.Id));
    }

    [Fact]
    public void Three_Stereo_And_Two_Mono_Give_Eight_Channels()
    {
        Assert.Equal(8, VirtualDeviceConfig.ChannelCount(Sample()));
    }

    [Fact]
    public void Empty_List_Stays_The_Legacy_Stereo_Pair()
    {
        Assert.Equal(2, VirtualDeviceConfig.ChannelCount(Array.Empty<VirtualLine>()));
    }

    [Fact]
    public void Channel_Count_Is_Capped()
    {
        var lines = new List<VirtualLine>();
        for (var i = 0; i < VirtualDeviceConfig.MaxChannels; i++)
            lines.Add(new VirtualLine { Id = $"mono-{i}", Kind = VirtualLineKind.Mono });
        lines.Add(new VirtualLine { Id = "one-too-many", Kind = VirtualLineKind.Mono });

        Assert.Equal(VirtualDeviceConfig.MaxChannels, VirtualDeviceConfig.ChannelCount(lines));
        Assert.Throws<InvalidOperationException>(() => VirtualDeviceConfig.Serialize(lines));
    }

    [Theory]
    [InlineData("{\"version\":2,\"lines\":[]}")]
    [InlineData("{\"version\":1,\"lines\":[{\"id\":\"a\",\"name\":\"A\",\"type\":\"quad\",\"source\":\"\",\"gain\":1,\"mute\":false}]}")]
    public void Parse_Rejects_What_The_Driver_Rejects(string json)
    {
        Assert.Throws<FormatException>(() => VirtualDeviceConfig.Parse(json));
    }

    [Fact]
    public void Duplicate_Ids_Are_Rejected()
    {
        var lines = Sample();
        lines[1].Id = lines[0].Id;

        Assert.Throws<InvalidOperationException>(() => VirtualDeviceConfig.Serialize(lines));
    }

    [Fact]
    public void Save_Then_Load_Round_Trips_Without_A_Bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jb-{Guid.NewGuid():N}", "virtual-device.json");
        try
        {
            VirtualDeviceConfig.Save(Sample(), path);

            Assert.Equal(VirtualDeviceConfig.Serialize(Sample()), File.ReadAllText(path));
            Assert.Equal(VirtualDeviceConfig.Serialize(Sample()),
                VirtualDeviceConfig.Serialize(VirtualDeviceConfig.Load(path)));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "JockeyBox.sln")))
            directory = directory.Parent;
        return directory?.FullName
               ?? throw new InvalidOperationException("repository root (JockeyBox.sln) not found");
    }
}
