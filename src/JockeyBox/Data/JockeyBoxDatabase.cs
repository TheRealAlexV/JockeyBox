using System.IO;
using Microsoft.Data.Sqlite;
using JockeyBox.Tools;

namespace JockeyBox.Data;

/// <summary>
/// Owns the SQLite file that replaces the 1.5.0 JSON stores
/// (<c>library.json</c>, <c>playlists.json</c>, <c>track-metadata.json</c>).
///
/// <para><b>Where it lives</b>: <c>%APPDATA%\JockeyBox\library.db</c>. WAL mode
/// writes sibling <c>-wal</c>/<c>-shm</c> files, so the database must never sit
/// next to the exe (Program Files is read-only for a normal user).</para>
///
/// <para><b>Connections</b>: one long-lived writer behind a
/// <see cref="SemaphoreSlim"/>, and a fresh (pooled) connection per read. A
/// <see cref="SqliteConnection"/> is never touched by two threads. There is no
/// async ADO.NET here on purpose - Microsoft.Data.Sqlite only fakes it - so file
/// work belongs on <c>Task.Run</c> and the public API of the repositories stays
/// synchronous like the JSON stores it replaces.</para>
///
/// <para><b>Pragmas</b> are applied on every open, not just the first, because
/// the pool hands back connections whose per-connection settings (busy timeout,
/// cache size) are not part of the file. WAL itself is persistent in the file,
/// the rest is not.</para>
///
/// <para><b>Migrations</b> are <c>PRAGMA user_version</c> based and every step is
/// idempotent, so <see cref="Initialize"/> is safe to call on every start.</para>
/// </summary>
public sealed class JockeyBoxDatabase : IDisposable
{
    /// <summary>Latest schema version this build understands.</summary>
    public const int SchemaVersion = 6;

    private const string AppFolderName = "JockeyBox";
    private const string DatabaseFileName = "library.db";

    /// <summary>Environment hook for tests/CI; production always uses %APPDATA%.</summary>
    public const string DirectoryOverrideVariable = "JOCKEYBOX_DATA_DIR";

    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly object _initGate = new();

    private SqliteConnection? _writer;
    private bool _initialized;
    private bool _disposed;

    public JockeyBoxDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        Path = System.IO.Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>The directory the app keeps its state in (override wins when set).</summary>
    public static string DefaultDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            return AppPaths.Home;
        }
    }

    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, DatabaseFileName);

    /// <summary>Full path of the database file.</summary>
    public string Path { get; }

    /// <summary>Connection string used for every reader and the writer.</summary>
    public string ConnectionString { get; }

    /// <summary>Directory holding the db (and the JSON files the importer reads).</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? ".";

    /// <summary>
    /// Open the production database, or null when it cannot be opened (read-only
    /// profile, corrupt file, missing native SQLite). Callers fall back to JSON.
    /// </summary>
    public static JockeyBoxDatabase? TryOpenDefault()
    {
        try
        {
            var db = new JockeyBoxDatabase(DefaultPath);
            db.Initialize();
            return db;
        }
        catch (Exception ex)
        {
            Log.Warn($"library.db unavailable at {DefaultPath}; staying on JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Create/migrate the schema. Idempotent; safe on every start and from
    /// several processes (the DDL is guarded and runs inside a transaction).
    /// </summary>
    public void Initialize()
    {
        lock (_initGate)
        {
            if (_initialized) return;

            System.IO.Directory.CreateDirectory(Directory);

            WithWriter(conn =>
            {
                Execute(conn, "CREATE TABLE IF NOT EXISTS schema_info (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;");

                var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));
                if (version > SchemaVersion)
                {
                    Log.Warn($"library.db is schema {version}; this build understands {SchemaVersion} and will read known columns only");
                    return 0;
                }

                for (var next = version + 1; next <= SchemaVersion; next++)
                {
                    var sql = MigrationSql(next);
                    Execute(conn, "BEGIN IMMEDIATE;");
                    try
                    {
                        Execute(conn, sql);
                        Execute(conn, $"PRAGMA user_version = {next};");
                        Execute(conn, "COMMIT;");
                        Log.Info($"library.db migrated to schema {next}");
                    }
                    catch
                    {
                        TryExecute(conn, "ROLLBACK;");
                        throw;
                    }
                }

                // Repair step: a column added after v1 shipped must not require a
                // version bump on a file that already ran v1. Idempotent.
                EnsureColumn(conn, "playlists", "content_hash", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(conn, "playlist_items", "source", "INTEGER NOT NULL DEFAULT 0");

                return 0;
            });

            _initialized = true;
        }
    }

    /// <summary>Open a pooled connection for one read operation; the caller disposes it.</summary>
    public SqliteConnection OpenReader()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    /// <summary>Run a read on its own connection.</summary>
    public T Read<T>(Func<SqliteConnection, T> body)
    {
        using var conn = OpenReader();
        return body(conn);
    }

    /// <summary>
    /// Run <paramref name="body"/> on the single writer connection. The writer is
    /// serialised process-wide (per database instance) by this gate; nested calls
    /// would deadlock, so repositories never call <see cref="WithWriter"/> from
    /// inside another write.
    /// </summary>
    public T WithWriter<T>(Func<SqliteConnection, T> body)
    {
        var conn = AcquireWriter();
        try
        {
            return body(conn);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public void WithWriter(Action<SqliteConnection> body) =>
        WithWriter(conn =>
        {
            body(conn);
            return 0;
        });

    /// <summary>Writer + <c>BEGIN IMMEDIATE</c>; commits on success, rolls back on throw.</summary>
    public T WriteTransaction<T>(Func<SqliteConnection, T> body) =>
        WithWriter(conn =>
        {
            Execute(conn, "BEGIN IMMEDIATE;");
            try
            {
                var result = body(conn);
                Execute(conn, "COMMIT;");
                return result;
            }
            catch
            {
                TryExecute(conn, "ROLLBACK;");
                throw;
            }
        });

    public void WriteTransaction(Action<SqliteConnection> body) =>
        WriteTransaction(conn =>
        {
            body(conn);
            return 0;
        });

    /// <summary>Value of a <c>schema_info</c> row, or null.</summary>
    public string? GetInfo(string key) =>
        Read(conn => GetInfo(conn, key));

    public string? GetInfo(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetInfo(string key, string value) =>
        WithWriter(conn => SetInfo(conn, key, value));

    public void SetInfo(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO schema_info(key, value) VALUES(@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a <c>schema_info</c> row (idempotent).</summary>
    public void RemoveInfo(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM schema_info WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Checkpoint the WAL; useful before backing the file up.</summary>
    public void Checkpoint()
    {
        try
        {
            WithWriter(conn => Execute(conn, "PRAGMA wal_checkpoint(TRUNCATE);"));
        }
        catch (Exception ex)
        {
            Log.Warn($"library.db checkpoint failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait for an in-flight write, then close the writer so the WAL is
        // checkpointed and the file is not left locked after a clean exit.
        _writerGate.Wait();
        try
        {
            if (_writer is { } writer)
            {
                try { Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* best effort */ }
                writer.Dispose();
            }
            _writer = null;
        }
        finally
        {
            _writerGate.Release();
            _writerGate.Dispose();
        }
    }

    // ---- internals ---------------------------------------------------------

    private SqliteConnection AcquireWriter()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JockeyBoxDatabase));

        _writerGate.Wait();
        try
        {
            if (_writer is { State: System.Data.ConnectionState.Open }) return _writer;

            _writer?.Dispose();
            _writer = new SqliteConnection(ConnectionString);
            _writer.Open();
            ApplyPragmas(_writer);
            return _writer;
        }
        catch
        {
            _writerGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Per-connection settings. Must run on every open: only <c>journal_mode</c>
    /// persists in the file, the rest are connection state that the pool does not
    /// restore for us.
    /// </summary>
    private static void ApplyPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA synchronous = NORMAL;" +
            "PRAGMA busy_timeout = 5000;" +
            "PRAGMA cache_size = -65536;" +
            "PRAGMA mmap_size = 268435456;" +
            "PRAGMA temp_store = MEMORY;" +
            "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
    }

    public static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static void TryExecute(SqliteConnection conn, string sql)
    {
        try { Execute(conn, sql); } catch { /* best effort */ }
    }

    /// <summary>
    /// Repair step: a column added after v1 shipped must not require a version
    /// bump on a file that already ran v1. Idempotent.
    /// </summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var has = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    has = true;
                    break;
                }
            }
        }

        if (has) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        try { alter.ExecuteNonQuery(); }
        catch { /* best effort: another process may have added it */ }
    }

    /// <summary>
    /// One migration step per schema version. Everything is guarded with
    /// <c>IF NOT EXISTS</c> so a half-applied file can be repaired by rerunning.
    /// </summary>
    private static string MigrationSql(int version) => version switch
    {
        1 => Migration1Core,
        2 => Migration2Search,
        3 => Migration3Lookups,
        4 => Migration4PlaylistNames,
        5 => Migration5PlaylistOrder,
        6 => Migration6HotCues,
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, "No such migration"),
    };

    /// <summary>
    /// v1: the durable shape. <c>tracks</c> is keyed by <c>path_key</c> (the
    /// normalised absolute path) so a case/separator variant of the same file
    /// updates one row instead of adding a second; <c>path</c> additionally
    /// carries a NOCASE unique index so a raw SQL insert cannot duplicate a file.
    ///
    /// <para><c>playlist_items.source_key</c> holds one of two things, decided by
    /// <c>source</c>: for an <b>internal member</b> it is the normalised local key
    /// of a downloaded file (<see cref="Sources.SourceTrack.NormalizeLocalKey"/>,
    /// the same identity as <c>TrackEntry.RelativeKey</c>), and for an <b>imported
    /// member</b> (Spotify/YouTube) it is the remote id, which has no
    /// <c>tracks</c> row until it is downloaded. Hence no foreign key to
    /// <c>tracks</c>: the row must be storable before the audio exists. The
    /// display columns on the item are a snapshot of what the remote source said,
    /// so a playlist can render without a network call or a <c>tracks</c> row.</para>
    /// </summary>
    private const string Migration1Core = """
        CREATE TABLE IF NOT EXISTS tracks (
            id                  INTEGER PRIMARY KEY,
            path                TEXT    NOT NULL,
            path_key            TEXT    NOT NULL UNIQUE,
            title               TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
            artist              TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
            album               TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
            album_artist        TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
            genre               TEXT    NOT NULL DEFAULT '' COLLATE NOCASE,
            year                INTEGER NOT NULL DEFAULT 0,
            track_no            INTEGER NOT NULL DEFAULT 0,
            disc_no             INTEGER NOT NULL DEFAULT 0,
            duration_ms         INTEGER NOT NULL DEFAULT 0,
            bpm                 REAL    NOT NULL DEFAULT 0,
            bpm_p10             REAL    NOT NULL DEFAULT 0,
            bpm_p90             REAL    NOT NULL DEFAULT 0,
            bpm_confidence      REAL    NOT NULL DEFAULT 0,
            first_beat_ms       INTEGER NOT NULL DEFAULT 0,
            half_double_suspect INTEGER NOT NULL DEFAULT 0,
            size_bytes          INTEGER NOT NULL DEFAULT 0,
            mtime_utc           INTEGER NOT NULL DEFAULT 0,
            added_utc           INTEGER NOT NULL DEFAULT 0,
            last_seen_utc       INTEGER NOT NULL DEFAULT 0,
            is_missing          INTEGER NOT NULL DEFAULT 0,
            sort_title          TEXT    NOT NULL DEFAULT '',
            sort_artist         TEXT    NOT NULL DEFAULT ''
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_tracks_path ON tracks(path COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_tracks_missing ON tracks(is_missing, last_seen_utc);

        CREATE TABLE IF NOT EXISTS playlists (
            id              TEXT    PRIMARY KEY NOT NULL,
            name            TEXT    NOT NULL COLLATE NOCASE,
            kind            INTEGER NOT NULL DEFAULT 0,
            source          INTEGER NOT NULL DEFAULT 0,
            source_id       TEXT,
            sync_enabled    INTEGER NOT NULL DEFAULT 0,
            last_synced_utc INTEGER NOT NULL DEFAULT 0,
            created_utc     INTEGER NOT NULL DEFAULT 0,
            updated_utc     INTEGER NOT NULL DEFAULT 0,
            content_hash    TEXT    NOT NULL DEFAULT ''
        );
        -- Names are labels, not identity (v4): two imports may share a title.
        CREATE INDEX IF NOT EXISTS ix_playlists_name ON playlists(name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_playlists_source ON playlists(source, source_id);

        CREATE TABLE IF NOT EXISTS playlist_items (
            playlist_id TEXT    NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            position    INTEGER NOT NULL,
            source_key  TEXT    NOT NULL, -- internal: normalised local key | imported: remote id
            row_id      TEXT    NOT NULL DEFAULT '',
            title       TEXT    NOT NULL DEFAULT '',
            artist      TEXT    NOT NULL DEFAULT '',
            album       TEXT    NOT NULL DEFAULT '',
            isrc        TEXT    NOT NULL DEFAULT '',
            cover_url   TEXT    NOT NULL DEFAULT '',
            duration_ms INTEGER NOT NULL DEFAULT 0,
            video_id    TEXT,
            library_path TEXT,
            source      INTEGER NOT NULL DEFAULT 0,
            added_utc   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (playlist_id, position)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_playlist_items_key ON playlist_items(playlist_id, source_key);

        CREATE TABLE IF NOT EXISTS karaoke_assets (
            track_id  INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            kind      TEXT    NOT NULL,
            path      TEXT    NOT NULL,
            offset_ms INTEGER NOT NULL DEFAULT 0,
            added_utc INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (track_id, kind)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_karaoke_assets_path ON karaoke_assets(path COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS track_lyrics (
            key              TEXT    PRIMARY KEY NOT NULL,
            track_id         INTEGER REFERENCES tracks(id) ON DELETE SET NULL,
            title            TEXT    NOT NULL DEFAULT '',
            artist           TEXT    NOT NULL DEFAULT '',
            lyrics_offset_ms REAL,
            lyrics_source_id TEXT    NOT NULL DEFAULT '',
            follow_mode      TEXT,
            updated_utc      INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_track_lyrics_track ON track_lyrics(track_id);

        CREATE TABLE IF NOT EXISTS lyric_sections (
            key       TEXT    NOT NULL REFERENCES track_lyrics(key) ON DELETE CASCADE,
            ordinal   INTEGER NOT NULL,
            start_ms  INTEGER NOT NULL,
            end_ms    INTEGER NOT NULL,
            offset_ms INTEGER NOT NULL,
            label     TEXT,
            PRIMARY KEY (key, ordinal)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS soundboard (
            id          INTEGER PRIMARY KEY,
            title       TEXT    NOT NULL DEFAULT '',
            path        TEXT    NOT NULL DEFAULT '',
            source_url  TEXT,
            duration_ms INTEGER NOT NULL DEFAULT 0,
            gain        REAL    NOT NULL DEFAULT 1.0,
            mode        TEXT    NOT NULL DEFAULT '',
            hotkey      TEXT,
            hotkey2     TEXT,
            sort        INTEGER NOT NULL DEFAULT 0,
            added_utc   INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_soundboard_path ON soundboard(path COLLATE NOCASE) WHERE path <> '';
        CREATE INDEX IF NOT EXISTS ix_soundboard_sort ON soundboard(sort);
        """;

    /// <summary>
    /// v2: full text search as an FTS5 <b>external content</b> table over
    /// <c>tracks</c>. The trigram tokenizer would give substring search but does
    /// not fold diacritics; <c>unicode61 remove_diacritics 2</c> does, and the
    /// substring cases trigram would cover are handled by a LIKE fallback in
    /// <see cref="TrackRepository.Search"/>. Content lives in <c>tracks</c>, so
    /// no text is stored twice; the AI/AD/AU triggers keep the index in step,
    /// including rows written by the importer.
    /// </summary>
    private const string Migration2Search = """
        CREATE VIRTUAL TABLE IF NOT EXISTS tracks_fts USING fts5(
            title,
            artist,
            album,
            album_artist,
            genre,
            path,
            content = 'tracks',
            content_rowid = 'id',
            tokenize = "unicode61 remove_diacritics 2"
        );

        CREATE TRIGGER IF NOT EXISTS tracks_ai AFTER INSERT ON tracks BEGIN
            INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, genre, path)
            VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.genre, new.path);
        END;

        CREATE TRIGGER IF NOT EXISTS tracks_ad AFTER DELETE ON tracks BEGIN
            INSERT INTO tracks_fts(tracks_fts, rowid, title, artist, album, album_artist, genre, path)
            VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.genre, old.path);
        END;

        CREATE TRIGGER IF NOT EXISTS tracks_au AFTER UPDATE ON tracks BEGIN
            INSERT INTO tracks_fts(tracks_fts, rowid, title, artist, album, album_artist, genre, path)
            VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.genre, old.path);
            INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, genre, path)
            VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.genre, new.path);
        END;

        INSERT INTO tracks_fts(tracks_fts) VALUES('rebuild');
        """;

    /// <summary>v3: lookups the browser and the snapshot loader need without a table scan.</summary>
    private const string Migration3Lookups = """
        CREATE INDEX IF NOT EXISTS ix_tracks_artist_title ON tracks(artist, title);
        CREATE INDEX IF NOT EXISTS ix_tracks_sort ON tracks(sort_artist, sort_title);
        CREATE INDEX IF NOT EXISTS ix_tracks_added ON tracks(added_utc);
        """;

    /// <summary>
    /// v4: a playlist name is a label, not identity. A user may import two lists
    /// with the same title (the fixture keeps two "YouTube playlist" rows), and
    /// the v1 unique index on <c>name</c> made the durable copy refuse to store
    /// them - which is why a database seeded from a real <c>playlists.json</c>
    /// could stay empty forever and every start re-parsed the file. Identity is
    /// <c>id</c>; the name keeps a plain index for lookups.
    /// </summary>
    private const string Migration4PlaylistNames = """
        DROP INDEX IF EXISTS ux_playlists_name;
        CREATE INDEX IF NOT EXISTS ix_playlists_name ON playlists(name COLLATE NOCASE);
        """;

    /// <summary>
    /// v5: the rail order is stored, not inferred from rowid. Existing rows keep
    /// the order they already had: the correlated count turns insertion order
    /// into a contiguous 0, 1, 2, ... run, so nothing collides at zero.
    /// </summary>
    private const string Migration5PlaylistOrder = """
        ALTER TABLE playlists ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
        UPDATE playlists SET sort_order = (
            SELECT COUNT(*) FROM playlists p2 WHERE p2.rowid < playlists.rowid
        );
        """;

    /// <summary>
    /// v6: eight saved hot cues per deck per track.
    ///
    /// <para>Keyed on <c>tracks.id</c>, deliberately <b>not</b> on the path-based
    /// key the beat grid uses (<c>Audio/Beat/BeatGridStore.cs</c> writes JSON keyed
    /// by path). A cue belongs to a row, so a track that is moved and rescanned
    /// loses its cues - the same trade <c>karaoke_assets</c> already makes, and the
    /// reason the columns here stay in media milliseconds rather than something
    /// derived from the file.</para>
    ///
    /// <para>The ephemeral quick cue is never stored: only one row per
    /// (track, deck, slot) exists, and <c>UNIQUE</c> is what makes <c>Set</c> an
    /// upsert instead of a pile of duplicates.</para>
    /// </summary>
    private const string Migration6HotCues = """
        CREATE TABLE IF NOT EXISTS hot_cues (
            id          INTEGER PRIMARY KEY,
            track_id    INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            deck        INTEGER NOT NULL,
            slot        INTEGER NOT NULL,
            position_ms INTEGER NOT NULL,
            label       TEXT,
            created_at  TEXT    NOT NULL,
            UNIQUE (track_id, deck, slot)
        );
        CREATE INDEX IF NOT EXISTS ix_hot_cues_track ON hot_cues(track_id, deck);
        """;
}
