using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A marker line (<see cref="LyricLine.IsInstrumental"/>) is not sung text. The
/// section selector already refused to let one <em>end</em> a section; these pin
/// the other half: a selection that starts on a marker must start at the next
/// real line, and a selection with no real line at all must be refused rather
/// than written at the marker's own time (mid-gap).
/// </summary>
public class LyricsSectionSelectorTests
{
    private static LyricLine Sung(int seconds, string text) =>
        new(TimeSpan.FromSeconds(seconds), text);

    private static LyricLine Marker(int seconds) =>
        new(TimeSpan.FromSeconds(seconds), InstrumentalGap.Marker) { IsInstrumental = true };

    private static LyricsDocument Doc(params LyricLine[] lines) => new()
    {
        Synced = true,
        Source = "test",
        SourceId = "test",
        Lines = lines.ToList(),
    };

    [Fact]
    public void MarkerOnlySelection_IsRefused()
    {
        // line 0 sung, line 1 marker, line 2 sung: selecting the marker alone
        // would otherwise write a section starting 5 s into the gap.
        var doc = Doc(Sung(2, "hold on"), Marker(7), Sung(42, "next verse"));

        Assert.Null(LyricSectionSelector.FromLines(doc, 1, 1));
    }

    [Fact]
    public void MarkerPlusRealLine_StartsAtTheRealLine()
    {
        var doc = Doc(Sung(2, "hold on"), Marker(7), Sung(42, "next verse"));

        var section = LyricSectionSelector.FromLines(doc, 1, 2);

        Assert.NotNull(section);
        Assert.Equal(42_000, section!.Value.StartMs);
        Assert.True(section.Value.EndMs > section.Value.StartMs);
    }

    [Fact]
    public void TwoMarkersAndNothingElse_AreRefused()
    {
        var doc = Doc(Marker(7), Marker(20));

        Assert.Null(LyricSectionSelector.FromLines(doc, 0, 1));
    }

    [Fact]
    public void OrdinaryTwoRealLineSelection_IsUnchanged()
    {
        var doc = Doc(Sung(2, "hold on"), Sung(5, "next verse"), Sung(9, "later"));

        var section = LyricSectionSelector.FromLines(doc, 0, 1);

        Assert.NotNull(section);
        Assert.Equal(2_000, section!.Value.StartMs);
        Assert.Equal(9_000, section.Value.EndMs);
    }
}

/// <summary>
/// The command path: the same resolution must drive the auto label, so a
/// selection that starts on a marker is named after the real line it resolved
/// to instead of the marker's raw index.
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class LyricsSectionSelectorViewModelTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _home;

    public LyricsSectionSelectorViewModelTests()
    {
        _previousHome = Environment.GetEnvironmentVariable("JOCKEYBOX_HOME");
        string root = OperatingSystem.IsWindows() ? @"C:\Dev" : Path.GetTempPath();
        _home = Path.Combine(root, "jb-lyrics-section", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch { /* a save timer may still hold it */ }
    }

    private static TrackEntry Track() => new()
    {
        RelativeKey = "covers/song",
        RelativePath = "covers/Song.mp3",
        Title = "Song",
        Artist = "Band",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static LyricLine Sung(int seconds, string text) =>
        new(TimeSpan.FromSeconds(seconds), text);

    private static LyricLine Marker(int seconds) =>
        new(TimeSpan.FromSeconds(seconds), InstrumentalGap.Marker) { IsInstrumental = true };

    private static LyricsDocument Doc(params LyricLine[] lines) => new()
    {
        Synced = true,
        Source = "test",
        SourceId = "test",
        Lines = lines.ToList(),
    };

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        typeof(MainViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args.Length == 0 ? null : args);

    [Fact]
    public void MarkerPlusRealLine_LabelNamesTheResolvedRealLine()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() =>
            {
                vm.LoadedTrack = Track();
                // 0 sung, 1 marker, 2 sung: the selection starts on the marker.
                Invoke(vm, "ApplyLyricsDocument", Doc(Sung(2, "hold on"), Marker(7), Sung(42, "next verse")), null);
                vm.SetTimingForSelectedLines(new[] { 1, 2 });
            });

            Assert.Contains("line 3", vm.StatusText);
            Assert.DoesNotContain("line 2", vm.StatusText);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }

        using var store = TrackMetadataStore.Open(Path.Combine(_home, "track-metadata.json"));
        var section = Assert.Single(store.GetSections("covers/song"));
        Assert.Equal(42_000, section.StartMs);
        Assert.Equal("line 3", section.Label);
    }

    [Fact]
    public void MarkerOnlySelection_IsRefused_AndWritesNothing()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() =>
            {
                vm.LoadedTrack = Track();
                Invoke(vm, "ApplyLyricsDocument", Doc(Sung(2, "hold on"), Marker(7), Sung(42, "next verse")), null);
                vm.SetTimingForSelectedLines(new[] { 1 });
            });

            Assert.Equal("Those lines have no usable timing.", vm.StatusText);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }

        using var store = TrackMetadataStore.Open(Path.Combine(_home, "track-metadata.json"));
        Assert.Empty(store.GetSections("covers/song"));
    }
}
