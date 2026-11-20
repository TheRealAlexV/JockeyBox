using System;
using System.IO;
using System.Reflection;
using JockeyBox.Library;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The lyrics header's offset slider reads and writes the number the store holds.
/// It used to cover only +/-2 s and hand the slider a clamped stand-in, so a 5 s
/// offset set from the Settings dialog was silently rewritten as 2 s the first
/// time the operator touched the thumb.
///
/// <para>Own home, because the no-track header writes the global offset into
/// settings and that must land in a temp directory, never the operator's.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class LyricsOffsetHeaderTests : IDisposable
{
    private readonly string _home;
    private readonly HomeLease _lease;
    private readonly MainViewModel _vm;

    public LyricsOffsetHeaderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "jb-lyrics-offset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _lease = HomeLease.Enter(_home, nameof(LyricsOffsetHeaderTests));
        _vm = UiThread.Run(() => new MainViewModel());
    }

    public void Dispose()
    {
        UiThread.Run(() => _vm.Dispose());
        _lease.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void StoredOffsetBeyondTheOldSliderRange_SurvivesTheReadWritePath()
    {
        // Nothing loaded: the header edits the global offset, which is what the
        // Settings dialog writes, so this is the operator's real setting.
        UiThread.Run(() => _vm.LyricsOffsetMs = 5000);
        Assert.Equal(5000, _vm.LyricsOffsetMs);

        // A panel refresh (track change, section edit) re-reads it into the slider.
        var shown = UiThread.Run(() =>
        {
            Invoke(_vm, "SyncLyricsOffsetEditor");
            return _vm.LyricsOffsetMs;
        });
        Assert.Equal(5000, shown);
        Assert.Equal("+5000 ms", _vm.LyricsOffsetText);

        // Touching the slider writes the number back unchanged...
        UiThread.Run(() => _vm.LyricsOffsetMs = shown);
        Assert.Equal(5000, _vm.LyricsOffsetMs);

        // ...and a nudge steps from 5 s, not from the old 2 s ceiling.
        UiThread.Run(() => _vm.NudgeLyricsCommand.Execute("100"));
        Assert.Equal(5100, _vm.LyricsOffsetMs);
    }

    /// <summary>
    /// The header's scope word must name the number the next move will write. A
    /// loaded track with no offset of its own still writes a track override on the
    /// first move, so the word is TRACK, not GLOBAL: the word used to disagree with
    /// the tooltip and with what the control actually did.
    /// </summary>
    [Fact]
    public void TrackLoadedWithoutAnOwnOffset_IndicatorSaysTrack()
    {
        UiThread.Run(() =>
        {
            _vm.LoadedTrack = new TrackEntry
            {
                RelativeKey = "covers/song",
                RelativePath = "covers/Song.mp3",
                Title = "Song",
                Artist = "Band",
                Duration = TimeSpan.FromMinutes(3),
            };
            Invoke(_vm, "SyncLyricsOffsetEditor");
        });

        Assert.Equal("TRACK", _vm.LyricsOffsetScope);

        // ...and the label is honest: the first move writes that track's offset.
        UiThread.Run(() => _vm.LyricsOffsetMs = 240);
        Assert.Equal(240, _vm.LyricsOffsetMs);
        Assert.Equal("TRACK", _vm.LyricsOffsetScope);
    }

    [Fact]
    public void NothingLoaded_IndicatorSaysGlobal()
    {
        UiThread.Run(() => Invoke(_vm, "SyncLyricsOffsetEditor"));
        Assert.Equal("GLOBAL", _vm.LyricsOffsetScope);
    }

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        vm.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args);
}
