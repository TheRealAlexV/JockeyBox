using System.Collections;
using JockeyBox.Library;
using JockeyBox.Sources;
using JockeyBox.YouTube;

namespace JockeyBox.ViewModels;

/// <summary>
/// The one track-row order behind every sort strip: the library tree, the Spotify
/// tab and the YouTube results. One comparer, one null policy.
///
/// <para>A row with no value for the chosen field (no key, no BPM, no length)
/// groups last in <em>both</em> directions and compares equal to the other
/// missing rows, so the arrow never moves it to the top and the group stays
/// contiguous. Descending swaps the operands rather than negating the result,
/// which is what keeps arrival order for equal values and stops the missing
/// group from flipping along with the column.</para>
/// </summary>
public sealed class TrackSortComparer : IComparer, IComparer<TrackEntry>
{
    private readonly TrackSortField _field;
    private readonly bool _descending;
    private readonly IReadOnlyDictionary<object, int>? _rank;

    /// <param name="rank">
    /// Optional arrival position per row, keyed by reference. WPF's
    /// <c>ListCollectionView</c> applies <c>CustomSort</c> through
    /// <c>ArrayList.Sort</c>, which is introsort and therefore unstable above
    /// ~16 rows: rows equal on the field would land in arbitrary order. The
    /// final tie-break on this rank reproduces the arrival order that LINQ
    /// <c>OrderBy</c> already gives the library for free. Leave it null (or
    /// pass the rank) on the library path: it is stable either way.
    /// </param>
    public TrackSortComparer(
        TrackSortField field, bool descending, IReadOnlyDictionary<object, int>? rank = null)
    {
        _field = field;
        _descending = descending;
        _rank = rank;
    }

    /// <summary>The collection views' entry point (Spotify, YouTube).</summary>
    int IComparer.Compare(object? x, object? y) => (x, y) switch
    {
        (TrackEntry a, TrackEntry b) => Tie(CompareLibrary(a, b), a, b),
        (SourceTrack a, SourceTrack b) => Tie(CompareSources(a, b), a, b),
        (YtEntry a, YtEntry b) => Tie(CompareYt(a, b), a, b),
        _ => 0,
    };

    /// <summary>The library's entry point (the tree sorts with LINQ OrderBy).</summary>
    public int Compare(TrackEntry? x, TrackEntry? y) => Tie(CompareLibrary(x!, y!), x!, y!);

    /// <summary>
    /// Rows equal on the sort field fall back to their arrival position, which is
    /// what a stable sort would have preserved. A missing rank keeps the previous
    /// "equal" result rather than inventing an order.
    /// </summary>
    private int Tie(int result, object a, object b) =>
        result != 0 || _rank is null
            ? result
            : _rank.TryGetValue(a, out var ra) && _rank.TryGetValue(b, out var rb)
                ? ra.CompareTo(rb)
                : 0;

    /// <summary>Library rows: <see cref="TrackEntry"/> tags, filled by LibraryScanner.</summary>
    private int CompareLibrary(TrackEntry a, TrackEntry b) => _field switch
    {
        TrackSortField.Artist => CompareText(a.Artist, b.Artist),
        TrackSortField.Bpm => CompareNumber(Number(a.Bpm), Number(b.Bpm)),
        TrackSortField.Key => CompareKey(NonEmpty(a.Key), NonEmpty(b.Key)),
        TrackSortField.Length => CompareNumber(Number(a.Duration), Number(b.Duration)),
        _ => CompareText(a.Title, b.Title),
    };

    /// <summary>Spotify rows: BPM and key live on the library entry they matched.</summary>
    private int CompareSources(SourceTrack a, SourceTrack b) => _field switch
    {
        TrackSortField.Artist => CompareText(a.Artist, b.Artist),
        TrackSortField.Bpm => CompareNumber(SourceBpm(a), SourceBpm(b)),
        TrackSortField.Key => CompareKey(SourceKey(a), SourceKey(b)),
        TrackSortField.Length => CompareNumber(Number(a.DurationMs), Number(b.DurationMs)),
        TrackSortField.DateAdded => CompareNumber(a.AddedAt, b.AddedAt),
        _ => CompareText(a.Title, b.Title),
    };

    /// <summary>YouTube rows have a title, a channel and a length; the rest falls back to the title.</summary>
    private int CompareYt(YtEntry a, YtEntry b) => _field switch
    {
        TrackSortField.Artist => CompareText(a.Uploader, b.Uploader),
        TrackSortField.Length => CompareNumber(Number(a.DurationSeconds), Number(b.DurationSeconds)),
        _ => CompareText(a.Title, b.Title),
    };

    /// <summary>Missing text sorts last both ways; present text swaps operands for descending.</summary>
    private int CompareText(string? a, string? b)
    {
        var aMissing = a is null;
        var bMissing = b is null;
        if (aMissing || bMissing) return aMissing == bMissing ? 0 : aMissing ? 1 : -1;
        return _descending ? Text(b!, a!) : Text(a!, b!);
    }

    /// <summary>
    /// Camelot order: number first, then letter, so the column reads
    /// 1A, 1B, 2A ... 12B instead of the lexicographic 10A, 11A, 12A, 1A.
    /// A missing or unparseable key groups last in both directions and
    /// compares equal within the group, exactly like the other fields.
    /// Note names are accepted here as well, so a value that skipped
    /// normalisation still sorts on the wheel rather than as text.
    /// </summary>
    private int CompareKey(string? a, string? b)
    {
        var hasA = KeyNotation.TryParseCamelot(a, out var aNumber, out var aLetter);
        var hasB = KeyNotation.TryParseCamelot(b, out var bNumber, out var bLetter);
        if (!hasA || !hasB) return hasA == hasB ? 0 : hasA ? -1 : 1;

        var byNumber = aNumber.CompareTo(bNumber);
        var result = byNumber != 0 ? byNumber : aLetter.CompareTo(bLetter);
        return _descending ? -result : result;
    }

    /// <summary>Missing values sort last both ways; present values swap operands for descending.</summary>
    private int CompareNumber<T>(T? a, T? b) where T : struct, IComparable<T>
    {
        var aMissing = a is null;
        var bMissing = b is null;
        if (aMissing || bMissing) return aMissing == bMissing ? 0 : aMissing ? 1 : -1;
        return _descending ? b!.Value.CompareTo(a!.Value) : a!.Value.CompareTo(b!.Value);
    }

    /// <summary>Culture- and case-insensitive text order with an ordinal tie-break.</summary>
    private static int Text(string a, string b)
    {
        var byText = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        return byText != 0 ? byText : string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// A Spotify row reaches its key through its library entry when the row's own
    /// <see cref="SourceTrack.Key"/> is still empty; the scan writes the TKEY tag
    /// onto <see cref="TrackEntry.Key"/>.
    /// </summary>
    private static string? SourceKey(SourceTrack track) =>
        NonEmpty(track.Key) ?? NonEmpty(track.LocalEntry?.Key);

    /// <summary>BPM comes from the library entry's tags; 0 means it had none.</summary>
    private static double? SourceBpm(SourceTrack track) =>
        track.LocalEntry is { Bpm: > 0 } entry ? entry.Bpm : null;

    private static double? Number(double value) => value > 0 ? value : null;

    private static double? Number(TimeSpan value) =>
        value > TimeSpan.Zero ? value.TotalMilliseconds : null;

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
