using System.Text.RegularExpressions;
using JockeyBox.Downloads;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.YouTube;

/// <summary>
/// YouTube-facing helpers on top of <see cref="YtDlp"/>: search hygiene for the
/// picker UI, tolerant parsing of pasted URLs, and mapping into the app's
/// source-track model.
/// </summary>
public static class YouTubeApi
{
    /// <summary>Anything longer than two hours is almost certainly a mix/DJ set, not a track.</summary>
    private const double MaxTrackSeconds = 7200;

    private static readonly Regex VideoIdRegex = new(
        @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/v/|/live/)([A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListParamRegex = new(
        @"[?&]list=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Channel and user pages are importable too: yt-dlp treats
    /// /@handle, /channel/UC…, /c/name and /user/name as a playlist of uploads,
    /// which is how "load a whole channel" works.
    /// </summary>
    private static readonly Regex ChannelUrlRegex = new(
        @"youtube\.com/(?:@[^/?#\s]+|channel/UC[A-Za-z0-9_-]{10,}|c/[^/?#\s]+|user/[^/?#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Searches YouTube and cleans the result for a music picker: live streams
    /// are dropped, and tracks that are absurdly long or have no duration are
    /// removed only when a usable result remains. Relevance order is preserved.
    /// </summary>
    public static async Task<List<YtEntry>> SearchAsync(
        string query, int maxResults, AppSettings settings, CancellationToken ct)
    {
        var results = await YtDlp.SearchAsync(query, maxResults, settings, ct);
        if (results.Count == 0) return results;

        var filtered = results.Where(e => !e.IsLive).ToList();

        var playable = filtered
            .Where(e => e.DurationSeconds > 0 && e.DurationSeconds <= MaxTrackSeconds)
            .ToList();

        if (playable.Count > 0)
        {
            filtered = playable;
        }
        else if (filtered.Count > 0)
        {
            Log.Debug($"YouTube search \"{query}\": no in-range durations, keeping {filtered.Count} result(s)");
        }

        var dropped = results.Count - filtered.Count;
        if (dropped > 0)
            Log.Debug($"YouTube search \"{query}\": dropped {dropped} live/overlong result(s)");

        return filtered;
    }

    /// <summary>
    /// Extracts a video id from watch?v= URLs, youtu.be links, /shorts/, /embed/,
    /// /live/ links or a bare 11-character id.
    /// </summary>
    public static bool TryParseVideoId(string input, out string videoId)
    {
        videoId = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim();

        if (YouTubeId.Video.IsMatch(text))
        {
            videoId = text;
            return true;
        }

        var match = VideoIdRegex.Match(text);
        if (match.Success)
        {
            videoId = match.Groups[1].Value;
            return true;
        }

        return false;
    }

    /// <summary>True when the input is a channel or user page rather than a list.</summary>
    public static bool IsChannelUrl(string input) =>
        ChannelUrlRegex.IsMatch((input ?? "").Trim());

    /// <summary>
    /// Extracts a playlist id from a "list=" URL parameter, a "list=&lt;id&gt;"
    /// fragment or a bare list id (see <see cref="YouTubeId.Playlist"/>).
    /// </summary>
    /// <remarks>
    /// A channel or user page returns false. It used to return true with an empty
    /// id so the caller could hand the URL to yt-dlp ("every upload ever"), and
    /// that is what let a handle become a playlist: the empty id was dropped, the
    /// raw <c>SourceId</c> took its place, and the row rendered as a playlist
    /// named "@handle" that could never be synced or removed. Channel imports go
    /// through <see cref="IsChannelUrl"/> and a different, explicit path.
    /// </remarks>
    public static bool TryParsePlaylistId(string input, out string listId)
    {
        listId = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim();

        var match = ListParamRegex.Match(text);
        if (match.Success)
        {
            listId = match.Groups[1].Value;
            return true;
        }

        if (text.StartsWith("list=", StringComparison.OrdinalIgnoreCase))
        {
            listId = text["list=".Length..].Trim();
            return listId.Length > 0;
        }

        if (YouTubeId.Playlist.IsMatch(text))
        {
            listId = text;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True only for a value that actually names a playlist: a <c>list=</c> URL or
    /// a bare list id (see <see cref="YouTubeId.Playlist"/>). The one rule every
    /// caller uses before treating a <see cref="UserPlaylist.SourceId"/> as
    /// something yt-dlp can fetch as a playlist, so a channel handle can never
    /// re-enter the store as one.
    /// </summary>
    public static bool IsPlaylistSource(string? input) =>
        TryParsePlaylistId(input ?? "", out var id) && id.Length > 0;

    /// <summary>
    /// Maps yt-dlp entries onto <see cref="SourceTrack"/>s for the download queue.
    /// Every track starts in <see cref="SourceTrackState.New"/> - the queue sets
    /// state, and a later ledger lookup flips it to InLibrary.
    /// </summary>
    public static List<SourceTrack> ToSourceTracks(IEnumerable<YtEntry> entries, string? playlistName,
                                                   string? artistFallback = null)
    {
        var tracks = new List<SourceTrack>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.VideoId)) continue;

            // Imported playlists are songs, not video listings: "Artist - Song
            // (Official Audio) ft. X" is stored as artist "Artist", title "Song"
            // so the table, the download and the ID3 tags all agree.
            var (title, artist) = MediaTitle.Clean(entry.Title, entry.Uploader);
            if (artist.Length == 0) artist = artistFallback ?? "";

            tracks.Add(new SourceTrack
            {
                Source = MediaSource.YouTube,
                Id = entry.VideoId,
                VideoId = entry.VideoId,
                Title = title.Length > 0 ? title : entry.Title ?? "",
                Artist = artist.Length > 0 ? artist : entry.Uploader ?? "",
                DurationMs = entry.DurationSeconds * 1000,
                CoverUrl = entry.ThumbnailUrl ?? "",
                PlaylistName = playlistName,
                State = SourceTrackState.New,
            });
        }

        return tracks;
    }
}
