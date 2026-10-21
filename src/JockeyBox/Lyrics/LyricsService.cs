using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JockeyBox.Library;
using JockeyBox.Settings;

namespace JockeyBox.Lyrics;

/// <summary>
/// Orchestrates the provider chain (local .lrc sidecar -> self-hosted Spotify
/// Lyrics API -> LRCLIB) and caches successful results on disk so a track only
/// ever hits the network once.
/// </summary>
public class LyricsService
{
    private readonly List<ILyricsProvider> _providers;
    private readonly AppSettings _settings;
    private readonly string _cacheDir;

    public LyricsService(AppSettings settings)
    {
        _settings = settings;
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            // Cap responses so a hostile/misconfigured endpoint can't exhaust memory.
            MaxResponseContentBufferSize = 5 * 1024 * 1024,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JockeyBox/1.0 (karaoke player)");

        _providers = new List<ILyricsProvider>
        {
            new LrcSidecarProvider(),
            new SpotifyColorLyricsProvider(http, settings),
            new SpotifyLyricsApiProvider(http, settings),
            new LrclibProvider(http),
        };

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JockeyBox", "lyrics");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Resolve lyrics for a track; null if no provider had anything.</summary>
    public async Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
    {
        // Sidecar files are intentionally NOT cached: users edit them live.
        var sidecar = await _providers[0].GetLyricsAsync(track, ct);
        if (sidecar != null) return sidecar;

        var cached = ReadCache(track);
        if (cached != null && !ShouldBypassCache(cached)) return cached;

        foreach (var provider in _providers.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            var doc = await provider.GetLyricsAsync(track, ct);
            if (doc != null)
            {
                WriteCache(track, doc);
                return doc;
            }
        }
        return null;
    }

    /// <summary>
    /// A cached fallback (LRCLIB) result must not permanently shadow the
    /// Spotify pipeline once the user configures it; re-run the chain in that
    /// case (a fresh result overwrites the cache entry).
    /// </summary>
    private bool ShouldBypassCache(LyricsDocument cached)
    {
        var oauth = new SpotifyOAuthService(_settings);
        bool spotifyConfigured = oauth.IsLoggedIn
            || !string.IsNullOrWhiteSpace(_settings.LyricsApiBaseUrl);
        return spotifyConfigured && cached.Source == "LRCLIB";
    }

    private string CachePath(TrackEntry track)
    {
        var key = $"{track.Artist}|{track.Title}|{(int)track.Duration.TotalSeconds}".ToLowerInvariant();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDir, hash + ".json");
    }

    private LyricsDocument? ReadCache(TrackEntry track)
    {
        try
        {
            var path = CachePath(track);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LyricsDocument>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private void WriteCache(TrackEntry track, LyricsDocument doc)
    {
        try
        {
            File.WriteAllText(CachePath(track), JsonSerializer.Serialize(doc));
        }
        catch { /* cache is best-effort */ }
    }
}
