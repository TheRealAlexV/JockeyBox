using JockeyBox.Settings;

namespace JockeyBox.Tests;

/// <summary>
/// The merge that stops a settings Save from reverting changes made while the
/// dialog was open. Each case names the three inputs: the snapshot the dialog was
/// built from, the file as it is at Save time, and what the controls now hold.
/// </summary>
public class SettingsMergeTests
{
    private static AppSettings Snapshot() => new()
    {
        MusicDirectory = @"C:\Music",
        OutputDelayMs = 100,
        SoundboardFollowsMain = false,
        DeckAVolume = 1.0f,
    };

    [Fact]
    public void FieldEditedInUi_IsWritten()
    {
        var snapshot = Snapshot();
        var current = snapshot.Clone();
        var edited = snapshot.Clone();
        edited.OutputDelayMs = 250;

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(250, merged.OutputDelayMs);
    }

    [Fact]
    public void FieldChangedOnDiskButNotInUi_Survives()
    {
        // The defect: the dialog still shows 100, but the soundboard routing was
        // switched elsewhere (the transport) while it was open.
        var snapshot = Snapshot();
        var current = snapshot.Clone();
        current.SoundboardFollowsMain = true;
        var edited = snapshot.Clone();

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.True(merged.SoundboardFollowsMain);
        Assert.Equal(100, merged.OutputDelayMs);
    }

    [Fact]
    public void FieldChangedInBoth_UiWins()
    {
        var snapshot = Snapshot();
        var current = snapshot.Clone();
        current.OutputDelayMs = 400;
        var edited = snapshot.Clone();
        edited.OutputDelayMs = 250;

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(250, merged.OutputDelayMs);
    }

    [Fact]
    public void FieldWithNoControl_IsCarriedFromDisk()
    {
        // DeckAVolume has no control in the settings window; an outside change must
        // not be reset just because this dialog saved.
        var snapshot = Snapshot();
        var current = snapshot.Clone();
        current.DeckAVolume = 0.35f;
        var edited = snapshot.Clone();

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(0.35f, merged.DeckAVolume);
    }

    [Fact]
    public void FieldWithNoControl_NotInAnyInput_UsesDiskValue()
    {
        var snapshot = Snapshot();
        var current = snapshot.Clone();
        current.ManualTempoRangePercent = 12.5;
        var edited = snapshot.Clone();

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(12.5, merged.ManualTempoRangePercent);
    }

    [Fact]
    public void CollectionRebuiltWithTheSameRows_IsNotAnEdit()
    {
        var snapshot = Snapshot();
        snapshot.MicDevices = new List<AppSettings.MicDeviceSettings>
        {
            new() { DeviceId = "mic-1", Volume = 1.0f },
        };
        var current = snapshot.Clone();
        current.MicDevices = new List<AppSettings.MicDeviceSettings>
        {
            new() { DeviceId = "mic-1", Volume = 0.5f }, // changed on disk
        };
        var edited = snapshot.Clone();
        edited.MicDevices = new List<AppSettings.MicDeviceSettings>
        {
            new() { DeviceId = "mic-1", Volume = 1.0f }, // rebuilt, same rows
        };

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(0.5f, Assert.Single(merged.MicDevices).Volume);
    }

    [Fact]
    public void CollectionEditedInUi_IsWritten()
    {
        var snapshot = Snapshot();
        snapshot.MicDevices = new List<AppSettings.MicDeviceSettings>
        {
            new() { DeviceId = "mic-1" },
            new() { DeviceId = "mic-2" },
        };
        var current = snapshot.Clone();
        current.MicDevices = new List<AppSettings.MicDeviceSettings> { new() { DeviceId = "mic-1" } };
        var edited = snapshot.Clone();
        edited.MicDevices = new List<AppSettings.MicDeviceSettings> { new() { DeviceId = "mic-1" } }; // mic-2 unticked

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal("mic-1", Assert.Single(merged.MicDevices).DeviceId);
    }

    [Fact]
    public void UnreadableDisk_KeepsWindowState()
    {
        // AppSettings.TryLoad() returns null when the file exists but cannot be
        // parsed; a merge must then keep the live state, never defaults.
        var snapshot = Snapshot();
        var edited = snapshot.Clone();
        edited.OutputDelayMs = 250;

        var merged = SettingsMerge.Merge(snapshot, null, edited);

        Assert.Equal(250, merged.OutputDelayMs);
        Assert.Equal(@"C:\Music", merged.MusicDirectory);
    }

    [Fact]
    public void MissingSnapshot_KeepsWindowState()
    {
        var current = Snapshot();
        var edited = Snapshot();
        edited.OutputDelayMs = 250;

        var merged = SettingsMerge.Merge(null, current, edited);

        Assert.Equal(250, merged.OutputDelayMs);
    }

    [Fact]
    public void MissingEdited_ReturnsDiskState()
    {
        var snapshot = Snapshot();
        var current = Snapshot();
        current.OutputDelayMs = 400;

        var merged = SettingsMerge.Merge(snapshot, current, null);

        Assert.Equal(400, merged.OutputDelayMs);
    }

    [Fact]
    public void AllNull_ReturnsUsableDefaults()
    {
        var merged = SettingsMerge.Merge(null, null, null);

        Assert.NotNull(merged);
        Assert.Equal(1.0f, merged.MasterVolume);
    }

    [Fact]
    public void RuntimeOnlySecret_SurvivesCloneAndMerge()
    {
        // SpotifyClientSecret/Tokens are [JsonIgnore]: a JSON round-trip clone
        // would blank them and the next Save would wipe the protected copies.
        var snapshot = Snapshot();
        snapshot.SpotifyClientSecret = "s3cret";
        snapshot.SpotifyAccessToken = "token";
        var current = snapshot.Clone();
        var edited = snapshot.Clone();

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal("s3cret", merged.SpotifyClientSecret);
        Assert.Equal("token", merged.SpotifyAccessToken);
    }

    [Fact]
    public void MergeDoesNotMutateInputs()
    {
        var snapshot = Snapshot();
        var current = snapshot.Clone();
        current.OutputDelayMs = 400;
        var edited = snapshot.Clone();
        edited.OutputDelayMs = 250;

        _ = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(100, snapshot.OutputDelayMs);
        Assert.Equal(400, current.OutputDelayMs);
        Assert.Equal(250, edited.OutputDelayMs);
    }

    [Fact]
    public void ReusingTheConstructorBaseline_SecondSaveStillCarriesNewerDiskValue()
    {
        // Pins why SettingsWindow does NOT refresh its snapshot after a save. The
        // window re-stamps `edited` from the controls before every save, and the
        // controls were stamped from the constructor snapshot, so an untouched
        // control still compares equal to that snapshot on the second save.
        var snapshot = Snapshot(); // controls stamped at OutputDelayMs = 100

        var firstDisk = snapshot.Clone();
        firstDisk.OutputDelayMs = 400; // external change while the dialog is open
        var save1 = SettingsMerge.Merge(snapshot, firstDisk, snapshot.Clone());
        Assert.Equal(400, save1.OutputDelayMs);

        var secondDisk = snapshot.Clone();
        secondDisk.OutputDelayMs = 500; // another external change, after save #1
        var edited2 = snapshot.Clone(); // read-back stamps the control's 100 again
        edited2.SoundboardFollowsMain = true; // operator edits an unrelated field

        var save2 = SettingsMerge.Merge(snapshot, secondDisk, edited2);

        Assert.Equal(500, save2.OutputDelayMs); // a refreshed baseline would write 400 here
        Assert.True(save2.SoundboardFollowsMain);
    }

    [Fact]
    public void TypedPathSurvivesAPreSaveTakenAfterTheInstallerWroteItsOwn()
    {
        // The engine install pre-save: the operator typed a path, the file already
        // says something else because the installer ran. The typed value wins.
        var snapshot = Snapshot();
        snapshot.PythonPath = "";
        var current = snapshot.Clone();
        current.PythonPath = @"C:\Program Files\Python312\python.exe"; // disk
        var edited = snapshot.Clone();
        edited.PythonPath = @"C:\Dev\venv\Scripts\python.exe"; // operator typed

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal(@"C:\Dev\venv\Scripts\python.exe", merged.PythonPath);
    }

    [Fact]
    public void ClearedPathIsAnEdit_NotADiskFallback()
    {
        var snapshot = Snapshot();
        snapshot.PythonPath = @"C:\Old\python.exe";
        var current = snapshot.Clone();
        var edited = snapshot.Clone();
        edited.PythonPath = ""; // operator cleared the box on purpose

        var merged = SettingsMerge.Merge(snapshot, current, edited);

        Assert.Equal("", merged.PythonPath);
    }
}
