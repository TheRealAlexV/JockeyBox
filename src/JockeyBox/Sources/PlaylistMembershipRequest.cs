namespace JockeyBox.Sources;

/// <summary>
/// The identity fields of one playlist row, copied into immutable strings.
///
/// Membership resolution runs on a worker while the UI thread may finish a
/// download on the same row (which rewrites <see cref="SourceTrack.LibraryPath"/>
/// and <see cref="SourceTrack.State"/>). Copying the five fields it needs means
/// the worker never reads a mutable row at all.
/// </summary>
public readonly record struct MembershipRow(
    string Id,
    string? LocalKey,
    string? LibraryPath,
    string? Artist,
    string? Title)
{
    public static MembershipRow From(SourceTrack track) => new(
        track.Id ?? "",
        // Normalised here as well: a hand-edited playlists.json can carry a key
        // the setter never saw, and an unnormalised key would never match.
        string.IsNullOrWhiteSpace(track.LocalKey) ? null : SourceTrack.NormalizeLocalKey(track.LocalKey),
        track.LibraryPath,
        track.Artist,
        track.Title);
}

/// <summary>
/// An immutable capture of everything membership resolution needs from a
/// <see cref="UserPlaylist"/>, taken on the UI thread.
///
/// <see cref="UserPlaylist.Tracks"/> is an <c>ObservableCollection</c> the UI
/// thread may edit (a sync pass adding rows, a delete). Enumerating it from
/// another thread would be a data race, so the rows are copied here first.
/// </summary>
public sealed record PlaylistMembershipRequest(
    string PlaylistId,
    string Name,
    MediaSource Source,
    bool IsImported,
    bool IsInternal,
    IReadOnlyList<MembershipRow> Rows)
{
    /// <summary>Snapshot <paramref name="playlist"/> for background resolution.</summary>
    public static PlaylistMembershipRequest Capture(UserPlaylist playlist)
    {
        var rows = new MembershipRow[playlist.Tracks.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = MembershipRow.From(playlist.Tracks[i]);

        return new PlaylistMembershipRequest(
            playlist.Id ?? "",
            playlist.Name ?? "",
            playlist.Source,
            playlist.IsImported,
            playlist.IsInternal,
            rows);
    }
}
