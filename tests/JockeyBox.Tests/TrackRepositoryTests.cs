using JockeyBox.Data;

namespace JockeyBox.Tests;

/// <summary>Upsert identity, scan bookkeeping and search behaviour.</summary>
public sealed class TrackRepositoryTests
{
    private static TrackRecord Row(TestDatabase fixture, string relative, string title, string artist = "Artist") => new()
    {
        Path = fixture.PathFor(relative),
        Title = title,
        Artist = artist,
        Album = "Album",
        DurationMs = 180_000,
    };

    [Fact]
    public void Upsert_ByPathKey_IsCaseAndSeparatorInsensitive()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;

        var first = tracks.Upsert(new TrackRecord
        {
            Path = fixture.PathFor("Music/ABBA/Arrival/01 - Dancing Queen.mp3"),
            Title = "Dancing Queen",
            Artist = "ABBA",
        });

        // Same file, Windows separators, different case: must update the row, not add one.
        var second = tracks.Upsert(new TrackRecord
        {
            Path = fixture.PathFor("Music/ABBA/Arrival/01 - Dancing Queen.mp3").Replace('/', '\\').ToUpperInvariant(),
            Title = "Dancing Queen (Remastered)",
            Artist = "ABBA",
        });

        Assert.Equal(first, second);
        Assert.Equal(1, tracks.Count());
        Assert.Equal("Dancing Queen (Remastered)", tracks.GetById(first)!.Title);
    }

    [Fact]
    public void Upsert_KeepsRowId_AndRefreshesTimestamps()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;
        var row = Row(fixture, "a.mp3", "A");

        var id = tracks.Upsert(row);
        var before = tracks.GetById(id)!;
        Thread.Sleep(15);

        tracks.Upsert(new TrackRecord { Path = row.Path, Title = "A (live version)" });
        var after = tracks.GetById(id)!;

        Assert.Equal(id, after.Id);
        Assert.Equal("A (live version)", after.Title);
        Assert.True(after.LastSeenUtc >= before.LastSeenUtc);
    }

    [Fact]
    public void Scan_Generation_MarksUnseenRowsMissing()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;

        tracks.Upsert(Row(fixture, "keep.mp3", "Keep"));
        tracks.Upsert(Row(fixture, "gone.mp3", "Gone"));

        var scan = tracks.BeginScan();
        tracks.Upsert(Row(fixture, "keep.mp3", "Keep"), scan);
        var marked = tracks.MarkMissingExcept(scan);

        Assert.Equal(1, marked);
        Assert.False(tracks.GetByPath(fixture.PathFor("keep.mp3"))!.IsMissing);
        Assert.True(tracks.GetByPath(fixture.PathFor("gone.mp3"))!.IsMissing);

        // A later scan that sees the file again clears the flag.
        var rescan = tracks.BeginScan();
        tracks.Upsert(Row(fixture, "gone.mp3", "Gone"), rescan);
        Assert.False(tracks.GetByPath(fixture.PathFor("gone.mp3"))!.IsMissing);
    }

    [Fact]
    public void ApplyScan_MarksEverythingOutsideTheListMissing()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;

        tracks.Upsert(Row(fixture, "one.mp3", "One"));
        tracks.Upsert(Row(fixture, "two.mp3", "Two"));

        var marked = tracks.ApplyScan(new[] { LibraryKeys.PathKey(fixture.PathFor("one.mp3")) });

        Assert.Equal(1, marked);
        Assert.Single(tracks.All(includeMissing: false));
        Assert.Equal(2, tracks.All(includeMissing: true).Count);
    }

    [Fact]
    public void UpsertMany_BulkImport_CommitsInBatches()
    {
        using var fixture = new TestDatabase();
        var rows = Enumerable.Range(0, 5_000)
            .Select(i => new TrackRecord
            {
                Path = fixture.PathFor($"bulk/{i:D4}.mp3"),
                Title = $"Track {i:D4}",
                Artist = $"Artist {i % 25}",
                DurationMs = 120_000 + i,
            })
            .ToList();

        var written = fixture.Tracks.UpsertMany(rows, batchSize: 1000);

        Assert.Equal(5_000, written);
        Assert.Equal(5_000, fixture.Tracks.Count());
    }

    [Fact]
    public void Search_FindsPrefixAndSubstring()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;

        tracks.UpsertMany(new[]
        {
            new TrackRecord { Path = fixture.PathFor("1.mp3"), Title = "Dancing Queen", Artist = "ABBA" },
            new TrackRecord { Path = fixture.PathFor("2.mp3"), Title = "Fernando", Artist = "ABBA" },
            new TrackRecord { Path = fixture.PathFor("3.mp3"), Title = "Queen of the Night", Artist = "Mozart" },
        });

        Assert.Contains(tracks.Search("dancing"), t => t.Title == "Dancing Queen");
        // Substring in the middle of a word: prefix matching alone would miss it.
        Assert.Contains(tracks.Search("ncin"), t => t.Title == "Dancing Queen");
        Assert.Contains(tracks.Search("abba"), t => t.Artist == "ABBA");
    }

    [Fact]
    public void Search_IsDiacriticInsensitive()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;

        tracks.Upsert(new TrackRecord { Path = fixture.PathFor("bjork.mp3"), Title = "Jóga", Artist = "Björk" });

        Assert.Single(tracks.Search("bjork"));
        Assert.Single(tracks.Search("Björk"));
        Assert.Single(tracks.Search("joga"));
        Assert.True(tracks.FtsContains("bjork"));
    }

    [Fact]
    public void Delete_RemovesFtsRowToo()
    {
        using var fixture = new TestDatabase();
        var tracks = fixture.Tracks;
        tracks.Upsert(new TrackRecord { Path = fixture.PathFor("x.mp3"), Title = "Ephemeral", Artist = "Nobody" });

        Assert.True(tracks.Delete(fixture.PathFor("x.mp3")));
        Assert.Empty(tracks.Search("ephemeral"));
    }

    [Fact]
    public void ReadApis_ReturnDetachedValues()
    {
        using var fixture = new TestDatabase();
        fixture.Tracks.Upsert(new TrackRecord { Path = fixture.PathFor("d.mp3"), Title = "Detached", Bpm = 128.5 });

        // The record survives the connection that produced it (plain record, no reader).
        var row = fixture.Tracks.All(includeMissing: true).Single();

        Assert.Equal("Detached", row.Title);
        Assert.Equal(128.5, row.Bpm);
    }
}
