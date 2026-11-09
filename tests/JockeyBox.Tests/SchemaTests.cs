using System.IO;
using JockeyBox.Data;
using Microsoft.Data.Sqlite;

namespace JockeyBox.Tests;

/// <summary>Schema idempotency, pragmas and the FTS5 external-content wiring.</summary>
public sealed class SchemaTests
{
    [Fact]
    public void Initialize_IsIdempotent_AndRecordsUserVersion()
    {
        using var fixture = new TestDatabase();

        // A second and third call must not throw, duplicate tables or bump the version.
        fixture.Database.Initialize();
        fixture.Database.Initialize();

        var version = fixture.Database.Read(conn =>
            Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "PRAGMA user_version;")));

        Assert.Equal(JockeyBoxDatabase.SchemaVersion, version);
    }

    [Theory]
    [InlineData("tracks")]
    [InlineData("playlists")]
    [InlineData("playlist_items")]
    [InlineData("karaoke_assets")]
    [InlineData("soundboard")]
    [InlineData("tracks_fts")]
    [InlineData("schema_info")]
    public void Initialize_CreatesEveryTable(string table)
    {
        using var fixture = new TestDatabase();

        var found = fixture.Database.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", table);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

        Assert.Equal(1, found);
    }

    [Fact]
    public void EveryOpen_AppliesWritePragmas()
    {
        using var fixture = new TestDatabase();

        fixture.Database.Read(conn =>
        {
            Assert.Equal("wal", JockeyBoxDatabase.Scalar(conn, "PRAGMA journal_mode;")?.ToString()?.ToLowerInvariant());
            Assert.Equal("1", JockeyBoxDatabase.Scalar(conn, "PRAGMA foreign_keys;")?.ToString());
            Assert.Equal("5000", JockeyBoxDatabase.Scalar(conn, "PRAGMA busy_timeout;")?.ToString());
            Assert.Equal("1", JockeyBoxDatabase.Scalar(conn, "PRAGMA synchronous;")?.ToString()); // NORMAL
            return 0;
        });
    }

    [Fact]
    public void WalSiblings_LiveNextToTheDatabaseFile()
    {
        using var fixture = new TestDatabase();
        fixture.Tracks.Upsert(new TrackRecord { Path = fixture.PathFor("a.mp3"), Title = "A" });

        var dbPath = fixture.Database.Path;
        Assert.True(File.Exists(dbPath));

        // WAL writes leave a -wal sibling while a connection is open, i.e. in the
        // temp/profile directory - never next to the exe.
        Assert.StartsWith(fixture.Directory, dbPath, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetDirectoryName(AppContext.BaseDirectory), Path.GetDirectoryName(dbPath));
    }

    [Fact]
    public void Fts_IsExternalContent_AndTriggersKeepItInSync()
    {
        using var fixture = new TestDatabase();

        var sql = fixture.Database.Read(conn =>
            JockeyBoxDatabase.Scalar(conn,
                "SELECT sql FROM sqlite_master WHERE name = 'tracks_fts';")?.ToString() ?? "");

        Assert.Contains("fts5", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content='tracks'", sql.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        var triggerCount = fixture.Database.Read(conn =>
            Convert.ToInt32(JockeyBoxDatabase.Scalar(conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'tracks' " +
                "AND (name LIKE '%ai' OR name LIKE '%ad' OR name LIKE '%au');")));

        Assert.Equal(3, triggerCount);
    }

    [Fact]
    public void PlaylistItems_HaveNoForeignKeyToTracks()
    {
        using var fixture = new TestDatabase();

        // An imported playlist row must be insertable before its file exists.
        fixture.Playlists.Save(new JockeyBox.Sources.UserPlaylist
        {
            Name = "Spotify",
            Kind = JockeyBox.Sources.PlaylistKind.Imported,
            Tracks = { new JockeyBox.Sources.SourceTrack { Id = "remote-1", Title = "Ghost" } },
        });

        var item = Assert.Single(fixture.Playlists.LoadAll());
        Assert.Equal("remote-1", Assert.Single(item.Tracks).Id);
    }

    [Fact]
    public void ForeignKeys_CascadePlaylistItems()
    {
        using var fixture = new TestDatabase();
        var playlist = new JockeyBox.Sources.UserPlaylist
        {
            Name = "Doomed",
            Tracks = { new JockeyBox.Sources.SourceTrack { Id = "x", Title = "X" } },
        };
        fixture.Playlists.Save(playlist);

        fixture.Playlists.Delete(playlist.Id);

        var remaining = fixture.Database.Read(conn =>
            Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM playlist_items;")));

        Assert.Equal(0, remaining);
    }
}
