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
        Assert.Equal("8A", TrackKeyResolver.Resolve(null, Analysed("Am")));
        Assert.Equal("8A", TrackKeyResolver.Resolve("  ", Analysed("Am")));
        // The estimate's own note name and its Camelot form are the same key.
        Assert.Equal("8A", TrackKeyResolver.Resolve(null, Analysed("8A")));
    }

    [Fact]
    public void NoteNameAndCamelotTagNormaliseToTheSameKey()
    {
        // Am, 8A and 08a are one key on the wheel; KeyNotation owns the mapping,
        // so a tag in either dialect must come back identical.
        Assert.Equal("8A", TrackKeyResolver.Resolve("Am", null));
        Assert.Equal("8A", TrackKeyResolver.Resolve("8A", null));
        Assert.Equal("8A", TrackKeyResolver.Resolve("08a", null));
        Assert.Equal("8A", TrackKeyResolver.Resolve("Am", Analysed(null)));
        Assert.Equal(TrackKeyResolver.Resolve("Am", null), TrackKeyResolver.Resolve("8A", null));
        Assert.Equal("11A", TrackKeyResolver.Resolve("F#m", null));
    }

    [Fact]
    public void NormalisedTagStillBeatsAnalysedEstimate()
    {
        // Fm is 4A; the estimate says 8A. Normalisation must not hand the row to
        // the estimate, and the winner must still read as Camelot.
        Assert.Equal("4A", TrackKeyResolver.Resolve("Fm", Analysed("8A")));
        Assert.Equal("4A", TrackKeyResolver.Resolve("Fm", Analysed("Am")));
    }

    [Fact]
    public void NoSourceMeansNullNotEmpty()
    {
        Assert.Null(TrackKeyResolver.Resolve(null, null));
        Assert.Null(TrackKeyResolver.Resolve(null, Analysed(null)));
        Assert.Null(TrackKeyResolver.Resolve(null, Analysed("")));
        Assert.Null(TrackKeyResolver.Resolve(null, new BeatGrid(128.0, 0.1)));
        // Never an empty string: the row's em-dash fallback keys off null.
        Assert.NotEqual("", TrackKeyResolver.Resolve(null, null));
        Assert.NotEqual("", TrackKeyResolver.Resolve(null, Analysed("")));
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
        // The cached grid keeps the analyser's raw note name; only the row is Camelot.
        Assert.Equal("F#m", grid!.Key);
        Assert.True(grid.KeyAnalysed);
        Assert.Equal("11A", TrackKeyResolver.Resolve(null, grid));
    }

    [Fact]
    public void RowKeyNotifiesWhenAnalysisFillsIt()
    {
        var row = new TrackEntry { RelativeKey = "a/song", RelativePath = "a/song.mp3" };
        var changed = new System.Collections.Generic.List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Key = TrackKeyResolver.Resolve(null, Analysed("Am"));

        Assert.Equal("8A", row.Key);
        Assert.Contains(nameof(TrackEntry.Key), changed);
    }
}
