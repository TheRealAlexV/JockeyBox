using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// A settings.json written by a different tree or a different build must survive
/// being opened and saved here. Two real failures motivate these tests, both of
/// which silently destroyed operator settings:
///
/// <para><b>Unknown members.</b> <see cref="AppSettings.Save()"/> reserialises the
/// typed model. Without <c>[JsonExtensionData]</c> every member the model does not
/// declare is dropped on the next save, at any schema version. The concrete case:
/// <c>feat/shell-panels</c> has no <c>VirtualDeviceLines</c>, so loading the live
/// tree's file here and saving deleted that member.</para>
///
/// <para><b>Unreadable members.</b> <see cref="AppSettings.Load"/> falls back to
/// defaults when the document cannot be parsed, and the old code left the corrupt
/// file in place to be overwritten by the first save. It is now moved aside so the
/// content that broke startup is still recoverable.</para>
///
/// <para>Home seeding follows <c>SettingsLoadTests</c>: a throwaway directory is
/// assigned to <c>JOCKEYBOX_HOME</c>, and the class sits in
/// <see cref="JockeyBoxHomeCollection"/> because <c>AppPaths.Home</c> is
/// process-wide.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public class SettingsRoundTripTests
{
    /// <summary>
    /// Run <paramref name="body"/> with the process home pointed at a fresh
    /// directory seeded with <paramref name="json"/> as settings.json.
    /// </summary>
    private static void InTempHome(string? json, Action<string> body)
    {
        string home = Path.Combine(
            Path.GetTempPath(), "jb-settings-roundtrip", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);
            if (json != null)
                File.WriteAllText(AppSettings.SettingsPath, json);
            body(home);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
        }
    }

    /// <summary>Read a saved document and hand back one member's JSON value.</summary>
    private static JsonElement ReadMember(string home, string name)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(home, "settings.json")));
        return document.RootElement.GetProperty(name).Clone();
    }

    [Fact]
    public void UnknownMember_SurvivesLoadThenSave()
    {
        InTempHome(
            """
            {
              "MusicDirectory": "C:\\Music",
              "SomeFutureBuildKey": "kept",
              "SomeFutureBuildObject": { "depth": [1, 2, 3], "flag": true }
            }
            """,
            home =>
            {
                var loaded = AppSettings.Load();
                loaded.Save();

                Assert.Equal("kept", ReadMember(home, "SomeFutureBuildKey").GetString());
                Assert.True(
                    JsonNode.DeepEquals(
                        JsonNode.Parse("""{"depth":[1,2,3],"flag":true}"""),
                        JsonNode.Parse(ReadMember(home, "SomeFutureBuildObject").GetRawText())),
                    "the unknown object member must round-trip with its value intact");
            });
    }

    [Fact]
    public void VirtualDeviceLines_SurvivesRoundTrip_AlthoughThisModelDoesNotDeclareIt()
    {
        // Named for the tree that motivated the fix: this model does not declare
        // VirtualDeviceLines, and before the extension-data pen it was deleted here.
        InTempHome(
            """
            {
              "MusicDirectory": "C:\\Music",
              "VirtualDeviceLines": [
                { "Name": "JockeyBox Virtual 1", "Channels": 8 },
                { "Name": "JockeyBox Virtual 2", "Channels": 2 }
              ]
            }
            """,
            home =>
            {
                var loaded = AppSettings.TryLoad();
                Assert.NotNull(loaded);
                loaded!.Save();

                JsonElement lines = ReadMember(home, "VirtualDeviceLines");
                Assert.Equal(JsonValueKind.Array, lines.ValueKind);
                Assert.Equal(2, lines.GetArrayLength());
                Assert.Equal(
                    "JockeyBox Virtual 1", lines[0].GetProperty("Name").GetString());
                Assert.Equal(8, lines[0].GetProperty("Channels").GetInt32());
            });
    }

    [Fact]
    public void CloneAndCopyFrom_CarryUnknownMembers()
    {
        // A merge snapshot is built with Clone(), which delegates to CopyFrom().
        // Both enumerate public read/write properties by reflection, so a member
        // the model does not declare must arrive on the copy too.
        InTempHome(
            """{ "MusicDirectory": "C:\\Music", "VirtualDeviceLines": [ { "Name": "V1" } ] }""",
            _ =>
            {
                var loaded = AppSettings.Load();
                Assert.NotNull(loaded.ExtensionData);

                var clone = loaded.Clone();
                Assert.NotNull(clone.ExtensionData);
                Assert.True(clone.ExtensionData!.ContainsKey("VirtualDeviceLines"));

                var target = new AppSettings();
                target.CopyFrom(loaded);
                Assert.NotNull(target.ExtensionData);
                Assert.True(target.ExtensionData!.ContainsKey("VirtualDeviceLines"));
            });
    }

    [Fact]
    public void MergeThenSave_KeepsUnknownMembers()
    {
        // The production write path (SettingsWindow.SaveMerged): snapshot and edited
        // are clones of the loaded settings, current is a fresh read of the file.
        // The merged result is what Save writes, so an unknown member has to survive
        // all three - clone, merge, save - not just load and save.
        InTempHome(
            """
            {
              "MusicDirectory": "C:\\Music",
              "VirtualDeviceLines": [ { "Name": "JockeyBox Virtual 1", "Channels": 8 } ]
            }
            """,
            home =>
            {
                var loaded = AppSettings.Load();
                var snapshot = loaded.Clone();
                var current = AppSettings.TryLoad();
                Assert.NotNull(current);
                var edited = loaded.Clone();

                var merged = SettingsMerge.Merge(snapshot, current, edited);

                Assert.NotNull(merged.ExtensionData);
                Assert.True(merged.ExtensionData!.ContainsKey("VirtualDeviceLines"));

                merged.Save();

                JsonElement lines = ReadMember(home, "VirtualDeviceLines");
                Assert.Equal(JsonValueKind.Array, lines.ValueKind);
                Assert.Equal("JockeyBox Virtual 1", lines[0].GetProperty("Name").GetString());
                Assert.Equal(8, lines[0].GetProperty("Channels").GetInt32());
            });
    }

    [Fact]
    public void CorruptFile_IsArchivedAndLoadStillReturnsDefaults()
    {
        const string corrupt = "{ \"MusicDirectory\": \"C:\\\\Music\", this is not json }";
        InTempHome(corrupt, home =>
        {
            var loaded = AppSettings.Load();

            Assert.NotNull(loaded);
            Assert.Equal(new AppSettings().MusicDirectory, loaded.MusicDirectory);

            // The original path is clear, and the unparseable content is still on
            // disk under the timestamped archive name.
            Assert.False(File.Exists(AppSettings.SettingsPath));
            string[] archived = Directory.GetFiles(home, "settings.json.corrupt-*");
            Assert.Single(archived);
            Assert.Equal(corrupt, File.ReadAllText(archived[0]));
        });
    }

    [Fact]
    public void ExistingSettingsFile_IsBackedUpBeforeSave()
    {
        const string original = """{ "MusicDirectory": "C:\\Original" }""";
        InTempHome(original, home =>
        {
            var loaded = AppSettings.Load();
            loaded.MusicDirectory = @"C:\Edited";
            loaded.Save();

            string backup = AppSettings.SettingsPath + ".bak";
            Assert.True(File.Exists(backup), "a save over an existing file writes .bak");
            Assert.Equal(original, File.ReadAllText(backup));

            // The backup above would still pass if Save wrote a correct .bak and
            // then failed to persist the edit, so read the live file back.
            Assert.Equal(@"C:\Edited", ReadMember(home, "MusicDirectory").GetString());
        });
    }

    [Fact]
    public void A_schema_5_profile_drops_retired_keys_on_a_plain_round_trip_and_keeps_unknown_ones()
    {
        // No migration runs here: the seeded version is already 5, so Load's
        // migration early-returns. The retired key must be dropped by Save's
        // retired-key list, not by a version upgrade.
        InTempHome(
            """
            {
              "SettingsSchemaVersion": 5,
              "MainOutputDeviceId": "{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}",
              "DeckADeviceId": "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}",
              "SomeFutureTreeKey": { "depth": [1, 2, 3], "flag": true }
            }
            """,
            home =>
            {
                var loaded = AppSettings.Load();
                loaded.Save();

                string json = File.ReadAllText(Path.Combine(home, "settings.json"));
                Assert.DoesNotContain("DeckADeviceId", json);
                // Not optional: without this, deleting every foreign key would
                // also make the line above pass - the regression the passthrough
                // exists to prevent.
                Assert.Contains("SomeFutureTreeKey", json);

                // The purge must not reach a declared property: MainOutputDeviceId
                // is what the routing migration writes retired deck ids into, so
                // an over-broad purge that stripped it would also pass above.
                Assert.Equal(
                    "{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}",
                    ReadMember(home, "MainOutputDeviceId").GetString());
            });
    }
}
