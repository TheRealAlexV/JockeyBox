using System.IO;
using JockeyBox.Data;
using JockeyBox.ViewModels;

namespace JockeyBox.Tests;

/// <summary>
/// The pieces the live read path stands on: the playlists.json stamp recorded in
/// the database (so an external edit is detectable), and the single-reset
/// collection that keeps a 5 000-row list to one layout pass.
/// </summary>
public sealed class LiveReadPathTests
{
    [Fact]
    public void FileStamp_ChangesWhenTheFileIsRewritten_AndIsZeroWhenAbsent()
    {
        using var fixture = new TestDatabase();
        var path = fixture.PathFor("playlists.json");

        Assert.Equal(0, PlaylistFileStamp.Of(path));

        File.WriteAllText(path, "{\"Version\":2,\"Playlists\":[]}");
        var first = PlaylistFileStamp.Of(path);
        Assert.NotEqual(0, first);

        // Different bytes (and a different length) must produce a different stamp.
        File.WriteAllText(path, "{\"Version\":2,\"Playlists\":[{\"Name\":\"x\"}]}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(first, PlaylistFileStamp.Of(path));
    }

    [Fact]
    public void SaveAll_RecordsTheJsonStamp_AndUpdatesItOnTheNextWrite()
    {
        using var fixture = new TestDatabase();
        var playlist = new JockeyBox.Sources.UserPlaylist { Name = "Lists", Kind = JockeyBox.Sources.PlaylistKind.Internal };

        fixture.Playlists.SaveAll(new[] { playlist }, jsonStamp: 111);
        Assert.Equal("111", fixture.Database.GetInfo(PlaylistRepository.JsonStampKey));

        playlist.Name = "Renamed";
        fixture.Playlists.SaveAll(new[] { playlist }, jsonStamp: 222);
        Assert.Equal("222", fixture.Database.GetInfo(PlaylistRepository.JsonStampKey));

        // A write that does not mention the file must not clobber the record.
        fixture.Playlists.SaveAll(new[] { playlist });
        Assert.Equal("222", fixture.Database.GetInfo(PlaylistRepository.JsonStampKey));
    }

    [Fact]
    public void LoadPlaylistRows_SingleQuery_ReturnsEveryRowInOrder()
    {
        using var fixture = new TestDatabase();
        const int count = 5_217;

        var playlist = new JockeyBox.Sources.UserPlaylist
        {
            Name = "Dirty",
            Kind = JockeyBox.Sources.PlaylistKind.Internal,
        };
        for (var i = 0; i < count; i++)
        {
            playlist.Tracks.Add(new JockeyBox.Sources.SourceTrack
            {
                Id = $"remote-{i:D5}",
                Title = $"Track {i:D5}",
                Artist = "Artist",
                DurationMs = 1000 + i,
            });
        }

        fixture.Playlists.Save(playlist);
        var loaded = fixture.Playlists.Load(playlist.Id);

        Assert.NotNull(loaded);
        Assert.Equal(count, loaded!.Tracks.Count);
        Assert.Equal("remote-00000", loaded.Tracks[0].Id);
        Assert.Equal($"remote-{count - 1:D5}", loaded.Tracks[^1].Id);
    }

    [Fact]
    public void ReplaceAll_RaisesOneReset_NotOneEventPerRow()
    {
        var collection = new BulkObservableCollection<int>();
        var resets = 0;
        var other = 0;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
            else other++;
        };

        var rows = Enumerable.Range(0, 5_217).ToList();
        collection.ReplaceAll(rows);

        Assert.Equal(1, resets);
        Assert.Equal(0, other);
        Assert.Equal(5_217, collection.Count);
        Assert.Equal(0, collection[0]);
        Assert.Equal(5_216, collection[^1]);

        collection.ReplaceAll(new[] { 7, 8 });
        Assert.Equal(2, resets);
        Assert.Equal(2, collection.Count);
    }

    [Fact]
    public void AddRange_RaisesOneReset_AndAppends()
    {
        var collection = new BulkObservableCollection<int>(new[] { 1, 2 });
        var events = 0;
        collection.CollectionChanged += (_, _) => events++;

        collection.AddRange(Enumerable.Range(3, 100));
        collection.AddRange(Array.Empty<int>());

        Assert.Equal(1, events);
        Assert.Equal(102, collection.Count);
        Assert.Equal(1, collection[0]);
        Assert.Equal(102, collection[^1]);
    }
}
