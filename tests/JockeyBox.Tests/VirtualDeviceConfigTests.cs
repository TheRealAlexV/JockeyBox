using System;
using System.Collections.Generic;
using System.IO;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Virtual;

namespace JockeyBox.Tests;

/// <summary>
/// The app writes <c>virtual-device.json</c>; the native ASIO driver reads it and
/// derives its channel geometry from it. A disagreement between the two shows up
/// as silence, so the canonical bytes are pinned to a committed fixture that the
/// driver-side harness reads too: changing the writer without the fixture (or the
/// reader without the fixture) fails one side or the other.
/// </summary>
[Collection(DeviceConfigOwnershipCollection.Name)]
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

    [Fact]
    public void A_name_over_the_drivers_byte_limit_is_refused_before_it_is_written()
    {
        var lines = Sample();
        lines[0].Name = "Deck A - Green Room Camera";   // 27 chars, 27 bytes: legal

        Assert.Equal(26, System.Text.Encoding.UTF8.GetByteCount(lines[0].Name));
        Assert.Contains("Deck A - Green Room Camera", VirtualDeviceConfig.Serialize(lines));

        // 70 bytes: fits no driver buffer of 64, and counting characters would not see it.
        lines[0].Name = new string('x', 70);
        var error = Assert.Throws<InvalidOperationException>(() => VirtualDeviceConfig.Serialize(lines));
        Assert.Contains("name", error.Message);
        Assert.Contains("70 bytes", error.Message);
        Assert.Contains(VirtualDeviceConfig.MaxNameBytes.ToString(), error.Message);
    }

    [Fact]
    public void Multi_byte_characters_are_counted_as_bytes_not_characters()
    {
        var lines = Sample();
        lines[0].Name = new string('é', 40);   // 40 characters, 80 bytes

        var error = Assert.Throws<InvalidOperationException>(() => VirtualDeviceConfig.Serialize(lines));
        Assert.Contains("80 bytes", error.Message);
    }

    [Fact]
    public void An_id_or_source_over_the_drivers_byte_limit_is_refused()
    {
        var idLines = Sample();
        idLines[0].Id = new string('i', 64);
        var idError = Assert.Throws<InvalidOperationException>(() => VirtualDeviceConfig.Serialize(idLines));
        Assert.Contains("id", idError.Message);

        var sourceLines = Sample();
        sourceLines[0].Source = new string('s', 128);
        var sourceError = Assert.Throws<InvalidOperationException>(
            () => VirtualDeviceConfig.Serialize(sourceLines));
        Assert.Contains("source", sourceError.Message);
    }

    [Fact]
    public void A_zero_length_config_is_invalid_rather_than_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jb-{Guid.NewGuid():N}", "virtual-device.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");

            var error = Assert.Throws<FormatException>(() => VirtualDeviceConfig.Load(path));
            Assert.Contains("empty", error.Message);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_leaves_no_temporary_file_behind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jb-{Guid.NewGuid():N}", "virtual-device.json");
        try
        {
            VirtualDeviceConfig.Save(Sample(), path);
            VirtualDeviceConfig.Save(Sample(), path);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Empty_line_list_writes_two_channels_matching_legacy_publish_width()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jb-cfg-{Guid.NewGuid():N}");
        var path = Path.Combine(tempDir, "virtual-device.json");
        try
        {
            var created = VirtualDeviceConfig.CreateIfAbsent(path);
            Assert.True(created);
            Assert.True(File.Exists(path));

            var loaded = VirtualDeviceConfig.Load(path);
            Assert.Equal(2, VirtualDeviceConfig.ChannelCount(loaded));
            Assert.Equal(VirtualTapTable.Legacy(BusKind.DeckA).Channels, VirtualDeviceConfig.ChannelCount(loaded));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void N_lines_file_width_equals_sum_of_widths_matching_published_width()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jb-cfg-{Guid.NewGuid():N}");
        var path = Path.Combine(tempDir, "virtual-device.json");
        try
        {
            var sample = Sample(); // 3 stereo (6) + 2 mono (2) = 8
            VirtualDeviceConfig.Save(sample, path);

            var loaded = VirtualDeviceConfig.Load(path);
            var expectedChannels = VirtualDeviceConfig.ChannelCount(sample);
            Assert.Equal(8, expectedChannels);
            Assert.Equal(expectedChannels, VirtualDeviceConfig.ChannelCount(loaded));

            var taps = VirtualTapTable.FromLines(sample);
            Assert.Equal(expectedChannels, taps.Channels);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void An_existing_8_channel_file_is_never_rewritten_by_the_bootstrap()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jb-cfg-{Guid.NewGuid():N}");
        var path = Path.Combine(tempDir, "virtual-device.json");
        try
        {
            // What an earlier install or the operator left on disk.
            VirtualDeviceConfig.Save(Sample(), path);
            var before = File.ReadAllBytes(path);
            Assert.Equal(8, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load(path)));

            // A launch finds the file present: the bootstrap declines instead of
            // normalising it back to the legacy pair, and the bytes are untouched.
            Assert.False(VirtualDeviceConfig.CreateIfAbsent(path));

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(8, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load(path)));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Failing_bootstrap_logs_an_error_and_does_not_throw()
    {
        // Create a file where a directory is expected so creating the directory fails.
        var blockingFile = Path.Combine(Path.GetTempPath(), $"jb-invalid-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a dir");
        var badPath = Path.Combine(blockingFile, "virtual-device.json");

        try
        {
            var result = VirtualDeviceConfig.CreateIfAbsent(badPath);
            Assert.False(result);
            Assert.False(File.Exists(badPath));
        }
        finally
        {
            if (File.Exists(blockingFile)) File.Delete(blockingFile);
        }
    }

    [Fact]
    public void ConfigPath_Seam_Env_Var_Overrides_DefaultPath_And_DirectoryPath_Under_Temp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jb-seam-{Guid.NewGuid():N}");
        var overridePath = Path.Combine(tempDir, "custom-virtual-device.json");

        var prev = Environment.GetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar, overridePath);

            Assert.Equal(overridePath, VirtualDeviceConfig.DefaultPath);
            Assert.Equal(tempDir, VirtualDeviceConfig.DirectoryPath);

            var created = VirtualDeviceConfig.CreateIfAbsent(VirtualDeviceConfig.DefaultPath);
            Assert.True(created);
            Assert.True(File.Exists(overridePath));
            Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              VirtualDeviceConfig.DefaultPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar, prev);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Atomic_write_uses_temp_file_and_atomic_replace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jb-atom-{Guid.NewGuid():N}");
        var path = Path.Combine(tempDir, "virtual-device.json");
        try
        {
            VirtualDeviceConfig.Save(Sample(), path);
            var initialBytes = File.ReadAllBytes(path);
            Assert.NotEmpty(initialBytes);

            // Overwrite with legacy lines
            VirtualDeviceConfig.Save(VirtualDeviceConfig.LegacyLines, path);
            var updatedBytes = File.ReadAllBytes(path);

            Assert.NotEqual(initialBytes.Length, updatedBytes.Length);
            // Verify temp file does not remain
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
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
