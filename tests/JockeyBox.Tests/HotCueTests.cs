using System.IO;
using JockeyBox.Data;
using Microsoft.Data.Sqlite;

namespace JockeyBox.Tests;

/// <summary>
/// The <c>hot_cues</c> table: the v5 → v6 migration, the track cascade, and the
/// rules the repository enforces before a row is written.
/// </summary>
public sealed class HotCueTests
{
    [Fact]
    public void Version5_database_gains_hot_cues_and_keeps_its_rows()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "library.db");

            // A 2.0 database as it was before this change: schema 5, one track,
            // no hot_cues table at all.
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                JockeyBoxDatabase.Execute(conn,
                    "CREATE TABLE tracks (id INTEGER PRIMARY KEY, path TEXT NOT NULL, " +
                    "path_key TEXT NOT NULL UNIQUE, title TEXT NOT NULL DEFAULT '');");
                JockeyBoxDatabase.Execute(conn,
                    "INSERT INTO tracks (id, path, path_key, title) " +
                    "VALUES (7, 'C:/Music/Old.mp3', 'c:/music/old.mp3', 'Old');");
                JockeyBoxDatabase.Execute(conn, "PRAGMA user_version = 5;");
            }

            using var db = new JockeyBoxDatabase(path);
            db.Initialize();

            Assert.Equal(JockeyBoxDatabase.SchemaVersion, db.Read(conn =>
                Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "PRAGMA user_version;"))));

            Assert.Equal(0, db.Read(conn => Convert.ToInt32(
                JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM hot_cues;"))));

            Assert.Equal(1, db.Read(conn => Convert.ToInt32(
                JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM tracks WHERE id = 7;"))));

            // The table is not just present but writable against the old row.
            var cues = new HotCueRepository(db);
            cues.Set(7, 0, 1, 12_345);
            Assert.Equal(12_345, Assert.Single(cues.Get(7, 0)).PositionMs);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Deleting_a_track_removes_its_cues()
    {
        using var fixture = new TestDatabase();
        var trackId = AddTrack(fixture.Database, @"C:\Music\Gone.mp3");

        var cues = new HotCueRepository(fixture.Database);
        cues.Set(trackId, 0, 1, 1_000);
        cues.Set(trackId, 1, 8, 60_000);
        Assert.Equal(2, cues.Get(trackId, 0).Count + cues.Get(trackId, 1).Count);

        fixture.Database.WithWriter(conn =>
            JockeyBoxDatabase.Execute(conn, $"DELETE FROM tracks WHERE id = {trackId};"));

        Assert.Empty(cues.Get(trackId, 0));
        Assert.Empty(cues.Get(trackId, 1));
        Assert.Equal(0, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM hot_cues;"))));
    }

    [Fact]
    public void Setting_the_same_slot_twice_updates_it()
    {
        using var fixture = new TestDatabase();
        var trackId = AddTrack(fixture.Database, @"C:\Music\Song.mp3");
        var cues = new HotCueRepository(fixture.Database);

        cues.Set(trackId, 0, 3, 4_000, "intro");
        cues.Set(trackId, 0, 3, 7_500, "chorus");

        var saved = Assert.Single(cues.Get(trackId, 0));
        Assert.Equal(7_500, saved.PositionMs);
        Assert.Equal("chorus", saved.Label);

        Assert.Equal(1, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM hot_cues;"))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void Out_of_range_slot_is_refused(int slot)
    {
        using var fixture = new TestDatabase();
        var trackId = AddTrack(fixture.Database, @"C:\Music\Song.mp3");
        var cues = new HotCueRepository(fixture.Database);

        Assert.Throws<ArgumentOutOfRangeException>(() => cues.Set(trackId, 0, slot, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => cues.Clear(trackId, 0, slot));
        Assert.Empty(cues.Get(trackId, 0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void Out_of_range_deck_is_refused(int deck)
    {
        using var fixture = new TestDatabase();
        var trackId = AddTrack(fixture.Database, @"C:\Music\Song.mp3");
        var cues = new HotCueRepository(fixture.Database);

        Assert.Throws<ArgumentOutOfRangeException>(() => cues.Set(trackId, deck, 1, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => cues.Get(trackId, deck));
        Assert.Throws<ArgumentOutOfRangeException>(() => cues.Clear(trackId, deck, 1));

        Assert.Equal(0, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM hot_cues;"))));
    }

    [Fact]
    public void Clear_removes_one_slot_and_ClearAll_removes_the_track()
    {
        using var fixture = new TestDatabase();
        var trackId = AddTrack(fixture.Database, @"C:\Music\Song.mp3");
        var otherId = AddTrack(fixture.Database, @"C:\Music\Other.mp3");
        var cues = new HotCueRepository(fixture.Database);

        cues.Set(trackId, 0, 1, 1_000);
        cues.Set(trackId, 0, 2, 2_000);
        cues.Set(trackId, 1, 1, 3_000);
        cues.Set(otherId, 0, 1, 4_000);

        cues.Clear(trackId, 0, 1);
        Assert.Equal(new[] { 2_000L }, cues.Get(trackId, 0).Select(c => c.PositionMs));

        cues.ClearAll(trackId);
        Assert.Empty(cues.Get(trackId, 0));
        Assert.Empty(cues.Get(trackId, 1));

        // Another track's cues are untouched by either call.
        Assert.Equal(4_000L, Assert.Single(cues.Get(otherId, 0)).PositionMs);
    }

    [Fact]
    public void Get_orders_by_slot()
    {
        using var fixture = new TestDatabase();
        var trackId = AddTrack(fixture.Database, @"C:\Music\Song.mp3");
        var cues = new HotCueRepository(fixture.Database);

        cues.Set(trackId, 0, 5, 50);
        cues.Set(trackId, 0, 1, 10);
        cues.Set(trackId, 0, 3, 30);

        Assert.Equal(new[] { 1, 3, 5 }, cues.Get(trackId, 0).Select(c => c.Slot));
    }

    private static long AddTrack(JockeyBoxDatabase db, string path)
    {
        var key = path.ToLowerInvariant().Replace('\\', '/');
        return db.WithWriter(conn =>
        {
            using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO tracks (path, path_key, title) VALUES (@path, @key, 'Song');";
            insert.Parameters.AddWithValue("@path", path);
            insert.Parameters.AddWithValue("@key", key);
            insert.ExecuteNonQuery();

            using var last = conn.CreateCommand();
            last.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(last.ExecuteScalar());
        });
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* temp cleanup must never fail a test */ }
    }
}
