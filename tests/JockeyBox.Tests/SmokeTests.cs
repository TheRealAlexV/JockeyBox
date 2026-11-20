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
    public void NewAudioDefaults_AreOffOrNeutral()
    {
        var s = new JockeyBox.Settings.AppSettings();
        Assert.Null(s.ProgramOutputDeviceId);
        Assert.Null(s.MonitorOutputDeviceId);
        Assert.False(s.DeckACueEnabled);
        Assert.False(s.DeckBCueEnabled);
        Assert.Equal(1.0f, s.MasterVolume);
        Assert.Equal(0, s.OutputDelayMs);
    }
}
