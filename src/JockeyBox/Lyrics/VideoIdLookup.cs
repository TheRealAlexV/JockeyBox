using System.IO;
using System.Text.Json;
using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// Resolves the YouTube video id of a library file from the download ledger, so
/// id-keyed providers (Unison) can be queried at all.
///
/// <c>Downloads/LibraryLedger</c> records <c>SourceKey</c> = YouTube video id for
/// imports, but only offers lookups by remote id and by artist/title - not by
/// path. Rather than widen that class (it belongs to the download pipeline),
/// this reads the same <c>%APPDATA%\JockeyBox\downloads\ledger.json</c> file
/// read-only and matches on path. Nothing here ever writes: a missing, locked
/// or corrupt ledger simply yields no id.
/// </summary>
internal static class VideoIdLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _byPath;
    private static DateTime _loadedUtc;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Video id for a track, or null when the track did not come from a YouTube
    /// import. Checks the music file first, then the karaoke file.
    /// </summary>
    public static string? ForTrack(TrackEntry track)
    {
        var map = Load();
        if (map is null || map.Count == 0) return null;

        return Find(map, track.MusicPath) ?? Find(map, track.KaraokePath);
    }

    private static string? Find(Dictionary<string, string> map, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // The ledger stores the absolute path written at download time; the
        // library may have been moved since, so fall back to the file name.
        if (map.TryGetValue(Normalize(path), out var byPath)) return byPath;

        var name = Path.GetFileName(Normalize(path));
        if (string.IsNullOrEmpty(name)) return null;
        return map.TryGetValue("name:" + name, out var byName) ? byName : null;
    }

    /// <summary>
    /// Read the ledger (cached for 30 s). Path keys are normalised; a file-name
    /// key is only added when it is unambiguous.
    /// </summary>
    private static Dictionary<string, string>? Load()
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (_byPath is not null && now - _loadedUtc < CacheFor) return _byPath;
            _loadedUtc = now;

            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "JockeyBox", "downloads", "ledger.json");

                if (!File.Exists(path))
                {
                    _byPath = null;
                    return _byPath;
                }

                var entries = JsonSerializer.Deserialize<List<LedgerRow>>(File.ReadAllText(path), JsonOptions);
                if (entries is null)
                {
                    _byPath = null;
                    return _byPath;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in entries)
                {
                    if (entry is null) continue;
                    if (!IsVideoId(entry.SourceKey)) continue;
                    if (string.IsNullOrWhiteSpace(entry.Path)) continue;

                    var normalised = Normalize(entry.Path);
                    map[normalised] = entry.SourceKey!;

                    var name = Path.GetFileName(normalised);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (names.TryGetValue(name, out var existing))
                    {
                        // Two entries produce the same file name: do not guess.
                        if (!string.Equals(existing, entry.SourceKey, StringComparison.OrdinalIgnoreCase))
                            names[name] = null;
                    }
                    else
                    {
                        names[name] = entry.SourceKey;
                    }
                }

                foreach (var pair in names)
                {
                    if (pair.Value is not null) map["name:" + pair.Key] = pair.Value;
                }

                _byPath = map;
                return map;
            }
            catch
            {
                _byPath = null;
                return null;
            }
        }
    }

    /// <summary>YouTube ids are exactly 11 chars of [A-Za-z0-9_-]; Spotify/original keys are not.</summary>
    internal static bool IsVideoId(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 11) return false;
        foreach (var c in key)
        {
            bool ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    private static string Normalize(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('.', '/');

    /// <summary>Ledger rows as written by <c>LibraryLedger</c> (PascalCase JSON).</summary>
    private sealed record LedgerRow(string? SourceKey, string? Path);
}
