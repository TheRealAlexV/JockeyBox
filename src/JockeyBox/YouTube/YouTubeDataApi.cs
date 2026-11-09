using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JockeyBox.Tools;

namespace JockeyBox.YouTube;

/// <summary>One of the signed-in channel's playlists, as YouTube reports it.</summary>
public sealed class YouTubePlaylist
{
    public string Id { get; init; } = "";

    /// <summary>Settable: a locally created playlist is renamed here before the next fetch.</summary>
    public string Title { get; set; } = "";

    public string Description { get; init; } = "";
    public string PrivacyStatus { get; init; } = "";

    /// <summary>
    /// Settable so an add/remove updates the rail badge and the cached list in
    /// place, without a <c>mine=true</c> refetch that would cost quota.
    /// </summary>
    public int ItemCount { get; set; }
}

/// <summary>One entry of a remote playlist.</summary>
public sealed class YouTubePlaylistItem
{
    public string ItemId { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string Title { get; init; } = "";
    public string ChannelTitle { get; init; } = "";
    public int Position { get; init; }

    /// <summary>
    /// Seconds, filled in from <c>videos.list</c>: playlistItems.snippet carries
    /// no duration, and the pane's duration column must not be blank for a
    /// remote playlist.
    /// </summary>
    public int DurationSeconds { get; set; }
}

/// <summary>A channel's identity, used to name the connection in the UI.</summary>
public sealed record YouTubeChannel(string Id, string Title);

/// <summary>
/// A Data API call Google refused, with the reason already turned into
/// something the user can act on. <see cref="Exception.Message"/> goes straight
/// to the status line, so it is written for the user, not for a log.
/// </summary>
public sealed class YouTubeApiException : Exception
{
    public YouTubeApiException(string message, HttpStatusCode? status = null, string reason = "")
        : base(message)
    {
        Status = status;
        Reason = reason;
    }

    public HttpStatusCode? Status { get; }
    public string Reason { get; }

    public bool IsQuota => Reason == "quotaExceeded" || Reason == "dailyLimitExceeded";
    public bool IsAuth => (Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && !IsQuota;
}

/// <summary>
/// YouTube Data API v3 client for playlist writes. Every call is authenticated
/// with the access token from <see cref="GoogleOAuthService"/>, which refreshes
/// transparently before expiry; a 401 triggers one forced refresh and a retry.
///
/// Only the calls this app needs are implemented:
///
///   GET    channels?part=snippet&amp;mine=true
///   GET    playlists?part=snippet,contentDetails&amp;mine=true            (paged)
///   POST   playlists?part=snippet,status
///   DELETE playlists?id=
///   GET    playlistItems?part=snippet&amp;playlistId=                     (paged)
///   GET    playlistItems?part=id&amp;playlistId=&amp;videoId=               (find item)
///   POST   playlistItems?part=snippet
///   DELETE playlistItems?id=
///
/// Quota: reads cost 1 unit per page, playlist insert 50, playlistItem insert 50,
/// deletes 50. The default budget is 10,000 units per project per day, so a few
/// hundred writes exhaust it - hence the explicit quota message below.
/// </summary>
public sealed class YouTubeDataApi
{
    private const string BaseUrl = "https://www.googleapis.com/youtube/v3/";
    private const int PageSize = 50;

    /// <summary>Quota units one playlist / playlistItem write bills to the project.</summary>
    public const int PlaylistWriteCost = 50;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GoogleOAuthService _auth;

    public YouTubeDataApi(GoogleOAuthService auth) => _auth = auth;

    // ---- reads ------------------------------------------------------------

    /// <summary>channels.list?part=snippet&amp;mine=true - the account the writes land on.</summary>
    public async Task<YouTubeChannel> GetMyChannelAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("channels?part=snippet&mine=true", ct);
        var items = Items(doc.RootElement);
        if (items.Count == 0)
            throw new YouTubeApiException(
                "Google returned no YouTube channel for this account. Create a channel at youtube.com " +
                "with this Google account, then press Reconnect.");

        var channel = items[0];
        var snippet = channel.TryGetProperty("snippet", out var s) ? s : default;
        return new YouTubeChannel(Str(channel, "id"), Str(snippet, "title"));
    }

    /// <summary>playlists?part=snippet,contentDetails&amp;mine=true, every page.</summary>
    public async Task<List<YouTubePlaylist>> ListMyPlaylistsAsync(CancellationToken ct)
    {
        var result = new List<YouTubePlaylist>();
        string? pageToken = null;

        do
        {
            var path = $"playlists?part=snippet,contentDetails&mine=true&maxResults={PageSize}";
            if (pageToken != null) path += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            using var doc = await GetAsync(path, ct);
            var root = doc.RootElement;

            foreach (var item in Items(root))
            {
                var snippet = Nested(item, "snippet");
                var details = Nested(item, "contentDetails");
                result.Add(new YouTubePlaylist
                {
                    Id = Str(item, "id"),
                    Title = Str(snippet, "title"),
                    Description = Str(snippet, "description"),
                    // playlists.list cannot carry the status fragment, so the
                    // privacy of an existing playlist is unknown here; the import
                    // treats that as "leave it alone".
                    PrivacyStatus = "",
                    ItemCount = Int(details, "itemCount"),
                });
            }

            pageToken = NextPageToken(root);
        }
        while (pageToken != null);

        return result;
    }

    /// <summary>playlistItems?part=snippet&amp;playlistId=..., every page, in playlist order.</summary>
    public async Task<List<YouTubePlaylistItem>> ListPlaylistItemsAsync(string playlistId, CancellationToken ct)
    {
        var result = new List<YouTubePlaylistItem>();
        await foreach (var page in ListPlaylistItemsPagesAsync(playlistId, ct))
            result.AddRange(page);
        return result;
    }

    /// <summary>
    /// playlistItems.list one page at a time, so the tab can paint the first 50
    /// rows of a 300-item playlist while the rest is still in flight. Same cost
    /// as <see cref="ListPlaylistItemsAsync"/>: one quota unit per page.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<YouTubePlaylistItem>> ListPlaylistItemsPagesAsync(
        string playlistId, [EnumeratorCancellation] CancellationToken ct)
    {
        string? pageToken = null;

        do
        {
            var path = $"playlistItems?part=snippet&playlistId={Uri.EscapeDataString(playlistId)}" +
                       $"&maxResults={PageSize}";
            if (pageToken != null) path += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            using var doc = await GetAsync(path, ct);
            var root = doc.RootElement;

            var page = new List<YouTubePlaylistItem>();
            foreach (var item in Items(root))
            {
                var snippet = Nested(item, "snippet");
                var resource = Nested(snippet, "resourceId");
                page.Add(new YouTubePlaylistItem
                {
                    ItemId = Str(item, "id"),
                    VideoId = Str(resource, "videoId"),
                    Title = Str(snippet, "title"),
                    ChannelTitle = Str(snippet, "videoOwnerChannelTitle"),
                    Position = Int(snippet, "position"),
                });
            }

            if (page.Count > 0) yield return page;

            pageToken = NextPageToken(root);
        }
        while (pageToken != null);
    }

    /// <summary>
    /// Durations for a set of videos, keyed by video id: <c>videos.list</c> takes
    /// up to <see cref="PageSize"/> ids per call (1 quota unit each). Used by the
    /// remote playlist pane, which otherwise has no duration to show. Unknown
    /// ids are simply absent from the result.
    /// </summary>
    public async Task<Dictionary<string, int>> GetDurationsAsync(
        IEnumerable<string> videoIds, CancellationToken ct)
    {
        var ids = videoIds.Where(IsVideoId).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var offset = 0; offset < ids.Count; offset += PageSize)
        {
            var page = ids.Skip(offset).Take(PageSize).ToList();
            var path = $"videos?part=contentDetails&id={Uri.EscapeDataString(string.Join(',', page))}" +
                       $"&maxResults={PageSize}";

            using var doc = await GetAsync(path, ct);
            foreach (var item in Items(doc.RootElement))
            {
                var id = Str(item, "id");
                var seconds = ParseIsoDuration(Str(Nested(item, "contentDetails"), "duration"));
                if (id.Length > 0 && seconds > 0) result[id] = seconds;
            }
        }

        return result;
    }

    /// <summary>ISO-8601 ("PT1H2M3S") to whole seconds; 0 when it cannot be read.</summary>
    private static int ParseIsoDuration(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        var match = IsoDurationRegex.Match(iso);
        if (!match.Success) return 0;

        var seconds = 0;
        if (match.Groups["h"].Success) seconds += int.Parse(match.Groups["h"].Value) * 3600;
        if (match.Groups["m"].Success) seconds += int.Parse(match.Groups["m"].Value) * 60;
        if (match.Groups["s"].Success) seconds += int.Parse(match.Groups["s"].Value);
        return seconds;
    }

    private static readonly Regex IsoDurationRegex = new(
        @"^P(?:\d+D)?T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The playlistItem id for a video in a playlist, or null when it is not
    /// there. playlistItems.delete takes that id, not the video id, so a delete
    /// has to look it up first.
    /// </summary>
    public async Task<string?> FindPlaylistItemIdAsync(string playlistId, string videoId, CancellationToken ct)
    {
        var path = $"playlistItems?part=id&playlistId={Uri.EscapeDataString(playlistId)}" +
                   $"&videoId={Uri.EscapeDataString(videoId)}&maxResults=1";
        using var doc = await GetAsync(path, ct);
        var items = Items(doc.RootElement);
        return items.Count == 0 ? null : Str(items[0], "id") is { Length: > 0 } id ? id : null;
    }

    // ---- writes -----------------------------------------------------------

    /// <summary>POST playlists?part=snippet,status with title, description and privacy.</summary>
    public async Task<YouTubePlaylist> CreatePlaylistAsync(
        string title, string description, string privacyStatus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A playlist needs a title.", nameof(title));

        var privacy = NormalizePrivacy(privacyStatus);
        var body = new
        {
            snippet = new { title = title.Trim(), description = description ?? "" },
            status = new { privacyStatus = privacy },
        };

        using var doc = await SendAsync(HttpMethod.Post, "playlists?part=snippet,status", body, ct);
        var root = doc.RootElement;
        var snippet = Nested(root, "snippet");
        return new YouTubePlaylist
        {
            Id = Str(root, "id"),
            Title = Str(snippet, "title") is { Length: > 0 } t ? t : title.Trim(),
            Description = Str(snippet, "description"),
            PrivacyStatus = privacy,
            ItemCount = 0,
        };
    }

    /// <summary>DELETE playlists?id=...</summary>
    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("A playlist id is required.", nameof(playlistId));

        await SendAsync(HttpMethod.Delete,
            $"playlists?id={Uri.EscapeDataString(playlistId)}", body: null, ct);
    }

    /// <summary>POST playlistItems?part=snippet - appends the video to the playlist.</summary>
    public async Task<string> InsertPlaylistItemAsync(string playlistId, string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("A playlist id is required.", nameof(playlistId));
        if (!IsVideoId(videoId))
            throw new YouTubeApiException(
                $"\"{videoId}\" is not a YouTube video id, so it cannot be added to a playlist.");

        var body = new
        {
            snippet = new
            {
                playlistId,
                resourceId = new { kind = "youtube#video", videoId },
            },
        };

        using var doc = await SendAsync(HttpMethod.Post, "playlistItems?part=snippet", body, ct);
        return Str(doc.RootElement, "id");
    }

    /// <summary>DELETE playlistItems?id=...</summary>
    public async Task DeletePlaylistItemAsync(string playlistItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playlistItemId))
            throw new ArgumentException("A playlist item id is required.", nameof(playlistItemId));

        await SendAsync(HttpMethod.Delete,
            $"playlistItems?id={Uri.EscapeDataString(playlistItemId)}", body: null, ct);
    }

    // ---- transport --------------------------------------------------------

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, path, body: null, ct);

    /// <summary>
    /// One authenticated call. A 401 forces a token refresh and retries once -
    /// Google can invalidate an access token before its stated expiry.
    /// </summary>
    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var response = await SendOnceAsync(method, path, body, ct, forceRefresh: false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _auth.InvalidateAccessToken();
            response = await SendOnceAsync(method, path, body, ct, forceRefresh: true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw TranslateError(response.StatusCode, text, path);

            // 204 No Content (deletes) has no JSON body; callers ignore the document.
            if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}");
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new YouTubeApiException(
                    $"YouTube returned a response this app could not read ({ex.Message}).", response.StatusCode);
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, object? body, CancellationToken ct, bool forceRefresh)
    {
        if (forceRefresh) _auth.InvalidateAccessToken();
        var token = await _auth.GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>
    /// Turn Google's error envelope into a sentence. The reason and message are
    /// kept verbatim so the user can search them; quota gets the explanation it
    /// deserves because 10k units a day is easy to burn through.
    /// </summary>
    private static YouTubeApiException TranslateError(HttpStatusCode status, string text, string path)
    {
        var reason = "";
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = Str(error, "message");
                if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in errors.EnumerateArray())
                    {
                        if (Str(entry, "reason") is { Length: > 0 } r) { reason = r; break; }
                    }
                }
            }
        }
        catch (JsonException)
        {
            message = text.Trim();
        }

        var detail = string.IsNullOrWhiteSpace(message) ? text.Trim() : message;
        if (detail.Length > 300) detail = detail[..300] + "\u2026";

        // The real failure, in full, for the log: the status and Google's own body
        // are the only way to tell an expired token from a missing scope.
        Log.Warn($"YouTube API {path} failed: {(int)status} {status}" +
                 (reason.Length > 0 ? $" [{reason}]" : "") + $" - {text.Trim()}");

        if (reason is "quotaExceeded" or "dailyLimitExceeded")
        {
            return new YouTubeApiException(
                "YouTube quota exhausted for this project today. Playlist writes cost " +
                $"{PlaylistWriteCost} units each and the default allowance is 10,000 units per day; reads cost 1. " +
                "Wait until the quota resets at midnight Pacific, or request more quota in Google Cloud Console " +
                $"(APIs & Services > YouTube Data API v3 > Quotas). Google said: {detail}", status, reason);
        }

        if (reason is "playlistItemsNotAccessible" or "playlistNotFound" or "forbidden")
        {
            return new YouTubeApiException(
                $"YouTube refused the change: {detail}. The playlist has to belong to the connected channel " +
                "(Import my playlists lists your own), and the OAuth client needs the youtube.force-ssl scope.",
                status, reason);
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            return new YouTubeApiException(
                $"YouTube rejected the login ({detail}). Press Reconnect on the YouTube tab.", status, reason);
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return new YouTubeApiException(
                $"YouTube refused access: {detail}" +
                (reason.Length > 0 ? $" [{reason}]" : "") +
                ". Check that the YouTube Data API v3 is enabled for the project and that the app was " +
                "authorized with the youtube.force-ssl scope.", status, reason);
        }

        if (status == HttpStatusCode.NotFound)
            return new YouTubeApiException($"YouTube could not find that item ({detail}).", status, reason);

        return new YouTubeApiException(
            $"YouTube request failed ({(int)status} {status})" +
            (reason.Length > 0 ? $" [{reason}]" : "") + $": {detail}", status, reason);
    }

    // ---- json helpers -----------------------------------------------------

    private static List<JsonElement> Items(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return new List<JsonElement>();
        return items.EnumerateArray().ToList();
    }

    private static string? NextPageToken(JsonElement root) =>
        Str(root, "nextPageToken") is { Length: > 0 } token ? token : null;

    private static JsonElement Nested(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    private static bool IsVideoId(string? videoId) =>
        videoId is { Length: 11 } && VideoIdRegex.IsMatch(videoId);

    private static readonly Regex VideoIdRegex = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private static string NormalizePrivacy(string privacy) =>
        privacy?.Trim().ToLowerInvariant() switch
        {
            "public" => "public",
            "unlisted" => "unlisted",
            // Anything unexpected becomes private: the safe default for a write.
            _ => "private",
        };
}
