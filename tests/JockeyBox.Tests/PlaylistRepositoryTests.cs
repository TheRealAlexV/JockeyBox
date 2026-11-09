using JockeyBox.Data;
using JockeyBox.Sources;

namespace JockeyBox.Tests;

/// <summary>Playlist round-trip, ordering and spaced positions.</summary>
public sealed class PlaylistRepositoryTests
{
    private static UserPlaylist InternalPlaylist(string name, int count = 3)
    {
        var playlist = new UserPlaylist { Name = name, Kind = PlaylistKind.Internal };
        for (var i = 0; i < count; i++)
        {
            playlist.Tracks.Add(new SourceTrack
            {
                Id = $"row-{i:D4}",
                LocalKey = $"artist {i % 7}/album/track {i:D4}",
                Title = $"Track {i:D4}",
                Artist = $"Artist {i % 7}",
                Album = "Album",
                DurationMs = 180_000 + i,
                Isrc = $"ISRC{i:D8}",
            });
        }

        return playlist;
    }

    [Fact]
    public void RoundTrip_5000Rows_PreservesOrderAndIdentity()
    {
        using var fixture = new TestDatabase();
        var repository = fixture.Playlists;
        var playlist = InternalPlaylist("Marathon", 5_000);

        repository.Save(playlist);
        var loaded = Assert.Single(repository.LoadAll());

        Assert.Equal(playlist.Id, loaded.Id);
        Assert.Equal("Marathon", loaded.Name);
        Assert.Equal(PlaylistKind.Internal, loaded.Kind);
        Assert.Equal(5_000, loaded.Tracks.Count);

        for (var i = 0; i < 5_000; i++)
        {
            Assert.Equal(playlist.Tracks[i].Id, loaded.Tracks[i].Id);
            Assert.Equal(playlist.Tracks[i].LocalKey, loaded.Tracks[i].LocalKey);
            Assert.Equal(playlist.Tracks[i].Title, loaded.Tracks[i].Title);
            Assert.Equal(playlist.Tracks[i].DurationMs, loaded.Tracks[i].DurationMs);
        }
    }

    [Fact]
    public void TwoPlaylistsWithTheSameName_AreBothDurable()
    {
        // Regression: playlists.name carried a UNIQUE index, so a real
        // playlists.json with two same-titled lists could never be seeded and
        // every start fell back to parsing 6 MB of JSON. Identity is the id.
        using var fixture = new TestDatabase();
        fixture.Playlists.SaveAll(new[]
        {
            InternalPlaylist("YouTube playlist", 2),
            InternalPlaylist("YouTube playlist", 3),
        }, jsonStamp: 42);

        var loaded = fixture.Playlists.LoadAll();

        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, p => Assert.Equal("YouTube playlist", p.Name));
        Assert.Equal(new[] { 2, 3 }, loaded.Select(p => p.Tracks.Count).OrderBy(n => n));
        Assert.Equal("42", fixture.Database.GetInfo(PlaylistRepository.JsonStampKey));
    }

    [Fact]
    public void Positions_AreSpacedBy1024()
    {
        using var fixture = new TestDatabase();
        fixture.Playlists.Save(InternalPlaylist("Spaced", 4));

        var items = fixture.Playlists.LoadItems(fixture.Playlists.LoadPlaylists()[0].Id);

        Assert.Equal(new long[] { 1024, 2048, 3072, 4096 }, items.Select(i => i.Position).ToArray());
    }

    [Fact]
    public void MoveItem_OneRow_TouchesOnlyThatRow()
    {
        using var fixture = new TestDatabase();
        var repository = fixture.Playlists;
        repository.Save(InternalPlaylist("Movable", 4));
        var id = repository.LoadPlaylists()[0].Id;

        var before = repository.LoadItems(id);
        Assert.True(repository.MoveItem(id, 3, 0));

        var after = repository.LoadItems(id);
        Assert.Equal(new[] { "Track 0003", "Track 0000", "Track 0001", "Track 0002" },
            after.Select(i => i.Title).ToArray());

        // Only the moved row's position changed; the other three kept theirs.
        var unchanged = after.Where((item, index) => index > 0)
            .Select(item => item.Position)
            .ToArray();
        Assert.Equal(before.Take(3).Select(i => i.Position).ToArray(), unchanged);
    }

    [Fact]
    public void SaveAll_RemovesPlaylistsThatVanished()
    {
        using var fixture = new TestDatabase();
        var repository = fixture.Playlists;
        var keep = InternalPlaylist("Keep", 2);
        var drop = InternalPlaylist("Drop", 2);
        repository.SaveAll(new[] { keep, drop });

        repository.SaveAll(new[] { keep });

        var loaded = Assert.Single(repository.LoadAll());
        Assert.Equal("Keep", loaded.Name);
    }

    [Fact]
    public void SaveAll_SkipsRewriteWhenNothingChanged()
    {
        using var fixture = new TestDatabase();
        var repository = fixture.Playlists;
        var playlist = InternalPlaylist("Fingerprinted", 3);
        repository.SaveAll(new[] { playlist });

        var hash1 = Hash(fixture);
        repository.SaveAll(new[] { playlist });

        Assert.Equal(hash1, Hash(fixture));
        Assert.Equal(3, repository.LoadItems(playlist.Id).Count);
    }

    private static string? Hash(TestDatabase fixture) => fixture.Database.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM playlists LIMIT 1;";
        return cmd.ExecuteScalar()?.ToString();
    });

    [Fact]
    public void ImportedPlaylist_KeysOnRemoteId_AndSurvivesWithNoTrackRow()
    {
        using var fixture = new TestDatabase();
        var repository = fixture.Playlists;
        var playlist = new UserPlaylist
        {
            Name = "Spotify mix",
            Kind = PlaylistKind.Imported,
            Source = MediaSource.Spotify,
            SourceId = "37i9dQZF1DXcBWIGoYBM5M",
            SyncEnabled = true,
            LastSyncedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Tracks =
            {
                new SourceTrack { Id = "4uLU6hMCjMI75M1A2tUJCW", Source = MediaSource.Spotify, Title = "Never Gonna Give You Up", Artist = "Rick Astley" },
            },
        };

        repository.Save(playlist);
        var loaded = repository.Load(playlist.Id)!;

        Assert.Equal(PlaylistKind.Imported, loaded.Kind);
        Assert.Equal(MediaSource.Spotify, loaded.Source);
        Assert.True(loaded.SyncEnabled);
        Assert.Equal(playlist.LastSyncedUtc, loaded.LastSyncedUtc);
        Assert.Null(Assert.Single(loaded.Tracks).LocalKey);

        var item = Assert.Single(repository.LoadItems(playlist.Id));
        Assert.Equal("4uLU6hMCjMI75M1A2tUJCW", item.SourceKey);
        Assert.Equal(0, fixture.Tracks.Count());
    }

    [Fact]
    public void EmptyPlaylist_RoundTrips()
    {
        using var fixture = new TestDatabase();
        var repository = fixture.Playlists;
        var playlist = new UserPlaylist { Name = "Nothing yet", Kind = PlaylistKind.Internal };

        repository.Save(playlist);
        var loaded = repository.Load(playlist.Id)!;

        Assert.Empty(loaded.Tracks);
        Assert.Equal(0, repository.LoadPlaylists()[0].TrackCount);
    }
}
