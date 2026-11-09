using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using JockeyBox.Sources;

namespace JockeyBox.Data;

/// <summary>What a <see cref="LegacyImporter.Run"/> actually moved.</summary>
public sealed record LegacyImportReport
{
    /// <summary>False when the guard said "already imported" or no JSON file existed.</summary>
    public bool Ran { get; init; }

    /// <summary>Rows taken from <c>library.json</c> (0 when that file is absent).</summary>
    public int Tracks { get; init; }

    public int Playlists { get; init; }
    public int PlaylistItems { get; init; }

    /// <summary>Rows taken from <c>track-metadata.json</c>.</summary>
    public int Lyrics { get; init; }
    public int LyricSections { get; init; }

    /// <summary>Rows skipped because their path/key was empty or malformed.</summary>
    public int Skipped { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// One-time, idempotent import of the 1.5.0 JSON state into SQLite:
/// <c>library.json</c> (tags/BPM), <c>playlists.json</c> (the playlist graph) and
/// <c>track-metadata.json</c> (lyrics offsets and sections).
///
/// <para><b>Guard</b>: a <c>schema_info</c> row (<see cref="GuardKey"/>) plus the
/// existence of each file. Once the row is written the import never runs again,
/// even if a JSON file is still on disk - which it always is, because the files
/// are deliberately left untouched as the backup. Delete the row (or the db) to
/// replay the import.</para>
///
/// <para><b>Tolerates the real 1.5.0 files</b>: 771 library entries, 5 272
/// playlist rows across four lists, three lyrics keys. Unknown/missing members
/// are skipped and counted rather than aborting the import; a row with no
/// artist (the "Dirty" YouTube list has none) imports with empty strings.</para>
/// </summary>
public sealed class LegacyImporter
{
    /// <summary><c>schema_info</c> key marking a completed import.</summary>
    public const string GuardKey = "legacy_import_v1";

    /// <summary>
    /// <c>schema_info</c> key holding the <see cref="PlaylistFileStamp"/> of the
    /// <c>library.json</c> this import mirrored. Written in the same run that
    /// reads the file, so the next start can tell an untouched cache from one
    /// the user (or another build) edited behind the app's back.
    /// </summary>
    public const string LibraryJsonStampKey = "library_json_stamp";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly JockeyBoxDatabase _db;
    private readonly TrackRepository _tracks;
    private readonly PlaylistRepository _playlists;

    public LegacyImporter(JockeyBoxDatabase db)
    {
        _db = db;
        _tracks = new TrackRepository(db);
        _playlists = new PlaylistRepository(db);
    }

    /// <summary>Paths of the legacy files for <paramref name="directory"/>.</summary>
    public static string LibraryJsonPath(string directory) => Path.Combine(directory, "library.json");
    public static string PlaylistsJsonPath(string directory) => Path.Combine(directory, "playlists.json");
    public static string MetadataJsonPath(string directory) => Path.Combine(directory, "track-metadata.json");

    /// <summary>True when the guard row says a previous run completed.</summary>
    public bool AlreadyImported() => _db.GetInfo(GuardKey) is { Length: > 0 };

    /// <summary>
    /// Import whatever legacy files sit in <paramref name="directory"/> (default:
    /// the database's own directory). Does nothing when the guard is set, unless
    /// <paramref name="force"/> is true.
    /// </summary>
    public LegacyImportReport Run(string? directory = null, bool force = false)
    {
        directory ??= _db.Directory;

        var libraryPath = LibraryJsonPath(directory);
        var playlistsPath = PlaylistsJsonPath(directory);
        var metadataPath = MetadataJsonPath(directory);

        var anyFile = File.Exists(libraryPath) || File.Exists(playlistsPath) || File.Exists(metadataPath);
        if (!anyFile) return new LegacyImportReport { Ran = false };

        if (!force && AlreadyImported())
        {
            return new LegacyImportReport { Ran = false };
        }

        var report = new LegacyImportReport { Ran = true };
        var skipped = 0;

        try
        {
            if (File.Exists(libraryPath))
            {
                // Stamp first: if the file is rewritten while it is being read,
                // the recorded stamp is the older one and the next start
                // reimports, which is the safe direction.
                var libraryStamp = PlaylistFileStamp.Of(libraryPath);
                var (tracks, skips) = ReadLibrary(libraryPath);
                skipped += skips;
                _tracks.UpsertMany(tracks, _tracks.BeginScan());
                _db.SetInfo(LibraryJsonStampKey, libraryStamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                report = report with { Tracks = tracks.Count, Skipped = skipped };
            }

            if (File.Exists(playlistsPath))
            {
                var (playlists, items, skippedItems) = ReadPlaylists(playlistsPath);
                skipped += skippedItems;
                // Record which file this graph mirrors in the same transaction, so
                // the next start can tell an untouched file from an external edit.
                if (playlists.Count > 0) _playlists.SaveAll(playlists, PlaylistFileStamp.Of(playlistsPath));
                report = report with
                {
                    Playlists = playlists.Count,
                    PlaylistItems = items,
                    Skipped = skipped,
                };
            }

            if (File.Exists(metadataPath))
            {
                var (lyrics, skippedLyrics) = ReadMetadata(metadataPath);
                skipped += skippedLyrics;
                WriteMetadata(lyrics);
                report = report with
                {
                    Lyrics = lyrics.Count,
                    LyricSections = lyrics.Sum(l => l.Sections.Count),
                    Skipped = skipped,
                };
            }

            _db.SetInfo(GuardKey, DateTimeOffset.UtcNow.ToString("O"));
            return report;
        }
        catch (Exception ex)
        {
            // No guard row: the next start retries. The JSON files are untouched,
            // so a retry replays from the same source.
            Tools.Log.Error("Legacy JSON import failed", ex);
            return report with { Error = ex.Message, Skipped = skipped };
        }
    }

    // ---- library.json ------------------------------------------------------

    private static (List<TrackRecord> Tracks, int Skipped) ReadLibrary(string path)
    {
        var tracks = new List<TrackRecord>(1024);
        var skipped = 0;

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Object) return (tracks, 1);

        var now = DateTime.UtcNow;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var filePath = entry.Name;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                skipped++;
                continue;
            }

            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            var mtimeTicks = GetInt64(value, "MTimeTicks");
            var duration = GetDouble(value, "DurationMs");

            tracks.Add(new TrackRecord
            {
                Path = LibraryKeys.NormalizePath(filePath),
                PathKey = LibraryKeys.PathKey(filePath),
                Title = GetString(value, "Title"),
                Artist = GetString(value, "Artist"),
                Album = GetString(value, "Album"),
                Genre = GetString(value, "Genre"),
                Bpm = GetDouble(value, "Bpm"),
                DurationMs = (long)Math.Round(duration),
                SizeBytes = GetInt64(value, "Size"),
                MTimeUtc = FromTicks(mtimeTicks),
                AddedUtc = now,
                LastSeenUtc = now,
                IsMissing = false,
            });
        }

        return (tracks, skipped);
    }

    /// <summary>File times were <see cref="DateTime.Ticks"/> in 1.5.0; a 0 means "unknown".</summary>
    private static DateTime FromTicks(long ticks)
    {
        if (ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return DateTime.MinValue;
        var value = new DateTime(ticks, DateTimeKind.Utc);
        return value.Year < 1970 ? DateTime.MinValue : value;
    }

    // ---- playlists.json ----------------------------------------------------

    private static (List<UserPlaylist> Playlists, int Items, int Skipped) ReadPlaylists(string path)
    {
        var playlists = new List<UserPlaylist>(8);
        var items = 0;
        var skipped = 0;

        LegacyPlaylistDocument? doc;
        using (var stream = File.OpenRead(path))
            doc = JsonSerializer.Deserialize<LegacyPlaylistDocument>(stream, JsonOpts);

        if (doc?.Playlists is null) return (playlists, 0, 1);

        var now = DateTime.UtcNow;

        foreach (var legacy in doc.Playlists)
        {
            if (legacy is null || string.IsNullOrWhiteSpace(legacy.Name)) { skipped++; continue; }

            var playlist = new UserPlaylist
            {
                Id = string.IsNullOrWhiteSpace(legacy.Id) ? Guid.NewGuid().ToString("N") : legacy.Id,
                Name = legacy.Name,
                Source = (MediaSource)legacy.Source,
                Kind = (PlaylistKind)legacy.Kind,
                SourceId = string.IsNullOrWhiteSpace(legacy.SourceId) ? null : legacy.SourceId,
                SyncEnabled = legacy.SyncEnabled,
                LastSyncedUtc = legacy.LastSyncedUtc.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(legacy.LastSyncedUtc, DateTimeKind.Utc)
                    : legacy.LastSyncedUtc.ToUniversalTime(),
            };

            foreach (var row in legacy.Tracks ?? new List<LegacyTrack>())
            {
                if (row is null || string.IsNullOrWhiteSpace(row.Id) && string.IsNullOrWhiteSpace(row.LocalKey))
                {
                    skipped++;
                    continue;
                }

                var localKey = SourceTrack.NormalizeLocalKey(row.LocalKey);
                var id = string.IsNullOrWhiteSpace(row.Id)
                    ? "manual:" + Guid.NewGuid().ToString("N")
                    : row.Id;

                playlist.Tracks.Add(new SourceTrack
                {
                    Id = id,
                    Source = (MediaSource)row.Source,
                    Title = row.Title ?? "",
                    Artist = row.Artist ?? "",
                    Album = row.Album ?? "",
                    Isrc = row.Isrc ?? "",
                    CoverUrl = row.CoverUrl ?? "",
                    DurationMs = row.DurationMs,
                    VideoId = string.IsNullOrWhiteSpace(row.VideoId) ? null : row.VideoId,
                    LibraryPath = string.IsNullOrWhiteSpace(row.LibraryPath) ? null : row.LibraryPath,
                    LocalKey = localKey.Length > 0 ? localKey : null,
                    PlaylistName = legacy.Name,
                });

                items++;
            }

            // Same inference PlaylistStore.NormalizeIdentity applies on load, so a
            // pre-v2 file (no Kind) lands in the same rail it always did.
            if (playlist.Kind != PlaylistKind.Internal
                && playlist.SourceId is null
                && playlist.Source == MediaSource.Manual
                && playlist.Tracks.Count > 0
                && playlist.Tracks.All(t => t.LocalKey is { Length: > 0 }))
            {
                playlist.Kind = PlaylistKind.Internal;
            }

            playlists.Add(playlist);
        }

        _ = now;
        return (playlists, items, skipped);
    }

    // ---- track-metadata.json -----------------------------------------------

    private static (List<LegacyLyrics> Lyrics, int Skipped) ReadMetadata(string path)
    {
        var rows = new List<LegacyLyrics>(4);
        var skipped = 0;

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Object) return (rows, 1);
        if (!doc.RootElement.TryGetProperty("tracks", out var list) || list.ValueKind != JsonValueKind.Array)
            return (rows, 1);

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) { skipped++; continue; }

            var key = GetString(entry, "key");
            if (key.Length == 0) { skipped++; continue; }

            var sections = new List<LegacySection>();
            if (entry.TryGetProperty("sections", out var sectionArray) && sectionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in sectionArray.EnumerateArray())
                {
                    if (section.ValueKind != JsonValueKind.Object) continue;
                    sections.Add(new LegacySection(
                        GetInt64(section, "startMs"),
                        GetInt64(section, "endMs"),
                        GetInt64(section, "offsetMs"),
                        GetString(section, "label") is { Length: > 0 } label ? label : null));
                }
            }

            rows.Add(new LegacyLyrics(
                key,
                GetString(entry, "title"),
                GetString(entry, "artist"),
                entry.TryGetProperty("lyricsOffsetMs", out var offset) && offset.ValueKind == JsonValueKind.Number
                    ? offset.GetDouble()
                    : null,
                GetString(entry, "lyricsSourceId"),
                entry.TryGetProperty("followMode", out var mode) && mode.ValueKind == JsonValueKind.String
                    ? mode.GetString()
                    : null,
                GetString(entry, "updatedUtc") is { Length: > 0 } updated && DateTime.TryParse(
                    updated, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var when)
                    ? when
                    : DateTime.MinValue,
                sections));
        }

        return (rows, skipped);
    }

    private void WriteMetadata(List<LegacyLyrics> rows)
    {
        if (rows.Count == 0) return;

        _db.WriteTransaction(conn =>
        {
            using var lyrics = conn.CreateCommand();
            lyrics.CommandText = """
                INSERT INTO track_lyrics (key, track_id, title, artist, lyrics_offset_ms, lyrics_source_id, follow_mode, updated_utc)
                VALUES (@key, @track_id, @title, @artist, @offset, @source_id, @follow, @updated)
                ON CONFLICT(key) DO UPDATE SET
                    title            = excluded.title,
                    artist           = excluded.artist,
                    lyrics_offset_ms = excluded.lyrics_offset_ms,
                    lyrics_source_id = excluded.lyrics_source_id,
                    follow_mode      = excluded.follow_mode,
                    updated_utc      = excluded.updated_utc;
                """;
            var keyParam = lyrics.Parameters.Add("@key", SqliteType.Text);
            var trackParam = lyrics.Parameters.Add("@track_id", SqliteType.Integer);
            lyrics.Parameters.Add("@title", SqliteType.Text);
            lyrics.Parameters.Add("@artist", SqliteType.Text);
            lyrics.Parameters.Add("@offset", SqliteType.Real);
            lyrics.Parameters.Add("@source_id", SqliteType.Text);
            lyrics.Parameters.Add("@follow", SqliteType.Text);
            lyrics.Parameters.Add("@updated", SqliteType.Integer);

            using var clearSections = conn.CreateCommand();
            clearSections.CommandText = "DELETE FROM lyric_sections WHERE key = @key;";
            var clearKey = clearSections.Parameters.Add("@key", SqliteType.Text);

            using var section = conn.CreateCommand();
            section.CommandText =
                "INSERT INTO lyric_sections (key, ordinal, start_ms, end_ms, offset_ms, label) " +
                "VALUES (@key, @ordinal, @start, @end, @offset, @label);";
            var sectionKey = section.Parameters.Add("@key", SqliteType.Text);
            section.Parameters.Add("@ordinal", SqliteType.Integer);
            section.Parameters.Add("@start", SqliteType.Integer);
            section.Parameters.Add("@end", SqliteType.Integer);
            section.Parameters.Add("@offset", SqliteType.Integer);
            section.Parameters.Add("@label", SqliteType.Text);

            foreach (var row in rows)
            {
                keyParam.Value = row.Key;
                trackParam.Value = (object?)ResolveTrackId(conn, row.Key) ?? DBNull.Value;
                lyrics.Parameters["@title"].Value = row.Title;
                lyrics.Parameters["@artist"].Value = row.Artist;
                lyrics.Parameters["@offset"].Value = (object?)row.LyricsOffsetMs ?? DBNull.Value;
                lyrics.Parameters["@source_id"].Value = row.LyricsSourceId;
                lyrics.Parameters["@follow"].Value = (object?)row.FollowMode ?? DBNull.Value;
                lyrics.Parameters["@updated"].Value = LibraryKeys.ToUnixMs(row.UpdatedUtc);
                lyrics.ExecuteNonQuery();

                clearKey.Value = row.Key;
                clearSections.ExecuteNonQuery();

                for (var i = 0; i < row.Sections.Count; i++)
                {
                    var s = row.Sections[i];
                    sectionKey.Value = row.Key;
                    section.Parameters["@ordinal"].Value = i;
                    section.Parameters["@start"].Value = s.StartMs;
                    section.Parameters["@end"].Value = s.EndMs;
                    section.Parameters["@offset"].Value = s.OffsetMs;
                    section.Parameters["@label"].Value = (object?)s.Label ?? DBNull.Value;
                    section.ExecuteNonQuery();
                }
            }

            return 0;
        });
    }

    /// <summary>
    /// Optional link from a lyrics key to a track row. A 1.5.0 key is the
    /// library-relative key when it names a file, but the real file contains
    /// opaque ids too ("xfkzrnyygfk"), so a miss is normal and the column stays null.
    /// </summary>
    private static long? ResolveTrackId(SqliteConnection conn, string key)
    {
        var normalized = TrackMetadataNormalize(key);
        if (normalized.Length == 0) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id FROM tracks WHERE path_key = @key OR path_key LIKE @suffix " +
            "ORDER BY LENGTH(path_key) LIMIT 1;";
        cmd.Parameters.AddWithValue("@key", normalized);
        cmd.Parameters.AddWithValue("@suffix", "%/" + normalized.Replace("%", "\\%").Replace("_", "\\_"));
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>Same normalisation as <c>TrackMetadataStore.NormalizeKey</c> (that type is not reusable here).</summary>
    private static string TrackMetadataNormalize(string? key) => SourceTrack.NormalizeLocalKey(key);

    // ---- JSON shapes (1.5.0, PascalCase playlists + camelCase metadata) -----

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static double GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : 0;

    private sealed class LegacyPlaylistDocument
    {
        public int Version { get; set; }
        public List<LegacyPlaylist>? Playlists { get; set; }
    }

    private sealed class LegacyPlaylist
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public int Source { get; set; }
        public int Kind { get; set; }
        public string? SourceId { get; set; }
        public bool SyncEnabled { get; set; }
        public DateTime LastSyncedUtc { get; set; }
        public List<LegacyTrack>? Tracks { get; set; }
    }

    private sealed class LegacyTrack
    {
        public string? Id { get; set; }
        public int Source { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public string? CoverUrl { get; set; }
        public double DurationMs { get; set; }
        public string? VideoId { get; set; }
        public string? PlaylistName { get; set; }
        public string? LocalKey { get; set; }
        public string? LibraryPath { get; set; }
    }

    private sealed record LegacyLyrics(
        string Key,
        string Title,
        string Artist,
        double? LyricsOffsetMs,
        string LyricsSourceId,
        string? FollowMode,
        DateTime UpdatedUtc,
        List<LegacySection> Sections);

    private readonly record struct LegacySection(long StartMs, long EndMs, long OffsetMs, string? Label);
}
