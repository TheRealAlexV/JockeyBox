using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Spotify;

/// <summary>
/// A Spotify Web API call failed. <see cref="StatusCode"/> is the HTTP status
/// (401 = not signed in / token rejected, 403 = missing scope, 429 = rate limit,
/// 502 = unreadable body).
/// </summary>
public sealed class SpotifyApiException : Exception
{
    public int StatusCode { get; }

    /// <summary>Raw response body, when there was one - the only place Spotify explains itself.</summary>
    public string? Body { get; }

    public SpotifyApiException(string message, int statusCode, string? body) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }
}

/// <summary>
/// Thin Spotify Web API client for the import screens: who am I, which
/// playlists do I have, and what is in them.
///
/// Tokens come from <see cref="SpotifyOAuthService"/>; this class never runs the
/// OAuth flow itself. It handles the three failure modes that matter in
/// practice - a rejected token (refresh once and retry), rate limiting (429 with
/// Retry-After) and a missing scope (403, needs a re-authorisation).
/// </summary>
public sealed class SpotifyApi
{
    private const string BaseUrl = "https://api.spotify.com/v1";

    /// <summary>Hard ceiling on a single listing so a runaway `next` chain cannot spin forever.</summary>
    private const int MaxItems = 10_000;

    /// <summary>Belt-and-braces page cap (100 items/page → well under <see cref="MaxItems"/>).</summary>
    private const int MaxPages = 500;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppSettings _settings;
    private readonly SpotifyOAuthService _oauth;
    private readonly HttpClient _http;

    public SpotifyApi(AppSettings settings, SpotifyOAuthService oauth)
    {
        _settings = settings;
        _oauth = oauth;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ---- account --------------------------------------------------------------

    /// <summary>The signed-in account, or null when Spotify returns a payload without an id.</summary>
    public async Task<SpotifyUser?> GetCurrentUserAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{BaseUrl}/me", ct);
        var root = doc.RootElement;

        var id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var displayName = GetString(root, "display_name");
        return new SpotifyUser(
            id,
            string.IsNullOrWhiteSpace(displayName) ? id : displayName,
            FirstImageUrl(root));
    }

    /// <summary>
    /// Cheap probe for whether the current grant includes the playlist scopes.
    /// Reads a single playlist and treats a 403 as "no" (every other failure is
    /// a real error and is rethrown).
    /// </summary>
    public async Task<bool> HasPlaylistAccessAsync(CancellationToken ct)
    {
        var url = $"{BaseUrl}/me/playlists?limit=1";
        using var response = await SendAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Log.Debug("Spotify playlist access probe: 403 (playlist scopes not granted).");
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw CreateException(url, response.StatusCode, body);
        }

        return true;
    }

    // ---- playlists ------------------------------------------------------------

    /// <summary>All playlists of the signed-in user (paged at 50, hard cap 10k).</summary>
    public async Task<List<SpotifyPlaylistInfo>> GetPlaylistsAsync(CancellationToken ct)
    {
        var result = new List<SpotifyPlaylistInfo>();
        string? url = $"{BaseUrl}/me/playlists?limit=50";
        var pages = 0;

        while (!string.IsNullOrEmpty(url) && pages < MaxPages && result.Count < MaxItems)
        {
            using var doc = await GetJsonAsync(url, ct);
            pages++;

            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (result.Count >= MaxItems) break;
                    var playlist = ParsePlaylist(item);
                    if (playlist != null) result.Add(playlist);
                }
            }

            Log.Debug($"Spotify playlists: page {pages}, {result.Count} playlists so far.");
            url = GetNextUrl(root);
        }

        if (pages >= MaxPages) Log.Warn($"Spotify playlists: stopped at the {MaxPages}-page cap ({result.Count} playlists).");
        return result;
    }

    /// <summary>
    /// The synthetic "Liked Songs" entry. The count comes from `GET /me/tracks`
    /// with limit=1 (Spotify still returns the full total), so describing a
    /// 10k-track library costs one small request.
    /// </summary>
    public async Task<List<SpotifyPlaylistInfo>> GetLikedSongsInfoAsync(CancellationToken ct)
    {
        var total = 0;
        using (var doc = await GetJsonAsync($"{BaseUrl}/me/tracks?limit=1", ct))
        {
            total = GetInt(doc.RootElement, "total") ?? 0;
        }

        var user = await GetCurrentUserAsync(ct);

        return new List<SpotifyPlaylistInfo>
        {
            new(
                Id: "liked",
                Name: "Liked Songs",
                TrackCount: total,
                ImageUrl: "",
                OwnerName: user?.DisplayName ?? "",
                IsLikedSongs: true),
        };
    }

    // ---- tracks ---------------------------------------------------------------

    /// <summary>
    /// Every playable track in a playlist. Tries the current `/items` endpoint
    /// first and silently falls back to the older `/tracks` one on 404, because
    /// which of the two exists depends on the API rollout the account is on.
    /// </summary>
    public async Task<List<SourceTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(playlistId);

        var url = $"{BaseUrl}/playlists/{id}/items?limit=100";
        var page = await TryGetJsonAsync(url, ct);
        if (page == null)
        {
            Log.Debug($"Spotify playlists/{playlistId}/items returned 404; falling back to /tracks.");
            url = $"{BaseUrl}/playlists/{id}/tracks?limit=100";
            page = await GetJsonAsync(url, ct);
        }

        var result = new List<SourceTrack>();
        var pages = 0;

        try
        {
            while (page != null && pages < MaxPages && result.Count < MaxItems)
            {
                using (page)
                {
                    pages++;
                    AppendTracks(page.RootElement, null, result);
                    Log.Debug($"Spotify playlist {playlistId}: page {pages}, {result.Count} tracks so far.");
                    url = GetNextUrl(page.RootElement);
                }

                page = string.IsNullOrEmpty(url) ? null : await GetJsonAsync(url, ct);
            }
        }
        finally
        {
            page?.Dispose();
        }

        if (pages >= MaxPages) Log.Warn($"Spotify playlist {playlistId}: stopped at the {MaxPages}-page cap ({result.Count} tracks).");
        return result;
    }

    /// <summary>Every playable track from the user's Liked Songs.</summary>
    public async Task<List<SourceTrack>> GetLikedSongsAsync(CancellationToken ct)
    {
        var result = new List<SourceTrack>();
        string? url = $"{BaseUrl}/me/tracks?limit=50";
        var pages = 0;

        while (!string.IsNullOrEmpty(url) && pages < MaxPages && result.Count < MaxItems)
        {
            using var doc = await GetJsonAsync(url, ct);
            pages++;

            AppendTracks(doc.RootElement, "Liked Songs", result);
            Log.Debug($"Spotify liked songs: page {pages}, {result.Count} tracks so far.");
            url = GetNextUrl(doc.RootElement);
        }

        if (pages >= MaxPages) Log.Warn($"Spotify liked songs: stopped at the {MaxPages}-page cap ({result.Count} tracks).");
        return result;
    }

    /// <summary>One track by id, or null when it no longer exists.</summary>
    public async Task<SourceTrack?> GetTrackAsync(string trackId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/tracks/{Uri.EscapeDataString(trackId)}";
        using var doc = await TryGetJsonAsync(url, ct);
        if (doc == null) return null;

        var dto = DeserializeTrack(doc.RootElement);
        return dto is { IsPlayable: true } ? dto.ToSourceTrack(null) : null;
    }

    // ---- parsing helpers ------------------------------------------------------

    private static SpotifyPlaylistInfo? ParsePlaylist(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var name = GetString(item, "name");
        var owner = item.TryGetProperty("owner", out var ownerEl)
            ? GetString(ownerEl, "display_name") ?? ""
            : "";

        return new SpotifyPlaylistInfo(
            id,
            string.IsNullOrWhiteSpace(name) ? "Untitled playlist" : name,
            GetTrackCount(item),
            FirstImageUrl(item),
            owner,
            IsLikedSongs: false);
    }

    /// <summary>`tracks.total` (legacy) → `items.total` (current) → array length.</summary>
    private static int GetTrackCount(JsonElement playlist)
    {
        foreach (var name in new[] { "tracks", "items" })
        {
            if (!playlist.TryGetProperty(name, out var container) || container.ValueKind != JsonValueKind.Object)
                continue;

            if (GetInt(container, "total") is { } total) return total;

            if (container.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                return items.GetArrayLength();
        }

        return 0;
    }

    /// <summary>
    /// Append the playable tracks of one page. Accepts both envelope shapes
    /// (`items[].track` for saved tracks, `items[].item` on the newer playlist
    /// endpoint) and drops local files, episodes and id-less entries.
    /// </summary>
    private static void AppendTracks(JsonElement root, string? playlistName, List<SourceTrack> target)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;

        foreach (var entry in items.EnumerateArray())
        {
            if (target.Count >= MaxItems) return;
            if (entry.ValueKind != JsonValueKind.Object) continue;

            if (!TryGetTrackObject(entry, out var track)) continue;

            var dto = DeserializeTrack(track);
            if (dto is not { IsPlayable: true }) continue;

            target.Add(dto.ToSourceTrack(playlistName));
        }
    }

    private static bool TryGetTrackObject(JsonElement entry, out JsonElement track)
    {
        track = default;

        if (entry.TryGetProperty("track", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            track = legacy;
            return true;
        }

        if (entry.TryGetProperty("item", out var current) && current.ValueKind == JsonValueKind.Object)
        {
            track = current;
            return true;
        }

        return false;
    }

    private static SpotifyTrackDto? DeserializeTrack(JsonElement element)
    {
        try
        {
            return element.Deserialize<SpotifyTrackDto>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetNextUrl(JsonElement root) =>
        root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString() ?? ""
            : "";

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string FirstImageUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        if (!element.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return "";

        foreach (var image in images.EnumerateArray())
        {
            var url = GetString(image, "url");
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return "";
    }

    // ---- HTTP -----------------------------------------------------------------

    /// <summary>
    /// Send a GET and return the response for the caller to inspect and dispose.
    /// Handles the two retryable failures: a rejected token (refresh once) and
    /// 429 with Retry-After (up to two waits, each capped at 10s).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        var authRetried = false;
        var rateLimitRetries = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();

                if (authRetried)
                {
                    Log.Warn($"Spotify rejected a freshly refreshed token (401) on {url}.");
                    throw new SpotifyApiException("Spotify rejected the access token. Sign in again.", 401, body);
                }

                var refreshed = await ForceRefreshAsync(token, ct);
                if (refreshed == null) throw new SpotifyApiException("Spotify rejected the access token. Sign in again.", 401, body);

                token = refreshed;
                authRetried = true;
                Log.Debug($"Spotify 401 on {url}; retrying once with a refreshed token.");
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var delay = GetRetryDelay(response);
                response.Dispose();

                if (rateLimitRetries >= 2)
                {
                    Log.Warn($"Spotify rate limit: giving up on {url} after {rateLimitRetries} retries.");
                    throw new SpotifyApiException("Spotify is rate limiting this request. Try again shortly.", 429, body);
                }

                rateLimitRetries++;
                Log.Warn($"Spotify rate limit on {url}; retry {rateLimitRetries}/2 in {delay.TotalSeconds:0.#}s.");
                await Task.Delay(delay, ct);
                continue;
            }

            return response;
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var token = await _oauth.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new SpotifyApiException("Not signed in to Spotify", 401, null);

        return token;
    }

    /// <summary>
    /// Make the OAuth service actually refresh: expire the cached token first,
    /// otherwise it would hand back the very value Spotify just rejected.
    /// Returns null when no different token could be obtained.
    /// </summary>
    private async Task<string?> ForceRefreshAsync(string staleToken, CancellationToken ct)
    {
        _settings.SpotifyTokenExpiry = DateTime.MinValue;
        _settings.Save();

        var token = await _oauth.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token) || string.Equals(token, staleToken, StringComparison.Ordinal))
        {
            Log.Warn("Spotify access token could not be refreshed; the user needs to sign in again.");
            return null;
        }

        return token;
    }

    /// <summary>GET that maps 404 to null (endpoint or entity missing).</summary>
    private async Task<JsonDocument?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Debug($"Spotify 404 on {url}.");
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw CreateException(url, response.StatusCode, body);

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Spotify returned unreadable JSON for {url}: {ex.Message}");
            throw new SpotifyApiException("Spotify returned an unreadable response.", 502, body);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) =>
        await TryGetJsonAsync(url, ct)
        ?? throw CreateException(url, HttpStatusCode.NotFound, null);

    private static SpotifyApiException CreateException(string url, HttpStatusCode status, string? body)
    {
        var code = (int)status;
        var message = status switch
        {
            HttpStatusCode.Forbidden =>
                "Spotify denied this request - re-authorise with playlist scopes (log out, then log in again).",
            HttpStatusCode.NotFound => "Spotify item not found.",
            HttpStatusCode.Unauthorized => "Spotify rejected the access token. Sign in again.",
            _ => $"Spotify request failed (HTTP {code}).",
        };

        Log.Warn($"Spotify {code} on {url}: {Truncate(body, 300)}");
        return new SpotifyApiException(message, code, body);
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        TimeSpan delay;
        if (retryAfter?.Delta is { } delta) delay = delta;
        else if (retryAfter?.Date is { } date) delay = date - DateTimeOffset.UtcNow;
        else delay = DefaultRetryDelay;

        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];
}
