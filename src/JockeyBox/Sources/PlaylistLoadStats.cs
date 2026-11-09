namespace JockeyBox.Sources;

/// <summary>
/// Timings of one <see cref="PlaylistStore"/> graph read, filled by
/// <see cref="PlaylistStore.LastLoadStats"/>.
///
/// <para>This exists because "playlist browsing is slow" is not a bug report a
/// profile can be taken from: the store has two very different read paths (a
/// 3 MB JSON document and an indexed SQLite query) and the per-row library
/// lookup used to be the expensive half of both. One line per read, with the
/// split between parse, materialise and the per-row availability probe, is what
/// makes a regression visible without a profiler.</para>
/// </summary>
public sealed record PlaylistLoadStats
{
    /// <summary><c>db</c> or <c>json</c>.</summary>
    public string Source { get; init; } = "json";

    /// <summary>Playlists in the graph that was read.</summary>
    public int Playlists { get; init; }

    /// <summary>Membership rows in the graph that was read.</summary>
    public int Rows { get; init; }

    /// <summary>Whole read, including normalisation and the per-row pass.</summary>
    public double TotalMs { get; init; }

    /// <summary>Reading the file from disk (0 when the database was the source).</summary>
    public double FileReadMs { get; init; }

    /// <summary>JSON deserialisation (0 when the database was the source).</summary>
    public double JsonParseMs { get; init; }

    /// <summary>The SQLite read: playlist headers plus every membership row (0 on the JSON path).</summary>
    public double DbReadMs { get; init; }

    /// <summary>Building <see cref="SourceTrack"/> objects and normalising identities.</summary>
    public double MaterialiseMs { get; init; }

    /// <summary>Total time spent in the per-row "is this file in the library?" probe.</summary>
    public double AvailabilityTotalMs { get; init; }

    /// <summary>How many rows took that probe.</summary>
    public int AvailabilityCalls { get; init; }

    /// <summary>The slowest single row's probe.</summary>
    public double WorstRowMs { get; init; }

    /// <summary>Identity of the slowest row (remote id / manual handle), for a log line.</summary>
    public string WorstRowId { get; init; } = "";

    /// <summary>One line, for the startup log and the perf probe.</summary>
    public string Summary =>
        $"source={Source} playlists={Playlists} rows={Rows} total={TotalMs:F1}ms " +
        $"file={FileReadMs:F1} json={JsonParseMs:F1} db={DbReadMs:F1} materialise={MaterialiseMs:F1} " +
        $"avail={AvailabilityTotalMs:F1}ms/{AvailabilityCalls} worst={WorstRowMs:F3}ms" +
        (WorstRowId.Length == 0 ? "" : $" ({WorstRowId})");
}
