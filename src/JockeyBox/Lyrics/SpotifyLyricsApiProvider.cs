using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using JockeyBox.Library;
using JockeyBox.Settings;

namespace JockeyBox.Lyrics;

/// <summary>
/// Lyrics via a self-hosted instance of akashrchandran/spotify-lyrics-api.
///
/// Pipeline:
///   1. Spotify Web API client-credentials token (cached until expiry)
///   2. /v1/search with the track's ID3 Title + Artist -> best-matching track ID
///      (scored by title/artist match and duration proximity)
///   3. GET {LyricsApiBaseUrl}/?trackid={id}&format=lrc -> line-synced lyrics
///
/// Inactive (returns null) until LyricsApiBaseUrl + Spotify credentials are
/// set in Settings, letting LyricsService fall through to LRCLIB.
/// </summary>
public class SpotifyLyricsApiProvider : ILyricsProvider
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly SpotifyOAuthService _oauth;

    private string? _accessToken;
    private DateTime _tokenExpiryUtc = DateTime.MinValue;

    public SpotifyLyricsApiProvider(HttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
        _oauth = new SpotifyOAuthService(settings);
    }

    public string Name => "Spotify Lyrics API";

    private bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.LyricsApiBaseUrl)
        && (_oauth.IsLoggedIn
            || (!string.IsNullOrWhiteSpace(_settings.SpotifyClientId)
                && !string.IsNullOrWhiteSpace(_settings.SpotifyClientSecret)));

    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(track.Title)) return null;

        try
        {
            var trackId = await SearchTrackIdAsync(track, ct);
            if (trackId == null) return null;
            return await FetchLyricsAsync(trackId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // network/instance issues - fall through to next provider
        }
    }

    // ---- Step 1: token -------------------------------------------------

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        // 1. Try OAuth PKCE (interactive login)
        if (_oauth.IsLoggedIn)
        {
            var oauthToken = await _oauth.GetAccessTokenAsync(ct);
            if (oauthToken != null) return oauthToken;
        }

        // 2. Fall back to legacy client credentials
        if (!string.IsNullOrWhiteSpace(_settings.SpotifyClientId)
            && !string.IsNullOrWhiteSpace(_settings.SpotifyClientSecret))
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiryUtc)
                return _accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                $"{_settings.SpotifyClientId}:{_settings.SpotifyClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
            });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _accessToken = json.RootElement.GetProperty("access_token").GetString();
            var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiryUtc = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            return _accessToken;
        }

        return null;
    }

    // ---- Step 2: search ------------------------------------------------

    private async Task<string?> SearchTrackIdAsync(TrackEntry track, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        if (token == null) return null;

        var query = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title
            : $"track:{track.Title} artist:{track.Artist}";
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=5";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("tracks", out var tracks)
            || !tracks.TryGetProperty("items", out var items)) return null;

        // Best match: smallest duration difference, must be within 5 seconds
        // when we know our local duration.
        string? bestId = null;
        string? firstId = null;
        double bestScore = double.MaxValue;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idProp) || idProp.GetString() is not string id)
                continue;
            firstId ??= id;
            double durationMs = item.TryGetProperty("duration_ms", out var d)
                                && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            double score = track.Duration > TimeSpan.Zero && durationMs > 0
                ? Math.Abs(durationMs - track.Duration.TotalMilliseconds)
                : 0;
            if (track.Duration > TimeSpan.Zero && score > 5000) continue;
            if (score < bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }
        // Fall back to first result when duration filtering rejected everything.
        return bestId ?? firstId;
    }

    // ---- Step 3: lyrics ------------------------------------------------

    private async Task<LyricsDocument?> FetchLyricsAsync(string trackId, CancellationToken ct)
    {
        // Spotify track IDs are base62; reject anything else so a hostile
        // search response can't inject paths/params into the lyrics URL.
        if (!trackId.All(char.IsAsciiLetterOrDigit)) return null;

        var baseUrl = _settings.LyricsApiBaseUrl!.TrimEnd('/');
        using var response = await _http.GetAsync(
            $"{baseUrl}/?trackid={Uri.EscapeDataString(trackId)}&format=lrc", ct);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (root.TryGetProperty("error", out var err) && err.GetBoolean()) return null;
        if (!root.TryGetProperty("lines", out var lines)) return null;

        var synced = root.TryGetProperty("syncType", out var st)
                     && st.GetString() == "LINE_SYNCED";

        var parsed = new List<LyricLine>();
        foreach (var line in lines.EnumerateArray())
        {
            var words = line.TryGetProperty("words", out var w) ? w.GetString() ?? "" : "";
            var time = TimeSpan.Zero;
            if (line.TryGetProperty("timeTag", out var tag) && tag.GetString() is string tagText)
                time = LrcParser.ParseTimeTag(tagText);
            parsed.Add(new LyricLine(time, words));
        }

        if (parsed.Count == 0) return null;
        return new LyricsDocument { Synced = synced, Lines = parsed, Source = Name };
    }
}
