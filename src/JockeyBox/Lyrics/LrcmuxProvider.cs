using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// lrcmux word-level lyrics (https://api.lrcmux.dev). The only word-capable
/// source that works from artist/title alone, so it covers Spotify-sourced
/// tracks that have no YouTube id in the download ledger.
///
/// Endpoint (no API key):
///   GET https://api.lrcmux.dev/get?artist=&lt;a&gt;&amp;title=&lt;t&gt;&amp;level=word&amp;format=json
/// Response: <c>{ "lines": [ { "text", "start", "end", "words": [ { "text", "start", "end" } ] } ] }</c>
/// with all times in milliseconds.
///
/// Rate limit: 60 requests/minute. Guarded by <see cref="TokenBucket.Lrcmux"/>
/// plus a single retry that honours <c>Retry-After</c> on 429; after that the
/// provider fails quiet for this track and the chain moves on.
/// </summary>
public class LrcmuxProvider : ILyricsProvider
{
    private const string BaseUrl = "https://api.lrcmux.dev";

    private static readonly TokenBucket Budget = TokenBucket.Lrcmux();

    private readonly HttpClient _http;

    public LrcmuxProvider(HttpClient http) => _http = http;

    public string Id => "lrcmux";

    public string Name => "lrcmux";

    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        // The API is keyed by artist+title; without an artist the query is noise.
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return null;

        var logKey = ProviderLog.Key(Id, track.Artist, track.Title);

        try
        {
            var url = BaseUrl + "/get"
                + $"?artist={Uri.EscapeDataString(track.Artist ?? "")}"
                + $"&title={Uri.EscapeDataString(track.Title)}"
                + "&level=word&format=json";

            var json = await GetWithRetryAsync(url, logKey, ct);
            if (json is null) return null;

            var doc = LrcmuxParser.Parse(json, Name, Id);
            if (doc is null)
            {
                ProviderLog.WarnOnce(logKey, $"lrcmux: unusable JSON payload for '{track.Artist} - {track.Title}'");
                return null;
            }

            if (!doc.HasWordTimings) WordTimeline.Synthesize(doc, track.Duration);
            return doc;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProviderLog.WarnOnce(logKey, $"lrcmux: lookup failed for '{track.Artist} - {track.Title}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// One GET; on 429 waits for <c>Retry-After</c> (or 2 s) and retries once.
    /// Returns the body only for 200 responses.
    /// </summary>
    private async Task<string?> GetWithRetryAsync(string url, string logKey, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!await Budget.WaitAsync(ct)) return null;

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == 1)
                {
                    ProviderLog.WarnOnce(logKey, "lrcmux: still rate limited after one retry; skipping this track");
                    return null;
                }

                var delay = RetryAfterDelay(response.Headers.RetryAfter);
                await Task.Delay(delay, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                ProviderLog.WarnOnce(logKey,
                    $"lrcmux: HTTP {(int)response.StatusCode} for '{url}'");
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }

        return null;
    }

    private static TimeSpan RetryAfterDelay(RetryConditionHeaderValue? retryAfter)
    {
        var fallback = TimeSpan.FromSeconds(2);
        if (retryAfter is null) return fallback;

        if (retryAfter.Delta is { } delta)
        {
            if (delta < TimeSpan.Zero) return fallback;
            // Never sleep longer than a track change would; the provider fails quiet instead.
            return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait <= TimeSpan.Zero) return fallback;
            return wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait;
        }

        return fallback;
    }
}
