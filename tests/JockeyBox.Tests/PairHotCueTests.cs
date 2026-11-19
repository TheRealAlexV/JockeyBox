using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JockeyBox.Data;
using JockeyBox.Playback;
using JockeyBox.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The pair-level hot-cue bank: the <c>pair_hot_cues</c> table, the v6 → v7
/// migration (on a copy of a real library, never the live file), the pair hotkey
/// family, and the rule that one pair cue moves both decks with one position.
/// </summary>
public sealed class PairHotCueTests
{
    // ---- storage ------------------------------------------------------------

    [Fact]
    public void PairCues_RoundTripAndTwoPairsDoNotCollide()
    {
        using var db = new TestDatabase();
        long render = AddTrack(db, "music/song (karaoke).wav");
        long original = AddTrack(db, "music/song.wav");
        long other = AddTrack(db, "music/other.wav");

        var repo = new PairHotCueRepository(db.Database);
        repo.Set(render, original, 1, 12_345);
        repo.Set(render, original, 3, 6_789);

        // Reload through a new instance: the rows, not the object, carry state.
        var reloaded = new PairHotCueRepository(db.Database).Get(render, original);
        Assert.Equal(new[] { 1, 3 }, reloaded.Select(r => r.Slot));
        Assert.Equal(12_345, reloaded[0].PositionMs);
        Assert.Equal(6_789, reloaded[1].PositionMs);

        // The reversed pair and a pair with a third track are different keys.
        Assert.Empty(repo.Get(original, render));
        Assert.Empty(repo.Get(render, other));

        // Re-setting a slot updates the row in place instead of adding one.
        repo.Set(render, original, 1, 99_000);
        var settled = repo.Get(render, original);
        Assert.Equal(2, settled.Count);
        Assert.Equal(99_000, settled.Single(r => r.Slot == 1).PositionMs);
        Assert.Equal(reloaded[0].Id, settled.Single(r => r.Slot == 1).Id);

        // Clearing one slot leaves the other alone.
        repo.Clear(render, original, 1);
        Assert.Equal(new[] { 3 }, repo.Get(render, original).Select(r => r.Slot));
    }

    // ---- migration ----------------------------------------------------------

    [Fact]
    public void Version6_database_gains_pair_hot_cues_and_keeps_its_hot_cues_rows()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, "library.db");

            // A 2.0 database as it was before this change: schema 6, two tracks,
            // hot_cues with one saved row, no pair_hot_cues table at all.
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                JockeyBoxDatabase.Execute(conn,
                    "CREATE TABLE tracks (id INTEGER PRIMARY KEY, path TEXT NOT NULL, " +
                    "path_key TEXT NOT NULL UNIQUE, title TEXT NOT NULL DEFAULT '');");
                JockeyBoxDatabase.Execute(conn,
                    "INSERT INTO tracks (id, path, path_key, title) VALUES " +
                    "(7, 'C:/Music/Song.wav', 'c:/music/song.wav', 'Song'), " +
                    "(8, 'C:/Music/Song (karaoke).wav', 'c:/music/song (karaoke).wav', 'Song');");
                JockeyBoxDatabase.Execute(conn,
                    "CREATE TABLE hot_cues (id INTEGER PRIMARY KEY, " +
                    "track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE, " +
                    "deck INTEGER NOT NULL, slot INTEGER NOT NULL, position_ms INTEGER NOT NULL, " +
                    "label TEXT, created_at TEXT NOT NULL, UNIQUE (track_id, deck, slot));");
                JockeyBoxDatabase.Execute(conn,
                    "INSERT INTO hot_cues (track_id, deck, slot, position_ms, created_at) " +
                    "VALUES (7, 0, 1, 1111, '2026-01-01T00:00:00.0000000Z'), " +
                    "(7, 1, 2, 2222, '2026-01-01T00:00:00.0000000Z');");
                JockeyBoxDatabase.Execute(conn, "PRAGMA user_version = 6;");
            }

            using var db = new JockeyBoxDatabase(path);
            db.Initialize();

            Assert.Equal(7, db.Read(conn =>
                Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "PRAGMA user_version;"))));

            // Additive only: the v6 rows are exactly where they were.
            var cues = db.Read(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT track_id, deck, slot, position_ms FROM hot_cues ORDER BY deck, slot;";
                var rows = new List<(long, int, int, long)>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)));
                return rows;
            });
            Assert.Equal(new[] { (7L, 0, 1, 1111L), (7L, 1, 2, 2222L) }, cues);

            // And the pair table is there, usable straight away.
            Assert.True(db.Read(conn =>
                JockeyBoxDatabase.Scalar(conn,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pair_hot_cues';")
                    is long tables && tables == 1));

            var pairRepo = new PairHotCueRepository(db);
            pairRepo.Set(7, 8, 1, 4_242);
            Assert.Equal(4_242, pairRepo.Get(7, 8).Single().PositionMs);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The same v6 → v7 step, on a <i>copy</i> of a real library file. Points at
    /// whatever <c>JOCKEYBOX_TEST_LIBRARYDB</c> names, so it never reads the live
    /// database and is skipped when the variable is unset.
    /// </summary>
    [Fact]
    public void Version6_real_library_copy_gains_pair_hot_cues_and_keeps_every_hot_cue()
    {
        string? source = Environment.GetEnvironmentVariable("JOCKEYBOX_TEST_LIBRARYDB");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;

        var directory = NewTempDirectory();
        try
        {
            string copy = Path.Combine(directory, "library.db");
            File.Copy(source, copy, overwrite: false);

            // Snapshot before the migration.
            long versionBefore;
            long trackCount;
            var cuesBefore = new List<string>();
            using (var conn = new SqliteConnection($"Data Source={copy}"))
            {
                conn.Open();
                versionBefore = Convert.ToInt64(JockeyBoxDatabase.Scalar(conn, "PRAGMA user_version;"));
                trackCount = Convert.ToInt64(JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM tracks;"));
                cuesBefore = ReadCueLines(conn);
            }

            // Only a v6 file exercises migration 7. A newer or older copy would
            // not prove what this test claims, so it says so instead of passing.
            Assert.Equal(6, versionBefore);

            using var db = new JockeyBoxDatabase(copy);
            db.Initialize();

            var cuesAfter = db.Read(ReadCueLines);
            Assert.Equal(cuesBefore, cuesAfter);
            Assert.Equal(trackCount, db.Read(conn =>
                Convert.ToInt64(JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM tracks;"))));
            Assert.Equal(7L, db.Read(conn =>
                Convert.ToInt64(JockeyBoxDatabase.Scalar(conn, "PRAGMA user_version;"))));
            Assert.True(db.Read(conn =>
                JockeyBoxDatabase.Scalar(conn,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pair_hot_cues';")
                    is long tables && tables == 1));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // ---- hotkeys ------------------------------------------------------------

    [Fact]
    public void PairHotkeyDefaults_are_ctrl_alt_and_collide_with_neither_deck_family()
    {
        var pair = PairCueHotkeys.Defaults();
        Assert.Equal(9, pair.Count); // eight saved slots and the quick cue
        Assert.Equal(
            new[] { "cue.p.1", "cue.p.2", "cue.p.3", "cue.p.4", "cue.p.5", "cue.p.6", "cue.p.7", "cue.p.8", "cue.p.q" },
            pair.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        Assert.All(pair, row => Assert.StartsWith("Ctrl+Alt+", row.Value));

        // Normalised chords, so casing and spacing cannot hide a collision.
        var pairChords = pair.Values.Select(GlobalHotkeyService.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deckChords = CueHotkeys.Defaults().Values
            .Select(GlobalHotkeyService.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(pairChords.Intersect(deckChords, StringComparer.OrdinalIgnoreCase));

        // The merged plan the binder is handed carries both families.
        var plan = JockeyBox.ViewModels.MainViewModel.MergedCuePlan();
        Assert.Equal(27, plan.Count); // 18 deck actions + 9 pair actions
        Assert.All(PairCueHotkeys.Defaults().Keys, id => Assert.True(plan.ContainsKey(id), id));
    }

    // ---- recall -------------------------------------------------------------

    [Fact]
    public void PairRecallAppliesTheSamePositionToBothChannels()
    {
        using var db = new TestDatabase();
        long render = AddTrack(db, "music/song (karaoke).wav");
        long original = AddTrack(db, "music/song.wav");

        var seeks = new List<double>();
        var bank = new PairCueBankViewModel(
            () => 12.5,
            seconds => seeks.Add(seconds), // stands in for "seek A and seek B", one position
            () => new PairHotCueRepository(db.Database));

        bank.SetPair(render, original);
        Assert.True(bank.TryCapture(1, out string captureError), captureError);
        Assert.Equal(12_500, bank.PositionOf(1));

        // A later position must be replaced by the cue on recall.
        var laterBank = new PairCueBankViewModel(() => 90.0, s => seeks.Add(s),
            () => new PairHotCueRepository(db.Database));
        laterBank.SetPair(render, original);
        Assert.True(laterBank.TryRecall(1, out string recallError), recallError);
        Assert.Equal(new[] { 12.5 }, seeks);

        // The saved row is the same single position, and the quick cue never is.
        var rows = new PairHotCueRepository(db.Database).Get(render, original);
        Assert.Single(rows);
        Assert.Equal(12_500, rows[0].PositionMs);

        laterBank.TryCapture(0, out _);
        Assert.Equal(90_000, laterBank.PositionOf(0));
        laterBank.TryRecall(0, out _);
        Assert.Equal(90.0, seeks[^1]);

        // The quick cue is memory only: capturing it writes no row.
        Assert.Single(new PairHotCueRepository(db.Database).Get(render, original));
    }

    // ---- helpers ------------------------------------------------------------

    private static long AddTrack(TestDatabase db, string relative)
    {
        string path = db.PathFor(relative);
        long id = 0;
        db.Database.WithWriter(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO tracks (path, path_key, title) VALUES (@path, @key, 'Song'); " +
                "SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@key", path.ToLowerInvariant());
            id = Convert.ToInt64(cmd.ExecuteScalar());
        });
        return id;
    }

    private static List<string> ReadCueLines(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT track_id || '|' || deck || '|' || slot || '|' || position_ms || '|' || created_at " +
            "FROM hot_cues ORDER BY track_id, deck, slot;";
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* temp cleanup must never fail a test run */ }
    }
}
