using System.Text;
using Microsoft.Data.Sqlite;
using JockeyBox.Sources;

namespace JockeyBox.Data;

/// <summary>One persisted playlist row, detached from its connection.</summary>
public sealed record PlaylistRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int SortOrder { get; init; }
    public PlaylistKind Kind { get; init; }
    public MediaSource Source { get; init; }
    public string? SourceId { get; init; }
    public bool SyncEnabled { get; init; }
    public DateTime LastSyncedUtc { get; init; }
    public int TrackCount { get; init; }
}

/// <summary>
/// One persisted membership row. <see cref="SourceKey"/> is the identity:
/// for an app-owned (internal) playlist it is the normalised library key
/// (<see cref="SourceTrack.LocalKey"/>); for an imported playlist it is the
/// remote id (Spotify track id / YouTube video id) which has no
/// <c>tracks</c> row until the file is downloaded.
/// </summary>
public sealed record PlaylistItemRecord
{
    public string PlaylistId { get; init; } = "";
    public long Position { get; init; }
    public string SourceKey { get; init; } = "";
    public string RowId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Isrc { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public double DurationMs { get; init; }
    public string? VideoId { get; init; }
    public string? LibraryPath { get; init; }
    public MediaSource Source { get; init; }
}

/// <summary>
/// Durable home of the playlist graph <see cref="PlaylistStore"/> exposes:
/// <c>playlists</c> + <c>playlist_items</c>.
///
/// <para><b>Positions</b> are spaced by <see cref="PositionStep"/> (1024), so
/// dropping a row between two neighbours is one <c>UPDATE</c> of that row's
/// position. Only when a gap is exhausted does the playlist get renumbered
/// (1024, 2048, ...) inside one transaction. The list order is the
/// <c>position</c> order; <c>playlist_items</c> is <c>WITHOUT ROWID</c> so the
/// primary key <c>(playlist_id, position)</c> is the table itself.</para>
///
/// <para><b>Membership identity</b>: <c>source_key</c> holds an internal row's
/// normalised library key and an imported row's remote id. That is why
/// <c>playlist_items</c> carries no foreign key to <c>tracks</c>: an imported
/// row is not on disk yet and would make every import fail an FK check.</para>
///
/// <para>Nothing here asyncs: see <see cref="JockeyBoxDatabase"/>.</para>
/// </summary>
public sealed class PlaylistRepository
{
    /// <summary>Gap between two membership rows; see the class remarks.</summary>
    public const int PositionStep = 1024;

    /// <summary>
    /// <c>schema_info</c> key holding the <see cref="PlaylistFileStamp"/> of the
    /// <c>playlists.json</c> the graph was last written from. When the file on
    /// disk no longer matches it, the JSON file changed behind the app's back and
    /// the graph has to be reimported (see <see cref="LibraryBoot"/>).
    /// </summary>
    public const string JsonStampKey = "playlists_json_stamp";

    private const string PlaylistColumns =
        "id, name, kind, source, source_id, sync_enabled, last_synced_utc, " +
        "(SELECT COUNT(*) FROM playlist_items i WHERE i.playlist_id = p.id) AS track_count, " +
        "sort_order";

    private readonly JockeyBoxDatabase _db;

    public PlaylistRepository(JockeyBoxDatabase db) => _db = db;

    /// <summary>True when the database already holds the playlist graph.</summary>
    public bool HasPlaylists() => _db.Read(conn =>
        Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM playlists;")) > 0);

    // ---- reads -------------------------------------------------------------

    /// <summary>
    /// The whole graph as the JSON store shaped it: playlists in their original
    /// insertion order, each with its membership in list order.
    /// </summary>
    public IReadOnlyList<UserPlaylist> LoadAll()
    {
        var records = LoadPlaylists();
        if (records.Count == 0) return Array.Empty<UserPlaylist>();

        var items = _db.Read(conn =>
        {
            var map = new Dictionary<string, List<PlaylistItemRecord>>(StringComparer.Ordinal);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT playlist_id, position, source_key, row_id, title, artist, album, isrc, " +
                "cover_url, duration_ms, video_id, library_path, source " +
                "FROM playlist_items ORDER BY playlist_id, position;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadItem(reader);
                if (!map.TryGetValue(item.PlaylistId, out var list))
                {
                    list = new List<PlaylistItemRecord>();
                    map[item.PlaylistId] = list;
                }

                list.Add(item);
            }

            return map;
        });

        var result = new List<UserPlaylist>(records.Count);
        foreach (var record in records)
        {
            var playlist = new UserPlaylist
            {
                Id = record.Id,
                Name = record.Name,
                Kind = record.Kind,
                Source = record.Source,
                SourceId = record.SourceId,
                SyncEnabled = record.SyncEnabled,
                LastSyncedUtc = record.LastSyncedUtc,
                SortOrder = record.SortOrder,
            };

            if (items.TryGetValue(record.Id, out var rows))
            {
                foreach (var row in rows)
                {
                    playlist.Tracks.Add(new SourceTrack
                    {
                        Id = string.IsNullOrEmpty(row.RowId) ? row.SourceKey : row.RowId,
                        Source = row.Source == MediaSource.Manual ? record.Source : row.Source,
                        Title = row.Title,
                        Artist = row.Artist,
                        Album = row.Album,
                        Isrc = row.Isrc,
                        CoverUrl = row.CoverUrl,
                        DurationMs = row.DurationMs,
                        VideoId = row.VideoId,
                        LibraryPath = row.LibraryPath,
                        PlaylistName = record.Name,
                        // An internal row's key is its identity; an imported row's
                        // source_key is a remote id and must not be read as a key.
                        LocalKey = record.Kind == PlaylistKind.Internal ? row.SourceKey : null,
                    });
                }
            }

            result.Add(playlist);
        }

        return result;
    }

    /// <summary>Playlist headers only (no membership), in insertion order.</summary>
    public IReadOnlyList<PlaylistRecord> LoadPlaylists() => _db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        // Explicit rail position first; rowid only breaks a legacy tie (a
        // database migrated before sort_order was populated).
        cmd.CommandText = $"SELECT {PlaylistColumns} FROM playlists p ORDER BY p.sort_order, p.rowid;";
        using var reader = cmd.ExecuteReader();
        var rows = new List<PlaylistRecord>();
        while (reader.Read())
        {
            rows.Add(new PlaylistRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = (PlaylistKind)reader.GetInt32(2),
                Source = (MediaSource)reader.GetInt32(3),
                SourceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                SyncEnabled = reader.GetInt64(5) != 0,
                LastSyncedUtc = LibraryKeys.FromUnixMs(reader.GetInt64(6)),
                TrackCount = reader.GetInt32(7),
                SortOrder = reader.GetInt32(8),
            });
        }

        return rows;
    });

    /// <summary>Membership of one playlist, in list order.</summary>
    public IReadOnlyList<PlaylistItemRecord> LoadItems(string playlistId) => _db.Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT playlist_id, position, source_key, row_id, title, artist, album, isrc, " +
            "cover_url, duration_ms, video_id, library_path, source " +
            "FROM playlist_items WHERE playlist_id = @id ORDER BY position;";
        cmd.Parameters.AddWithValue("@id", playlistId);

        using var reader = cmd.ExecuteReader();
        var rows = new List<PlaylistItemRecord>();
        while (reader.Read()) rows.Add(ReadItem(reader));
        return rows;
    });

    public UserPlaylist? Load(string playlistId)
    {
        var record = _db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {PlaylistColumns} FROM playlists p WHERE p.id = @id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", playlistId);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new PlaylistRecord
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Kind = (PlaylistKind)reader.GetInt32(2),
                    Source = (MediaSource)reader.GetInt32(3),
                    SourceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SyncEnabled = reader.GetInt64(5) != 0,
                    LastSyncedUtc = LibraryKeys.FromUnixMs(reader.GetInt64(6)),
                    TrackCount = reader.GetInt32(7),
                }
                : null;
        });

        if (record is null) return null;

        var playlist = new UserPlaylist
        {
            Id = record.Id,
            Name = record.Name,
            Kind = record.Kind,
            Source = record.Source,
            SourceId = record.SourceId,
            SyncEnabled = record.SyncEnabled,
            LastSyncedUtc = record.LastSyncedUtc,
        };

        foreach (var row in LoadItems(record.Id))
        {
            playlist.Tracks.Add(new SourceTrack
            {
                Id = string.IsNullOrEmpty(row.RowId) ? row.SourceKey : row.RowId,
                Source = row.Source == MediaSource.Manual ? record.Source : row.Source,
                Title = row.Title,
                Artist = row.Artist,
                Album = row.Album,
                Isrc = row.Isrc,
                CoverUrl = row.CoverUrl,
                DurationMs = row.DurationMs,
                VideoId = row.VideoId,
                LibraryPath = row.LibraryPath,
                PlaylistName = record.Name,
                LocalKey = record.Kind == PlaylistKind.Internal ? row.SourceKey : null,
            });
        }

        return playlist;
    }

    // ---- writes ------------------------------------------------------------

    /// <summary>
    /// Make the database match <paramref name="playlists"/>: playlists absent
    /// from the list are deleted (their items cascade), everything present is
    /// upserted, and membership is rewritten only for a playlist whose content
    /// fingerprint changed.
    ///
    /// <para><paramref name="jsonStamp"/> records which <c>playlists.json</c> this
    /// graph mirrors (see <see cref="JsonStampKey"/>); it is written in the same
    /// transaction as the graph so the two can never disagree.</para>
    ///
    /// <para><paramref name="pruneAbsent"/> guards the destructive half. Pass false
    /// when the caller's graph is not a complete read of this database (a store
    /// running off <c>playlists.json</c>): such a graph can be missing rows the
    /// database still holds, and deleting those loses user playlists silently.
    /// The rows present are still upserted either way.</para>
    /// </summary>
    public void SaveAll(IEnumerable<UserPlaylist> playlists, long? jsonStamp = null, bool pruneAbsent = true)
    {
        if (playlists is null) return;

        var list = playlists.Where(p => p is not null).ToList();
        var fingerprints = list.ToDictionary(p => p.Id, Fingerprint, StringComparer.Ordinal);

        _db.WriteTransaction(conn =>
        {
            var keep = new HashSet<string>(list.Select(p => p.Id), StringComparer.Ordinal);

            // Drop playlists that no longer exist in memory - only when the caller
            // vouches that its graph is the whole database (see pruneAbsent).
            if (pruneAbsent)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id FROM playlists;";
                using var reader = cmd.ExecuteReader();
                var doomed = new List<string>();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!keep.Contains(id)) doomed.Add(id);
                }

                reader.Close();

                foreach (var id in doomed)
                {
                    using var del = conn.CreateCommand();
                    del.CommandText = "DELETE FROM playlists WHERE id = @id;";
                    del.Parameters.AddWithValue("@id", id);
                    del.ExecuteNonQuery();
                }
            }

            using var upsert = CreatePlaylistUpsert(conn);
            using var selectHash = conn.CreateCommand();
            selectHash.CommandText = "SELECT content_hash FROM playlists WHERE id = @id;";
            var hashParam = selectHash.Parameters.Add("@id", SqliteType.Text);

            foreach (var playlist in list)
            {
                var hash = fingerprints[playlist.Id];
                hashParam.Value = playlist.Id;
                var previous = selectHash.ExecuteScalar() as string;

                BindPlaylist(upsert, playlist, hash);
                upsert.ExecuteNonQuery();

                if (!string.Equals(previous, hash, StringComparison.Ordinal))
                    WriteItems(conn, playlist);
            }

            if (jsonStamp is { } stamp)
                _db.SetInfo(conn, JsonStampKey, stamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
    }

    /// <summary>
    /// Record the identity of the playlists.json now on disk, without touching
    /// the graph.
    ///
    /// <para>The mirror writes the file *after* the mutation's row write, so the
    /// stamp stored with that row write describes the previous file. Left alone,
    /// every start would compare a stale stored stamp against the file it just
    /// wrote and reimport the whole 5.9 MB document. This is one row update in
    /// exchange for a clean start.</para>
    /// </summary>
    public void SetJsonStamp(long stamp) =>
        _db.SetInfo(JsonStampKey, stamp.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Persist one playlist (header + membership).</summary>
    public void Save(UserPlaylist playlist)    {
        if (playlist is null) return;

        var hash = Fingerprint(playlist);
        _db.WriteTransaction(conn =>
        {
            using var upsert = CreatePlaylistUpsert(conn);
            BindPlaylist(upsert, playlist, hash);
            upsert.ExecuteNonQuery();
            WriteItems(conn, playlist);
        });
    }

    /// <summary>Delete one playlist and, by cascade, its membership. Returns true when a row went.</summary>
    public bool Delete(string playlistId) => _db.WithWriter(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM playlists WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", playlistId);
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>
    /// Move the row at <paramref name="fromIndex"/> so it ends up at
    /// <paramref name="toIndex"/>. One row is touched when the neighbour gap
    /// still has room; otherwise the playlist is renumbered inside the same
    /// transaction. Returns true when the list order actually changed.
    /// </summary>
    public bool MoveItem(string playlistId, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0) return false;

        return _db.WriteTransaction(conn =>
        {
            var positions = ReadPositions(conn, playlistId);
            if (fromIndex >= positions.Count || toIndex >= positions.Count) return false;

            var rest = new List<long>(positions);
            var moving = rest[fromIndex];
            rest.RemoveAt(fromIndex);

            long target = 0;
            var oneRow = false;

            if (rest.Count == 0)
            {
                // Nothing else to order against; a plain renumber is the answer.
            }
            else if (toIndex <= 0)
            {
                target = rest[0] - PositionStep;
                // Positions below 1024 are legal (negative ones too), so moving to the
                // front is always a single-row update. Only absurd drift renumbers.
                oneRow = target > -1_000_000_000;
            }
            else if (toIndex >= rest.Count)
            {
                target = rest[^1] + PositionStep;
                oneRow = true;
            }
            else
            {
                var before = rest[toIndex - 1];
                var after = rest[toIndex];
                oneRow = after - before >= 2;
                if (oneRow) target = before + (after - before) / 2;
            }

            if (oneRow)
            {
                using var update = conn.CreateCommand();
                update.CommandText =
                    "UPDATE playlist_items SET position = @new WHERE playlist_id = @id AND position = @old;";
                update.Parameters.AddWithValue("@new", target);
                update.Parameters.AddWithValue("@id", playlistId);
                update.Parameters.AddWithValue("@old", moving);
                return update.ExecuteNonQuery() > 0;
            }

            // No room next to the neighbours (or no neighbours at all): rewrite
            // every position in the *reordered* list. Renumbering the old order
            // here would silently drop the move.
            rest.Insert(Math.Min(toIndex, rest.Count), moving);
            RewritePositions(conn, playlistId, rest);
            return true;
        });
    }

    /// <summary>Rewrite positions as 1024, 2048, ... in current order.</summary>
    public void Renumber(string playlistId) => _db.WriteTransaction(conn =>
    {
        RewritePositions(conn, playlistId, ReadPositions(conn, playlistId));
        return true;
    });

    private static List<long> ReadPositions(SqliteConnection conn, string playlistId)
    {
        var positions = new List<long>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT position FROM playlist_items WHERE playlist_id = @id ORDER BY position;";
        cmd.Parameters.AddWithValue("@id", playlistId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) positions.Add(reader.GetInt64(0));
        return positions;
    }

    /// <summary>Rewrite <paramref name="positions"/> (in display order) as 1024, 2048, ...</summary>
    private static void RewritePositions(SqliteConnection conn, string playlistId, List<long> positions)
    {
        using var update = conn.CreateCommand();
        update.CommandText =
            "UPDATE playlist_items SET position = @new WHERE playlist_id = @id AND position = @old;";
        var newPos = update.Parameters.Add("@new", SqliteType.Integer);
        var id = update.Parameters.Add("@id", SqliteType.Text);
        var oldPos = update.Parameters.Add("@old", SqliteType.Integer);
        id.Value = playlistId;

        // Ascending rewrite would collide with PK rows still holding the new
        // position (e.g. 512 -> 1024 while 1024 exists), so move through a
        // negative staging value first.
        for (var i = 0; i < positions.Count; i++)
        {
            newPos.Value = -(i + 1);
            oldPos.Value = positions[i];
            update.ExecuteNonQuery();
        }

        for (var i = 0; i < positions.Count; i++)
        {
            newPos.Value = (long)(i + 1) * PositionStep;
            oldPos.Value = -(i + 1);
            update.ExecuteNonQuery();
        }
    }

    // ---- internals ---------------------------------------------------------

    private static SqliteCommand CreatePlaylistUpsert(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playlists (id, name, kind, source, source_id, sync_enabled, last_synced_utc, sort_order, created_utc, updated_utc, content_hash)
            VALUES (@id, @name, @kind, @source, @source_id, @sync_enabled, @last_synced, @sort_order, @now, @now, @hash)
            ON CONFLICT(id) DO UPDATE SET
                name            = excluded.name,
                kind            = excluded.kind,
                source          = excluded.source,
                source_id       = excluded.source_id,
                sync_enabled    = excluded.sync_enabled,
                last_synced_utc = excluded.last_synced_utc,
                sort_order      = excluded.sort_order,
                updated_utc     = excluded.updated_utc,
                content_hash    = excluded.content_hash;
            """;

        cmd.Parameters.Add("@id", SqliteType.Text);
        cmd.Parameters.Add("@name", SqliteType.Text);
        cmd.Parameters.Add("@kind", SqliteType.Integer);
        cmd.Parameters.Add("@source", SqliteType.Integer);
        cmd.Parameters.Add("@source_id", SqliteType.Text);
        cmd.Parameters.Add("@sync_enabled", SqliteType.Integer);
        cmd.Parameters.Add("@last_synced", SqliteType.Integer);
        cmd.Parameters.Add("@sort_order", SqliteType.Integer);
        cmd.Parameters.Add("@now", SqliteType.Integer);
        cmd.Parameters.Add("@hash", SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    private static void BindPlaylist(SqliteCommand cmd, UserPlaylist playlist, string hash)
    {
        var p = cmd.Parameters;
        p["@id"].Value = playlist.Id;
        p["@name"].Value = playlist.Name ?? "";
        p["@kind"].Value = (int)playlist.Kind;
        p["@source"].Value = (int)playlist.Source;
        p["@source_id"].Value = (object?)playlist.SourceId ?? DBNull.Value;
        p["@sync_enabled"].Value = playlist.SyncEnabled ? 1 : 0;
        p["@last_synced"].Value = LibraryKeys.ToUnixMs(playlist.LastSyncedUtc);
        p["@sort_order"].Value = playlist.SortOrder;
        p["@now"].Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        p["@hash"].Value = hash;
    }

    private static void WriteItems(SqliteConnection conn, UserPlaylist playlist)
    {
        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM playlist_items WHERE playlist_id = @id;";
            clear.Parameters.AddWithValue("@id", playlist.Id);
            clear.ExecuteNonQuery();
        }

        if (playlist.Tracks.Count == 0) return;

        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO playlist_items (
                playlist_id, position, source_key, row_id, title, artist, album, isrc,
                cover_url, duration_ms, video_id, library_path, source, added_utc)
            VALUES (
                @playlist_id, @position, @source_key, @row_id, @title, @artist, @album, @isrc,
                @cover_url, @duration_ms, @video_id, @library_path, @source, @now);
            """;

        var playlistId = insert.Parameters.Add("@playlist_id", SqliteType.Text);
        var position = insert.Parameters.Add("@position", SqliteType.Integer);
        var sourceKey = insert.Parameters.Add("@source_key", SqliteType.Text);
        var rowId = insert.Parameters.Add("@row_id", SqliteType.Text);
        var title = insert.Parameters.Add("@title", SqliteType.Text);
        var artist = insert.Parameters.Add("@artist", SqliteType.Text);
        var album = insert.Parameters.Add("@album", SqliteType.Text);
        var isrc = insert.Parameters.Add("@isrc", SqliteType.Text);
        var cover = insert.Parameters.Add("@cover_url", SqliteType.Text);
        var duration = insert.Parameters.Add("@duration_ms", SqliteType.Integer);
        var videoId = insert.Parameters.Add("@video_id", SqliteType.Text);
        var libraryPath = insert.Parameters.Add("@library_path", SqliteType.Text);
        var source = insert.Parameters.Add("@source", SqliteType.Integer);
        var now = insert.Parameters.Add("@now", SqliteType.Integer);
        insert.Prepare();

        var isInternal = playlist.Kind == PlaylistKind.Internal;
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long slot = 0;
        foreach (var track in playlist.Tracks)
        {
            slot += PositionStep;

            // Internal membership is the library key; imported membership is the
            // remote id. Both live in one column because they are never mixed
            // inside a playlist, and no FK points at tracks.
            var key = isInternal
                ? SourceTrack.NormalizeLocalKey(track.LocalKey) is { Length: > 0 } local
                    ? local
                    : SourceTrack.NormalizeLocalKey(track.Id)
                : track.Id;

            playlistId.Value = playlist.Id;
            position.Value = slot;
            sourceKey.Value = key;
            rowId.Value = track.Id ?? "";
            title.Value = track.Title ?? "";
            artist.Value = track.Artist ?? "";
            album.Value = track.Album ?? "";
            isrc.Value = track.Isrc ?? "";
            cover.Value = track.CoverUrl ?? "";
            duration.Value = (long)Math.Round(track.DurationMs);
            videoId.Value = (object?)track.VideoId ?? DBNull.Value;
            libraryPath.Value = (object?)track.LibraryPath ?? DBNull.Value;
            source.Value = (int)track.Source;
            now.Value = stamp;

            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Cheap content identity of a playlist: header fields plus each row's
    /// identity in order. Used to skip the 5 000-row rewrite when only a
    /// different playlist changed.
    /// </summary>
    private static string Fingerprint(UserPlaylist playlist)
    {
        var sb = new StringBuilder(playlist.Tracks.Count * 24);
        Append(sb, playlist.Name);
        sb.Append('|').Append((int)playlist.Kind).Append('|').Append((int)playlist.Source)
          .Append('|').Append(playlist.SourceId ?? "").Append('|').Append(playlist.SyncEnabled ? 1 : 0);

        foreach (var track in playlist.Tracks)
        {
            sb.Append('\n');
            Append(sb, track.Id);
            Append(sb, track.LocalKey);
            Append(sb, track.Title);
            Append(sb, track.Artist);
            Append(sb, track.Album);
            Append(sb, track.Isrc);
            Append(sb, track.CoverUrl);
            Append(sb, track.VideoId);
            Append(sb, track.LibraryPath);
            Append(sb, ((int)track.DurationMs).ToString());
            Append(sb, ((int)track.Source).ToString());
        }

        return Fnv1a(sb.ToString());
    }

    private static void Append(StringBuilder sb, string? value) =>
        sb.Append(value ?? "").Append('\u241F');

    private static string Fnv1a(string text)
    {
        // 64-bit FNV-1a: stable across processes, unlike string.GetHashCode.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash.ToString("x16");
    }

    private static PlaylistItemRecord ReadItem(SqliteDataReader reader) => new()
    {
        PlaylistId = reader.GetString(0),
        Position = reader.GetInt64(1),
        SourceKey = reader.GetString(2),
        RowId = reader.GetString(3),
        Title = reader.GetString(4),
        Artist = reader.GetString(5),
        Album = reader.GetString(6),
        Isrc = reader.GetString(7),
        CoverUrl = reader.GetString(8),
        DurationMs = reader.GetDouble(9),
        VideoId = reader.IsDBNull(10) ? null : reader.GetString(10),
        LibraryPath = reader.IsDBNull(11) ? null : reader.GetString(11),
        Source = (MediaSource)reader.GetInt32(12),
    };
}
