using System.Text.Json;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// <see cref="DeviceSourceKey"/> is the identity of a strip: the whole-device form must stay
/// byte-identical to the bare endpoint id that <c>mixer-settings.json</c> already holds, and every
/// channel of a source must get a slug of its own.
/// </summary>
public class DeviceSourceKeyTests : IDisposable
{
    private const string WasapiA = "{0.0.1.00000000}.{6b2f8e9c-1a2b-4c3d-8e4f-5a6b7c8d9e0f}";
    private const string WasapiB = "{0.0.1.00000000}.{c298e408-4d5a-4f1b-9c23-8f7e6d5c4b3a}";
    private const string Asio = "Focusrite USB ASIO";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("jb-device-source-key-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch { /* the OS reaps it */ }
    }

    private MixerSettingsStore Store() => new(_dir.FullName);

    private string WriteFile(string json)
    {
        var path = Path.Combine(_dir.FullName, MixerSettingsStore.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_negative_channel_is_exactly_the_bare_endpoint_id()
    {
        Assert.Equal(WasapiA, DeviceSourceKey.Compose("wasapi", WasapiA, -1));
        Assert.Equal(WasapiB, DeviceSourceKey.Compose("wasapi", WasapiB, DeviceSourceKey.WholeDevice));
        Assert.Equal(Asio, DeviceSourceKey.Compose("asio", Asio, -5));

        // The whole-device slug is the one the live machine already has.
        Assert.Equal("f22f2d874051f93a", DeviceControlId.Slug(DeviceSourceKey.Compose("wasapi", WasapiA, -1)));
    }

    [Fact]
    public void Channel_zero_is_a_composite_key_not_the_whole_device()
    {
        string key = DeviceSourceKey.Compose("wasapi", WasapiA, 0);

        Assert.NotEqual(WasapiA, key);
        Assert.NotEqual(DeviceControlId.Slug(WasapiA), DeviceControlId.Slug(key));
        Assert.Equal("wasapi:{0.0.1.00000000}.{6b2f8e9c-1a2b-4c3d-8e4f-5a6b7c8d9e0f}#ch0", key);
    }

    [Fact]
    public void Three_endpoints_times_five_channels_give_fifteen_distinct_slugs_that_miss_every_bare_slug()
    {
        (string Kind, string Endpoint)[] endpoints =
        {
            ("wasapi", WasapiA),
            ("wasapi", WasapiB),
            ("asio", Asio),
        };

        var bareSlugs = new HashSet<string>(endpoints.Select(e => DeviceControlId.Slug(e.Endpoint)));
        var channelSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string kind, string endpoint) in endpoints)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                string slug = DeviceControlId.Slug(DeviceSourceKey.Compose(kind, endpoint, channel));
                Assert.True(channelSlugs.Add(slug), $"slug collision at {kind}:{endpoint}#ch{channel}: {slug}");
                Assert.DoesNotContain(slug, bareSlugs);
            }
        }

        Assert.Equal(15, channelSlugs.Count);
        Assert.Equal(3, bareSlugs.Count);
        Assert.Empty(channelSlugs.Intersect(bareSlugs));
    }

    [Fact]
    public void Composite_slugs_are_pinned()
    {
        Assert.Equal("bf514977323dbd95",
            DeviceControlId.Slug(DeviceSourceKey.Compose("asio", Asio, 2)));
        Assert.Equal("fa8872d79cf3432f",
            DeviceControlId.Slug(DeviceSourceKey.Compose("wasapi", WasapiA, 0)));
    }

    [Fact]
    public void A_bare_endpoint_id_parses_as_the_whole_device()
    {
        Assert.True(DeviceSourceKey.TryParse(WasapiA, out string kind, out string endpoint, out int channel));

        Assert.Equal("", kind);
        Assert.Equal(WasapiA, endpoint);
        Assert.Equal(DeviceSourceKey.WholeDevice, channel);
    }

    [Fact]
    public void Compose_and_parse_round_trip_for_every_channel()
    {
        (string Kind, string Endpoint)[] endpoints =
        {
            ("wasapi", WasapiA),
            ("asio", Asio),
        };

        foreach ((string kind, string endpoint) in endpoints)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                string key = DeviceSourceKey.Compose(kind, endpoint, channel);

                Assert.True(DeviceSourceKey.TryParse(key, out string parsedKind, out string parsedEndpoint, out int parsedChannel));
                Assert.Equal(kind, parsedKind);
                Assert.Equal(endpoint, parsedEndpoint);
                Assert.Equal(channel, parsedChannel);
            }
        }
    }

    [Theory]
    [InlineData("wasapi:#ch0")]
    [InlineData("wasapi:endpoint#ch")]
    [InlineData("wasapi:endpoint#ch-1")]
    [InlineData("wasapi:endpoint#chx")]
    [InlineData(":endpoint#ch0")]
    [InlineData("#ch0")]
    public void A_malformed_composite_key_is_rejected(string key)
    {
        Assert.False(DeviceSourceKey.TryParse(key, out _, out _, out _));
    }

    [Fact]
    public void Compose_refuses_a_kind_or_endpoint_that_would_break_parsing()
    {
        Assert.Throws<ArgumentException>(() => DeviceSourceKey.Compose("wa:sapi", WasapiA, 0));
        Assert.Throws<ArgumentException>(() => DeviceSourceKey.Compose("was#api", WasapiA, 0));
        Assert.Throws<ArgumentException>(() => DeviceSourceKey.Compose("wasapi", "end#point", 0));
        Assert.Throws<ArgumentException>(() => DeviceSourceKey.Compose(" ", WasapiA, 0));
    }

    [Fact]
    public void A_settings_file_keyed_by_bare_endpoint_ids_still_loads_with_the_slugs_it_has_today()
    {
        WriteFile($$"""
        {
          "Schema": 1,
          "Devices": {
            "{{WasapiA}}": { "Bypass": true },
            "{{WasapiB}}": { "Bypass": false }
          }
        }
        """);

        MixerSettingsLoad load = Store().Load();

        Assert.False(load.HasProblem, load.Reason);
        Assert.Equal(new[] { WasapiA, WasapiB }.OrderBy(k => k, StringComparer.Ordinal), load.Devices.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(load.Devices[WasapiA].MasterBypass);

        // The two slugs the live machine derives today, pinned so a change to the key or the hash
        // cannot move an existing API id.
        Assert.Equal("f22f2d874051f93a", DeviceControlId.Slug(load.Devices.Keys.First(k => k == WasapiA)));
        Assert.Equal("d825e15105ca31e1", DeviceControlId.Slug(load.Devices.Keys.First(k => k == WasapiB)));
    }

    [Fact]
    public void A_composite_keyed_file_round_trips_through_save_and_load_with_the_same_keys_and_slugs()
    {
        var saved = new Dictionary<string, StripParameters>(StringComparer.Ordinal)
        {
            [WasapiA] = StripParameters.Neutral,
            [DeviceSourceKey.Compose("wasapi", WasapiA, 0)] = StripParameters.Neutral,
            [DeviceSourceKey.Compose("wasapi", WasapiA, 1)] = StripParameters.Neutral,
            [DeviceSourceKey.Compose("asio", Asio, 2)] = StripParameters.Neutral,
        };

        MixerSettingsStore store = Store();
        store.Save(saved);
        MixerSettingsLoad load = store.Load();

        Assert.False(load.HasProblem, load.Reason);
        Assert.Equal(saved.Keys.OrderBy(k => k, StringComparer.Ordinal), load.Devices.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(
            saved.Keys.Select(DeviceControlId.Slug).OrderBy(s => s, StringComparer.Ordinal),
            load.Devices.Keys.Select(DeviceControlId.Slug).OrderBy(s => s, StringComparer.Ordinal));

        // The separators survive as written, not escaped or normalised by the JSON writer.
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("#ch2", json);
        Assert.Contains("asio:", json);
    }

    [Fact]
    public void Composite_keys_and_bare_keys_coexist_in_one_file()
    {
        // The upgrade path: today's file already holds the bare key, and a channel strip is added
        // beside it without touching it.
        WriteFile($$"""
        {
          "Schema": 1,
          "Devices": {
            "{{WasapiA}}": { "Bypass": true }
          }
        }
        """);

        MixerSettingsStore store = Store();
        MixerSettingsLoad first = store.Load();
        Assert.False(first.HasProblem, first.Reason);

        var merged = new Dictionary<string, StripParameters>(first.Devices, StringComparer.Ordinal)
        {
            [DeviceSourceKey.Compose("wasapi", WasapiA, 1)] = StripParameters.Neutral,
        };
        store.Save(merged);

        MixerSettingsLoad second = store.Load();
        Assert.False(second.HasProblem, second.Reason);
        Assert.Equal(2, second.Devices.Count);
        Assert.True(second.Devices[WasapiA].MasterBypass);
        Assert.Equal("f22f2d874051f93a", DeviceControlId.Slug(WasapiA));
        Assert.NotEqual(DeviceControlId.Slug(WasapiA), DeviceControlId.Slug(DeviceSourceKey.Compose("wasapi", WasapiA, 1)));
    }

    [Fact]
    public void The_saved_document_still_declares_schema_one()
    {
        MixerSettingsStore store = Store();
        store.Save(new Dictionary<string, StripParameters>(StringComparer.Ordinal)
        {
            [DeviceSourceKey.Compose("asio", Asio, 0)] = StripParameters.Neutral,
        });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.Equal(MixerSettingsStore.SchemaVersion, document.RootElement.GetProperty("Schema").GetInt32());
    }

    [Fact]
    public void A_pair_key_round_trips_and_parses_back_as_a_pair()
    {
        string key = DeviceSourceKey.Compose("asio", Asio, 2, AsioChannelMode.StereoPair);

        Assert.Equal("asio:Focusrite USB ASIO#pair2", key);
        Assert.True(DeviceSourceKey.TryParse(
            key, out string kind, out string endpoint, out int offset, out AsioChannelMode mode));
        Assert.Equal("asio", kind);
        Assert.Equal(Asio, endpoint);
        Assert.Equal(2, offset);
        Assert.Equal(AsioChannelMode.StereoPair, mode);

        // The mode-less overload still parses the pair key; it just drops the mode.
        Assert.True(DeviceSourceKey.TryParse(key, out kind, out endpoint, out offset));
        Assert.Equal(2, offset);
    }

    [Fact]
    public void A_mono_key_and_a_bare_id_produce_exactly_todays_keys_in_both_modes()
    {
        // The pinned literals above must keep passing; these assert the mode-aware entry point
        // does not change a single byte for mono or for the whole device.
        Assert.Equal(DeviceSourceKey.Compose("asio", Asio, 2),
            DeviceSourceKey.Compose("asio", Asio, 2, AsioChannelMode.Mono));
        Assert.Equal(DeviceSourceKey.Compose("wasapi", WasapiA, 0),
            DeviceSourceKey.Compose("wasapi", WasapiA, 0, AsioChannelMode.Mono));
        Assert.Equal(WasapiA, DeviceSourceKey.Compose("wasapi", WasapiA, -1, AsioChannelMode.StereoPair));

        Assert.True(DeviceSourceKey.TryParse(WasapiA, out _, out _, out int channel, out AsioChannelMode mode));
        Assert.Equal(DeviceSourceKey.WholeDevice, channel);
        Assert.Equal(AsioChannelMode.Mono, mode);
    }

    [Fact]
    public void Channel_two_and_pair_two_are_different_keys_with_different_slugs()
    {
        string mono = DeviceSourceKey.Compose("asio", Asio, 2);
        string pair = DeviceSourceKey.Compose("asio", Asio, 2, AsioChannelMode.StereoPair);

        Assert.NotEqual(mono, pair);
        Assert.NotEqual(DeviceControlId.Slug(mono), DeviceControlId.Slug(pair));
    }

    [Fact]
    public void A_pair_at_the_last_source_channel_does_not_fit_and_the_failure_names_both_numbers()
    {
        string key = DeviceSourceKey.Compose("asio", Asio, 5, AsioChannelMode.StereoPair);
        Assert.True(DeviceSourceKey.TryParse(key, out _, out _, out int offset, out AsioChannelMode mode));
        Assert.Equal(AsioChannelMode.StereoPair, mode);

        // A pair needs offset and offset + 1, so offset 5 does not fit six channels: channel 6
        // does not exist. The message has to name the offset and the channel count.
        var ex = Assert.Throws<ArgumentException>(() => AsioChannelMap.Validate(6, offset, mode));
        Assert.Contains("5..6", ex.Message);
        Assert.Contains("6-channel", ex.Message);

        // The last pair that does fit on six channels starts at four.
        AsioChannelMap.Validate(6, 4, mode);
    }

    [Theory]
    [InlineData("asio:endpoint#pair")]
    [InlineData("asio:endpoint#pair-1")]
    [InlineData("asio:endpoint#pairx")]
    [InlineData("asio:endpoint#pair1.5")]
    [InlineData(":endpoint#pair0")]
    [InlineData("#pair0")]
    public void A_malformed_pair_key_is_rejected(string key)
    {
        Assert.False(DeviceSourceKey.TryParse(key, out _, out _, out _, out _));
        Assert.False(DeviceSourceKey.TryParse(key, out _, out _, out _));
    }
}
