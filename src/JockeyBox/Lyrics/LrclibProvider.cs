using System.Net.Http;
using System.Text.Json;
using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// Zero-configuration fallback using the free LRCLIB service. Queries by ID3
/// Title/Artist(/Album/duration) directly - no track ID or auth required.
/// Docs: https://lrclib.net/docs
/// </summary>
public class LrclibProvider : ILyricsProvider
{
    private readonly HttpClient _http;

    public LrclibProvider(HttpClient http) => _http = http;

    public string Name => "LRCLIB";

    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;

        try
        {
            // Exact-signature lookup first (best hit rate with duration).
            var exactUrl = "https://lrclib.net/api/get"
                + $"?track_name={Uri.EscapeDataString(track.Title)}"
                + $"&artist_name={Uri.EscapeDataString(track.Artist)}"
                + (string.IsNullOrWhiteSpace(track.Album) ? "" : $"&album_name={Uri.EscapeDataString(track.Album)}")
                + (track.Duration > TimeSpan.Zero ? $"&duration={(int)Math.Round(track.Duration.TotalSeconds)}" : "");

            var doc = await FetchAsync(exactUrl, singleObject: true, ct);
            if (doc != null) return doc;

            // Fuzzy search as a second chance.
            var searchUrl = "https://lrclib.net/api/search"
                + $"?track_name={Uri.EscapeDataString(track.Title)}"
                + (string.IsNullOrWhiteSpace(track.Artist) ? "" : $"&artist_name={Uri.EscapeDataString(track.Artist)}");
            return await FetchAsync(searchUrl, singleObject: false, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private async Task<LyricsDocument?> FetchAsync(string url, bool singleObject, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        if (singleObject)
            return ParseRecord(root);

        // /api/search returns an array; take the first record with synced lyrics,
        // else the first with plain lyrics.
        LyricsDocument? plainFallback = null;
        foreach (var item in root.EnumerateArray())
        {
            var doc = ParseRecord(item);
            if (doc == null) continue;
            if (doc.Synced) return doc;
            plainFallback ??= doc;
        }
        return plainFallback;
    }

    private LyricsDocument? ParseRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) return null;

        if (record.TryGetProperty("syncedLyrics", out var synced)
            && synced.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(synced.GetString()))
        {
            return LrcParser.Parse(synced.GetString()!, Name);
        }

        if (record.TryGetProperty("plainLyrics", out var plain)
            && plain.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(plain.GetString()))
        {
            var lines = plain.GetString()!.Split('\n').Select(l => l.TrimEnd('\r'));
            return LyricsDocument.Unsynced(lines, Name);
        }
        return null;
    }
}
