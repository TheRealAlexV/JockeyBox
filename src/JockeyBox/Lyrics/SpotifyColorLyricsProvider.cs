using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using JockeyBox.Library;
using JockeyBox.Settings;

namespace JockeyBox.Lyrics;

/// <summary>
/// Lyrics directly from Spotify's internal color-lyrics API using the OAuth
/// access token. No self-hosted server or SP_DC cookie needed — the PKCE
/// token is sufficient.
///
/// Endpoint: spclient.wg.spotify.com/color-lyrics/v2/track/{id}?format=json
/// </summary>
public class SpotifyColorLyricsProvider : ILyricsProvider
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly SpotifyOAuthService _oauth;

    public SpotifyColorLyricsProvider(HttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
        _oauth = new SpotifyOAuthService(settings);
    }

    public string Name => "Spotify Color Lyrics";

    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        if (!_oauth.IsLoggedIn || string.IsNullOrWhiteSpace(track.Title))
            return null;

        try
        {
            var token = await _oauth.GetAccessTokenAsync(ct);
            if (token == null) return null;

            // Step 1: search for the track
            var trackId = await SearchTrackIdAsync(token, track, ct);
            if (trackId == null) return null;

            // Step 2: fetch color lyrics
            return await FetchColorLyricsAsync(token, trackId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<string?> SearchTrackIdAsync(string token, TrackEntry track, CancellationToken ct)
    {
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
            if (score < bestScore) { bestScore = score; bestId = id; }
        }
        return bestId ?? firstId;
    }

    private async Task<LyricsDocument?> FetchColorLyricsAsync(string token, string trackId, CancellationToken ct)
    {
        if (!trackId.All(char.IsAsciiLetterOrDigit)) return null;

        var url = $"https://spclient.wg.spotify.com/color-lyrics/v2/track/{trackId}?format=json&market=from_token";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // The spclient endpoint requires specific headers that mimic the web player
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
        request.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://open.spotify.com/");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        if (!root.TryGetProperty("lyrics", out var lyrics)) return null;
        if (!lyrics.TryGetProperty("lines", out var lines)) return null;

        var synced = lyrics.TryGetProperty("syncType", out var st)
                     && st.GetString() == "LINE_SYNCED";

        var parsed = new List<LyricLine>();
        foreach (var line in lines.EnumerateArray())
        {
            var words = line.TryGetProperty("words", out var w) ? w.GetString() ?? "" : "";
            var time = TimeSpan.Zero;
            if (line.TryGetProperty("startTimeMs", out var tMs) && tMs.GetString() is string msText
                && long.TryParse(msText, out var ms))
                time = TimeSpan.FromMilliseconds(ms);
            parsed.Add(new LyricLine(time, words));
        }

        if (parsed.Count == 0) return null;
        return new LyricsDocument { Synced = synced, Lines = parsed, Source = Name };
    }
}
