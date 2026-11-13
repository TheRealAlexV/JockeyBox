using System;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Beat;

/// <summary>
/// Push the grids already on disk into the two stores that answer "what is this
/// track's tempo" - <c>library.json</c> and the <c>tracks</c> columns - without
/// running the analyser again.
///
/// <para><b>Why a pass and not a re-analysis.</b> A grid is a Python run of
/// seconds per track; a library of 773 already carries 772 measured grids. Paying
/// for them twice to move numbers between three files is the wrong trade, so this
/// copies what the document already holds.</para>
///
/// <para><b>One-shot.</b> The database records that it ran
/// (<see cref="MarkerKey"/>), so the 774 updates are not repeated on every start.
/// It is idempotent anyway: the cache only fills a blank (a tag tempo wins) and
/// the columns receive the same values, so a lost marker costs one pass, not a
/// wrong number. It cannot fight the scanner: it runs at the end of a scan, after
/// the prune, and only adds <c>Bpm</c> to entries the scan just cached - an entry
/// the prune removed stays removed.</para>
///
/// <para><b>No database is fine.</b> With the DB off (<see cref="LibraryDatabase.JsonOnly"/>)
/// the JSON half still runs, and no marker is written; the next start repeats the
/// JSON pass cheaply because every entry already carries its BPM.</para>
/// </summary>
public static class BpmBackfill
{
    /// <summary>Info key in the library database; "1" means the columns were filled.</summary>
    public const string MarkerKey = "beat_grid_bpm_backfill";

    /// <summary>
    /// Fill both stores from <paramref name="grids"/>. Returns how many cache
    /// entries and track rows actually changed.
    /// </summary>
    public static (int Json, int Db) Run(BeatGridStore grids, TagCache cache, JockeyBoxDatabase? db)
    {
        if (grids.Count == 0) return (0, 0);
        if (db != null && db.GetInfo(MarkerKey) == "1") return (0, 0);

        var tracks = db == null ? null : new TrackRepository(db);
        var json = 0;
        var rows = 0;

        foreach (var (path, grid) in grids.Snapshot())
        {
            if (grid.Bpm <= 0) continue;

            // Tag first, exactly as the row shows it: the estimate fills a blank in
            // the cache and in the columns, never an authored TBPM.
            var tagged = cache.BpmOf(path);
            if (tagged <= 0 && cache.SetBpm(path, grid.Bpm)) json++;

            if (tracks != null && tracks.UpdateBeatGrid(
                    path, TrackBpmResolver.Resolve(tagged, grid), grid.BpmP10, grid.BpmP90,
                    grid.Confidence, (long)Math.Round(grid.FirstBeatSeconds * 1000.0)))
                rows++;
        }

        // One save for the whole pass, not one per grid.
        if (json > 0) cache.Save();

        // Only after the columns are written: a crash mid-pass leaves no marker,
        // so the next start simply runs it again.
        db?.SetInfo(MarkerKey, "1");

        Log.Info($"bpm backfill: {json} library.json entr(ies), {rows} track row(s) from {grids.Count} grid(s)");
        return (json, rows);
    }
}
