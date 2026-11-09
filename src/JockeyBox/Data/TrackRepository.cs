using System.Text;
using Microsoft.Data.Sqlite;

namespace JockeyBox.Data;

/// <summary>
/// Read/write API for the <c>tracks</c> table: what <c>library.json</c> +
/// the scanner's in-memory list used to be.
///
/// <para><b>Identity</b>: <c>path_key</c> (case- and separator-folded absolute
/// path), so a rescan that reports <c>D:\Music\A.mp3</c> after <c>d:/music/a.mp3</c>
/// updates one row instead of creating a duplicate. The raw path is still stored
/// for display and playback.</para>
///
/// <para><b>Scan generation</b>: <see cref="BeginScan"/> hands out a stamp; every
/// row written through <see cref="Upsert"/> or <see cref="UpsertMany"/> carries
/// it as <c>last_seen_utc</c>; <see cref="MarkMissingExcept"/> then flags every
/// row from an older generation. Nothing is deleted: a missing track keeps its
/// BPM, its karaoke link and its playlist membership.</para>
///
/// <para>All methods are synchronous (Microsoft.Data.Sqlite's async API is a
/// thread-pool fake); callers that must not block the UI wrap them in
/// <c>Task.Run</c>.</para>
/// </summary>
public sealed class TrackRepository
{
    /// <summary>Rows per commit during a bulk import. 2 000 keeps the WAL small without paying per-row fsync.</summary>
    public const int DefaultBatchSize = 2000;

    private const string Columns =
        "id, path, path_key, title, artist, album, album_artist, genre, year, track_no, disc_no, " +
        "duration_ms, bpm, bpm_p10, bpm_p90, bpm_confidence, first_beat_ms, half_double_suspect, " +
        "size_bytes, mtime_utc, added_utc, last_seen_utc, is_missing, sort_title, sort_artist";

    private const string UpsertSql = """
        INSERT INTO tracks (
            path, path_key, title, artist, album, album_artist, genre, year, track_no, disc_no,
            duration_ms, bpm, bpm_p10, bpm_p90, bpm_confidence, first_beat_ms, half_double_suspect,
            size_bytes, mtime_utc, added_utc, last_seen_utc, is_missing, sort_title, sort_artist)
        VALUES (
            @path, @path_key, @title, @artist, @album, @album_artist, @genre, @year, @track_no, @disc_no,
            @duration_ms, @bpm, @bpm_p10, @bpm_p90, @bpm_confidence, @first_beat_ms, @half_double_suspect,
            @size_bytes, @mtime_utc, @added_utc, @last_seen_utc, @is_missing, @sort_title, @sort_artist)
        ON CONFLICT(path_key) DO UPDATE SET
            path                = excluded.path,
            title               = excluded.title,
            artist              = excluded.artist,
            album               = excluded.album,
            album_artist        = excluded.album_artist,
            genre               = excluded.genre,
            year                = excluded.year,
            track_no            = excluded.track_no,
            disc_no             = excluded.disc_no,
            duration_ms         = excluded.duration_ms,
            bpm                 = excluded.bpm,
            bpm_p10             = excluded.bpm_p10,
            bpm_p90             = excluded.bpm_p90,
            bpm_confidence      = excluded.bpm_confidence,
            first_beat_ms       = excluded.first_beat_ms,
            half_double_suspect = excluded.half_double_suspect,
            size_bytes          = excluded.size_bytes,
            mtime_utc           = excluded.mtime_utc,
            -- first-seen is history, never overwritten by a later scan
            added_utc           = CASE WHEN tracks.added_utc > 0 THEN tracks.added_utc ELSE excluded.added_utc END,
            last_seen_utc       = excluded.last_seen_utc,
            is_missing          = excluded.is_missing,
            sort_title          = excluded.sort_title,
            sort_artist         = excluded.sort_artist
        RETURNING id;
        """;

    private readonly JockeyBoxDatabase _db;
    private long _lastScanStamp;

    public TrackRepository(JockeyBoxDatabase db) => _db = db;

    /// <summary>Number of rows (missing ones included unless <paramref name="includeMissing"/> is false).</summary>
    public int Count(bool includeMissing = true) => _db.Read(conn =>
    {
        var value = JockeyBoxDatabase.Scalar(
            conn,
            includeMissing ? "SELECT COUNT(*) FROM tracks;" : "SELECT COUNT(*) FROM tracks WHERE is_missing = 0;");
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    });

    // ---- scan generation ---------------------------------------------------

    /// <summary>
    /// Start a scan generation. The stamp is strictly greater than the previous
    /// one even when two scans start inside the same millisecond, so
    /// <see cref="MarkMissingExcept"/> can never mistake a fresh row for a stale one.
    /// </summary>
    public long BeginScan()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Must be strictly newer than every stored last_seen_utc, otherwise a scan
        // that starts in the same millisecond as the previous upsert would leave the
        // old rows looking fresh and MarkMissingExcept would miss them.
        var newest = _db.Read(conn =>
        {
            var value = JockeyBoxDatabase.Scalar(conn, "SELECT COALESCE(MAX(last_seen_utc), 0) FROM tracks;");
            return value is null or DBNull ? 0L : Convert.ToInt64(value);
        });

        _lastScanStamp = Math.Max(now, Math.Max(newest + 1, _lastScanStamp + 1));
        return _lastScanStamp;
    }

    /// <summary>
    /// Flag as missing every row not stamped by <paramref name="scanStamp"/>.
    /// Returns how many rows were flipped. Rows are never deleted.
    /// </summary>
    public int MarkMissingExcept(long scanStamp) => _db.WithWriter(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE tracks SET is_missing = 1 " +
            "WHERE is_missing = 0 AND (last_seen_utc < @stamp OR last_seen_utc = 0);";
        cmd.Parameters.AddWithValue("@stamp", scanStamp);
        return cmd.ExecuteNonQuery();
    });

    /// <summary>
    /// Mark every stored path that is not in <paramref name="presentPathKeys"/> as
    /// missing and clear the flag on the ones that are; returns how many rows were
    /// flipped to missing. For callers that diff the disk themselves instead of
    /// using <see cref="BeginScan"/>.
    /// </summary>
    public int ApplyScan(IReadOnlyList<string> presentPathKeys)
    {
        var keys = new HashSet<string>(presentPathKeys.Select(LibraryKeys.PathKey), StringComparer.Ordinal);

        return _db.WriteTransaction(conn =>
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE tracks SET is_missing = 0, last_seen_utc = @now WHERE path_key = @key;";
                var now = cmd.Parameters.Add("@now", SqliteType.Integer);
                now.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var key = cmd.Parameters.Add("@key", SqliteType.Text);

                foreach (var k in keys)
                {
                    key.Value = k;
                    cmd.ExecuteNonQuery();
                }
            }

            using var mark = conn.CreateCommand();
            mark.CommandText =
                "UPDATE tracks SET is_missing = 1 WHERE is_missing = 0 AND path_key NOT IN (" +
                "SELECT value FROM json_each(@keys));";
            mark.Parameters.AddWithValue("@keys", System.Text.Json.JsonSerializer.Serialize(keys.ToArray()));
            return mark.ExecuteNonQuery();
        });
    }

    // ---- writes ------------------------------------------------------------

    /// <summary>Insert or update one row; returns its <c>id</c>.</summary>
    public long Upsert(TrackRecord track, long? scanStamp = null)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));

        return _db.WithWriter(conn =>
        {
            using var cmd = CreateUpsertCommand(conn);
            BindUpsert(cmd, track, scanStamp ?? _lastScanStamp);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        });
    }

    /// <summary>
    /// Bulk insert/update in batches of <paramref name="batchSize"/> rows, one
    /// <c>BEGIN IMMEDIATE</c> per batch, with a single re-used prepared command.
    /// </summary>
    public int UpsertMany(IEnumerable<TrackRecord> tracks, long? scanStamp = null, int batchSize = DefaultBatchSize)
    {
        if (tracks is null) throw new ArgumentNullException(nameof(tracks));
        if (batchSize < 1) batchSize = 1;

        var stamp = scanStamp ?? (_lastScanStamp != 0 ? _lastScanStamp : BeginScan());
        var written = 0;
        var buffer = new List<TrackRecord>(batchSize);

        foreach (var track in tracks)
        {
            if (track is null) continue;
            buffer.Add(track);
            if (buffer.Count < batchSize) continue;

            written += WriteBatch(buffer, stamp);
            buffer.Clear();
        }

        if (buffer.Count > 0) written += WriteBatch(buffer, stamp);
        return written;
    }

    private int WriteBatch(List<TrackRecord> batch, long stamp) => _db.WriteTransaction(conn =>
    {
        using var cmd = CreateUpsertCommand(conn);
        var count = 0;
        foreach (var track in batch)
        {
            BindUpsert(cmd, track, stamp);
            cmd.ExecuteNonQuery();
            count++;
        }

        return count;
    });

    /// <summary>Delete a row by identity (playlist membership survives: it has no FK).</summary>
    public bool Delete(string pathOrKey) => _db.WithWriter(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tracks WHERE path_key = @key;";
        cmd.Parameters.AddWithValue("@key", LibraryKeys.PathKey(pathOrKey));
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>
    /// Mirror an analysed beat grid onto a track row. Returns false when the path
    /// is not in the library (a file outside the scanned folders) or the DB is
    /// read-only; the JSON beat-grid document stays authoritative either way.
    /// </summary>
    public bool UpdateBeatGrid(string path, double bpm, double bpmP10, double bpmP90,
                               double confidence, long firstBeatMs) => _db.WithWriter(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE tracks SET bpm = @bpm, bpm_p10 = @p10, bpm_p90 = @p90, " +
            "bpm_confidence = @confidence, first_beat_ms = @first WHERE path_key = @key;";
        cmd.Parameters.AddWithValue("@bpm", bpm);
        cmd.Parameters.AddWithValue("@p10", bpmP10);
        cmd.Parameters.AddWithValue("@p90", bpmP90);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@first", firstBeatMs);
        cmd.Parameters.AddWithValue("@key", LibraryKeys.PathKey(path));
        return cmd.ExecuteNonQuery() > 0;
    });

    // ---- reads -------------------------------------------------------------

    public TrackRecord? GetByPath(string path) => GetByPathKey(LibraryKeys.PathKey(path));

    public TrackRecord? GetByPathKey(string pathKey) => _db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE path_key = @key LIMIT 1;";
        cmd.Parameters.AddWithValue("@key", pathKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    });

    public TrackRecord? GetById(long id) => _db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    });

    /// <summary>All rows in display order (<c>sort_artist, sort_title</c>).</summary>
    public IReadOnlyList<TrackRecord> All(bool includeMissing = false, int limit = 0) => _db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {Columns} FROM tracks" +
            (includeMissing ? "" : " WHERE is_missing = 0") +
            " ORDER BY sort_artist, sort_title, path" +
            (limit > 0 ? " LIMIT @limit;" : ";");
        if (limit > 0) cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        var rows = new List<TrackRecord>();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    });

    /// <summary>Rows by <c>id</c>, in the order the ids were given, missing ids skipped.</summary>
    public IReadOnlyList<TrackRecord> GetByIds(IEnumerable<long> ids)
    {
        var wanted = new List<long>();
        var seen = new HashSet<long>();
        foreach (var id in ids)
        {
            if (seen.Add(id)) wanted.Add(id);
        }

        if (wanted.Count == 0) return Array.Empty<TrackRecord>();

        var rows = _db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT {Columns} FROM tracks WHERE id IN (SELECT value FROM json_each(@ids));";
            cmd.Parameters.AddWithValue("@ids", System.Text.Json.JsonSerializer.Serialize(wanted));

            using var reader = cmd.ExecuteReader();
            var found = new Dictionary<long, TrackRecord>(wanted.Count);
            while (reader.Read())
            {
                var row = Read(reader);
                found[row.Id] = row;
            }

            return found;
        });

        var result = new List<TrackRecord>(wanted.Count);
        foreach (var id in wanted)
        {
            if (rows.TryGetValue(id, out var row)) result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// Full-text search over title/artist/album/album artist/genre/path.
    ///
    /// <para><b>Why FTS5 + LIKE and not the trigram tokenizer:</b> the required
    /// diacritic behaviour ("Björk" finds "bjork") only exists with
    /// <c>unicode61 remove_diacritics 2</c>; FTS5's <c>trigram</c> tokenizer does
    /// not implement <c>remove_diacritics</c> at all. So token prefix search goes
    /// through FTS5, and the substring case trigram would have covered is a
    /// bounded <c>LIKE '%q%'</c> fallback over the same name columns. FTS hits
    /// come first; LIKE-only hits follow, both in display order.</para>
    /// </summary>
    public IReadOnlyList<TrackRecord> Search(string? query, int limit = 200, bool includeMissing = false)
    {
        if (limit <= 0) limit = 200;
        var like = (query ?? "").Trim();
        if (like.Length == 0) return All(includeMissing, limit);

        var fts = BuildFtsQuery(like);

        return _db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();

            if (fts.Length == 0)
            {
                cmd.CommandText =
                    $"SELECT {Columns} FROM tracks " +
                    "WHERE (title LIKE @like ESCAPE '\\' OR artist LIKE @like ESCAPE '\\' " +
                    "   OR album LIKE @like ESCAPE '\\' OR album_artist LIKE @like ESCAPE '\\')" +
                    (includeMissing ? "" : " AND is_missing = 0") +
                    " ORDER BY sort_artist, sort_title, path LIMIT @limit;";
                cmd.Parameters.AddWithValue("@like", "%" + EscapeLike(like) + "%");
                cmd.Parameters.AddWithValue("@limit", limit);
            }
            else
            {
                // Two ranked sources: FTS phrase/prefix hits (wave 0) then LIKE
                // substring hits FTS did not already return (wave 1).
                cmd.CommandText =
                    $"""
                    WITH fts AS (
                        SELECT rowid AS id, 0 AS wave FROM tracks_fts WHERE tracks_fts MATCH @fts
                    ),
                    like_hits AS (
                        SELECT id, 1 AS wave FROM tracks
                        WHERE (title LIKE @like ESCAPE '\' OR artist LIKE @like ESCAPE '\'
                            OR album LIKE @like ESCAPE '\' OR album_artist LIKE @like ESCAPE '\')
                    ),
                    ranked AS (
                        SELECT id, MIN(wave) AS wave
                        FROM (SELECT id, wave FROM fts UNION ALL SELECT id, wave FROM like_hits)
                        GROUP BY id
                    )
                    SELECT {Columns} FROM tracks
                    WHERE id IN (SELECT id FROM ranked)
                    {(includeMissing ? "" : "AND is_missing = 0")}
                    ORDER BY (SELECT wave FROM ranked WHERE ranked.id = tracks.id),
                             sort_artist, sort_title, path
                    LIMIT @limit;
                    """;
                cmd.Parameters.AddWithValue("@fts", fts);
                cmd.Parameters.AddWithValue("@like", "%" + EscapeLike(like) + "%");
                cmd.Parameters.AddWithValue("@limit", limit);
            }

            using var reader = cmd.ExecuteReader();
            var rows = new List<TrackRecord>();
            while (reader.Read()) rows.Add(Read(reader));
            return rows;
        });
    }

    /// <summary>True when the FTS index exists and describes <paramref name="term"/> in at least one row.</summary>
    public bool FtsContains(string term)
    {
        var fts = BuildFtsQuery(term);
        if (fts.Length == 0) return false;

        return _db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM tracks_fts WHERE tracks_fts MATCH @fts LIMIT 1;";
            cmd.Parameters.AddWithValue("@fts", fts);
            return cmd.ExecuteScalar() is not null;
        });
    }

    /// <summary>
    /// Turn free text into a safe FTS5 query: every word quoted, the last one a
    /// prefix. Quotes and control characters are stripped, so a search for
    /// <c>AC/DC - "Live"</c> cannot raise a syntax error.
    /// </summary>
    public static string BuildFtsQuery(string? query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return "";

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(tokens[i].Replace("\"", "\"\"")).Append('"');
            if (i == tokens.Count - 1) sb.Append('*');
        }

        return sb.ToString();
    }

    private static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ---- mapping -----------------------------------------------------------

    private static SqliteCommand CreateUpsertCommand(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = UpsertSql;

        cmd.Parameters.Add("@path", SqliteType.Text);
        cmd.Parameters.Add("@path_key", SqliteType.Text);
        cmd.Parameters.Add("@title", SqliteType.Text);
        cmd.Parameters.Add("@artist", SqliteType.Text);
        cmd.Parameters.Add("@album", SqliteType.Text);
        cmd.Parameters.Add("@album_artist", SqliteType.Text);
        cmd.Parameters.Add("@genre", SqliteType.Text);
        cmd.Parameters.Add("@year", SqliteType.Integer);
        cmd.Parameters.Add("@track_no", SqliteType.Integer);
        cmd.Parameters.Add("@disc_no", SqliteType.Integer);
        cmd.Parameters.Add("@duration_ms", SqliteType.Integer);
        cmd.Parameters.Add("@bpm", SqliteType.Real);
        cmd.Parameters.Add("@bpm_p10", SqliteType.Real);
        cmd.Parameters.Add("@bpm_p90", SqliteType.Real);
        cmd.Parameters.Add("@bpm_confidence", SqliteType.Real);
        cmd.Parameters.Add("@first_beat_ms", SqliteType.Integer);
        cmd.Parameters.Add("@half_double_suspect", SqliteType.Integer);
        cmd.Parameters.Add("@size_bytes", SqliteType.Integer);
        cmd.Parameters.Add("@mtime_utc", SqliteType.Integer);
        cmd.Parameters.Add("@added_utc", SqliteType.Integer);
        cmd.Parameters.Add("@last_seen_utc", SqliteType.Integer);
        cmd.Parameters.Add("@is_missing", SqliteType.Integer);
        cmd.Parameters.Add("@sort_title", SqliteType.Text);
        cmd.Parameters.Add("@sort_artist", SqliteType.Text);

        cmd.Prepare();
        return cmd;
    }

    private static void BindUpsert(SqliteCommand cmd, TrackRecord track, long scanStamp)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seen = Math.Max(now, scanStamp);

        var path = LibraryKeys.NormalizePath(track.Path);
        var p = cmd.Parameters;

        p["@path"].Value = path;
        p["@path_key"].Value = LibraryKeys.PathKey(path);
        p["@title"].Value = track.Title ?? "";
        p["@artist"].Value = track.Artist ?? "";
        p["@album"].Value = track.Album ?? "";
        p["@album_artist"].Value = track.AlbumArtist ?? "";
        p["@genre"].Value = track.Genre ?? "";
        p["@year"].Value = track.Year;
        p["@track_no"].Value = track.TrackNo;
        p["@disc_no"].Value = track.DiscNo;
        p["@duration_ms"].Value = track.DurationMs;
        p["@bpm"].Value = track.Bpm;
        p["@bpm_p10"].Value = track.BpmP10;
        p["@bpm_p90"].Value = track.BpmP90;
        p["@bpm_confidence"].Value = track.BpmConfidence;
        p["@first_beat_ms"].Value = track.FirstBeatMs;
        p["@half_double_suspect"].Value = track.HalfDoubleSuspect ? 1 : 0;
        p["@size_bytes"].Value = track.SizeBytes;
        p["@mtime_utc"].Value = LibraryKeys.ToUnixMs(track.MTimeUtc);
        p["@added_utc"].Value = LibraryKeys.ToUnixMs(track.AddedUtc);
        p["@last_seen_utc"].Value = seen;
        p["@is_missing"].Value = track.IsMissing ? 1 : 0;
        p["@sort_title"].Value = string.IsNullOrEmpty(track.SortTitle)
            ? LibraryKeys.SortKey(track.Title)
            : track.SortTitle;
        p["@sort_artist"].Value = string.IsNullOrEmpty(track.SortArtist)
            ? LibraryKeys.SortKey(track.Artist)
            : track.SortArtist;
    }

    private static TrackRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        // Stored with '/' separators (identity); emit native, which is what the
        // filesystem, ledger and tag cache key on.
        Path = LibraryKeys.NativePath(reader.GetString(1)),
        PathKey = reader.GetString(2),
        Title = reader.GetString(3),
        Artist = reader.GetString(4),
        Album = reader.GetString(5),
        AlbumArtist = reader.GetString(6),
        Genre = reader.GetString(7),
        Year = reader.GetInt32(8),
        TrackNo = reader.GetInt32(9),
        DiscNo = reader.GetInt32(10),
        DurationMs = reader.GetInt64(11),
        Bpm = reader.GetDouble(12),
        BpmP10 = reader.GetDouble(13),
        BpmP90 = reader.GetDouble(14),
        BpmConfidence = reader.GetDouble(15),
        FirstBeatMs = reader.GetInt64(16),
        HalfDoubleSuspect = reader.GetInt64(17) != 0,
        SizeBytes = reader.GetInt64(18),
        MTimeUtc = LibraryKeys.FromUnixMs(reader.GetInt64(19)),
        AddedUtc = LibraryKeys.FromUnixMs(reader.GetInt64(20)),
        LastSeenUtc = LibraryKeys.FromUnixMs(reader.GetInt64(21)),
        IsMissing = reader.GetInt64(22) != 0,
        SortTitle = reader.GetString(23),
        SortArtist = reader.GetString(24),
    };
}
