using System.IO;
using JockeyBox.Data;
using JockeyBox.Sources;

namespace JockeyBox.Tests;

public sealed class LibrarySnapshotTests
{
    private static TrackRecord Row(string path, string title, string artist, string album = "", double durationMs = 200_000)
    {
        var normalized = LibraryKeys.NormalizePath(path);
        return new TrackRecord
        {
            Path = normalized,
            PathKey = LibraryKeys.PathKey(normalized),
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = artist,
            DurationMs = (long)durationMs,
            SortTitle = LibraryKeys.SortKey(title),
            SortArtist = LibraryKeys.SortKey(artist),
            AddedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        };
    }

    [Fact]
    public void OrdersBySortArtistThenSortTitle()
    {
        var snapshot = LibrarySnapshot.Build(new[]
        {
            Row("/music/The Beatles/01 - Something.mp3", "Something", "The Beatles"),
            Row("/music/ABBA/01 - Dancing Queen.mp3", "Dancing Queen", "ABBA"),
            Row("/music/ABBA/02 - Fernando.mp3", "Fernando", "ABBA"),
        });

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(
            new[] { "Dancing Queen", "Fernando", "Something" },
            snapshot.Tracks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void TracksForKeys_PreservesRequestOrder_AndSkipsMisses()
    {
        var abba = Row("/music/ABBA/Arrival/01 - Dancing Queen.mp3", "Dancing Queen", "ABBA");
        var bjork = Row("/music/Björk/Homogenic/01 - Hunter.mp3", "Hunter", "Björk");
        var snapshot = LibrarySnapshot.Build(new[] { abba, bjork });

        var keys = new[]
        {
            "abba/arrival/01 - dancing queen",   // relative, case-insensitive
            "missing/not-a-file",
            "björk/homogenic/01 - hunter",
            "01 - dancing queen",                 // bare file name
            LibraryKeys.PathKey(bjork.Path),      // absolute path key
        };

        var found = snapshot.TracksForKeys(keys);

        Assert.Equal(new[] { "Dancing Queen", "Hunter", "Dancing Queen", "Hunter" },
            found.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void FindByKey_HandlesWindowsPathsOnAnyPlatform()
    {
        var row = Row("/music/ABBA/Arrival/02 - Fernando.mp3", "Fernando", "ABBA");
        var snapshot = LibrarySnapshot.Build(new[] { row });

        Assert.NotNull(snapshot.FindByKey("abba/arrival/02 - fernando"));
        Assert.NotNull(snapshot.FindByKey("02 - Fernando"));
        Assert.Null(snapshot.FindByKey("abba/arrival/09 - nope"));
        Assert.Null(snapshot.FindByKey(""));
        Assert.Null(snapshot.FindByKey(null));
    }

    [Fact]
    public void MissingRows_AreNotResolutionTargets()
    {
        var present = Row("/music/ABBA/Arrival/01 - Dancing Queen.mp3", "Dancing Queen", "ABBA");
        var gone = Row("/music/ABBA/Arrival/02 - Fernando.mp3", "Fernando", "ABBA") with { IsMissing = true };

        var snapshot = LibrarySnapshot.Build(new[] { present, gone });

        Assert.Equal(1, snapshot.Count);
        Assert.Null(snapshot.FindByKey("abba/arrival/02 - fernando"));

        var withMissing = LibrarySnapshot.Build(new[] { present, gone }, includeMissing: true);
        Assert.Equal(2, withMissing.Count);
    }

    [Fact]
    public void Resolve_UsesArtistTitleThenTitleThenFileName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jockeybox-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "ABBA"));

        var exactPath = Path.Combine(directory, "ABBA", "01 - Dancing Queen.mp3");
        File.WriteAllText(exactPath, "x");

        var titleOnlyPath = Path.Combine(directory, "ABBA", "02 - Fernando.mp3");
        File.WriteAllText(titleOnlyPath, "x");

        var snapshot = LibrarySnapshot.Build(new[]
        {
            Row(exactPath, "Dancing Queen", "ABBA", "Arrival"),
            Row(titleOnlyPath, "Fernando", "ABBA", "Arrival"),
        });

        try
        {
            // 1. same artist + title
            var exact = snapshot.Resolve(new SourceTrack { Title = "Dancing Queen", Artist = "ABBA" });
            Assert.Equal(exactPath, exact);

            // 2. same title, different artist spelled differently
            var titled = snapshot.Resolve(new SourceTrack { Title = "Fernando", Artist = "ABBA Tribute" });
            Assert.Equal(titleOnlyPath, titled);

            // 3. no tag match at all; the file name carries it
            var byName = snapshot.Resolve(new SourceTrack { Title = "Dancing Queen", Artist = "ABBA" });
            Assert.Equal(exactPath, byName);

            Assert.Null(snapshot.Resolve(new SourceTrack { Title = "Nothing Here", Artist = "ABBA" }));
            Assert.False(snapshot.Contains(new SourceTrack { Title = "Nothing Here", Artist = "ABBA" }));

            // A file that vanished is no longer resolved by default.
            File.Delete(exactPath);
            Assert.Null(snapshot.Resolve(new SourceTrack { Title = "Dancing Queen", Artist = "ABBA" }));
            Assert.Equal(exactPath, snapshot.Resolve(new SourceTrack { Title = "Dancing Queen", Artist = "ABBA" }, requireFileExists: false));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void NativePath_UsesThePlatformSeparator()
    {
        // The last hop before a path reaches the filesystem/ledger: a '/' form
        // must come back with the separator this process keys on. This is the
        // regression that made Resolve hand back "C:/Users/..." on Windows.
        var expected = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "jockeybox", "ABBA", "01 - Dancing Queen.mp3");

        var slashForm = System.IO.Path.GetTempPath().TrimEnd('/', '\\') +
                        "/jockeybox/ABBA/01 - Dancing Queen.mp3";

        Assert.Equal(expected, LibraryKeys.NativePath(slashForm));
        if (System.IO.Path.DirectorySeparatorChar != '/')
            Assert.DoesNotContain('/', LibraryKeys.NativePath(slashForm));
        Assert.Equal("", LibraryKeys.NativePath("  "));
    }

    [Fact]
    public void Store_SwapsWholeSnapshots()
    {
        var store = new LibrarySnapshotStore();
        Assert.True(store.Current.IsEmpty);

        var first = store.Build(new[] { Row("/music/ABBA/a.mp3", "A", "ABBA") });
        Assert.Equal(1, first.Generation);
        Assert.Equal("A", store.Current.TracksForKeys(new[] { "abba/a" }).Single().Title);

        var second = store.Build(new[]
        {
            Row("/music/ABBA/a.mp3", "A", "ABBA"),
            Row("/music/ABBA/b.mp3", "B", "ABBA"),
        });
        Assert.Equal(2, second.Generation);
        Assert.Equal(2, store.Current.Count);

        store.Clear();
        Assert.True(store.Current.IsEmpty);
    }
}
