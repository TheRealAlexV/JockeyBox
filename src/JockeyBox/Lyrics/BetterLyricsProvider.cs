using System.Net;
using System.Net.Http;
using System.Text.Json;
using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// Better Lyrics (https://api.betterlyrics.org), which serves Apple-Music-derived
/// syllable-level TTML - the richest word timing available.
///
/// Endpoint:
///   GET https://api.betterlyrics.org/getLyrics?s=&lt;title&gt;&amp;a=&lt;artist&gt;&amp;al=&lt;album&gt;&amp;d=&lt;seconds&gt;
/// Response: <c>{ "ttml": "&lt;tt …&gt;", "score": 0.97 }</c>
///
/// New API keys are paused: the service answers cached titles without a key and
/// everything else with <c>401 {"error":"API key required"}</c>. JockeyBox sends
/// no key, treats 401/403/429 as "unavailable" and backs off for a few minutes so
/// a library full of uncached tracks cannot hammer the endpoint (observed budget:
/// <c>x-ratelimit-limit: 15</c>). The log line is written once per back-off.
/// </summary>
public class BetterLyricsProvider : ILyricsProvider
{
    private const string BaseUrl = "https://api.betterlyrics.org/getLyrics";

    private static readonly TokenBucket Budget = TokenBucket.BetterLyrics();

    private static readonly TimeSpan KeylessBackoff = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static DateTime _unavailableUntil = DateTime.MinValue;

    private readonly HttpClient _http;

    public BetterLyricsProvider(HttpClient http) => _http = http;

    public string Id => "betterlyrics";

    public string Name => "Better Lyrics";

    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title))
        {
            LyricsTrace.Note(Id, "no title");
            return null;
        }

        lock (Gate)
        {
            if (DateTime.UtcNow < _unavailableUntil)
            {
                LyricsTrace.Note(Id, "in back-off (no API key)");
                return null;
            }
        }

        var logKey = ProviderLog.Key(Id, track.Artist, track.Title);

        try
        {
            var url = BaseUrl
                + $"?s={Uri.EscapeDataString(track.Title)}"
                + $"&a={Uri.EscapeDataString(track.Artist ?? "")}"
                + $"&al={Uri.EscapeDataString(track.Album ?? "")}"
                + (track.Duration > TimeSpan.Zero
                    ? $"&d={(int)Math.Round(track.Duration.TotalSeconds)}"
                    : "");

            if (!await Budget.WaitAsync(ct))
            {
                LyricsTrace.Note(Id, "local rate budget exhausted");
                return null;
            }

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests)
            {
                // Keyless reads only work for cached titles; nothing else to try.
                var backoff = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? RetryAfterDelay(response.Headers.RetryAfter)
                    : KeylessBackoff;

                var until = DateTime.UtcNow + backoff;
                lock (Gate)
                {
                    // Never shorten an existing back-off.
                    if (until > _unavailableUntil) _unavailableUntil = until;
                }

                ProviderLog.WarnOncePerSession(
                    Id + "|" + response.StatusCode,
                    $"betterlyrics: unavailable (HTTP {(int)response.StatusCode}) - pausing lookups for "
                    + $"{backoff.TotalMinutes:0.#} min; cached titles still resolve");
                LyricsTrace.Note(Id, $"HTTP {(int)response.StatusCode} (no API key)");
                return null;
            }

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                LyricsTrace.Note(Id, "404");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                ProviderLog.WarnOnce(logKey,
                    $"betterlyrics: HTTP {(int)response.StatusCode} for '{track.Artist} - {track.Title}'");
                LyricsTrace.Note(Id, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var ttml = ExtractTtml(json);
            if (ttml is null)
            {
                LyricsTrace.Note(Id, "no TTML in response");
                return null;
            }

            var doc = TtmlParser.Parse(ttml, Name, Id);
            if (doc is null)
            {
                ProviderLog.WarnOnce(logKey,
                    $"betterlyrics: could not parse TTML for '{track.Artist} - {track.Title}'");
                LyricsTrace.Note(Id, "unparseable TTML");
                return null;
            }

            // No tiling here: word spans are built at load time, after the
            // instrumental marker exists (MainViewModel's lyrics pass).
            return doc;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProviderLog.WarnOnce(logKey,
                $"betterlyrics: lookup failed for '{track.Artist} - {track.Title}': {ex.Message}");
            LyricsTrace.Note(Id, "error: " + ex.Message);
            return null;
        }
    }

    /// <summary>Pull the TTML string out of the <c>{ "ttml": … }</c> envelope.</summary>
    internal static string? ExtractTtml(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var payload = JsonDocument.Parse(json);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("ttml", out var ttml)) return null;
            if (ttml.ValueKind != JsonValueKind.String) return null;

            var text = ttml.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan RetryAfterDelay(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter)
    {
        var fallback = TimeSpan.FromSeconds(60);
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta > KeylessBackoff ? KeylessBackoff : delta;

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait > KeylessBackoff ? KeylessBackoff : wait;
        }

        return fallback;
    }
}
