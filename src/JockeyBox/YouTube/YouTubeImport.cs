using System.Text.RegularExpressions;

namespace JockeyBox.YouTube;

/// <summary>What a pasted YouTube link points at. Channels are deliberately absent.</summary>
public enum YouTubeImportKind
{
    Video,
    Playlist,
}

/// <summary>
/// A link the YouTube tab can list: exactly one video, or one playlist. The id
/// and the canonical URL are both derived from the input, so a pasted link with
/// tracking parameters, a mobile host or a bare id all reach yt-dlp as the same
/// URL.
/// </summary>
public sealed record YouTubeImportTarget(YouTubeImportKind Kind, string Id)
{
    public bool IsPlaylist => Kind == YouTubeImportKind.Playlist;
    public bool IsVideo => Kind == YouTubeImportKind.Video;

    /// <summary>Canonical URL handed to yt-dlp.</summary>
    public string Url => IsPlaylist
        ? "https://www.youtube.com/playlist?list=" + Id
        : "https://www.youtube.com/watch?v=" + Id;
}

/// <summary>
/// Turns whatever the user pasted into a <see cref="YouTubeImportTarget"/>.
///
/// <para>Accepted: a watch URL (youtube.com, music.youtube.com, m.youtube.com),
/// youtu.be, /shorts/, /embed/, a /playlist URL, a bare 11-character video id,
/// or a bare list id (see <see cref="YouTubeId.Playlist"/> for the prefixes).
/// A channel or handle URL is rejected with one clear sentence rather than being
/// half supported: yt-dlp reads a channel as "every upload ever", which is a
/// different feature with different failure modes.</para>
///
/// <para>Parsing is pure and lives here so it is unit-testable without a
/// network or a settings file.</para>
/// </summary>
public static class YouTubeImportParser
{
    public const string EmptyMessage = "Paste a YouTube video or playlist link.";
    public const string NotALinkMessage = "That does not look like a YouTube video or playlist link.";
    public const string ChannelMessage = "Channel links are not supported here \u2014 paste a video or playlist link.";
    public const string MissingListMessage = "That playlist link has no list id.";

    private static readonly Regex BareVideoIdRegex = YouTubeId.Video;

    // A bare list id always starts with a two-letter kind prefix. Widened over
    // time by YouTube; a word that merely looks like an id ("notalink") must
    // stay invalid rather than becoming a bogus playlist lookup.
    // A bare list id always starts with a two-letter kind prefix. Widened over
    // time by YouTube; a word that merely looks like an id ("notalink") must
    // stay invalid rather than becoming a bogus playlist lookup.
    private static readonly Regex BarePlaylistIdRegex = YouTubeId.Playlist;

    private static readonly Regex ListParamRegex =
        new(@"[?&]list=([A-Za-z0-9_-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VideoParamRegex =
        new(@"[?&]v=([A-Za-z0-9_-]{11})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShortLinkRegex =
        new(@"^https?://(?:www\.)?youtu\.be/([A-Za-z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PathVideoRegex =
        new(@"^https?://(?:www\.|m\.|music\.)?youtube\.com/(?:shorts|embed|live|v)/([A-Za-z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse <paramref name="input"/>. Returns false with one short sentence in
    /// <paramref name="error"/> - callers show it inline, never in a dialog.
    /// </summary>
    public static bool TryParse(string? input, out YouTubeImportTarget? target, out string error)
    {
        target = null;
        error = "";

        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = EmptyMessage;
            return false;
        }

        // A bare id needs no URL shape. The playlist test runs first because the
        // two shapes can collide at 11 characters (a short list id such as
        // "WLabc123456" also matches the video pattern); a prefix is the more
        // specific signal, so it wins.
        if (!LooksLikeUrl(text) && BarePlaylistIdRegex.IsMatch(text))
        {
            target = new YouTubeImportTarget(YouTubeImportKind.Playlist, text);
            return true;
        }

        if (BareVideoIdRegex.IsMatch(text))
        {
            target = new YouTubeImportTarget(YouTubeImportKind.Video, text);
            return true;
        }

        if (!LooksLikeYouTubeUrl(text))
        {
            error = NotALinkMessage;
            return false;
        }

        // Scheme-less "youtube.com/..." is common in chat and bookmarks.
        var url = text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : "https://" + text;

        if (YouTubeApi.IsChannelUrl(url))
        {
            error = ChannelMessage;
            return false;
        }

        // A /playlist link is a playlist even though it carries no video.
        if (url.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
        {
            var list = ListParamRegex.Match(url);
            if (!list.Success || list.Groups[1].Value.Length == 0)
            {
                error = MissingListMessage;
                return false;
            }

            target = new YouTubeImportTarget(YouTubeImportKind.Playlist, list.Groups[1].Value);
            return true;
        }

        var shortLink = ShortLinkRegex.Match(url);
        if (shortLink.Success)
        {
            target = new YouTubeImportTarget(YouTubeImportKind.Video, shortLink.Groups[1].Value);
            return true;
        }

        var pathVideo = PathVideoRegex.Match(url);
        if (pathVideo.Success)
        {
            target = new YouTubeImportTarget(YouTubeImportKind.Video, pathVideo.Groups[1].Value);
            return true;
        }

        // watch?v=… - a "watch" URL that also carries &list= is still one video:
        // the user pasted a video, and importing a playlist is the /playlist link.
        var video = VideoParamRegex.Match(url);
        if (video.Success)
        {
            target = new YouTubeImportTarget(YouTubeImportKind.Video, video.Groups[1].Value);
            return true;
        }

        // A bare "list=PL…" fragment, which the playlist picker copies.
        if (url.Contains("list=", StringComparison.OrdinalIgnoreCase))
        {
            var list = ListParamRegex.Match("?" + url.Split('?', 2).Last());
            if (list.Success)
            {
                target = new YouTubeImportTarget(YouTubeImportKind.Playlist, list.Groups[1].Value);
                return true;
            }
        }

        error = NotALinkMessage;
        return false;
    }

    private static bool LooksLikeUrl(string text) =>
        text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        || text.Contains("://", StringComparison.Ordinal);

    private static bool LooksLikeYouTubeUrl(string text) =>
        text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
}
