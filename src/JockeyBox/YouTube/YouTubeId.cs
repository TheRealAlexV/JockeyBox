using System.Text.RegularExpressions;

namespace JockeyBox.YouTube;

/// <summary>
/// The one definition of what a bare YouTube id looks like. Every caller that
/// accepts or classifies a bare id uses these, so the sync path and the importer
/// can never disagree about which prefixes are playlists.
/// </summary>
internal static class YouTubeId
{
    // A bare list id always starts with a two-letter kind prefix. Widened over
    // time by YouTube; a word that merely looks like an id ("notalink") must
    // stay invalid rather than becoming a bogus playlist lookup.
    public static readonly Regex Playlist =
        new("^(?:PL|UU|OL|RD|LL|FL|WL|TL|PU|SP|EC|VL)[A-Za-z0-9_-]{2,}$", RegexOptions.Compiled);

    public static readonly Regex Video =
        new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
}
