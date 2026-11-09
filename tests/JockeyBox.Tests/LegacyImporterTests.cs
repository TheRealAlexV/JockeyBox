using System.IO;
using System.Text.Json;
using JockeyBox.Data;
using JockeyBox.Sources;

namespace JockeyBox.Tests;

/// <summary>
/// The importer must survive the real 1.5.0 files: 5 272 playlist rows spread
/// over four lists, a handful of rows with no artist, and lyric keys that are
/// opaque ids rather than paths.
/// </summary>
public sealed class LegacyImporterTests
{
    private static string WriteLegacyFiles(string directory, int playlistRows)
    {
        Directory.CreateDirectory(directory);

        // library.json: absolute path -> tags, no stable "Id" (1.5.0 shape).
        var tracks = new Dictionary<string, object>
        {
            ["C:\\Music\\Björk\\Homogenic\\01 - Hunter.mp3"] = new
            {
                Size = 1234567L,
                MTimeTicks = DateTime.UtcNow.Ticks,
                Title = "Hunter",
                Artist = "Björk",
                Album = "Homogenic",
                Bpm = 0.0,
                DurationMs = 250_000.0,
            },
            ["C:\\Music\\Abba\\Arrival\\01 - Dancing Queen.mp3"] = new
            {
                Size = 2345678L,
                MTimeTicks = DateTime.UtcNow.Ticks,
                Title = "Dancing Queen",
                Artist = "ABBA",
                Album = "Arrival",
                Bpm = 101.0,
                DurationMs = 231_000.0,
            },
            ["C:\\Music\\Untagged\\mystery.mp3"] = new
            {
                Size = 1000L,
                MTimeTicks = DateTime.UtcNow.Ticks,
                Title = "",
                Artist = "",
                Album = "",
                Bpm = 0.0,
                DurationMs = 0.0,
            },
            [""] = new { Size = 0L, MTimeTicks = 0L, Title = "", Artist = "", Album = "", Bpm = 0.0, DurationMs = 0.0 },
            ["C:\\Music\\Broken.mp3"] = "not an object",
        };

        File.WriteAllText(Path.Combine(directory, "library.json"), JsonSerializer.Serialize(tracks));

        var rows = new List<object>(playlistRows);
        for (var i = 0; i < playlistRows; i++)
        {
            var remote = i % 3 == 0;
            rows.Add(new
            {
                Id = "row" + i,
                Source = remote ? 1 : 0,
                Title = "Title " + i,
                Artist = i % 97 == 0 ? "" : "Artist " + (i % 50), // the real files have empty artists
                Album = "Album " + (i % 20),
                DurationMs = 180_000.0 + i,
                VideoId = remote ? "vid" + i : null,
                PlaylistName = "Chill",
            });
        }

        var playlists = new
        {
            Version = 2,
            Playlists = new object[]
            {
                new
                {
                    Id = "pl-chill",
                    Name = "Chill",
                    Source = 0,
                    Kind = 0,
                    SourceId = (string?)null,
                    SyncEnabled = false,
                    LastSyncedUtc = DateTime.MinValue,
                    Tracks = rows,
                },
                new
                {
                    Id = "pl-spot",
                    Name = "Spotify Mix",
                    Source = 1,
                    Kind = 1,
                    SourceId = "37i9dQ",
                    SyncEnabled = true,
                    LastSyncedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    Tracks = new object[]
                    {
                        new { Id = "spot1", Source = 1, Title = "Loud", Artist = "Rihanna", Album = "", DurationMs = 200_000.0, VideoId = "abc" },
                    },
                },
            },
        };

        File.WriteAllText(Path.Combine(directory, "playlists.json"), JsonSerializer.Serialize(playlists));

        // camelCase, opaque lyric key, sections as in the real file.
        var metadata = new
        {
            version = 2,
            legacyLyricsImported = false,
            tracks = new object[]
            {
                new
                {
                    key = "xfkzrnyygfk",
                    title = "",
                    artist = "",
                    lyricsOffsetMs = -1402.4947917116988,
                    lyricsSourceId = "",
                    sections = Array.Empty<object>(),
                    updatedUtc = "2026-10-21T21:42:06.4963945Z",
                },
                new
                {
                    key = "C:\\Music\\Björk\\Homogenic\\01 - Hunter.mp3",
                    title = "Hunter",
                    artist = "Björk",
                    lyricsOffsetMs = 250.0,
                    lyricsSourceId = "lrclib",
                    sections = new object[]
                    {
                        new { startMs = 0L, endMs = 1000L, offsetMs = 0L, label = "Intro" },
                    },
                    updatedUtc = "2026-10-22T00:00:00Z",
                },
            },
        };

        File.WriteAllText(Path.Combine(directory, "track-metadata.json"), JsonSerializer.Serialize(metadata));

        return directory;
    }

    [Fact]
    public void Imports_RealShapes_AndLeavesJsonInPlace()
    {
        using var fixture = new TestDatabase();
        var legacy = WriteLegacyFiles(Path.Combine(fixture.Directory, "legacy"), 300);
        var before = Directory.GetFiles(legacy).ToDictionary(f => f, File.ReadAllText);

        var importer = new LegacyImporter(fixture.Database);
        var report = importer.Run(legacy);

        Assert.Null(report.Error);
        Assert.True(report.Ran);
        Assert.Equal(3, report.Tracks); // three usable library entries
        Assert.Equal(2, report.Skipped); // "" key and the non-object value
        Assert.Equal(2, report.Playlists);
        Assert.Equal(301, report.PlaylistItems);
        Assert.Equal(2, report.Lyrics);
        Assert.Equal(1, report.LyricSections);

        // Guard row set, JSON untouched.
        Assert.True(importer.AlreadyImported());

        var tracks = fixture.Tracks;
        Assert.Equal(3, tracks.Count());
        var bjork = tracks.GetByPath("C:\\Music\\Björk\\Homogenic\\01 - Hunter.mp3");
        Assert.NotNull(bjork);
        Assert.Equal("Björk", bjork!.Artist);

        var loaded = fixture.Playlists.LoadAll();
        Assert.Equal(2, loaded.Count);
        var chill = loaded.Single(p => p.Name == "Chill");
        Assert.Equal(300, chill.Tracks.Count);
        Assert.Equal("row0", chill.Tracks[0].Id);
        Assert.Equal("Title 0", chill.Tracks[0].Title);
        Assert.Equal("row299", chill.Tracks[299].Id);
        Assert.Equal("", chill.Tracks[97].Artist); // empty artist survives
        Assert.Equal(300, chill.Tracks.Select(t => t.Id).Distinct().Count());

        var spotify = loaded.Single(p => p.Name == "Spotify Mix");
        Assert.Equal(MediaSource.Spotify, spotify.Source);
        Assert.Equal("37i9dQ", spotify.SourceId);
        Assert.True(spotify.SyncEnabled);
        Assert.Single(spotify.Tracks);

        var lyrics = fixture.Database.Read(conn =>
            Convert.ToInt64(JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM track_lyrics;")));
        Assert.Equal(2L, lyrics);

        foreach (var file in before)
            Assert.Equal(file.Value, File.ReadAllText(file.Key));
    }

    [Fact]
    public void SecondRun_IsIdempotent_AndForceReplaysWithoutDuplicates()
    {
        using var fixture = new TestDatabase();
        var legacy = WriteLegacyFiles(Path.Combine(fixture.Directory, "legacy"), 40);

        var importer = new LegacyImporter(fixture.Database);
        Assert.True(importer.Run(legacy).Ran);

        var again = importer.Run(legacy);
        Assert.False(again.Ran);
        Assert.Equal(3, fixture.Tracks.Count());

        var forced = importer.Run(legacy, force: true);
        Assert.True(forced.Ran);
        Assert.Equal(3, fixture.Tracks.Count()); // upsert, not append
        Assert.Equal(2, fixture.Playlists.LoadAll().Count);
        Assert.Equal(40, fixture.Playlists.LoadAll().Single(p => p.Name == "Chill").Tracks.Count);
    }

    [Fact]
    public void NoFiles_DoesNothing()
    {
        using var fixture = new TestDatabase();
        var importer = new LegacyImporter(fixture.Database);

        var report = importer.Run(Path.Combine(fixture.Directory, "empty"));

        Assert.False(report.Ran);
        Assert.False(importer.AlreadyImported());
    }
}
