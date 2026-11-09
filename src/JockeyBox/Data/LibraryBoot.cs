using System.Globalization;

namespace JockeyBox.Data;

/// <summary>What <see cref="LibraryBoot"/> managed to do, for the one startup log line.</summary>
public sealed record LibraryBootReport
{
    public string Path { get; init; } = "";
    public int Schema { get; init; }
    public int Tracks { get; init; }
    public int Playlists { get; init; }
    public int PlaylistItems { get; init; }
    public bool ImportRan { get; init; }
    public bool ReimportedFromDisk { get; init; }

    /// <summary>Rows in the published <see cref="LibrarySnapshot"/> (missing files excluded).</summary>
    public int SnapshotTracks { get; init; }

    /// <summary>Milliseconds the whole boot (open + import + snapshot) cost.</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>
/// Brings <c>library.db</c> up before any view model is built: opens the shared
/// database (<see cref="LibraryDatabase.Current"/>), replays the legacy JSON
/// import once, publishes the library snapshot and reimports when
/// <c>playlists.json</c> or <c>library.json</c> changed on disk behind the app's
/// back.
///
/// <para>Cheap on a warm start: the import is guarded by
/// <see cref="LegacyImporter.GuardKey"/> and the disk check is two
/// <c>stat</c> calls plus two <c>schema_info</c> reads.</para>
///
/// <para>Never throws. A database that cannot be opened or imported returns null
/// (or a report with no counts) and the app keeps reading the JSON files, which
/// stay authoritative on disk.</para>
/// </summary>
public static class LibraryBoot
{
    /// <summary>
    /// Support switch: <c>JOCKEYBOX_JSON_ONLY=1</c> in the environment, or a file
    /// named <c>library.db.off</c> in the data directory, keeps the app on the
    /// JSON stores (no database opened). See <see cref="LibraryDatabase.JsonOnlyVariable"/>.
    /// </summary>
    public const string JsonOnlyVariable = LibraryDatabase.JsonOnlyVariable;

    /// <summary>Marker file that disables the database (see <see cref="JsonOnlyVariable"/>).</summary>
    public static string JsonOnlyMarker => LibraryDatabase.JsonOnlyMarker;

    /// <summary>The report from the last <see cref="Boot"/>, or null when the DB is off.</summary>
    public static LibraryBootReport? Last { get; private set; }

    /// <summary>True when the database is the live read path for this process.</summary>
    public static bool IsActive => Last is not null;

    /// <summary>
    /// Open + initialize the default database, import the legacy files, publish
    /// the snapshot, and reimport when either JSON file no longer matches what
    /// the database was built from.
    /// </summary>
    public static LibraryBootReport? Boot()
    {
        if (LibraryDatabase.DisabledReason is { } reason)
        {
            Tools.Log.Info($"library db: disabled by {reason}; reading json stores");
            Last = null;
            return null;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var db = LibraryDatabase.Current;
        if (db is null)
        {
            Tools.Log.Warn("library db: unavailable (open failed); reading json stores");
            Last = null;
            return null;
        }

        try
        {
            var importer = new LegacyImporter(db);
            var import = importer.Run();
            var ran = import.Ran;
            var reimported = false;

            if (import.Error is { Length: > 0 } error)
            {
                // The JSON files are untouched, so this is not fatal: the store
                // read below falls back to playlists.json on its own.
                Tools.Log.Warn($"library db: legacy import failed ({error}); json stays authoritative");
            }

            // External edit: a JSON file changed after the database mirrored it.
            // JSON wins, exactly as FileStamp/Revision already did for the JSON
            // path - playlists.json for the graph, library.json for track tags.
            var playlistsChanged = ChangedOnDisk(db, PlaylistRepository.JsonStampKey,
                PlaylistFileStamp.Read(db.Directory));
            var libraryChanged = ChangedOnDisk(db, LegacyImporter.LibraryJsonStampKey,
                PlaylistFileStamp.Of(LegacyImporter.LibraryJsonPath(db.Directory)));

            if (playlistsChanged || libraryChanged)
            {
                var what = playlistsChanged && libraryChanged ? "playlists.json and library.json"
                    : playlistsChanged ? "playlists.json" : "library.json";
                Tools.Log.Info($"library db: {what} changed on disk; reimporting");
                var forced = importer.Run(force: true);
                ran |= forced.Ran;
                reimported = forced.Ran && forced.Error is null;
            }

            var snapshot = PublishSnapshot(db);
            started.Stop();

            var report = new LibraryBootReport
            {
                Path = db.Path,
                Schema = ReadSchema(db),
                Tracks = Count(db, "tracks"),
                Playlists = Count(db, "playlists"),
                PlaylistItems = Count(db, "playlist_items"),
                ImportRan = ran,
                ReimportedFromDisk = reimported,
                SnapshotTracks = snapshot?.Count ?? 0,
                ElapsedMs = started.Elapsed.TotalMilliseconds,
            };

            Tools.Log.Info(
                $"library db: {report.Path} schema={report.Schema} tracks={report.Tracks} " +
                $"playlists={report.Playlists} import={(ran ? "ran" : "skipped")}" +
                (reimported ? " (reimported after external edit)" : "") +
                $" snapshot={report.SnapshotTracks} boot={report.ElapsedMs:F0}ms");

            Last = report;
            return report;
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"library db: startup failed ({ex.Message}); reading json stores");
            Last = null;
            return null;
        }
    }

    /// <summary>
    /// Re-mirror the json files into the database and remember their stamps.
    /// The host calls this after it rewrote one of them itself (the tag cache is
    /// saved on shutdown), so the next start does not mistake our own write for
    /// an external edit - and the tracks table still gets the tags.
    /// Returns true when the mirror ran cleanly.
    /// </summary>
    public static bool SyncFromDisk(string why)
    {
        var db = LibraryDatabase.Current;
        if (db is null) return false;

        try
        {
            var report = new LegacyImporter(db).Run(force: true);
            if (report.Error is { Length: > 0 } error)
            {
                Tools.Log.Warn($"library db: re-mirror after {why} failed ({error})");
                return false;
            }

            PublishSnapshot(db);
            Tools.Log.Info(
                $"library db: re-mirrored after {why} (tracks={report.Tracks} " +
                $"playlists={report.Playlists} skipped={report.Skipped})");
            return true;
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"library db: re-mirror after {why} failed ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Build the in-memory <see cref="LibrarySnapshot"/> from the tracks table and
    /// publish it on <see cref="LibraryDatabase.Snapshot"/>, so playlist membership
    /// and remote-row resolution can be answered before (or without) a disk scan.
    /// Returns null when the database holds no tracks.
    /// </summary>
    public static LibrarySnapshot? PublishSnapshot(JockeyBoxDatabase db)
    {
        try
        {
            var snapshot = LibraryDatabase.Snapshot.BuildFrom(new TrackRepository(db));
            return snapshot.IsEmpty ? null : snapshot;
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"library db: snapshot build failed ({ex.Message}); membership falls back to the scan");
            return null;
        }
    }

    /// <summary>
    /// True when a JSON file that exists on disk is not the one the database was
    /// built from. A missing file (stamp 0) is never an edit.
    /// </summary>
    private static bool ChangedOnDisk(JockeyBoxDatabase db, string key, long diskStamp)
    {
        if (diskStamp == 0) return false;

        var recorded = db.GetInfo(key);
        return recorded is not { Length: > 0 } ||
               !long.TryParse(recorded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               value != diskStamp;
    }

    private static int ReadSchema(JockeyBoxDatabase db) => db.Read(conn =>
        Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "PRAGMA user_version;")));

    /// <summary>Row count of one table; 0 when the table is absent.</summary>
    private static int Count(JockeyBoxDatabase db, string table)
    {
        try
        {
            return db.Read(conn => Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, $"SELECT COUNT(*) FROM {table};")));
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
