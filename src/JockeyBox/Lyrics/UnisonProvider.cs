using System.Net;
using System.Net.Http;
using System.Text.Json;
using JockeyBox.Library;

namespace JockeyBox.Lyrics;

/// <summary>
/// Unison community-synced lyrics (https://unison.boidu.dev), keyed by YouTube
/// video id. The id comes from the download ledger for the track's file
/// (<see cref="VideoIdLookup"/>); without one the provider is skipped, so it
/// only ever serves tracks imported from YouTube.
///
/// Endpoint (no API key), per the project's docs page:
///   GET https://unison.boidu.dev/lyrics?v=&lt;videoId&gt;
/// Response:
/// <code>
/// { "success": true,
///   "data": { "videoId": "...", "format": "ttml" | "lrc" | "plain",
///             "syncType": "richsync" | "linesync", "lyrics": "..." } }
/// </code>
/// A miss is <c>404 { "success": false, "code": "NOT_FOUND" }</c> and is not an
/// error worth logging - most videos are simply not in their index.
/// </summary>
public class UnisonProvider : ILyricsProvider
{
    private const string LyricsUrl = "https://unison.boidu.dev/lyrics";

    private readonly HttpClient _http;

    public UnisonProvider(HttpClient http) => _http = http;

    public string Id => "unison";

    public string Name => "Unison";

    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        var videoId = VideoIdLookup.ForTrack(track);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            // No YouTube identity in the download ledger: nothing to ask.
            LyricsTrace.Note(Id, "no YouTube id in ledger");
            return null;
        }

        var logKey = ProviderLog.Key(Id, track.Artist, track.Title);

        try
        {
            var url = LyricsUrl + "?v=" + Uri.EscapeDataString(videoId);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                LyricsTrace.Note(Id, "404 for video id");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                ProviderLog.WarnOnce(logKey,
                    $"unison: HTTP {(int)response.StatusCode} for video {videoId}");
                LyricsTrace.Note(Id, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var payload = JsonDocument.Parse(json);

            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

            string? format = data.TryGetProperty("format", out var formatElement)
                && formatElement.ValueKind == JsonValueKind.String
                    ? formatElement.GetString()
                    : null;

            string? lyrics = data.TryGetProperty("lyrics", out var lyricsElement)
                && lyricsElement.ValueKind == JsonValueKind.String
                    ? lyricsElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(lyrics))
            {
                LyricsTrace.Note(Id, "empty lyrics payload");
                return null;
            }

            var doc = ParsePayload(lyrics!, format);
            if (doc is null)
            {
                ProviderLog.WarnOnce(logKey,
                    $"unison: could not parse '{format ?? "unknown"}' lyrics for video {videoId}");
                return null;
            }

            // Line-level documents get estimated word spans; a plain (unsynced)
            // payload has no times at all and stays unsynced.
            if (doc.Synced && !doc.HasWordTimings) WordTimeline.Synthesize(doc, track.Duration);
            return doc;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProviderLog.WarnOnce(logKey, $"unison: lookup failed for video {videoId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turn the <c>lyrics</c>/<c>format</c> pair into a document. TTML goes
    /// through <see cref="TtmlParser"/>, LRC through <see cref="LrcParser"/>, and
    /// plain text stays unsynced (it has no times to invent). An unknown format
    /// is sniffed from the text so a renamed field cannot lose usable lyrics.
    /// </summary>
    internal static LyricsDocument? ParsePayload(string lyrics, string? format)
    {
        var kind = (format ?? "").Trim().ToLowerInvariant();

        if (kind.Length == 0)
        {
            var trimmed = lyrics.TrimStart();
            if (trimmed.StartsWith('<')) kind = "ttml";
            else if (trimmed.StartsWith('[')) kind = "lrc";
            else kind = "plain";
        }

        switch (kind)
        {
            case "ttml":
            case "xml":
                return TtmlParser.Parse(lyrics, "Unison", "unison");

            case "lrc":
            {
                var parsed = LrcParser.Parse(lyrics, "Unison", "unison");
                return parsed is { Synced: true } ? parsed : null;
            }

            case "plain":
            case "text":
            {
                var lines = lyrics
                    .Split('\n')
                    .Select(static l => l.TrimEnd('\r', ' '))
                    .Where(static l => l.Trim().Length > 0)
                    .ToList();
                if (lines.Count == 0) return null;
                return LyricsDocument.Unsynced(lines, "Unison", "unison");
            }

            default:
                return null;
        }
    }
}
