using System;
using System.IO;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// A settings file written by an older build must still load. The operator's own
/// settings.json predates the render pipeline change and still carries members this
/// build no longer has, so an unknown member must never turn into a startup crash.
///
/// <para>The load path is <see cref="AppSettings.TryLoad"/>, whose deserialize call
/// uses default <c>JsonSerializerOptions</c>: unmapped members are skipped, not
/// rejected. This test pins that, so flipping the global default (or adding
/// <c>JsonUnmappedMemberHandling.Disallow</c>) fails here instead of on the
/// operator's machine.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public class SettingsLoadTests
{
    [Fact]
    public void FileWithRemovedKaraokeMembers_LoadsAndKeepsEveryOtherField()
    {
        string home = Path.Combine(
            Path.GetTempPath(), "jb-legacy-settings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            // Shape of a settings.json written before the pipeline rewrite: the
            // removed format member, the removed model member, and the real values
            // that have to survive around them.
            File.WriteAllText(AppSettings.SettingsPath, """
                {
                  "MusicDirectory": "C:\\Music",
                  "SeparationOutputFormat": "FLAC",
                  "SeparationModel": "UVR-MDX-NET-Inst_HQ_3.onnx",
                  "SeparationUseGpu": true,
                  "OutputDelayMs": 175,
                  "DownloadBitrateKbps": 256
                }
                """);

            var loaded = AppSettings.TryLoad();

            Assert.NotNull(loaded);
            Assert.Equal(@"C:\Music", loaded!.MusicDirectory);
            Assert.True(loaded.SeparationUseGpu);
            Assert.Equal(175, loaded.OutputDelayMs);
            Assert.Equal(256, loaded.DownloadBitrateKbps);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
        }
    }
}
