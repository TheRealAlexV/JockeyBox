using System.IO;
using JockeyBox.Downloads;
using JockeyBox.Library;

namespace JockeyBox.Sources;

/// <summary>
/// The library's answer for one remote row: the scanned <see cref="TrackEntry"/>
/// that stands for it, or nothing when the file is not here yet.
///
/// <para>The entry is kept by reference on purpose - the local library binds the
/// same object, so a row that starts a karaoke separation shows "⚙ 42%" and then
/// "✓" on the remote tab without a rescan, exactly as it does in the browser.</para>
/// </summary>
public readonly record struct RemoteTrackFacts(TrackEntry? Entry)
{
    public static readonly RemoteTrackFacts None = default;

    /// <summary>A local file exists (music, a karaoke-only rip, or both).</summary>
    public bool HasLocalFile => Entry is not null;

    /// <summary>The same test the local library's "✓" column makes.</summary>
    public bool HasKaraoke => Entry?.KaraokePath is not null;

    /// <summary>Local music exists and no karaoke does: the library's "⭕ needed".</summary>
    public bool KaraokeNeeded => Entry?.NeedsKaraoke == true;

    /// <summary>Identical text to the local library's karaoke column.</summary>
    public string KaraokeStatus => Entry?.KaraokeStatus ?? "";

    /// <summary>Screen-reader / live-tree label; carries the state, not just the column.</summary>
    public string KaraokeAutomationName => HasKaraoke ? "Karaoke available"
        : KaraokeNeeded ? "Karaoke needed"
        : "No karaoke version";
}

/// <summary>
/// A one-pass index of the scanned library that answers "does this remote row
/// have a local file, and does it have a karaoke version?" in O(1).
///
/// <para>Why it exists: a YouTube or Spotify playlist can hold thousands of rows
/// and the tab re-derives every row after each queue event. Walking the download
/// ledger - and then the disk - per row was the slow part; this index is built
/// once per scan (<see cref="MainViewModel"/> rebuilds it next to
/// <c>LibraryIndex.Rebuild</c>) and every row lookup afterwards is one dictionary
/// hit with no file I/O at all.</para>
///
/// <para>Rows are matched on the path the tab already resolved, then on
/// artist+title - the same two steps the playlist membership resolver uses, so
/// the two never disagree about "the same song".</para>
/// </summary>
public sealed class RemoteLibraryFacts
{
    private readonly Dictionary<string, TrackEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackEntry> _byMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackEntry> _bySourceId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackEntry> _byLedgerMetadata = new(StringComparer.Ordinal);

    /// <summary>How many scanned files the index knows; 0 until the first scan lands.</summary>
    public int Count { get; private set; }

    /// <summary>How many downloaded rows are addressable by remote id.</summary>
    public int DownloadedCount { get; private set; }

    /// <summary>
    /// Fold the download ledger in: it remembers the *remote* identity a file was
    /// fetched for (Spotify track id / YouTube video id), which is exactly what a
    /// remote row carries and what the file's own tags usually do not.
    ///
    /// <para>One pass over the ledger and one dictionary hit per row afterwards -
    /// the per-row ledger walk this replaces was the slow part of refreshing a
    /// 5,000-row tab. A ledger row whose file no longer scans is dropped: the
    /// index only ever answers with entries the current scan produced.</para>
    /// </summary>
    public void RebuildLedger(IEnumerable<LedgerEntry>? entries)
    {
        var bySourceId = new Dictionary<string, TrackEntry>(StringComparer.Ordinal);
        var byLedgerMetadata = new Dictionary<string, TrackEntry>(StringComparer.Ordinal);
        var count = 0;

        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) continue;

                TrackEntry? scanned = null;
                foreach (var path in Paths(entry.Path))
                {
                    if (_byPath.TryGetValue(path, out var hit)) { scanned = hit; break; }
                }

                if (scanned is null) continue; // downloaded, but the file is gone
                count++;

                if (!string.IsNullOrWhiteSpace(entry.SourceKey)) bySourceId.TryAdd(entry.SourceKey.Trim(), scanned);

                var metadata = LibraryKey(entry.Artist, entry.Title);
                if (metadata.Length > 0) byLedgerMetadata.TryAdd(metadata, scanned);
            }
        }

        _bySourceId.Clear();
        foreach (var (key, entry) in bySourceId) _bySourceId[key] = entry;

        _byLedgerMetadata.Clear();
        foreach (var (key, entry) in byLedgerMetadata) _byLedgerMetadata[key] = entry;

        DownloadedCount = count;
    }

    /// <summary>Rebuild from the scan results. One pass, no disk access.</summary>
    public void Rebuild(IEnumerable<TrackEntry>? tracks)
    {
        var byPath = new Dictionary<string, TrackEntry>(_byPath.Count, StringComparer.OrdinalIgnoreCase);
        var byMetadata = new Dictionary<string, TrackEntry>(_byMetadata.Count, StringComparer.OrdinalIgnoreCase);
        var count = 0;

        if (tracks is not null)
        {
            foreach (var entry in tracks)
            {
                if (entry is null) continue;
                count++;

                if (entry.MusicPath is { Length: > 0 } music)
                    foreach (var path in Paths(music)) byPath.TryAdd(path, entry);

                if (entry.KaraokePath is { Length: > 0 } karaoke)
                    foreach (var path in Paths(karaoke)) byPath.TryAdd(path, entry);

                var metadata = MetadataKey(entry.Artist, entry.Title);
                if (metadata.Length > 0) byMetadata.TryAdd(metadata, entry);
            }
        }

        _byPath.Clear();
        foreach (var (key, entry) in byPath) _byPath[key] = entry;

        _byMetadata.Clear();
        foreach (var (key, entry) in byMetadata) _byMetadata[key] = entry;

        Count = count;
    }

    /// <summary>Facts for one remote row. Never touches the disk.</summary>
    public RemoteTrackFacts For(SourceTrack? row) => new(Find(row));

    /// <summary>
    /// Apply the index to a batch of rows. Only rows whose entry actually changed
    /// raise property-changed, so virtualization repaints just those.
    /// </summary>
    public void Apply(IEnumerable<SourceTrack>? rows)
    {
        if (rows is null) return;

        foreach (var row in rows)
        {
            if (row is null) continue;
            row.ApplyLibraryFacts(For(row));
        }
    }

    /// <summary>Resolve one row back to its library entry (reveal / make karaoke).</summary>
    public TrackEntry? Find(SourceTrack? row)
    {
        if (row is null) return null;

        foreach (var path in Paths(row.LibraryPath))
        {
            if (_byPath.TryGetValue(path, out var byPath)) return byPath;
        }

        // The remote id this row was fetched for - the most precise answer, and
        // the one that works for titles whose tags differ from the video's title.
        if (!string.IsNullOrWhiteSpace(row.Id) && _bySourceId.TryGetValue(row.Id.Trim(), out var byId))
            return byId;

        // A stale stored path (moved, renamed) still matches on the metadata the
        // download was recorded with, then on the file's own tags.
        var key = LibraryKey(row.Artist, row.Title);
        if (key.Length > 0 && _byLedgerMetadata.TryGetValue(key, out var byLedger)) return byLedger;

        var metadata = MetadataKey(row.Artist, row.Title);
        return metadata.Length > 0 && _byMetadata.TryGetValue(metadata, out var byMetadata)
            ? byMetadata
            : null;
    }

    private static IEnumerable<string> Paths(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;

        var trimmed = path.Trim();
        yield return trimmed;

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            yield break; // not a normalisable path: the raw form already missed
        }

        if (!string.Equals(full, trimmed, StringComparison.Ordinal)) yield return full;
    }

    /// <summary>
    /// artist|title, trimmed and lower-cased. Mirrors
    /// <see cref="LibraryMembershipSnapshot"/>'s metadata key so both agree on
    /// what "the same song" means.
    /// </summary>
    private static string MetadataKey(string? artist, string? title)
    {
        var a = (artist ?? "").Trim().ToLowerInvariant();
        var t = (title ?? "").Trim().ToLowerInvariant();
        return a.Length == 0 && t.Length == 0 ? "" : a + "|" + t;
    }

    /// <summary>
    /// The canonical "same song" key the ledger and the playlist resolver use:
    /// noise words ("(Official Audio)", "feat. …") folded out, so a YouTube title
    /// still meets the downloaded file it produced.
    /// </summary>
    private static string LibraryKey(string? artist, string? title) =>
        SourceMatcher.LibraryKey(artist, title);
}
