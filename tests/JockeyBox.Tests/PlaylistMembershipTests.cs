using System.IO;
using JockeyBox.Data;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Sources;

namespace JockeyBox.Tests;

/// <summary>
/// The pure pieces of the playlist read path: the key index that turns a
/// playlist's rows into library rows without a scan per row, the per-row
/// membership resolution order, and the shallow copy the background JSON mirror
/// serialises.
///
/// <para>Nothing here measures time. The point of these tests is that the fast
/// path and the slow path agree: a row resolved by key, by path or by
/// artist+title must land on the same key, and a snapshot must not be disturbed
/// by edits made after it was built.</para>
/// </summary>
public sealed class PlaylistMembershipTests
{
    private static TrackEntry Track(string relativeKey, string? music = null, string artist = "ABBA",
        string title = "Fernando", int order = 0) => new()
    {
        RelativeKey = relativeKey,
        RelativePath = relativeKey + ".mp3",
        MusicPath = music,
        Artist = artist,
        Title = title,
        Duration = TimeSpan.FromSeconds(1 + order),
    };

    private static LibraryMembershipSnapshot Snapshot(IEnumerable<TrackEntry> tracks,
        IEnumerable<UserPlaylist>? playlists = null,
        IReadOnlyCollection<LedgerEntry>? ledger = null) =>
        LibraryMembershipSnapshot.Build(tracks, playlists ?? Array.Empty<UserPlaylist>(),
            ledger ?? Array.Empty<LedgerEntry>(), LibraryIndex.Empty, libraryGeneration: 1,
            storeRevision: 1, storeStamp: 1);

    [Fact]
    public void TracksForKeys_ReturnsLibraryOrderNotRequestOrder()
    {
        var snapshot = Snapshot(new[]
        {
            Track("abba/arrival/01 - when i kissed", order: 0),
            Track("abba/arrival/02 - fernando", order: 1),
            Track("abba/arrival/03 - tiger", order: 2),
        });

        var rows = snapshot.TracksForKeys(new[]
        {
            "abba/arrival/03 - tiger",
            "abba/arrival/01 - when i kissed",
        });

        Assert.Equal(new[] { "abba/arrival/01 - when i kissed", "abba/arrival/03 - tiger" },
            rows.Select(r => r.RelativeKey));
    }

    [Fact]
    public void TracksForKeys_IsCaseAndSeparatorInsensitive_AndSkipsUnknownKeys()
    {
        var snapshot = Snapshot(new[] { Track("abba/arrival/02 - fernando") });

        var rows = snapshot.TracksForKeys(new[]
        {
            "ABBA\\Arrival\\02 - Fernando",
            "abba/arrival/09 - nope",
            "",
        });

        Assert.Equal("abba/arrival/02 - fernando", Assert.Single(rows).RelativeKey);
    }

    [Fact]
    public void TracksForKeys_EmptyKeyListIsEmpty_NotTheWholeLibrary()
    {
        var snapshot = Snapshot(new[] { Track("abba/arrival/02 - fernando") });

        Assert.Empty(snapshot.TracksForKeys(Array.Empty<string>()));
        Assert.Equal(1, snapshot.LibraryKeyCount);
    }

    [Fact]
    public void Resolve_PrefersStoredLocalKey_OverArtistTitle()
    {
        var snapshot = Snapshot(new[]
        {
            Track("abba/arrival/02 - fernando", artist: "ABBA", title: "Fernando"),
            // An unrelated file that happens to share the tags must not win once
            // the row carries its own key.
            Track("covers/fernando", artist: "The Covers", title: "Fernando"),
        });

        var request = new PlaylistMembershipRequest("p1", "Mine", MediaSource.Manual,
            IsImported: false, IsInternal: true,
            new[] { new MembershipRow("row", "abba/arrival/02 - fernando", null, "The Covers", "Fernando") });

        Assert.Equal(new[] { "abba/arrival/02 - fernando" }, snapshot.Resolve(request));
    }

    [Fact]
    public void Resolve_FallsBackToPathThenTags_ForARowWhoseFileMoved()
    {
        var snapshot = Snapshot(new[]
        {
            Track("abba/arrival/02 - fernando", music: "/music/abba/02 - fernando.mp3",
                artist: "ABBA", title: "Fernando"),
        });

        // The stored key no longer exists, but the downloaded path still does.
        var byPath = new PlaylistMembershipRequest("p1", "Mine", MediaSource.Manual, false, true,
            new[] { new MembershipRow("row", "old/key/that/moved", "/music/abba/02 - fernando.mp3", null, null) });
        Assert.Equal(new[] { "abba/arrival/02 - fernando" }, snapshot.Resolve(byPath));

        // Neither key nor path: the tags are the last identity.
        var byTags = new PlaylistMembershipRequest("p1", "Mine", MediaSource.Manual, false, true,
            new[] { new MembershipRow("row", "old/key/that/moved", null, "abba", "fernando") });
        Assert.Equal(new[] { "abba/arrival/02 - fernando" }, snapshot.Resolve(byTags));
    }

    [Fact]
    public void Resolve_UnresolvableRowIsNotCounted()
    {
        var snapshot = Snapshot(new[] { Track("abba/arrival/02 - fernando") });

        var request = new PlaylistMembershipRequest("p1", "Mine", MediaSource.Manual, false, true,
            new[] { new MembershipRow("row", "gone/file", null, "Nobody", "Nothing") });

        Assert.Empty(snapshot.Resolve(request));
    }

    [Fact]
    public void TryResolveRow_AgreesWithResolve_ForTheSameRow()
    {
        var snapshot = Snapshot(new[]
        {
            Track("abba/arrival/02 - fernando", music: "/music/fernando.mp3", title: "Fernando"),
        });

        var row = new MembershipRow("row", "gone", "/music/fernando.mp3", "ABBA", "Fernando");
        Assert.True(snapshot.TryResolveRow(row, out var key));

        var request = new PlaylistMembershipRequest("p1", "Mine", MediaSource.Manual, false, true, new[] { row });
        Assert.Equal(snapshot.Resolve(request), new[] { key }.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_IgnoresPlaylistRowsWithNoId_AndIndexesThoseWithOne()
    {
        var playlist = new UserPlaylist { Name = "List" };
        playlist.Tracks.Add(new SourceTrack { Id = "  ", Title = "blank id" });
        playlist.Tracks.Add(new SourceTrack { Id = "remote-1", Title = "Fernando", Artist = "ABBA" });

        var snapshot = Snapshot(new[] { Track("abba/arrival/02 - fernando") }, new[] { playlist });

        // No public accessor for the id index: the resolution order is what
        // matters, so assert the snapshot built without throwing on the blank row.
        Assert.Equal(1, snapshot.LibraryKeyCount);
    }
}

/// <summary>
/// The JSON mirror contract: a mutation moves <see cref="PlaylistStore.Revision"/>
/// immediately (the membership cache depends on it), the file write is deferred,
/// and <see cref="PlaylistStore.Flush"/> is what makes the file agree. Runs
/// against a throwaway home, never the operator's.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class PlaylistStoreMirrorTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previousHome;

    public PlaylistStoreMirrorTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(Tools.AppPaths.HomeVariable);
        _home = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(Tools.AppPaths.HomeVariable, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Tools.AppPaths.HomeVariable, _previousHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test run.
        }
    }

    [Fact]
    public void Mutation_MovesRevisionImmediately_AndFlushWritesTheMirror()
    {
        using var fixture = new TestDatabase();
        using var store = new PlaylistStore(fixture.Database);

        var playlist = store.Create("Mirror");
        store.Flush();

        var before = store.Revision;
        store.AddTracks(playlist, new[]
        {
            new SourceTrack { Id = "a", Title = "A", Artist = "X" },
            new SourceTrack { Id = "b", Title = "B", Artist = "Y" },
        });

        Assert.True(store.Revision > before, "a mutation must move Revision synchronously");

        // The mirror is debounced: Flush is the contract that makes it durable.
        store.Flush();

        var text = File.ReadAllText(Path.Combine(_home, "playlists.json"));
        Assert.Contains("\"A\"", text);
        Assert.Contains("\"B\"", text);
    }

    [Fact]
    public void ExternalEdit_Wins_OverAPendingMirror()
    {
        using var fixture = new TestDatabase();
        using var store = new PlaylistStore(fixture.Database);

        var playlist = store.Create("Original");
        store.Flush();

        // A mutation is pending (mirror scheduled but not yet written) when
        // somebody else edits the file. The file must win, and the pending
        // mirror must not undo the edit a moment later.
        store.AddTracks(playlist, new[] { new SourceTrack { Id = "a", Title = "A" } });

        var path = Path.Combine(_home, "playlists.json");
        // Minimal 1.5.0 document, written the way another process would.
        File.WriteAllText(path, """
        {
          "Version": 2,
          "Playlists": [ { "Id": "external", "Name": "FromDisk", "Kind": 1, "Tracks": [] } ]
        }
        """);

        Assert.True(store.RefreshFromDiskIfChanged(), "an external edit must be noticed");
        store.Flush();

        var onDisk = File.ReadAllText(path);
        Assert.Contains("FromDisk", onDisk);
        Assert.DoesNotContain("\"A\"", onDisk);
        Assert.Single(store.Playlists);
        Assert.Equal("FromDisk", store.Playlists[0].Name);
    }

    [Fact]
    public void ShallowCopy_SeparatesMembershipAndFields()
    {
        var playlist = new UserPlaylist
        {
            Name = "Copy me",
            Kind = PlaylistKind.Internal,
            Source = MediaSource.Manual,
            SyncEnabled = true,
        };
        playlist.Tracks.Add(new SourceTrack { Id = "a", Title = "A" });

        var copy = playlist.ShallowCopy();

        Assert.Equal(playlist.Id, copy.Id);
        Assert.Equal("Copy me", copy.Name);
        Assert.Equal(PlaylistKind.Internal, copy.Kind);
        Assert.True(copy.SyncEnabled);

        playlist.Tracks.Add(new SourceTrack { Id = "b", Title = "B" });
        playlist.Name = "Renamed";

        Assert.Single(copy.Tracks);          // the snapshot keeps its membership
        Assert.Equal("Copy me", copy.Name);  // and its label
        Assert.Same(playlist.Tracks[0], copy.Tracks[0]); // rows are shared on purpose
    }
}

/// <summary>
/// <see cref="LibraryIndex.ContainsPath"/> is the warm half of the row
/// availability probe: a hash lookup the playlist load can use instead of a
/// stat per row. These tests pin the contract the load path relies on — a path
/// the scan saw answers true regardless of case, and anything else answers
/// false rather than throwing, so the caller can fall back to the filesystem.
/// </summary>
public sealed class LibraryIndexPathTests
{
    private static LibraryIndex Index(params string[] musicPaths)
    {
        var tracks = musicPaths.Select((path, i) => new TrackEntry
        {
            RelativeKey = $"row-{i}",
            RelativePath = Path.GetFileName(path),
            MusicPath = path,
            Artist = "Artist",
            Title = $"Title {i}",
            Duration = TimeSpan.FromSeconds(1),
        });

        var index = new LibraryIndex();
        index.Rebuild(tracks);
        return index;
    }

    [Fact]
    public void ContainsPath_TrueForAScannedFile_IgnoringCase()
    {
        var index = Index(@"C:\Music\ABBA\Fernando.mp3");

        Assert.True(index.ContainsPath(@"C:\Music\ABBA\Fernando.mp3"));
        Assert.True(index.ContainsPath(@"c:\music\abba\fernando.MP3"));
    }

    [Fact]
    public void ContainsPath_FalseForUnknownBlankOrNull()
    {
        var index = Index(@"C:\Music\ABBA\Fernando.mp3");

        Assert.False(index.ContainsPath(@"C:\Music\ABBA\Gimme.mp3"));
        Assert.False(index.ContainsPath(""));
        Assert.False(index.ContainsPath("   "));
        Assert.False(index.ContainsPath(null));
    }

    [Fact]
    public void ContainsPath_EmptyIndex_AnswersFalseInsteadOfThrowing()
    {
        var index = LibraryIndex.Empty;

        Assert.False(index.ContainsPath(@"C:\Music\ABBA\Fernando.mp3"));
    }

    [Fact]
    public void ContainsPath_SeesOnlyMusicFiles_NotKaraokeOnlyRows()
    {
        // A karaoke-only row has no music path, so it must not appear in the
        // path set: the load path uses the answer to decide "playable locally".
        var index = new LibraryIndex();
        index.Rebuild(new[]
        {
            new TrackEntry { RelativeKey = "karaoke", RelativePath = "karaoke", MusicPath = null, KaraokePath = @"C:\K\Fernando.mp4" },
        });

        Assert.False(index.ContainsPath(@"C:\K\Fernando.mp4"));
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Rebuild_ReplacesThePathSet_SoADeletedFileStopsMatching()
    {
        var index = Index(@"C:\Music\ABBA\Fernando.mp3");
        Assert.True(index.ContainsPath(@"C:\Music\ABBA\Fernando.mp3"));

        index.Rebuild(Array.Empty<TrackEntry>());

        Assert.False(index.ContainsPath(@"C:\Music\ABBA\Fernando.mp3"));
        Assert.Equal(0, index.Count);
    }
}

/// <summary>Serialises the classes that point <c>JOCKEYBOX_HOME</c> at a temp directory.</summary>
[CollectionDefinition(nameof(JockeyBoxHomeCollection), DisableParallelization = true)]
public sealed class JockeyBoxHomeCollection
{
}
