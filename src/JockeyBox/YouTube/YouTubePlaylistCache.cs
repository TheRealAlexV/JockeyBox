using System.IO;
using System.Text.Json;
using JockeyBox.Tools;

namespace JockeyBox.YouTube;

/// <summary>
/// The last-known state of the signed-in channel's playlists.
///
/// Quota is the reason this exists: <c>playlists.list?mine=true</c> and
/// <c>playlistItems.list</c> are 1 unit each but the daily allowance is 10,000
/// and a write is 50, so a tab switch must never re-fetch. Two layers:
///
///  * memory, with a short TTL - <see cref="ListTtl"/> for the playlist list,
///    <see cref="ItemsTtl"/> for one playlist's items. Anything younger is
///    served from here and costs no quota at all;
///  * %APPDATA%\JockeyBox\youtube-playlists-cache.json, written after every
///    successful fetch. <see cref="Load"/> reads it at startup so the rail is
///    populated before the network call returns; the strip shows the file's
///    timestamp dimmed, so stale data is visibly stale rather than blocking.
///
/// A manual refresh (<c>↻</c>) bypasses both by passing <c>force: true</c> to the
/// view model's loader; the cache itself only ever answers "fresh" or "not".
/// </summary>
public sealed class YouTubePlaylistCache
{
    /// <summary>How long <c>mine=true</c> is considered current.</summary>
    public static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(5);

    /// <summary>How long one playlist's items are considered current.</summary>
    public static readonly TimeSpan ItemsTtl = TimeSpan.FromMinutes(2);

    private const int SchemaVersion = 1;

    /// <summary>How many playlists' item lists are kept on disk (memory keeps all).</summary>
    private const int MaxPersistedItemLists = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The channel's playlists as last fetched, in YouTube's order.</summary>
    public sealed class CachedList
    {
        public string ChannelId { get; set; } = "";
        public string ChannelTitle { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public List<YouTubePlaylist> Playlists { get; set; } = new();
    }

    /// <summary>One playlist's items as last fetched, in YouTube's order.</summary>
    public sealed class CachedItems
    {
        public string PlaylistId { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public List<YouTubePlaylistItem> Items { get; set; } = new();
    }

    /// <summary>On-disk shape; separate from the two classes above so the file notes its version.</summary>
    private sealed class CacheDocument
    {
        public int Version { get; set; } = SchemaVersion;
        public CachedList? List { get; set; }
        public List<CachedItems> Items { get; set; } = new();
    }

    private readonly object _lock = new();
    private CachedList? _list;
    private readonly Dictionary<string, CachedItems> _items = new(StringComparer.Ordinal);

    /// <summary>%APPDATA%\JockeyBox\youtube-playlists-cache.json</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JockeyBox", "youtube-playlists-cache.json");

    /// <summary>The cached list, or null before the first successful fetch.</summary>
    public CachedList? List
    {
        get { lock (_lock) return _list; }
    }

    /// <summary>When the cached list was fetched (null: never).</summary>
    public DateTime? ListFetchedUtc => List?.FetchedUtc;

    /// <summary>
    /// True while the cached list may be served without a request. An empty
    /// result is cached too: "the channel has no playlists" is an answer, and
    /// re-asking on every tab switch would be exactly the quota waste this
    /// class exists to avoid.
    /// </summary>
    public bool IsListFresh
    {
        get
        {
            var list = List;
            return list != null && DateTime.UtcNow - list.FetchedUtc < ListTtl;
        }
    }

    public bool HasList => List != null;

    /// <summary>Record a successful <c>mine=true</c> fetch (memory + disk).</summary>
    public void SetList(IReadOnlyList<YouTubePlaylist> playlists,
                        string channelId, string channelTitle, DateTime fetchedUtc)
    {
        lock (_lock)
        {
            _list = new CachedList
            {
                ChannelId = channelId ?? "",
                ChannelTitle = channelTitle ?? "",
                FetchedUtc = fetchedUtc,
                Playlists = playlists.Select(Clone).ToList(),
            };
        }
        Save();
    }

    /// <summary>
    /// One playlist's items if they are younger than <see cref="ItemsTtl"/>.
    /// <paramref name="fetchedUtc"/> is the age of what came back, for the
    /// strip's "cached N min ago" wording; when nothing is cached it is default.
    /// </summary>
    public IReadOnlyList<YouTubePlaylistItem>? GetItems(string playlistId, out DateTime fetchedUtc)
    {
        fetchedUtc = default;
        if (string.IsNullOrWhiteSpace(playlistId)) return null;
        lock (_lock)
        {
            if (!_items.TryGetValue(playlistId, out var entry)) return null;
            fetchedUtc = entry.FetchedUtc;
            return DateTime.UtcNow - entry.FetchedUtc < ItemsTtl ? entry.Items : null;
        }
    }

    /// <summary>Items as of the last fetch, whatever their age (for the instant paint).</summary>
    public IReadOnlyList<YouTubePlaylistItem>? GetAnyItems(string playlistId, out DateTime fetchedUtc)
    {
        fetchedUtc = default;
        if (string.IsNullOrWhiteSpace(playlistId)) return null;
        lock (_lock)
        {
            if (!_items.TryGetValue(playlistId, out var entry)) return null;
            fetchedUtc = entry.FetchedUtc;
            return entry.Items;
        }
    }

    public bool IsItemsFresh(string playlistId, out DateTime fetchedUtc) =>
        GetItems(playlistId, out fetchedUtc) != null;

    /// <summary>Record a successful <c>playlistItems.list</c> for one playlist.</summary>
    public void SetItems(string playlistId, IReadOnlyList<YouTubePlaylistItem> items, DateTime fetchedUtc)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return;
        lock (_lock)
        {
            _items[playlistId] = new CachedItems
            {
                PlaylistId = playlistId,
                FetchedUtc = fetchedUtc,
                Items = items.Select(Clone).ToList(),
            };
        }
        SetItemCount(playlistId, items.Count);
    }

    /// <summary>
    /// Rewrite one playlist's item count in the cached list, so a +Remote
    /// (0 items) or an add/remove shows immediately after a restart without a
    /// refetch - the list itself is not re-ordered or re-fetched.
    /// </summary>
    public void SetItemCount(string playlistId, int count)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return;
        lock (_lock)
        {
            var list = _list;
            if (list == null) return;
            var match = list.Playlists.FirstOrDefault(p => p.Id == playlistId);
            if (match == null)
            {
                // A playlist created here is not in the cached page yet: add it
                // at the top, which is where YouTube puts a new playlist too.
                list.Playlists.Insert(0, new YouTubePlaylist { Id = playlistId, Title = "", ItemCount = count });
            }
            else
            {
                match.ItemCount = count;
            }
        }
    }

    /// <summary>Insert a just-created playlist into the cached list (no refetch).</summary>
    public void InsertPlaylist(YouTubePlaylist playlist)
    {
        lock (_lock)
        {
            if (_list == null) return;
            _list.Playlists.RemoveAll(p => p.Id == playlist.Id);
            _list.Playlists.Insert(0, Clone(playlist));
        }
    }

    /// <summary>Forget the list and every item list (channel changed / signed out).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _list = null;
            _items.Clear();
        }
        Save();
    }

    /// <summary>Write the whole cache to <see cref="FilePath"/>. Never throws.</summary>
    public void Save()
    {
        try
        {
            CacheDocument doc;
            lock (_lock)
            {
                doc = new CacheDocument
                {
                    List = _list,
                    // Only the most recently read item lists travel to disk: the
                    // list itself is what makes startup instant, and this keeps the
                    // file (rewritten after every add/remove) small.
                    Items = _items.Values
                        .OrderByDescending(i => i.FetchedUtc)
                        .Take(MaxPersistedItemLists)
                        .ToList(),
                };
            }

            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write-then-replace so a crash mid-write cannot leave a half file
            // that the next startup would fail to parse.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube: could not write the playlist cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Read <see cref="FilePath"/>; a missing or unparsable file is an empty
    /// cache, never an error - the first fetch fills it in.
    /// </summary>
    public static YouTubePlaylistCache Load()
    {
        var cache = new YouTubePlaylistCache();
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return cache;

            var doc = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), JsonOptions);
            if (doc == null) return cache;

            lock (cache._lock)
            {
                cache._list = doc.List;
                foreach (var entry in doc.Items ?? new List<CachedItems>())
                {
                    if (string.IsNullOrWhiteSpace(entry.PlaylistId)) continue;
                    cache._items[entry.PlaylistId] = entry;
                }
            }

            Log.Debug($"YouTube: loaded cached playlist list " +
                      $"({cache._list?.Playlists.Count ?? 0} playlist(s), " +
                      $"{cache._items.Count} item list(s), fetched {cache._list?.FetchedUtc:u}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube: could not read the playlist cache: {ex.Message}");
        }
        return cache;
    }

    private static YouTubePlaylist Clone(YouTubePlaylist playlist) => new()
    {
        Id = playlist.Id,
        Title = playlist.Title,
        Description = playlist.Description,
        PrivacyStatus = playlist.PrivacyStatus,
        ItemCount = playlist.ItemCount,
    };

    private static YouTubePlaylistItem Clone(YouTubePlaylistItem item) => new()
    {
        ItemId = item.ItemId,
        VideoId = item.VideoId,
        Title = item.Title,
        ChannelTitle = item.ChannelTitle,
        Position = item.Position,
        DurationSeconds = item.DurationSeconds,
    };
}
