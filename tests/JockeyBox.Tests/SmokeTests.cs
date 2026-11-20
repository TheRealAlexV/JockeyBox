namespace JockeyBox.Tests;

/// <summary>Proves the harness itself works before any 2.0 logic lands.</summary>
public class SmokeTests
{
    [Fact]
    public void AppAssembly_Loads_InTheTestHost()
    {
        var asm = typeof(JockeyBox.Settings.AppSettings).Assembly;
        Assert.Equal("JockeyBox", asm.GetName().Name);
    }

    [Fact]
    public void NewAudioDefaults_CueOnAndEverythingElseNeutral()
    {
        var s = new JockeyBox.Settings.AppSettings();
        Assert.Null(s.ProgramOutputDeviceId);
        Assert.Null(s.MonitorOutputDeviceId);
        // Cue gates default ON: the button switches a deck's cue off, and a
        // profile that never wrote the flag must keep cueing.
        Assert.True(s.DeckACueEnabled);
        Assert.True(s.DeckBCueEnabled);
        Assert.Equal(1.0f, s.MasterVolume);
        Assert.Equal(0, s.OutputDelayMs);
    }
}
