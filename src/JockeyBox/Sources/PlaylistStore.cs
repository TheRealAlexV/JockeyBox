using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Library;

namespace JockeyBox.Sources;

/// <summary>
/// Persists the user's playlists (Spotify imports, YouTube imports and
/// app-owned library lists) to %APPDATA%\JockeyBox\playlists.json.
///
/// Track membership and metadata are stored; track *state* is not (see
/// <see cref="SourceTrack.State"/>) - it is re-derived from the library on load,
/// so a crashed download or a preview can never come back as a stuck row.
///
/// App-owned (<see cref="PlaylistKind.Internal"/>) membership is the library key
/// (<see cref="SourceTrack.LocalKey"/>), never a remote id; imported membership
/// stays the remote id.
/// </summary>
public class PlaylistStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");

    public static string FilePath => Path.Combine(AppDataDir, "playlists.json");

    /// <summary>Copy of the pre-migration file, written once before the first migration save.</summary>
    public static string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Schema version written on every save. A file without a version (or with a
    /// lower one) is migrated on load: identities are normalised, the pre-migration
    /// copy is kept at <see cref="BackupPath"/>, then the new shape is written.
    /// </summary>
    private const int CurrentVersion = 2;

    private readonly object _lock = new();

    public ObservableCollection<UserPlaylist> Playlists { get; } = new();

    /// <summary>Raised after a mutation that changed the on-disk content.</summary>
    public event Action? Changed;

    /// <summary>
    /// Incremented by every <see cref="Save"/>. Anything derived from the playlist
    /// file (see <see cref="PlaylistMembershipCache"/>) stamps itself with this so
    /// an add/remove/rename can never be served a pre-edit answer. In-session only;
    /// pair it with <see cref="FileStamp"/> for values that outlive the process.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Durable identity of playlists.json: last-write ticks mixed with its length.
    /// Changes on any save, including one made by an earlier session, so a cache
    /// entry from the last run is not trusted after the file was edited.
    /// </summary>
    public long FileStamp { get; private set; }

    /// <summary>
    /// Optional host hook: given a track id, return the library file that exists
    /// for it (or null). <c>MainViewModel</c> points this at the live download
    /// ledger after it constructs one; without it the store falls back to a
    /// read-only snapshot of ledger.json.
    /// </summary>
    public Func<string, string?>? LibraryPathResolver { get; set; }

    public PlaylistStore()
    {
        Load();
        FileStamp = ProbeFileStamp();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var doc = JsonSerializer.Deserialize<PlaylistDocument>(File.ReadAllText(FilePath), JsonOpts);
            if (doc?.Playlists == null) return;

            var migrated = doc.Version < CurrentVersion;

            Playlists.Clear();
            foreach (var playlist in doc.Playlists)
            {
                if (playlist is null) continue;

                playlist.Tracks ??= new ObservableCollection<SourceTrack>();

                var wasInternal = playlist.Kind == PlaylistKind.Internal;
                var hadRemoteId = playlist.SourceId != null;
                playlist.NormalizeIdentity();
                if (wasInternal != playlist.IsInternal || hadRemoteId != (playlist.SourceId != null))
                    migrated = true;

                // Older files may predate the playlist name being copied onto
                // each track; fill it in so the playlist layout preset works.
                foreach (var track in playlist.Tracks)
                {
                    track.PlaylistName ??= playlist.Name;
                    SanitizeState(track);
                }
                Playlists.Add(playlist);
            }

            if (migrated) SaveAfterBackup();
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"playlists.json unreadable, starting empty: {ex.Message}");
        }
    }

    /// <summary>
    /// First migration write: keep the pre-migration file beside it as
    /// <c>playlists.json.bak</c>. An existing backup is never overwritten - it is
    /// the oldest, i.e. the one that still has the data a later bug could lose.
    /// </summary>
    private void SaveAfterBackup()
    {
        try
        {
            if (File.Exists(FilePath) && !File.Exists(BackupPath))
            {
                File.Copy(FilePath, BackupPath, overwrite: false);
                Tools.Log.Info($"playlists.json migrated; original kept at {BackupPath}");
            }
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"Could not back up playlists.json before migrating: {ex.Message}");
        }

        Save();
    }

    /// <summary>
    /// Re-derive every track's state from the library. Call after wiring
    /// <see cref="LibraryPathResolver"/> so an already-loaded list picks up the
    /// live ledger (MainViewModel does this once at startup).
    /// </summary>
    public void SanitizeStates()
    {
        foreach (var playlist in Playlists)
        {
            foreach (var track in playlist.Tracks) SanitizeState(track);
        }
    }

    /// <summary>
    /// Replace whatever was loaded with a state the library can actually prove:
    /// "in library" when a live file exists on disk, otherwise "New". Transient
    /// states (Queued/Downloading) are never kept - they are what turns a
    /// previewed track into a row stuck at "⋯ 0%" across restarts.
    ///
    /// Imported membership is resolved by remote id through the ledger. Internal
    /// membership is *never* nulled here: its row is the app's own record of a
    /// library file, so a missing ledger line (manual, pre-provenance, re-encoded)
    /// must not erase it - the browser resolves it later by
    /// <see cref="SourceTrack.LocalKey"/>, then by path, then by artist/title.
    /// </summary>
    private void SanitizeState(SourceTrack track)
    {
        if (track is null) return;

        if (track.Id.StartsWith("manual:", StringComparison.Ordinal))
        {
            track.Progress = 0;
            track.Error = null;
            track.State = SourceTrackState.InLibrary;
            return;
        }

        var path = ResolveLibraryPath(track);
        if (!string.IsNullOrWhiteSpace(path))
        {
            track.LibraryPath = path;
            track.State = SourceTrackState.InLibrary;
            track.Progress = 0;
            track.Error = null;
            return;
        }

        track.Progress = 0;
        if (track.State is SourceTrackState.Queued
            or SourceTrackState.Downloading
            or SourceTrackState.InLibrary)
        {
            track.LibraryPath = null;
            track.State = SourceTrackState.New;
        }
    }

    private string? ResolveLibraryPath(SourceTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.Id)) return null;

        try
        {
            var resolve = LibraryPathResolver ??= CreateLedgerLookup();
            return resolve(track.Id.Trim());
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"Library lookup for '{track.Id}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Default resolver: a one-off snapshot of the download ledger. Only used by
    /// stores created before the host wires <see cref="LibraryPathResolver"/>
    /// (and by the Settings window's throwaway store).
    /// </summary>
    private static Func<string, string?> CreateLedgerLookup()
    {
        var ledger = new Downloads.LibraryLedger();

        return id =>
        {
            var entry = ledger.TryGet(id);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) return null;

            try
            {
                return File.Exists(entry.Path) ? entry.Path : null;
            }
            catch (Exception ex)
            {
                Tools.Log.Warn($"Could not probe ledger path '{entry.Path}': {ex.Message}");
                return null;
            }
        };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            PlaylistDocument doc;
            lock (_lock)
                doc = new PlaylistDocument { Version = CurrentVersion, Playlists = Playlists.ToList() };

            var json = JsonSerializer.Serialize(doc, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // atomic-ish replace

            // Publish the identity of what is now on disk *before* telling the
            // host, so a Changed handler that consults the membership cache sees
            // the new stamps and misses (rather than serving the pre-edit answer).
            Revision++;
            FileStamp = ProbeFileStamp();

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Tools.Log.Error("Saving playlists.json failed", ex);
        }
    }

    /// <summary>
    /// Ticks+XOR-length of playlists.json, or 0 when it cannot be read. Stable
    /// across processes (unlike <see cref="HashCode"/>, which is seeded per run).
    /// </summary>
    private static long ProbeFileStamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length * 2654435761L) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Create a playlist. A source id makes it an imported mirror.</summary>
    public UserPlaylist Create(string name, MediaSource source = MediaSource.Manual, string? sourceId = null)
    {
        var playlist = new UserPlaylist
        {
            Name = name,
            Source = source,
            SourceId = sourceId,
            SyncEnabled = sourceId != null && source != MediaSource.Manual,
        };
        playlist.NormalizeIdentity();
        Playlists.Add(playlist);
        Save();
        return playlist;
    }

    /// <summary>Create an app-owned playlist (LIBRARY rail's "+ New").</summary>
    public UserPlaylist CreateInternal(string name)
    {
        var playlist = new UserPlaylist
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New playlist" : name.Trim(),
            Source = MediaSource.Manual,
            Kind = PlaylistKind.Internal,
            SourceId = null,
            SyncEnabled = false,
        };
        Playlists.Add(playlist);
        Save();
        return playlist;
    }

    public bool Rename(UserPlaylist playlist, string name)
    {
        if (!playlist.IsInternal) return false;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;
        if (string.Equals(playlist.Name, trimmed, StringComparison.Ordinal)) return true;

        playlist.Name = trimmed;
        foreach (var track in playlist.Tracks) track.PlaylistName = trimmed;
        Save();
        return true;
    }

    /// <summary>Delete an app-owned playlist. Imported mirrors are removed by their own tab.</summary>
    public bool Delete(UserPlaylist playlist)
    {
        if (!playlist.IsInternal) return false;
        if (!Playlists.Remove(playlist)) return false;
        Save();
        return true;
    }

    public void Remove(UserPlaylist playlist)
    {
        if (!Playlists.Remove(playlist)) return;
        Save();
    }

    public UserPlaylist? FindBySourceId(MediaSource source, string sourceId) =>
        Playlists.FirstOrDefault(p => p.Source == source && p.SourceId == sourceId);

    public UserPlaylist? FindByName(string name) =>
        Playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Add tracks to a playlist, skipping any whose remote id is already
    /// present. Returns how many were actually added.
    /// </summary>
    public int AddTracks(UserPlaylist playlist, IEnumerable<SourceTrack> tracks)
    {
        // App-owned membership is the library key; a remote id would never resolve.
        if (playlist.IsInternal) return 0;

        var existing = new HashSet<string>(playlist.Tracks.Select(t => t.Id), StringComparer.Ordinal);
        var added = 0;
        foreach (var track in tracks)
        {
            if (!existing.Add(track.Id)) continue;
            track.PlaylistName = playlist.Name;
            playlist.Tracks.Add(track);
            added++;
        }

        if (added > 0)
        {
            playlist.NotifyTrackCountChanged();
            Save();
        }
        return added;
    }

    /// <summary>
    /// Add library files to an app-owned playlist. Membership is the library key
    /// (<see cref="TrackEntry.RelativeKey"/>), extension-stripped, so the same file
    /// is never added twice and a later rename can still be resolved by metadata.
    /// Returns how many rows were actually added.
    /// </summary>
    public int AddLibraryTracks(UserPlaylist playlist, IEnumerable<TrackEntry> tracks)
    {
        if (!playlist.IsInternal) return 0;

        var existing = new HashSet<string>(
            playlist.Tracks.Select(t => t.LocalKey).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var entry in tracks)
        {
            var key = SourceTrack.NormalizeLocalKey(entry?.RelativeKey);
            if (key.Length == 0 || !existing.Add(key)) continue;

            playlist.Tracks.Add(new SourceTrack
            {
                Id = "manual:" + Guid.NewGuid().ToString("N"),
                Source = MediaSource.Manual,
                Title = entry!.Title,
                Artist = entry.Artist,
                Album = entry.Album,
                DurationMs = entry.Duration.TotalMilliseconds,
                LocalKey = key,
                LibraryPath = entry.MusicPath,
                PlaylistName = playlist.Name,
                State = SourceTrackState.InLibrary,
            });
            added++;
        }

        if (added > 0)
        {
            playlist.NotifyTrackCountChanged();
            Save();
        }
        return added;
    }

    /// <summary>
    /// Remove library files from an app-owned playlist by key (or, when the row
    /// has no key, by artist/title). Returns how many rows were removed.
    /// </summary>
    public int RemoveLibraryTracks(UserPlaylist playlist, IEnumerable<TrackEntry> tracks)
    {
        if (!playlist.IsInternal) return 0;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in tracks)
        {
            if (entry is null) continue;
            keys.Add(SourceTrack.NormalizeLocalKey(entry.RelativeKey));
            metadata.Add(RowMetadataKey(entry.Artist, entry.Title));
        }

        var doomed = playlist.Tracks.Where(t =>
                (t.LocalKey != null && keys.Contains(t.LocalKey))
                || (t.LocalKey is null && metadata.Contains(RowMetadataKey(t.Artist, t.Title))))
            .ToList();

        if (doomed.Count == 0) return 0;

        foreach (var row in doomed) playlist.Tracks.Remove(row);
        playlist.NotifyTrackCountChanged();
        Save();
        return doomed.Count;
    }

    /// <summary>
    /// Repoint playlist rows after a library file was renamed or moved.
    ///
    /// App-owned rows are identified by the library key
    /// (<see cref="SourceTrack.LocalKey"/>): their key becomes
    /// <paramref name="newKey"/> and their cached <see cref="SourceTrack.LibraryPath"/>
    /// becomes <paramref name="newPath"/>, whether or not a path was cached
    /// before. Imported rows keep their remote <see cref="SourceTrack.Id"/>; only
    /// a cached <see cref="SourceTrack.LibraryPath"/> that pointed at the old file
    /// is repointed.
    ///
    /// Idempotent: rows already naming the new key/path are skipped, and nothing
    /// is written when no row changed. Returns how many rows were updated.
    /// </summary>
    public int RepointLibraryFile(string? oldKey, string? newKey, string? oldPath, string? newPath)
    {
        var fromKey = SourceTrack.NormalizeLocalKey(oldKey);
        var toKey = SourceTrack.NormalizeLocalKey(newKey);
        var keyChanged = fromKey.Length > 0 && toKey.Length > 0
                         && !string.Equals(fromKey, toKey, StringComparison.Ordinal);
        var newFull = (newPath ?? "").Trim();
        var pathChanged = newFull.Length > 0 && !string.IsNullOrWhiteSpace(oldPath)
                          && !string.Equals(oldPath!.Trim(), newFull, StringComparison.OrdinalIgnoreCase);

        if (!keyChanged && !pathChanged) return 0;

        var changed = 0;
        foreach (var playlist in Playlists)
        {
            foreach (var track in playlist.Tracks)
            {
                var touched = false;

                if (keyChanged && string.Equals(track.LocalKey, fromKey, StringComparison.OrdinalIgnoreCase))
                {
                    track.LocalKey = toKey;
                    touched = true;
                }

                // Internal rows follow their key even when they never cached a
                // path; imported rows are matched by the path they had cached.
                if (pathChanged
                    && (touched || PathMatches(track.LibraryPath, oldPath!)))
                {
                    track.LibraryPath = newFull;
                    touched = true;
                }

                if (!touched) continue;

                track.State = SourceTrackState.InLibrary;
                track.Progress = 0;
                track.Error = null;
                changed++;
            }
        }

        if (changed > 0) Save();
        return changed;
    }

    /// <summary>Case-insensitive path comparison, tolerating relative vs absolute forms.</summary>
    private static bool PathMatches(string? candidate, string wanted)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var trimmed = candidate.Trim();
        if (string.Equals(trimmed, wanted.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            return string.Equals(
                Path.GetFullPath(trimmed), Path.GetFullPath(wanted.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string RowMetadataKey(string? artist, string? title) =>
        SourceTrack.NormalizeLocalKey(artist + "\u241F" + title);

    /// <summary>Reorder a row inside an app-owned playlist (atomic save).</summary>
    public bool MoveTrack(UserPlaylist playlist, int fromIndex, int toIndex)
    {
        if (!playlist.IsInternal) return false;
        if (fromIndex < 0 || fromIndex >= playlist.Tracks.Count) return false;
        if (toIndex < 0 || toIndex >= playlist.Tracks.Count) return false;
        if (fromIndex == toIndex) return true;

        playlist.Tracks.Move(fromIndex, toIndex);
        Save();
        return true;
    }

    private sealed class PlaylistDocument
    {
        /// <summary>Schema version: 2 adds <see cref="UserPlaylist.Kind"/> and local library keys.</summary>
        public int Version { get; set; } = CurrentVersion;
        public List<UserPlaylist> Playlists { get; set; } = new();
    }
}
