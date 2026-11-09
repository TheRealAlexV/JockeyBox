namespace JockeyBox.YouTube;

/// <summary>
/// What yt-dlp returned for a pasted link: the playlist's own title (or the
/// video's), the entries that resolved, and how many entries yt-dlp listed but
/// could not describe (private, deleted, members-only). The last number is shown
/// in the tab so a playlist of 12 that lists 10 says so instead of quietly
/// dropping two rows.
/// </summary>
public sealed record YtListing(
    string Title,
    IReadOnlyList<YtEntry> Entries,
    int SkippedCount = 0)
{
    public int Count => Entries.Count;
    public bool HasSkipped => SkippedCount > 0;
}
