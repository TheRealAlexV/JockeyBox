using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JockeyBox.Data;

/// <summary>
/// One saved hot cue as the database sees it. <see cref="PositionMs"/> is media
/// time, not wall time: the cue sits on the raw media clock, so it survives a
/// tempo change and means the same thing on either deck.
/// </summary>
public sealed record HotCue
{
    public long Id { get; init; }
    public long TrackId { get; init; }
    public int Deck { get; init; }
    public int Slot { get; init; }
    public long PositionMs { get; init; }
    public string? Label { get; init; }
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// The eight saved hot cues per deck per track (<c>hot_cues</c> table).
///
/// <para><b>Identity</b>: rows point at <c>tracks.id</c>, not at a path. That is
/// the trade written into the migration: a moved file keeps its row only if the
/// scanner matches it again, and the same is already true of
/// <c>karaoke_assets</c>. The path-keyed alternative (the beat grid) survives a
/// move but loses the cue when the file is replaced, and a cue on the wrong file
/// is worse than no cue.</para>
///
/// <para><b>Deck and slot numbering</b>: deck 0 is Deck A and deck 1 is Deck B,
/// the order the audio layer's <c>PlaybackDeck</c> declares (A, then B); slots
/// are 1..<see cref="SlotCount"/>.</para>
///
/// <para><b>The quick cue is not here</b>: the ephemeral cue on the transport bar
/// is one position in memory and is never written, by design.</para>
///
/// <para><b>Writes</b> go through <see cref="JockeyBoxDatabase.WithWriter"/>, the
/// same single-writer gate every other repository uses. No new locking.</para>
/// </summary>
public sealed class HotCueRepository
{
    /// <summary>Slots per deck: 1..<see cref="SlotCount"/>.</summary>
    public const int SlotCount = 8;

    /// <summary>Mixer decks: 0..<see cref="DeckCount"/> - 1.</summary>
    public const int DeckCount = 2;

    private readonly JockeyBoxDatabase _db;

    public HotCueRepository(JockeyBoxDatabase db) => _db = db;

    /// <summary>Every saved cue for one deck of one track, ordered by slot.</summary>
    public IReadOnlyList<HotCue> Get(long trackId, int deck)
    {
        if (deck is < 0 or >= DeckCount)
            throw new ArgumentOutOfRangeException(nameof(deck), deck, $"Deck must be 0..{DeckCount - 1}.");

        return _db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, track_id, deck, slot, position_ms, label, created_at " +
                "FROM hot_cues WHERE track_id = @track AND deck = @deck ORDER BY slot;";
            cmd.Parameters.AddWithValue("@track", trackId);
            cmd.Parameters.AddWithValue("@deck", deck);

            var rows = new List<HotCue>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new HotCue
                {
                    Id = reader.GetInt64(0),
                    TrackId = reader.GetInt64(1),
                    Deck = reader.GetInt32(2),
                    Slot = reader.GetInt32(3),
                    PositionMs = reader.GetInt64(4),
                    Label = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedUtc = ParseUtc(reader.GetString(6)),
                });
            }
            return rows;
        });
    }

    /// <summary>
    /// Save one slot. Re-setting an occupied slot moves it: the row keeps its id
    /// and <c>created_at</c>, so a cue that is nudged repeatedly is still one cue.
    /// </summary>
    public void Set(long trackId, int deck, int slot, long positionMs, string? label = null)
    {
        if (deck is < 0 or >= DeckCount)
            throw new ArgumentOutOfRangeException(nameof(deck), deck, $"Deck must be 0..{DeckCount - 1}.");
        if (slot is < 1 or > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be 1..{SlotCount}.");

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        _db.WithWriter(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO hot_cues (track_id, deck, slot, position_ms, label, created_at) " +
                "VALUES (@track, @deck, @slot, @position, @label, @created) " +
                "ON CONFLICT (track_id, deck, slot) DO UPDATE SET " +
                "position_ms = excluded.position_ms, label = excluded.label;";
            cmd.Parameters.AddWithValue("@track", trackId);
            cmd.Parameters.AddWithValue("@deck", deck);
            cmd.Parameters.AddWithValue("@slot", slot);
            cmd.Parameters.AddWithValue("@position", positionMs);
            cmd.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", now);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Remove one slot; clearing an empty slot is a no-op.</summary>
    public void Clear(long trackId, int deck, int slot)
    {
        if (deck is < 0 or >= DeckCount)
            throw new ArgumentOutOfRangeException(nameof(deck), deck, $"Deck must be 0..{DeckCount - 1}.");
        if (slot is < 1 or > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be 1..{SlotCount}.");

        _db.WithWriter(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM hot_cues WHERE track_id = @track AND deck = @deck AND slot = @slot;";
            cmd.Parameters.AddWithValue("@track", trackId);
            cmd.Parameters.AddWithValue("@deck", deck);
            cmd.Parameters.AddWithValue("@slot", slot);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Remove every cue of a track on every deck (relink, purge, replace).</summary>
    public void ClearAll(long trackId)
    {
        _db.WithWriter(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM hot_cues WHERE track_id = @track;";
            cmd.Parameters.AddWithValue("@track", trackId);
            cmd.ExecuteNonQuery();
        });
    }

    private static DateTime ParseUtc(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime()
            : DateTime.MinValue;
}
