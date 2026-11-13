using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JockeyBox.Audio.Beat;

namespace JockeyBox.Tests;

/// <summary>
/// The beat-grid document is expensive to rebuild (one Python run per track), so
/// the cases here are about not losing it: a clean round trip, a crash in the
/// middle of a write, and a corrupt file that must never take the app down.
/// </summary>
public sealed class BeatGridStoreTests : IDisposable
{
    private readonly string _dir;

    public BeatGridStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jb-tests", "beat-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string DocumentPath => Path.Combine(_dir, "beat-grids.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BeatGrid Grid(double bpm = 128.0) => new(
        bpm, 0.412,
        Enumerable.Range(0, 16).Select(i => 0.412 + i * 60.0 / bpm).ToArray(),
        bpmP10: bpm - 1.4, bpmP90: bpm + 1.1, confidence: 0.83);

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        store.Save(@"C:\Music\a.mp3", Grid());
        store.Flush();
        store.Dispose();

        Assert.True(File.Exists(DocumentPath));

        using var reopened = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        Assert.Equal(1, reopened.Count);
        Assert.True(reopened.Has(@"C:\Music\a.mp3"));

        var grid = reopened.Get(@"C:\Music\a.mp3");
        Assert.NotNull(grid);
        Assert.Equal(128.0, grid!.Bpm, 3);
        Assert.Equal(0.412, grid.FirstBeatSeconds, 3);
        Assert.Equal(16, grid.Beats.Count);
        Assert.Equal(0.412, grid.Beats[0], 3);
        Assert.Equal(126.6, grid.BpmP10, 3);
        Assert.Equal(129.1, grid.BpmP90, 3);
        Assert.Equal(0.83, grid.Confidence, 3);
    }

    [Fact]
    public void PathComparisonIgnoresCaseAndSeparators()
    {
        using var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        store.Save(@"C:\Music\A.mp3", Grid());

        Assert.True(store.Has(@"c:\music\a.mp3"));
        Assert.NotNull(store.Get(@"C:\Music\a.mp3"));
        Assert.False(store.Has(@"C:\Music\b.mp3"));
        Assert.Null(store.Get(@""));
    }

    [Fact]
    public void WritesAtomicallyAndLeavesNoTempBehind()
    {
        using var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        store.Save(@"C:\Music\a.mp3", Grid(124));
        store.Flush();

        Assert.True(File.Exists(DocumentPath));
        Assert.False(File.Exists(DocumentPath + ".tmp"));

        // The document is valid JSON with the track in it, i.e. the rename that the
        // reader sees is always a complete document.
        using var doc = JsonDocument.Parse(File.ReadAllText(DocumentPath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public void CorruptDocumentIsQuarantinedAndTheStoreStartsEmpty()
    {
        File.WriteAllText(DocumentPath, "{ this is not json");

        using var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);

        Assert.Equal(0, store.Count);
        Assert.Null(store.Get(@"C:\Music\a.mp3"));
        Assert.True(File.Exists(DocumentPath + ".corrupt"));
        Assert.False(File.Exists(DocumentPath));
    }

    [Fact]
    public void LeftoverTempFileFromACrashIsDiscarded()
    {
        using (var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true))
        {
            store.Save(@"C:\Music\a.mp3", Grid());
            store.Flush();
        }

        var json = File.ReadAllText(DocumentPath);
        File.WriteAllText(DocumentPath + ".tmp", json[..(json.Length / 2)]);

        using var reopened = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        Assert.Equal(1, reopened.Count);
        Assert.False(File.Exists(DocumentPath + ".tmp"));
    }

    [Fact]
    public void EmptyFileIsTreatedAsNoData()
    {
        File.WriteAllText(DocumentPath, "   ");
        using var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void UnusableGridsAreNotStored()
    {
        using var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
        store.Save(@"C:\Music\a.mp3", new BeatGrid(0, 0));
        Assert.Equal(0, store.Count);
        Assert.False(store.Has(@"C:\Music\a.mp3"));
    }

    [Fact]
    public void MirrorReceivesTheSavedGridAndPath()
    {
        var seen = new List<(string? Path, double Bpm)>();
        using var store = new BeatGridStore(DocumentPath, (grid, path) =>
        {
            seen.Add((path, grid.Bpm));
            return true;
        });

        store.Save(@"C:\Music\a.mp3", Grid(126));
        store.Save(@"C:\Music\b.mp3", Grid(130));

        Assert.Equal(2, seen.Count);
        Assert.Equal(@"C:\Music\a.mp3", seen[0].Path);
        Assert.Equal(126, seen[0].Bpm, 3);
        Assert.Equal(130, seen[1].Bpm, 3);
    }

    [Fact]
    public void MirrorFailureDoesNotLoseTheGrid()
    {
        using var store = new BeatGridStore(DocumentPath, (_, _) => throw new IOException("db gone"));
        store.Save(@"C:\Music\a.mp3", Grid(122));

        Assert.Equal(1, store.Count);
        Assert.Equal(122, store.Get(@"C:\Music\a.mp3")!.Bpm, 3);
    }

    [Fact]
    public void DatabaseMirrorWritesTheBeatColumns()
    {
        using var db = new TestDatabase();
        var path = db.PathFor("music/track.mp3");
        db.Tracks.Upsert(new JockeyBox.Data.TrackRecord
        {
            Path = path,
            PathKey = JockeyBox.Data.LibraryKeys.PathKey(path),
            Title = "track",
            DurationMs = 180_000,
        });

        Assert.True(db.Tracks.UpdateBeatGrid(path, 128.0, 126.5, 129.5, 0.82, 412));

        var record = db.Tracks.GetByPath(path);
        Assert.NotNull(record);
        Assert.Equal(128.0, record!.Bpm, 3);
        Assert.Equal(126.5, record.BpmP10, 3);
        Assert.Equal(129.5, record.BpmP90, 3);
        Assert.Equal(0.82, record.BpmConfidence, 3);
        Assert.Equal(412, record.FirstBeatMs);
    }

    [Fact]
    public void StaleTruncatedTempNeverReplacesTheGoodDocument()
    {
        using (var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true))
        {
            for (var i = 0; i < 40; i++) store.Save($@"C:\Music\t{i}.mp3", Grid(120 + i));
            store.Flush();
        }

        var good = File.ReadAllBytes(DocumentPath);

        // The shape of a kill mid-write: the new document is half on the temp file,
        // the live one is still the previous complete document.
        File.WriteAllText(DocumentPath + ".tmp", "{\"c:\\\\music\\\\t0.mp3\":{\"bpm\":12");

        using var reopened = new BeatGridStore(DocumentPath, mirror: (_, _) => true);

        Assert.Equal(40, reopened.Count);
        Assert.False(File.Exists(DocumentPath + ".tmp"));
        Assert.Equal(good, File.ReadAllBytes(DocumentPath));
    }

    [Fact]
    public void TruncatedDocumentIsQuarantinedByteForByteAndTheNextSaveDoesNotTouchIt()
    {
        using (var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true))
        {
            for (var i = 0; i < 40; i++) store.Save($@"C:\Music\t{i}.mp3", Grid(120 + i));
            store.Flush();
        }

        // A writer that was killed without the temp-then-rename dance left a
        // prefix of a whole document in place.
        var whole = File.ReadAllText(DocumentPath);
        var truncated = whole[..(whole.Length / 2)];
        File.WriteAllText(DocumentPath, truncated);

        using var reopened = new BeatGridStore(DocumentPath, mirror: (_, _) => true);

        Assert.Equal(0, reopened.Count);
        Assert.False(File.Exists(DocumentPath));
        Assert.Equal(truncated, File.ReadAllText(DocumentPath + ".corrupt"));

        // Nothing in the store may write over the preserved bytes.
        reopened.Save(@"C:\Music\new.mp3", Grid(128));
        reopened.Flush();

        Assert.Equal(truncated, File.ReadAllText(DocumentPath + ".corrupt"));
        using var doc = JsonDocument.Parse(File.ReadAllText(DocumentPath));
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public void CorruptDocumentThatCannotBeMovedAsideIsNeverOverwritten()
    {
        const string corrupt = "{ this is not json";
        File.WriteAllText(DocumentPath, corrupt);

        // A reader (a backup tool, an antivirus scan) holding the file open: the
        // quarantine rename cannot land, so the bytes on disk are the only copy.
        using (var held = new FileStream(DocumentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var store = new BeatGridStore(DocumentPath, mirror: (_, _) => true);
            Assert.Equal(0, store.Count);

            store.Save(@"C:\Music\a.mp3", Grid(128));
            store.Flush();
        }

        Assert.Equal(corrupt, File.ReadAllText(DocumentPath));
        Assert.False(File.Exists(DocumentPath + ".corrupt"));
    }
}
