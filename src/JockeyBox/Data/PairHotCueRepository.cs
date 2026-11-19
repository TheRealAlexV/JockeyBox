using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JockeyBox.Data;

/// <summary>
/// One saved cue as the pair table sees it. The two track ids name the pair: the
/// render on deck A and the original on deck B. <see cref="PositionMs"/> is media
/// time, the same clock <see cref="HotCue.PositionMs"/> uses, so a tempo change
/// does not move it.
/// </summary>
public sealed record PairHotCue
{
    public long Id { get; init; }
    public long TrackIdA { get; init; }
    public long TrackIdB { get; init; }
    public int Slot { get; init; }
    public long PositionMs { get; init; }
    public string? Label { get; init; }
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// The eight saved cues of a karaoke pair (<c>pair_hot_cues</c> table).
///
/// <para><b>Why not one <c>track_id</c></b>: a pair is two tracks at once, so the
/// row is keyed by both. This is deliberately a separate table rather than a
/// sentinel <c>deck</c> value in <c>hot_cues</c>: the per-deck table and every
/// reader of it keep their current meaning.</para>
///
/// <para><b>The quick cue is not here</b>: like the per-deck bank, the ephemeral
/// cue is one position in memory and is never written.</para>
///
/// <para><b>Writes</b> go through <see cref="JockeyBoxDatabase.WithWriter"/>, the
/// same single-writer gate every other repository uses.</para>
/// </summary>
public sealed class PairHotCueRepository
{
    /// <summary>Slots per pair: 1..<see cref="SlotCount"/>.</summary>
    public const int SlotCount = 8;

    private readonly JockeyBoxDatabase _db;

    public PairHotCueRepository(JockeyBoxDatabase db) => _db = db;

    /// <summary>Every saved cue for one pair, ordered by slot.</summary>
    public IReadOnlyList<PairHotCue> Get(long trackIdA, long trackIdB)
    {
        return _db.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, track_id_a, track_id_b, slot, position_ms, label, created_at " +
                "FROM pair_hot_cues WHERE track_id_a = @a AND track_id_b = @b ORDER BY slot;";
            cmd.Parameters.AddWithValue("@a", trackIdA);
            cmd.Parameters.AddWithValue("@b", trackIdB);

            var rows = new List<PairHotCue>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new PairHotCue
                {
                    Id = reader.GetInt64(0),
                    TrackIdA = reader.GetInt64(1),
                    TrackIdB = reader.GetInt64(2),
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
    public void Set(long trackIdA, long trackIdB, int slot, long positionMs, string? label = null)
    {
        if (slot is < 1 or > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be 1..{SlotCount}.");

        string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        _db.WithWriter(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO pair_hot_cues (track_id_a, track_id_b, slot, position_ms, label, created_at) " +
                "VALUES (@a, @b, @slot, @position, @label, @created) " +
                "ON CONFLICT (track_id_a, track_id_b, slot) DO UPDATE SET " +
                "position_ms = excluded.position_ms, label = excluded.label;";
            cmd.Parameters.AddWithValue("@a", trackIdA);
            cmd.Parameters.AddWithValue("@b", trackIdB);
            cmd.Parameters.AddWithValue("@slot", slot);
            cmd.Parameters.AddWithValue("@position", positionMs);
            cmd.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", now);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Remove one slot; clearing an empty slot is a no-op.</summary>
    public void Clear(long trackIdA, long trackIdB, int slot)
    {
        if (slot is < 1 or > SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot must be 1..{SlotCount}.");

        _db.WithWriter(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM pair_hot_cues WHERE track_id_a = @a AND track_id_b = @b AND slot = @slot;";
            cmd.Parameters.AddWithValue("@a", trackIdA);
            cmd.Parameters.AddWithValue("@b", trackIdB);
            cmd.Parameters.AddWithValue("@slot", slot);
            cmd.ExecuteNonQuery();
        });
    }

    private static DateTime ParseUtc(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime()
            : DateTime.MinValue;
}
