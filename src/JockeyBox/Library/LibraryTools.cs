using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Sources;

namespace JockeyBox.Library;

/// <summary>
/// Library housekeeping that came over from the Python tool this app replaces:
/// duplicate detection, Exportify (Spotify) CSV import and a JSON export.
///
/// All three are read-only with respect to the music folder. Nothing here
/// renames, moves, writes over or deletes an audio file - the duplicate scan
/// only reports, and the import/export touch a CSV/JSON file the user picked.
///
/// Normalisation is <see cref="SourceMatcher.Normalize"/> plus the diacritic
/// fold used by <see cref="LibraryIndex"/>, so "Beyoncé" and "Beyonce" land on
/// the same key here as they do in the "already in the library?" lookup.
/// </summary>
public static class LibraryTools
{
    // ---- duplicates --------------------------------------------------------

    /// <summary>
    /// Group the library's music files into duplicate sets.
    ///
    /// Two independent indexes are built and both are reported:
    ///   * "artist+title" - normalised artist and title together, the strict
    ///     case (same tags, two files),
    ///   * "title" - the normalised title alone, which catches the same song
    ///     sitting under different artist spellings and cover/remix tags.
    ///
    /// Only <see cref="TrackEntry.MusicPath"/> participates. A karaoke file is
    /// the same song by design, so <see cref="TrackEntry.KaraokePath"/> is
    /// never a duplicate of anything and karaoke/music pairs cannot form a
    /// group. A group is only reported when it holds 2+ distinct files, and no
    /// file is listed twice: a title set that contains a strict pair plus more
    /// files replaces that pair, and a title set that is exactly a strict pair
    /// keeps the stricter "artist+title" label.
    /// </summary>
    public static DuplicateGroups FindDuplicates(
        IReadOnlyList<TrackEntry>? tracks, CancellationToken ct)
    {
        if (tracks is null) return DuplicateGroups.Empty;

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byArtistTitle = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var musicFiles = 0;

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (track is null) continue;

            // Music files only: karaoke counterparts are intentionally excluded.
            var path = track.MusicPath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!seenPaths.Add(path)) continue;

            var artistKey = Key(track.Artist);
            var titleKey = Key(track.Title);

            // Untagged files fall back to the file name, like the scanner does.
            if (titleKey.Length == 0)
                titleKey = Key(System.IO.Path.GetFileNameWithoutExtension(path));
            if (titleKey.Length == 0) continue;

            musicFiles++;
            var row = new Row(path, artistKey, titleKey, track.Artist, track.Title, track.Album);

            if (artistKey.Length > 0)
                Add(byArtistTitle, artistKey + "|" + titleKey, row);
            Add(byTitle, titleKey, row);
        }

        var strictGroups = new List<DuplicateGroup>();
        foreach (var entry in byArtistTitle.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (BuildGroup(entry.Key, "artist+title", entry.Value) is { } group)
                strictGroups.Add(group);
        }

        // Title-only sets. A title set that holds a strict pair plus more files
        // supersedes that pair (one 3-file set beats a pair next to an orphan),
        // while a title set that is exactly a strict pair is the same set and is
        // left to the stricter label.
        var groups = new List<DuplicateGroup>();
        var superseded = new HashSet<DuplicateGroup>();

        foreach (var entry in byTitle.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (BuildGroup(entry.Key, "title", entry.Value) is not { } group) continue;

            var paths = PathsOf(group);
            var inside = strictGroups.Where(s => paths.IsProperSupersetOf(PathsOf(s))).ToList();
            if (inside.Count == 0)
            {
                // Identical to a strict pair? Then it is already reported.
                if (strictGroups.Any(s => paths.SetEquals(PathsOf(s)))) continue;
                groups.Add(group);
                continue;
            }

            foreach (var s in inside) superseded.Add(s);
            groups.Add(group);
        }

        groups.AddRange(strictGroups.Where(s => !superseded.Contains(s)));

        // Most files first: the noisiest set is the one worth acting on.
        var ordered = groups
            .OrderByDescending(g => g.Files.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        return new DuplicateGroups(ordered, tracks.Count, musicFiles);
    }

    private static DuplicateGroup? BuildGroup(string key, string matchedBy, List<Row> rows)
    {
        var files = new List<DuplicateFile>(rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!seen.Add(row.Path)) continue;
            var (size, exists) = Stat(row.Path);
            files.Add(new DuplicateFile(row.Path, size, exists, row.Artist, row.Title, row.Album));
        }

        return files.Count < 2 ? null : new DuplicateGroup(key, matchedBy, files);
    }

    private static HashSet<string> PathsOf(DuplicateGroup group) =>
        new(group.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

    private static (long Size, bool Exists) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, true) : (0, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, false);
        }
    }

    private sealed record Row(string Path, string ArtistKey, string TitleKey,
        string Artist, string Title, string Album);

    // ---- Exportify CSV import ----------------------------------------------

    /// <summary>
    /// Read an Exportify CSV export of a Spotify playlist.
    ///
    /// The header is read, not assumed: recognised columns are
    /// "Track URI", "Track Name", "Artist Name(s)", "Album Name",
    /// "Duration (ms)" and "ISRC" (a few aliases are accepted, and any of them
    /// may be missing). Quoted fields with commas, doubled quotes, CRLF and a
    /// UTF-8 BOM are handled. Rows without a title are counted in
    /// <paramref name="skipped"/>; blank lines are ignored silently.
    ///
    /// Id is the track URI's last segment (the Spotify track id), or a
    /// deterministic hash of artist/title/album when the file has no URI, so
    /// re-importing the same CSV does not create new rows.
    /// </summary>
    public static List<SourceTrack> ImportExportifyCsv(string path, out int skipped)
    {
        skipped = 0;
        var result = new List<SourceTrack>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;

        var rows = ParseCsv(File.ReadAllText(path));
        if (rows.Count == 0) return result;

        var (headerIndex, columns) = FindHeader(rows);
        if (headerIndex < 0)
            throw new InvalidDataException(
                "No \"Track Name\" column found - this does not look like an Exportify CSV export.");

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var title = Cell(row, columns, "title").Trim();
            if (title.Length == 0)
            {
                skipped++;
                continue;
            }

            var artist = Cell(row, columns, "artist").Trim();
            var album = Cell(row, columns, "album").Trim();
            var uri = Cell(row, columns, "uri").Trim();

            result.Add(new SourceTrack
            {
                Id = IdFromUri(uri) ?? HashId(artist, title, album),
                Source = MediaSource.Spotify,
                Title = title,
                Artist = artist,
                Album = album,
                Isrc = Cell(row, columns, "isrc").Trim(),
                DurationMs = ParseMilliseconds(Cell(row, columns, "duration")),
            });
        }

        return result;
    }

    private static (int Index, Dictionary<string, int> Columns) FindHeader(List<string[]> rows)
    {
        var limit = Math.Min(rows.Count, 5);
        for (var i = 0; i < limit; i++)
        {
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            var cells = rows[i];
            for (var c = 0; c < cells.Length; c++)
            {
                var kind = ColumnKind(cells[c]);
                if (kind != null) columns.TryAdd(kind, c);
            }

            if (columns.ContainsKey("title")) return (i, columns);
        }

        return (-1, new Dictionary<string, int>(StringComparer.Ordinal));
    }

    /// <summary>Map a header cell onto a logical column, or null when it is unknown.</summary>
    private static string? ColumnKind(string header)
    {
        var h = header.Trim().Trim('\uFEFF').ToLowerInvariant();
        h = string.Join(' ', h.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return h switch
        {
            "track uri" or "spotify uri" or "spotify track uri" or "track url" or "uri" => "uri",
            "track name" or "song name" or "title" or "name" or "song" => "title",
            "artist name(s)" or "artist name" or "artist(s)" or "artists" or "artist" => "artist",
            "album name" or "albums" or "album(s)" or "album" => "album",
            "duration (ms)" or "duration ms" or "track duration (ms)" or "duration" => "duration",
            "isrc" => "isrc",
            _ => null,
        };
    }

    private static string Cell(string[] row, Dictionary<string, int> columns, string kind) =>
        columns.TryGetValue(kind, out var index) && index < row.Length ? row[index] : "";

    private static string? IdFromUri(string uri)
    {
        if (uri.Length == 0) return null;

        var query = uri.IndexOf('?');
        if (query >= 0) uri = uri[..query];
        uri = uri.TrimEnd('/');
        if (uri.Length == 0) return null;

        var separator = uri.LastIndexOfAny(new[] { ':', '/' });
        var id = separator >= 0 ? uri[(separator + 1)..] : uri;
        return id.Length == 0 ? null : id;
    }

    /// <summary>Stable local id for CSVs without a track URI.</summary>
    private static string HashId(string artist, string title, string album)
    {
        var material = $"{Key(artist)}|{Key(title)}|{Key(album)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "local-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static double ParseMilliseconds(string text)
    {
        if (text.Length == 0) return 0;

        var ms = 0.0;
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out ms))
        {
            var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
            if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out ms))
                return 0;
        }

        return ms is > 0 and <= 86_400_000 ? ms : 0;
    }

    /// <summary>
    /// Minimal RFC 4180 reader: quoted fields may hold commas, newlines and
    /// doubled quotes; CRLF and LF both end a record.
    /// </summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add(fields.ToArray());
                    fields.Clear();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }

    // ---- JSON export -------------------------------------------------------

    /// <summary>
    /// Write the whole library - tracks plus playlists - as one indented JSON
    /// document. The shape is a set of flat DTOs, not the internal observable
    /// classes, so later changes to <see cref="TrackEntry"/> cannot silently
    /// alter the file format.
    ///
    /// The write is atomic: the document goes to "&lt;path&gt;.tmp" and is
    /// moved over the target only once it is complete.
    /// </summary>
    /// <returns>Number of tracks written.</returns>
    public static async Task<int> ExportLibraryJsonAsync(
        string path,
        IReadOnlyList<TrackEntry> tracks,
        PlaylistStore store,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target path is required.", nameof(path));
        tracks ??= Array.Empty<TrackEntry>();

        var exportTracks = new List<ExportTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (track is null) continue;

            exportTracks.Add(new ExportTrack(
                RelativePath: track.RelativePath,
                MusicPath: track.MusicPath,
                KaraokePath: track.KaraokePath,
                Artist: track.Artist,
                Title: track.Title,
                Album: track.Album,
                DurationMs: track.Duration.TotalMilliseconds,
                Bpm: track.Bpm));
        }

        var exportPlaylists = new List<ExportPlaylist>();
        if (store is not null)
        {
            foreach (var playlist in store.Playlists)
            {
                ct.ThrowIfCancellationRequested();
                if (playlist is null) continue;

                var rows = new List<ExportPlaylistTrack>(playlist.Tracks.Count);
                foreach (var track in playlist.Tracks)
                {
                    if (track is null) continue;
                    rows.Add(new ExportPlaylistTrack(
                        Id: track.Id,
                        Source: track.Source.ToString(),
                        Artist: track.Artist,
                        Title: track.Title,
                        Album: track.Album,
                        Isrc: track.Isrc,
                        DurationMs: track.DurationMs,
                        State: track.State.ToString(),
                        LibraryPath: track.LibraryPath));
                }

                exportPlaylists.Add(new ExportPlaylist(
                    Id: playlist.Id,
                    Name: playlist.Name,
                    Source: playlist.Source.ToString(),
                    SourceId: playlist.SourceId,
                    SyncEnabled: playlist.SyncEnabled,
                    LastSyncedUtc: playlist.LastSyncedUtc,
                    Tracks: rows));
            }
        }

        var document = new ExportDocument(
            ExportedUtc: DateTime.UtcNow,
            TrackCount: exportTracks.Count,
            Tracks: exportTracks,
            Playlists: exportPlaylists);

        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tmp = full + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                             FileShare.None, 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOpts, ct);
                await stream.FlushAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }

        return exportTracks.Count;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- keys --------------------------------------------------------------

    /// <summary>
    /// Lookup key: <see cref="SourceMatcher.Normalize"/> plus the diacritic
    /// fold from <see cref="LibraryIndex"/> (kept local so the tool does not
    /// need the index instance).
    /// </summary>
    private static string Key(string? text)
    {
        var normalized = SourceMatcher.Normalize(text);
        if (normalized.Length == 0) return normalized;

        var ascii = true;
        foreach (var ch in normalized)
        {
            if (ch > 0x7F) { ascii = false; break; }
        }

        if (ascii) return normalized;

        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void Add(Dictionary<string, List<Row>> map, string key, Row row)
    {
        if (map.TryGetValue(key, out var list))
        {
            list.Add(row);
            return;
        }

        map[key] = new List<Row>(1) { row };
    }

    // ---- export shape ------------------------------------------------------

    /// <summary>Root of the exported document.</summary>
    public sealed record ExportDocument(
        DateTime ExportedUtc,
        int TrackCount,
        IReadOnlyList<ExportTrack> Tracks,
        IReadOnlyList<ExportPlaylist> Playlists);

    /// <summary>One library row in the export.</summary>
    public sealed record ExportTrack(
        string RelativePath,
        string? MusicPath,
        string? KaraokePath,
        string Artist,
        string Title,
        string Album,
        double DurationMs,
        double Bpm);

    /// <summary>One playlist in the export.</summary>
    public sealed record ExportPlaylist(
        string Id,
        string Name,
        string Source,
        string? SourceId,
        bool SyncEnabled,
        DateTime LastSyncedUtc,
        IReadOnlyList<ExportPlaylistTrack> Tracks);

    /// <summary>One playlist member in the export.</summary>
    public sealed record ExportPlaylistTrack(
        string Id,
        string Source,
        string Artist,
        string Title,
        string Album,
        string Isrc,
        double DurationMs,
        string State,
        string? LibraryPath);
}

/// <summary>Result of <see cref="LibraryTools.FindDuplicates"/>.</summary>
public sealed class DuplicateGroups
{
    public static DuplicateGroups Empty { get; } = new(Array.Empty<DuplicateGroup>(), 0, 0);

    public DuplicateGroups(IReadOnlyList<DuplicateGroup> groups, int trackRows, int musicFiles)
    {
        Groups = groups;
        TrackRows = trackRows;
        MusicFiles = musicFiles;
        DuplicateFileCount = groups.Sum(g => g.Files.Count);
        ReclaimableBytes = groups.Sum(g => g.TotalBytes - g.Files.Max(f => f.SizeBytes));
    }

    /// <summary>Duplicate sets, most files first.</summary>
    public IReadOnlyList<DuplicateGroup> Groups { get; }

    /// <summary>Rows handed to the scan (karaoke-only rows included).</summary>
    public int TrackRows { get; }

    /// <summary>Distinct music files scanned.</summary>
    public int MusicFiles { get; }

    /// <summary>Files that sit in at least one duplicate set.</summary>
    public int DuplicateFileCount { get; }

    /// <summary>Bytes recoverable by keeping one file per set (reporting only).</summary>
    public long ReclaimableBytes { get; }

    public bool IsEmpty => Groups.Count == 0;
}

/// <summary>
/// One duplicate set. <see cref="Key"/> is the normalised key that matched and
/// <see cref="MatchedBy"/> says which index produced it ("artist+title" or
/// "title").
/// </summary>
public sealed record DuplicateGroup(string Key, string MatchedBy, IReadOnlyList<DuplicateFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    public string DisplayName
    {
        get
        {
            var first = Files[0];
            return first.Artist.Length > 0 && first.Title.Length > 0
                ? $"{first.Artist} - {first.Title}"
                : System.IO.Path.GetFileNameWithoutExtension(first.Path);
        }
    }
}

/// <summary>One music file inside a duplicate set.</summary>
public sealed record DuplicateFile(
    string Path, long SizeBytes, bool Exists, string Artist, string Title, string Album)
{
    public string DisplayName => Artist.Length > 0 && Title.Length > 0
        ? $"{Artist} - {Title}"
        : System.IO.Path.GetFileNameWithoutExtension(Path);
}
