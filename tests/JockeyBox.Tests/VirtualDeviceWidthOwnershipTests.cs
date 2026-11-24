using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JockeyBox.Audio;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Virtual;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Who owns the virtual device's width. The native driver reads
/// <c>virtual-device.json</c> and nothing else, so the app has to treat that file as
/// the one authority: no launch may rewrite it from a per-user default, and a read
/// for the route has to come from it.
///
/// <para>The file was previously re-derived from <c>settings.json</c> on every launch,
/// which put a width the operator never chose into the file the elevated ASIO host
/// reads. These tests all run against a temp path: nothing here may touch
/// <c>%ProgramData%\JockeyBox</c> or the operator's profile.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeviceConfigOwnershipCollection
{
    public const string Name = "device-config-ownership";
}

[Collection(DeviceConfigOwnershipCollection.Name)]
public sealed class VirtualDeviceWidthOwnershipTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousDeviceConfig;
    private readonly string? _previousHome;

    public VirtualDeviceWidthOwnershipTests()
    {
        _previousDeviceConfig = Environment.GetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar);
        _previousHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);

        _root = Path.Combine(Path.GetTempPath(), "jb-width-owner", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        // Both overrides go in before anything can read them: the device file, and a
        // settings home so AppSettings.Load cannot reach the operator's profile.
        Environment.SetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar, DevicePath);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, Path.Combine(_root, "home"));
    }

    private string DevicePath => Path.Combine(_root, "virtual-device.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar, _previousDeviceConfig);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _previousHome);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test run.
        }
    }

    /// <summary>Four stereo lines: 8 interleaved channels, not the legacy 2.</summary>
    private static List<VirtualLine> EightChannels() =>
        Enumerable.Range(0, 4)
            .Select(i => new VirtualLine
            {
                Id = $"line-{i + 1}",
                Name = $"Line {i + 1}",
                Kind = VirtualLineKind.Stereo,
                Source = "deck:a",
            })
            .ToList();

    /// <summary>
    /// A configured width survives a restart: saves it, then builds the route a fresh
    /// process would build, from the file alone.
    /// </summary>
    [Fact]
    public void Configured_width_survives_a_restart()
    {
        VirtualDeviceConfig.Save(EightChannels());
        Assert.Equal(8, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load()));

        // The restart: a new settings object (which knows nothing about the width) and
        // the route it produces.
        var settings = new AppSettings { MainOutputDeviceId = "no-such-output-in-this-test" };
        var route = DualDeckTransport.BuildRouteFromAppSettings(settings);

        Assert.Equal(4, route.VirtualLines.Count);
        Assert.Equal(8, VirtualDeviceConfig.ChannelCount(route.VirtualLines));
    }

    /// <summary>
    /// A launch with nothing configured must not touch a file that already carries a
    /// width. This is the launch-time clobber: the startup path re-derived the file
    /// from the (empty) settings list, which overwrote the operator's 8 channels with
    /// the legacy stereo pair.
    /// </summary>
    [Fact]
    public async Task Launch_with_no_configured_width_does_not_overwrite_the_device_file()
    {
        VirtualDeviceConfig.Save(EightChannels());
        var before = File.ReadAllText(DevicePath);

        var devices = new DeviceService();
        using var transport = new DualDeckTransport(
            new DeckChannel("Deck A", devices),
            new DeckChannel("Deck B", devices));

        // Wait for the launch to settle instead of racing it: "it has not happened yet"
        // has to be told apart from "it did not happen".
        await transport.Ready.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(before, File.ReadAllText(DevicePath));
        Assert.Equal(8, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load()));
    }

    /// <summary>
    /// A fresh install has no file to edit: the driver would fall back to legacy stereo
    /// and nothing would say a wider device was ever possible. One launch must leave the
    /// default behind, exactly once: a second launch leaves those bytes untouched.
    /// </summary>
    [Fact]
    public async Task A_fresh_home_gets_one_file_and_a_second_launch_leaves_it_byte_identical()
    {
        Assert.False(File.Exists(DevicePath), "this fixture starts from a home with no file");

        // The same call App.OnStartup makes, with the same explicit argument: the one
        // place allowed to create the file. A launch does not write it at all.
        Assert.True(VirtualDeviceConfig.CreateIfAbsent(VirtualDeviceConfig.DefaultPath),
            "a fresh install must be left a file to edit");

        Assert.Equal(2, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load()));
        Assert.Contains("\"version\":1", File.ReadAllText(DevicePath));
        var afterFirst = Sha256(DevicePath);

        await LaunchOnce();
        await LaunchOnce();

        Assert.Equal(afterFirst, Sha256(DevicePath));
    }

    /// <summary>
    /// The clobber proof at the file's own width: a 4-channel file is the operator's, and
    /// a launch may not rewrite one byte of it.
    /// </summary>
    [Fact]
    public async Task An_existing_four_channel_file_is_byte_identical_after_a_launch()
    {
        VirtualDeviceConfig.Save(TwoStereoLines());
        Assert.Equal(4, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load()));
        var before = Sha256(DevicePath);

        await LaunchOnce();

        Assert.Equal(before, Sha256(DevicePath));
        Assert.Equal(4, VirtualDeviceConfig.ChannelCount(VirtualDeviceConfig.Load()));
    }

    /// <summary>
    /// The one write the app makes unasked must not fail silently: an unwritable path
    /// leaves the operator no file, so the launch has to say so instead of proceeding as
    /// though the default landed.
    /// </summary>
    [Fact]
    public void A_bootstrap_that_cannot_write_is_reported()
    {
        // A regular file where a directory would have to be: the save cannot succeed.
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var unwritable = Path.Combine(blocker, "virtual-device.json");
        Environment.SetEnvironmentVariable(VirtualDeviceConfig.ConfigPathEnvVar, unwritable);

        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            // The composition root's call, on a path that cannot be written.
            Assert.False(VirtualDeviceConfig.CreateIfAbsent(unwritable));
        }
        finally
        {
            Log.LineWritten -= Handler;
        }

        Assert.False(File.Exists(unwritable));
        Assert.Contains(lines, line => line.Contains("could not be created", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One app launch, waited for: the file work happens inside it.</summary>
    private static async Task LaunchOnce()
    {
        var devices = new DeviceService();
        using var transport = new DualDeckTransport(
            new DeckChannel("Deck A", devices),
            new DeckChannel("Deck B", devices));
        await transport.Ready.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static IReadOnlyList<VirtualLine> TwoStereoLines() => new[]
    {
        new VirtualLine { Id = "line-1", Name = "Line 1", Kind = VirtualLineKind.Stereo, Source = "deck:a" },
        new VirtualLine { Id = "line-2", Name = "Line 2", Kind = VirtualLineKind.Stereo, Source = "deck:b" },
    };
}
