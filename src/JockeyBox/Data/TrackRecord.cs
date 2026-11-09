namespace JockeyBox.Data;

/// <summary>
/// One row of <c>tracks</c>, detached from any connection. Immutable: a reader
/// may hold on to it for as long as it likes.
/// </summary>
public sealed record TrackRecord
{
    public long Id { get; init; }

    /// <summary>
    /// Absolute path in the platform's own separators (<see cref="LibraryKeys.NativePath"/>),
    /// i.e. the form the filesystem, the ledger and the tag cache key on. The
    /// database stores the <c>/</c> form; every read converts once, here.
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>Identity: <see cref="LibraryKeys.PathKey"/> of <see cref="Path"/>.</summary>
    public string PathKey { get; init; } = "";

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string AlbumArtist { get; init; } = "";
    public string Genre { get; init; } = "";
    public int Year { get; init; }
    public int TrackNo { get; init; }
    public int DiscNo { get; init; }
    public long DurationMs { get; init; }
    public double Bpm { get; init; }
    public double BpmP10 { get; init; }
    public double BpmP90 { get; init; }
    public double BpmConfidence { get; init; }
    public long FirstBeatMs { get; init; }
    public bool HalfDoubleSuspect { get; init; }
    public long SizeBytes { get; init; }
    public DateTime MTimeUtc { get; init; }
    public DateTime AddedUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public bool IsMissing { get; init; }

    /// <summary>Sort key (<see cref="LibraryKeys.SortKey"/>), kept in the row so ORDER BY is index-only.</summary>
    public string SortTitle { get; init; } = "";

    public string SortArtist { get; init; } = "";

    /// <summary>File name for display, never throws on malformed paths.</summary>
    public string FileName
    {
        get
        {
            // Path is native, but a row can be built by hand with the storage
            // form (tests, importer); split on either separator.
            var slash = Math.Max(Path.LastIndexOf('/'), Path.LastIndexOf('\\'));
            return slash >= 0 && slash + 1 < Path.Length ? Path[(slash + 1)..] : Path;
        }
    }

    /// <summary>Library-relative key of this file: the last path segment without its audio extension.</summary>
    public string LocalKey => LibraryKeys.LocalKey(FileName);
}
