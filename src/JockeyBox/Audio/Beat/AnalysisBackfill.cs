using System;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Beat;

/// <summary>
/// Push the grids already on disk into the stores that answer "what is this
/// track's tempo and key" - <c>library.json</c> and, for tempo, the <c>tracks</c>
/// columns - without running the analyser again.
///
/// <para><b>Why a pass and not a re-analysis.</b> A grid is a Python run of
/// seconds per track; a library of 773 already carries 772 measured grids. Paying
/// for them twice to move numbers between files is the wrong trade, so this copies
/// what the document already holds.</para>
///
/// <para><b>Key has no column.</b> The v1 <c>tracks</c> shape carries tempo only
/// (<c>bpm</c>, <c>bpm_p10</c>, <c>bpm_p90</c>, <c>bpm_confidence</c>,
/// <c>first_beat_ms</c>); there is no key column, and this lane does not invent a
/// migration for one. A key's durable home is <c>library.json</c>, the same place
/// an authored <c>TKEY</c> tag is cached, so the key half is a JSON-only fill.</para>
///
/// <para><b>One-shot, per half.</b> Each pass records its own marker
/// (<see cref="BpmMarkerKey"/>, <see cref="KeyMarkerKey"/>) so neither runs again
/// on every start. They are separate because the tempo pass shipped first: a
/// profile that has already run it must still get the key pass. Both are
/// idempotent anyway - the cache only fills a blank, so a tag always wins and a
/// lost marker costs one cheap pass, not a wrong number. This cannot fight the
/// scanner: it runs at the end of a scan, after the prune, and only adds values to
/// entries the scan just cached - an entry the prune removed stays removed.</para>
///
/// <para><b>No database is fine.</b> With the DB off
/// (<see cref="LibraryDatabase.JsonOnly"/>) both JSON halves still run and no
/// marker is written; the next start repeats them cheaply because every entry
/// already carries its values.</para>
/// </summary>
public static class AnalysisBackfill
{
    /// <summary>Info key; "1" means the tempo columns were filled.</summary>
    public const string BpmMarkerKey = "beat_grid_bpm_backfill";

    /// <summary>Info key; "1" means <c>library.json</c> was filled with analysed keys.</summary>
    public const string KeyMarkerKey = "beat_grid_key_backfill";

    /// <summary>
    /// Fill both stores from <paramref name="grids"/>. Returns how many cache
    /// entries gained a tempo, how many gained a key, and how many track rows
    /// were updated.
    /// </summary>
    public static (int BpmJson, int KeyJson, int Db) Run(
        BeatGridStore grids, TagCache cache, JockeyBoxDatabase? db)
    {
        if (grids.Count == 0) return (0, 0, 0);

        var fillBpm = db == null || db.GetInfo(BpmMarkerKey) != "1";
        var fillKey = db == null || db.GetInfo(KeyMarkerKey) != "1";
        if (!fillBpm && !fillKey) return (0, 0, 0);

        var tracks = db == null || !fillBpm ? null : new TrackRepository(db);
        var bpmJson = 0;
        var keyJson = 0;
        var rows = 0;

        foreach (var (path, grid) in grids.Snapshot())
        {
            if (fillBpm && grid.Bpm > 0)
            {
                // Tag first, exactly as the row shows it: the estimate fills a blank
                // in the cache and in the columns, never an authored TBPM.
                var tagged = cache.BpmOf(path);
                if (tagged <= 0 && cache.SetBpm(path, grid.Bpm)) bpmJson++;

                if (tracks != null && tracks.UpdateBeatGrid(
                        path, TrackBpmResolver.Resolve(tagged, grid), grid.BpmP10, grid.BpmP90,
                        grid.Confidence, (long)Math.Round(grid.FirstBeatSeconds * 1000.0)))
                    rows++;
            }

            // Same tag-first rule through SetKey, and the same refusal to create an
            // entry: a grid for a file the library lost must not resurrect it.
            if (fillKey && cache.SetKey(path, grid.Key)) keyJson++;
        }

        // One save for the whole pass, not one per grid.
        if (bpmJson > 0 || keyJson > 0) cache.Save();

        // Only after the values are written: a crash mid-pass leaves no marker,
        // so the next start simply runs it again.
        if (fillBpm) db?.SetInfo(BpmMarkerKey, "1");
        if (fillKey) db?.SetInfo(KeyMarkerKey, "1");

        Log.Info($"analysis backfill: {bpmJson} tempo + {keyJson} key(s) into library.json, " +
                 $"{rows} track row(s) from {grids.Count} grid(s)");
        return (bpmJson, keyJson, rows);
    }
}
