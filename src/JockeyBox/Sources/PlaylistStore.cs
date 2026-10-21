using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JockeyBox.Sources;

/// <summary>
/// Persists the user's playlists (Spotify imports, YouTube imports and
/// hand-built lists) to %APPDATA%\JockeyBox\playlists.json.
///
/// Track state is stored alongside the membership so a relaunch shows the same
/// ticks: anything already in the library stays "in library" without touching
/// the network.
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

    private readonly object _lock = new();

    public ObservableCollection<UserPlaylist> Playlists { get; } = new();

    /// <summary>Raised after a mutation that changed the on-disk content.</summary>
    public event Action? Changed;

    public PlaylistStore()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var doc = JsonSerializer.Deserialize<PlaylistDocument>(File.ReadAllText(FilePath), JsonOpts);
            if (doc?.Playlists == null) return;

            Playlists.Clear();
            foreach (var playlist in doc.Playlists)
            {
                // Older files may predate the playlist name being copied onto
                // each track; fill it in so the playlist layout preset works.
                foreach (var track in playlist.Tracks)
                    track.PlaylistName ??= playlist.Name;
                Playlists.Add(playlist);
            }
        }
        catch (Exception ex)
        {
            Tools.Log.Warn($"playlists.json unreadable, starting empty: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            PlaylistDocument doc;
            lock (_lock)
                doc = new PlaylistDocument { Playlists = Playlists.ToList() };

            var json = JsonSerializer.Serialize(doc, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // atomic-ish replace
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Tools.Log.Error("Saving playlists.json failed", ex);
        }
    }

    public UserPlaylist Create(string name, MediaSource source = MediaSource.Manual, string? sourceId = null)
    {
        var playlist = new UserPlaylist
        {
            Name = name,
            Source = source,
            SourceId = sourceId,
            SyncEnabled = sourceId != null && source != MediaSource.Manual,
        };
        Playlists.Add(playlist);
        Save();
        return playlist;
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

    private sealed class PlaylistDocument
    {
        public int Version { get; set; } = 1;
        public List<UserPlaylist> Playlists { get; set; } = new();
    }
}
