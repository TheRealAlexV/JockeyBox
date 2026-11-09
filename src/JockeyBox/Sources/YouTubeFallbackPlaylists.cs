namespace JockeyBox.Sources;

/// <summary>
/// The static lists the YouTube tab offers when the account's own playlists
/// cannot be listed: not signed in, or the OAuth refresh token lapsed (an
/// unverified app is capped at 7 days). The user asked for this explicitly -
/// something to add and load when "my playlists" is not available.
///
/// <para>An entry is a title plus the YouTube search the tab runs once, when the
/// entry is added. Deliberately not a hard-coded playlist id: an id that has been
/// deleted upstream or was mistyped would be a row that never loads, and the
/// point of this list is that every entry works. What lands in the store is an
/// ordinary local playlist - persisted in playlists.json / library.db, playable,
/// removable like any other row.</para>
/// </summary>
public static class YouTubeFallbackPlaylists
{
    /// <summary>How many results one entry pulls in when it is added.</summary>
    public const int TracksPerEntry = 25;

    public sealed record Entry(string Title, string Query);

    /// <summary>
    /// Genre-level collections, so the choice is useful without pretending to be
    /// anyone's account. Titles are shown verbatim on the rail.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Entries = new[]
    {
        new Entry("Synthwave", "synthwave"),
        new Entry("French house", "french house"),
        new Entry("Italo disco", "italo disco"),
        new Entry("Nu disco", "nu disco"),
        new Entry("Chillout", "chillout mix"),
    };
}
