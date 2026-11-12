using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Tools;

namespace JockeyBox.Playback;

/// <summary>
/// The deck a queue row is routed to. Each deck is an independent player: a
/// track loads on one of them (primary file plus its karaoke sub-stem), so
/// routing a row picks which deck it plays (see <c>MainViewModel.LoadTrack</c>).
/// </summary>
public enum PlaybackDeck
{
    A,
    B,
}

public static class PlaybackDecks
{
    /// <summary>The other deck, for the A/B rotation.</summary>
    public static PlaybackDeck Opposite(this PlaybackDeck deck) =>
        deck == PlaybackDeck.A ? PlaybackDeck.B : PlaybackDeck.A;

    /// <summary>Persisted form: <c>"A"</c> / <c>"B"</c>.</summary>
    public static string ToPersistedString(this PlaybackDeck deck) =>
        deck == PlaybackDeck.A ? "A" : "B";

    /// <summary>Read a persisted deck name; unparsable values are not a deck.</summary>
    public static bool TryParse(string? value, out PlaybackDeck deck)
    {
        if (string.Equals(value, "A", StringComparison.OrdinalIgnoreCase))
        {
            deck = PlaybackDeck.A;
            return true;
        }

        if (string.Equals(value, "B", StringComparison.OrdinalIgnoreCase))
        {
            deck = PlaybackDeck.B;
            return true;
        }

        deck = PlaybackDeck.A;
        return false;
    }
}

/// <summary>
/// One persisted queue row. <see cref="Key"/> is a library
/// <c>TrackEntry.RelativeKey</c> for <c>"library"</c> rows and the remote
/// identity for <c>"remote"</c> rows (see <c>PlaybackQueueItem</c>).
///
/// <see cref="Deck"/> is <c>"A"</c>/<c>"B"</c>. Files written before the field
/// existed have no deck; <see cref="PlaybackQueueStore.Load"/> fills those in
/// (first row A, then alternating) so an old queue keeps a usable rotation.
/// </summary>
public sealed class PlaybackQueueRecord
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "library";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("played")] public bool Played { get; set; }
    [JsonPropertyName("deck")] public string? Deck { get; set; }

    /// <summary>Pair identity shared by both rows of a karaoke pair; null for a normal row.</summary>
    [JsonPropertyName("pair")] public string? Pair { get; set; }

    /// <summary>"karaoke" / "original" inside a pair; null for a normal row.</summary>
    [JsonPropertyName("role")] public string? Role { get; set; }
}

/// <summary>
/// Persists the playback queue to %APPDATA%\JockeyBox\playback-queue.json.
///
/// The queue is a convenience, not library state: a malformed or missing file
/// must never stop the app from starting, so every read is wrapped and a bad
/// file is simply treated as an empty queue. Writes are debounced (~500 ms)
/// because play state changes on every track end, and flushed on dispose.
/// </summary>
public sealed class PlaybackQueueStore : IDisposable
{
    /// <summary>Upper bound for a restored file; anything larger is a bug, not a queue.</summary>
    private const int MaxRecords = 5000;

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string AppDataDir => Path.Combine(AppPaths.Home);

    public static string FilePath => Path.Combine(AppDataDir, "playback-queue.json");

    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private List<PlaybackQueueRecord>? _pending;
    private bool _disposed;

    public PlaybackQueueStore()
    {
        _saveTimer = new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Read the persisted rows; never throws, never returns null.</summary>
    public IReadOnlyList<PlaybackQueueRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<PlaybackQueueRecord>();

            var records = JsonSerializer.Deserialize<List<PlaybackQueueRecord>>(
                File.ReadAllText(FilePath), JsonOpts);
            if (records == null) return Array.Empty<PlaybackQueueRecord>();

            var result = new List<PlaybackQueueRecord>(Math.Min(records.Count, MaxRecords));

            // Deck routing: a file written before the field existed has no deck.
            // Those rows get A first and then alternate, so the restore is not one
            // deck's queue with the other's hole in it.
            var lastDeck = PlaybackDeck.B;

            foreach (var record in records)
            {
                if (record == null) continue;
                if (string.IsNullOrWhiteSpace(record.Key)) continue;
                if (!IsKnownKind(record.Kind)) continue;

                if (PlaybackDecks.TryParse(record.Deck, out var deck))
                {
                    lastDeck = deck;
                }
                else
                {
                    lastDeck = lastDeck.Opposite();
                    record.Deck = lastDeck.ToPersistedString();
                }

                result.Add(record);
                if (result.Count >= MaxRecords) break;
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"playback-queue.json unreadable, starting with an empty queue: {ex.Message}");
            return Array.Empty<PlaybackQueueRecord>();
        }
    }

    /// <summary>Queue a write; several calls inside the delay collapse into one.</summary>
    public void Save(IEnumerable<PlaybackQueueRecord> records)
    {
        var collection = records as ICollection<PlaybackQueueRecord>;
        var snapshot = collection != null
            ? new List<PlaybackQueueRecord>(collection)
            : records.ToList();

        lock (_gate)
        {
            if (_disposed) return;
            _pending = snapshot;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Write now (startup/shutdown paths where the delay is pointless).</summary>
    public void SaveNow(IEnumerable<PlaybackQueueRecord> records)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = records.ToList();
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        FlushPending();
    }

    /// <summary>
    /// Rewrite persisted <c>"library"</c> rows whose key was the old
    /// <c>TrackEntry.RelativeKey</c>, keeping their played flag. Writes once,
    /// immediately (the on-disk mirror is not the live queue). Returns how many
    /// rows changed; nothing is written when none did.
    /// </summary>
    public int RepointLibraryKey(string? oldKey, string? newKey)
    {
        var from = (oldKey ?? "").Trim();
        var to = (newKey ?? "").Trim();
        if (from.Length == 0 || to.Length == 0
            || string.Equals(from, to, StringComparison.Ordinal))
            return 0;

        var records = Load().ToList();
        var changed = 0;
        foreach (var record in records)
        {
            if (!string.Equals(record.Kind, "library", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(record.Key, from, StringComparison.OrdinalIgnoreCase)) continue;

            record.Key = to;
            changed++;
        }

        if (changed > 0) SaveNow(records);
        return changed;
    }

    private static bool IsKnownKind(string? kind) =>
        string.Equals(kind, "library", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "remote", StringComparison.OrdinalIgnoreCase);

    private void FlushPending()
    {
        List<PlaybackQueueRecord>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending == null) return;
        Write(pending);
    }

    private static void Write(List<PlaybackQueueRecord> records)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(records, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the playback queue: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        FlushPending();
        _saveTimer.Dispose();
    }
}
