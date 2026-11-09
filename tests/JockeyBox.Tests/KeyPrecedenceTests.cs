using System;
using System.ComponentModel;
using System.IO;
using JockeyBox.Audio.Beat;
using JockeyBox.Library;

namespace JockeyBox.Tests;

/// <summary>
/// The TrackEntry key comes from two sources with a fixed precedence: the file's
/// TKEY tag, else the analyser's estimate. These cases pin that order, the
/// "no key is null, never empty" contract the em-dash fallback relies on, and the
/// fact that an analysed key survives a restart through beat-grids.json.
/// </summary>
public sealed class KeyPrecedenceTests : IDisposable
{
    private readonly string _dir;

    public KeyPrecedenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jb-tests", "key-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BeatGrid Analysed(string? key) =>
        new(128.0, 0.1, key: key, keyConfidence: 0.71, keyAnalysed: key != null);

    [Fact]
    public void TagKeyBeatsAnalysedKey()
    {
        Assert.Equal("8A", TrackKeyResolver.Resolve("8A", Analysed("Am")));
    }

    [Fact]
    public void TaglessTrackTakesAnalysedKey()
    {
        Assert.Equal("Am", TrackKeyResolver.Resolve(null, Analysed("Am")));
        Assert.Equal("Am", TrackKeyResolver.Resolve("  ", Analysed("Am")));
    }

    [Fact]
    public void NoSourceMeansNullNotEmpty()
    {
        Assert.Null(TrackKeyResolver.Resolve(null, null));
        Assert.Null(TrackKeyResolver.Resolve(null, Analysed(null)));
        Assert.Null(TrackKeyResolver.Resolve(null, new BeatGrid(128.0, 0.1)));
    }

    [Fact]
    public void AnalysedKeySurvivesRestart()
    {
        var path = @"C:\Music\tagless.mp3";
        var document = Path.Combine(_dir, "beat-grids.json");

        var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(path, Analysed("F#m"));
        store.Flush();
        store.Dispose();

        using var reopened = new BeatGridStore(document, mirror: (_, _) => true);
        var grid = reopened.Get(path);
        Assert.NotNull(grid);
        Assert.Equal("F#m", grid!.Key);
        Assert.True(grid.KeyAnalysed);
        Assert.Equal("F#m", TrackKeyResolver.Resolve(null, grid));
    }

    [Fact]
    public void RowKeyNotifiesWhenAnalysisFillsIt()
    {
        var row = new TrackEntry { RelativeKey = "a/song", RelativePath = "a/song.mp3" };
        var changed = new System.Collections.Generic.List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Key = TrackKeyResolver.Resolve(null, Analysed("Am"));

        Assert.Equal("Am", row.Key);
        Assert.Contains(nameof(TrackEntry.Key), changed);
    }
}
