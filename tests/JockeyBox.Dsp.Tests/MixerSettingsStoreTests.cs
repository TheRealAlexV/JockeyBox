namespace JockeyBox.Dsp.Tests;

public class MixerSettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("jb-mixer-settings-");

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

    private static StripParameters Loud()
    {
        var neutral = StripParameters.Neutral;
        return new StripParameters(
            neutral.Eq with
            {
                Gain31Db = -6,
                Gain63Db = 2.5,
                Gain125Db = 0,
                Gain250Db = -1.25,
                Gain500Db = 3,
                Gain1kDb = 0.5,
                Gain2kDb = -4,
                Gain4kDb = 7.75,
                Gain8kDb = 0,
                Gain16kDb = 1.5,
            },
            new FilterParameters(Enabled: true, FrequencyHz: 80),
            new FilterParameters(Enabled: false, FrequencyHz: 16000),
            new GateParameters(Enabled: true, ThresholdDb: -35, HysteresisDb: 4, AttackMs: 2,
                ReleaseMs: 250, HoldMs: 60, FloorDb: -55),
            new CompressorParameters(Enabled: true, ThresholdDb: -18, Ratio: 3.5, AttackMs: 8,
                ReleaseMs: 120, KneeDb: 4, MakeupDb: 2),
            new LimiterParameters(Enabled: true, CeilingDb: -1, ReleaseMs: 80),
            new DeEsserParameters(Enabled: true, FrequencyHz: 7200, ThresholdDb: -22, Ratio: 6,
                AttackMs: 1.5, ReleaseMs: 65, MaxReductionDb: 9, SidechainLowHz: 5000,
                SidechainHighHz: 10000),
            MasterBypass: true);
    }

    private static StripParameters Quiet()
    {
        var neutral = StripParameters.Neutral;
        return new StripParameters(
            neutral.Eq with { Gain63Db = -9, Gain4kDb = -2.25 },
            new FilterParameters(Enabled: false, FrequencyHz: 120),
            new FilterParameters(Enabled: true, FrequencyHz: 9000),
            new GateParameters(Enabled: false, ThresholdDb: -70, HysteresisDb: 8, AttackMs: 20,
                ReleaseMs: 400, HoldMs: 0, FloorDb: -90),
            new CompressorParameters(Enabled: false, ThresholdDb: -40, Ratio: 12, AttackMs: 30,
                ReleaseMs: 300, KneeDb: 12, MakeupDb: -3),
            LimiterParameters.Neutral,
            new DeEsserParameters(Enabled: false, FrequencyHz: 5200, ThresholdDb: -35, Ratio: 2,
                AttackMs: 0.2, ReleaseMs: 25, MaxReductionDb: 6, SidechainLowHz: 3800,
                SidechainHighHz: 11000),
            MasterBypass: false);
    }

    [Fact]
    public void Round_trip_keeps_every_parameter_of_two_devices()
    {
        var a = Loud();
        var b = Quiet();
        Store().Save(new Dictionary<string, StripParameters> { ["{capture-a}"] = a, ["{capture-b}"] = b });

        var loaded = Store().Load();

        Assert.Null(loaded.Reason);
        Assert.Equal(2, loaded.Devices.Count);
        Assert.Equal(a, loaded.Devices["{capture-a}"]);
        Assert.Equal(b, loaded.Devices["{capture-b}"]);
        Assert.NotEqual(loaded.Devices["{capture-a}"], loaded.Devices["{capture-b}"]);

        // Schema and the documented key names are what another build has to read back.
        var text = File.ReadAllText(Path.Combine(_dir.FullName, MixerSettingsStore.FileName));
        Assert.Contains("\"Schema\":1", text);
        Assert.Contains("\"Eq\":[", text);
        Assert.Contains("\"HighPassHz\":", text);
        Assert.Contains("\"HighPassOn\":", text);
        Assert.Contains("\"LowPassHz\":", text);
        Assert.Contains("\"LowPassOn\":", text);
        Assert.Contains("\"Gate\":{", text);
        Assert.Contains("\"Comp\":{", text);
        Assert.Contains("\"Limiter\":{", text);
        Assert.Contains("\"Bypass\":", text);
    }

    [Fact]
    public void Absent_file_loads_as_an_empty_map_without_an_error()
    {
        var loaded = Store().Load();

        Assert.Empty(loaded.Devices);
        Assert.Null(loaded.Reason);
        Assert.False(loaded.HasProblem);
    }

    [Fact]
    public void Corrupt_file_loads_as_an_empty_map_with_a_reason()
    {
        WriteFile("{ \"Schema\": 1, \"Devices\": { \"{capture-a}\": ");

        var loaded = Store().Load();

        Assert.Empty(loaded.Devices);
        Assert.True(loaded.HasProblem);
        Assert.False(string.IsNullOrWhiteSpace(loaded.Reason));
    }

    [Fact]
    public void Save_leaves_one_file_and_no_temp_file_behind()
    {
        Store().Save(new Dictionary<string, StripParameters> { ["{capture-a}"] = Loud() });

        var files = _dir.GetFiles("*", SearchOption.AllDirectories);

        Assert.Single(files);
        Assert.Equal(MixerSettingsStore.FileName, files[0].Name);
        Assert.Empty(_dir.GetFiles("*.tmp", SearchOption.AllDirectories));
        Assert.Empty(_dir.GetFiles("*.tmp*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Out_of_range_values_in_a_hand_written_file_come_back_clamped()
    {
        WriteFile("""
        {
          "Schema": 1,
          "Devices": {
            "{capture-a}": {
              "Eq": [999, -999, 3, 0, 0, 0, 0, 0, 0, 40],
              "HighPassHz": 5,
              "HighPassOn": true,
              "LowPassHz": 99999,
              "LowPassOn": true,
              "Gate": { "On": true, "ThresholdDb": -500, "HysteresisDb": 100, "AttackMs": 0, "ReleaseMs": 99999, "HoldMs": -5, "FloorDb": 20 },
              "Comp": { "On": true, "ThresholdDb": -500, "Ratio": 500, "AttackMs": 0, "ReleaseMs": 99999, "KneeDb": 0, "MakeupDb": 0 },
              "Limiter": { "On": true, "CeilingDb": -500, "ReleaseMs": 0 },
              "Bypass": false
            }
          }
        }
        """);

        var strip = Store().Load().Devices["{capture-a}"];

        Assert.Equal(EqParameters.MaxGainDb, strip.Eq.Gain31Db);
        Assert.Equal(EqParameters.MinGainDb, strip.Eq.Gain63Db);
        Assert.Equal(3, strip.Eq.Gain125Db);
        Assert.Equal(EqParameters.MaxGainDb, strip.Eq.Gain16kDb);
        Assert.Equal(FilterParameters.MinFrequencyHz, strip.HighPass.FrequencyHz);
        Assert.Equal(FilterParameters.MaxFrequencyHz, strip.LowPass.FrequencyHz);
        Assert.Equal(-80, strip.Gate.ThresholdDb);
        Assert.Equal(24, strip.Gate.HysteresisDb);
        Assert.Equal(0.01, strip.Gate.AttackMs);
        Assert.Equal(5000, strip.Gate.ReleaseMs);
        Assert.Equal(0, strip.Gate.HoldMs);
        Assert.Equal(0, strip.Gate.FloorDb);
        Assert.Equal(-60, strip.Compressor.ThresholdDb);
        Assert.Equal(20, strip.Compressor.Ratio);
        Assert.Equal(-12, strip.Limiter.CeilingDb);
        Assert.Equal(1, strip.Limiter.ReleaseMs);
        Assert.Equal(strip, strip.Clamp());
    }

    [Fact]
    public void Extra_unknown_keys_still_load()
    {
        WriteFile("""
        {
          "Schema": 1,
          "WrittenBy": "a newer build",
          "Devices": {
            "{capture-a}": {
              "Eq": [1, 2, 3, 0, 0, 0, 0, 0, 0, 0],
              "HighPassHz": 60,
              "HighPassOn": true,
              "LowPassHz": 18000,
              "LowPassOn": false,
              "SomethingNew": { "Nested": [1, 2, 3] },
              "Bypass": true
            }
          },
          "AlsoNew": 42
        }
        """);

        var loaded = Store().Load();

        Assert.Null(loaded.Reason);
        var strip = loaded.Devices["{capture-a}"];
        Assert.Equal(1, strip.Eq.Gain31Db);
        Assert.Equal(3, strip.Eq.Gain125Db);
        Assert.Equal(60, strip.HighPass.FrequencyHz);
        Assert.False(strip.LowPass.Enabled);
        Assert.True(strip.MasterBypass);
    }

    [Fact]
    public void Two_devices_stay_independent()
    {
        var a = Loud();
        var b = Quiet();
        var store = Store();
        store.Save(new Dictionary<string, StripParameters> { ["{capture-a}"] = a, ["{capture-b}"] = b });

        var edited = store.Load().Devices.ToDictionary(kv => kv.Key, kv => kv.Value);
        edited["{capture-a}"] = edited["{capture-a}"] with { MasterBypass = false, Eq = EqParameters.Flat };
        store.Save(edited);

        var reloaded = store.Load();
        Assert.Equal(edited["{capture-a}"], reloaded.Devices["{capture-a}"]);
        Assert.Equal(b, reloaded.Devices["{capture-b}"]);
        Assert.NotEqual(reloaded.Devices["{capture-a}"], reloaded.Devices["{capture-b}"]);
    }

    [Fact]
    public void Saving_twice_in_a_row_writes_the_same_bytes()
    {
        var a = Loud();
        var b = Quiet();
        var store = Store();
        var path = Path.Combine(_dir.FullName, MixerSettingsStore.FileName);

        store.Save(new Dictionary<string, StripParameters> { ["{capture-a}"] = a, ["{capture-b}"] = b });
        var first = File.ReadAllBytes(path);

        // Reverse insertion order: the bytes must not depend on dictionary order.
        store.Save(new Dictionary<string, StripParameters> { ["{capture-b}"] = b, ["{capture-a}"] = a });
        var second = File.ReadAllBytes(path);

        Assert.Equal(first, second);
        Assert.Equal(store.Load().Devices["{capture-a}"], a);
    }

    [Fact]
    public void An_unparsable_device_keeps_its_id_instead_of_being_pruned()
    {
        WriteFile("""
        {
          "Schema": 1,
          "Devices": {
            "{capture-a}": { "Eq": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], "HighPassHz": 40, "HighPassOn": true },
            "{capture-broken}": "not an object"
          }
        }
        """);

        var loaded = Store().Load();

        Assert.True(loaded.Devices.ContainsKey("{capture-broken}"));
        Assert.Equal(StripParameters.Neutral, loaded.Devices["{capture-broken}"]);
        Assert.Equal(40, loaded.Devices["{capture-a}"].HighPass.FrequencyHz);
        Assert.True(loaded.HasProblem);
    }

    [Fact]
    public void Deesser_settings_round_trip_through_the_mixer_store()
    {
        // Every field deliberately off its default, so a dropped or reordered field fails here.
        var deEsser = new DeEsserParameters(
            Enabled: true,
            FrequencyHz: 7100,
            ThresholdDb: -24.5,
            Ratio: 5.5,
            AttackMs: 0.75,
            ReleaseMs: 65,
            MaxReductionDb: 9.5,
            SidechainLowHz: 4200,
            SidechainHighHz: 10500);
        var strip = StripParameters.Neutral with { DeEsser = deEsser };

        Store().Save(new Dictionary<string, StripParameters> { ["{capture-a}"] = strip });

        var loaded = Store().Load();

        Assert.Null(loaded.Reason);
        Assert.Equal(deEsser, loaded.Devices["{capture-a}"].DeEsser);
        Assert.Equal(strip, loaded.Devices["{capture-a}"]);

        var text = File.ReadAllText(Path.Combine(_dir.FullName, MixerSettingsStore.FileName));
        Assert.Contains("\"DeEsser\":", text);
        Assert.Contains("\"SidechainHighHz\":", text);
    }
}
