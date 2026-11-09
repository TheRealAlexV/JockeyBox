using System.Globalization;
using Microsoft.Data.Sqlite;

namespace JockeyBox.Data;

/// <summary>
/// One soundboard pad as the database sees it. Immutable: the JSON file stays the
/// source of truth, so a row here is a snapshot written on every mutation.
/// </summary>
public sealed record SoundboardRow
{
    /// <summary>Row id derived from the pad's short string id; see <see cref="SoundboardRepository.RowIdFor"/>.</summary>
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Path { get; init; } = "";
    public string? SourceUrl { get; init; }
    public double DurationMs { get; init; }
    public double Gain { get; init; } = 1.0;

    /// <summary>Playback mode ("oneshot" / "loop"); stored so the index survives a future mode column.</summary>
    public string Mode { get; init; } = "";
    public string? Hotkey { get; init; }
    public string? Hotkey2 { get; init; }
    public int Sort { get; init; }
    public DateTime AddedUtc { get; init; }
}

/// <summary>
/// Durable index over the soundboard (<c>soundboard</c> table).
///
/// <para><b>Why mirror at all</b>: the JSON file is still what the app loads, but
/// the table is what anything outside the process (a Stream Deck plugin looking
/// for a pad's file, a future search) can read without parsing JSON. Writes go
/// through the same <see cref="WriteQueue"/> the playlist store uses, so several
/// rapid edits collapse into one transaction off the UI thread.</para>
///
/// <para><b>Identity</b>: the schema's id is <c>INTEGER PRIMARY KEY</c> while a
/// pad id is a short hex string (<c>Guid.NewGuid().ToString("N")[..8]</c>). The
/// mapping is a pure function of the string, so a row can always be found again
/// from the pad without an extra column: hex ids parse straight to their int,
/// anything else falls back to a stable FNV-1a hash.</para>
///
/// <para>Nothing here asyncs: see <see cref="JockeyBoxDatabase"/>.</para>
/// </summary>
public sealed class SoundboardRepository
{
    private readonly JockeyBoxDatabase _db;

    public SoundboardRepository(JockeyBoxDatabase db) => _db = db;

    /// <summary>True when the table already holds at least one pad.</summary>
    public bool HasRows() => _db.Read(conn =>
        Convert.ToInt32(JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM soundboard;")) > 0);

    /// <summary>All rows in board order.</summary>
    public IReadOnlyList<SoundboardRow> LoadAll() => _db.Read(conn =>
    {
        var rows = new List<SoundboardRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, title, path, source_url, duration_ms, gain, mode, hotkey, hotkey2, sort, added_utc " +
            "FROM soundboard ORDER BY sort, id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SoundboardRow
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                Path = reader.GetString(2),
                SourceUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                DurationMs = reader.GetDouble(4),
                Gain = reader.GetDouble(5),
                Mode = reader.GetString(6),
                Hotkey = reader.IsDBNull(7) ? null : reader.GetString(7),
                Hotkey2 = reader.IsDBNull(8) ? null : reader.GetString(8),
                Sort = reader.GetInt32(9),
                AddedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)).UtcDateTime,
            });
        }

        return rows;
    });

    /// <summary>
    /// Replace the table with <paramref name="rows"/> in one transaction. A pad
    /// that vanished from the board vanishes here too - delete+insert is the
    /// whole mirror, and a board is small enough (dozens of rows) that a diff
    /// would be more code for no measurable gain.
    /// </summary>
    public void SaveAll(IReadOnlyList<SoundboardRow> rows)
    {
        _db.WriteTransaction(conn =>
        {
            JockeyBoxDatabase.Execute(conn, "DELETE FROM soundboard;");

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                // OR REPLACE: path has a partial UNIQUE index. Two pads pointing at
                // one file is a board-level problem the view model prevents, but the
                // mirror must never fail a whole transaction over it.
                "INSERT OR REPLACE INTO soundboard " +
                "(id, title, path, source_url, duration_ms, gain, mode, hotkey, hotkey2, sort, added_utc) " +
                "VALUES ($id, $title, $path, $source_url, $duration_ms, $gain, $mode, $hotkey, $hotkey2, $sort, $added_utc);";

            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            var title = cmd.Parameters.Add("$title", SqliteType.Text);
            var path = cmd.Parameters.Add("$path", SqliteType.Text);
            var sourceUrl = cmd.Parameters.Add("$source_url", SqliteType.Text);
            var durationMs = cmd.Parameters.Add("$duration_ms", SqliteType.Integer);
            var gain = cmd.Parameters.Add("$gain", SqliteType.Real);
            var mode = cmd.Parameters.Add("$mode", SqliteType.Text);
            var hotkey = cmd.Parameters.Add("$hotkey", SqliteType.Text);
            var hotkey2 = cmd.Parameters.Add("$hotkey2", SqliteType.Text);
            var sort = cmd.Parameters.Add("$sort", SqliteType.Integer);
            var addedUtc = cmd.Parameters.Add("$added_utc", SqliteType.Integer);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                id.Value = row.Id;
                title.Value = row.Title ?? "";
                path.Value = row.Path ?? "";
                sourceUrl.Value = (object?)row.SourceUrl ?? DBNull.Value;
                durationMs.Value = (long)Math.Round(row.DurationMs);
                gain.Value = row.Gain;
                mode.Value = row.Mode ?? "";
                hotkey.Value = (object?)row.Hotkey ?? DBNull.Value;
                hotkey2.Value = (object?)row.Hotkey2 ?? DBNull.Value;
                sort.Value = i;
                addedUtc.Value = new DateTimeOffset(
                    DateTime.SpecifyKind(row.AddedUtc == default ? DateTime.UtcNow : row.AddedUtc, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds();
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Drop every row (the board was emptied).</summary>
    public void Clear() => _db.WithWriter(conn => JockeyBoxDatabase.Execute(conn, "DELETE FROM soundboard;"));

    // ---- id mapping ---------------------------------------------------------

    /// <summary>
    /// Map a pad's string id to the table's integer id. Eight hex characters (the
    /// shape <c>SoundboardPad</c> generates) map to their exact value; any other
    /// id maps through a deterministic FNV-1a hash so the same pad always lands on
    /// the same row.
    /// </summary>
    public static long RowIdFor(string? padId)
    {
        var value = (padId ?? "").Trim();
        if (value.Length == 0) return 0;

        if (value.Length <= 8
            && long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;

        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= prime;
            }

            // Stay inside a positive 31-bit range: the row id is only an index.
            return (long)(hash & 0x7FFFFFFF);
        }
    }

    /// <summary>The inverse of <see cref="RowIdFor"/> for hex-shaped ids.</summary>
    public static string PadIdFor(long rowId) => rowId.ToString("x8", CultureInfo.InvariantCulture);
}
