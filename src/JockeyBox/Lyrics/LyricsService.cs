using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JockeyBox.Library;
using JockeyBox.Settings;

namespace JockeyBox.Lyrics;

/// <summary>
/// Orchestrates the provider chain (local .lrc sidecar -> lrcmux -> Better Lyrics
/// -> Unison -> Spotify Color Lyrics -> self-hosted spotify-lyrics-api -> LRCLIB)
/// and caches successful results on disk so a track only ever hits the network
/// once.
///
/// Order rationale: word-capable sources win for karaoke, but a hand-made .lrc
/// sidecar stays first because it is authoritative. Among the word sources,
/// lrcmux needs only artist/title (so it covers Spotify imports), Better Lyrics
/// gives Apple syllable timings, and Unison needs a YouTube id from the download
/// ledger. The line-level sources are the fallback.
///
/// Results are cached per provider id, so the user can switch a track to a
/// different source without the old document winning the race.
/// </summary>
public class LyricsService
{
    /// <summary>Provider id of the local .lrc sidecar (never cached - users edit it live).</summary>
    public const string SidecarSourceId = "lrc";

    private readonly List<ILyricsProvider> _providers;
    private readonly AppSettings _settings;
    private readonly string _cacheDir;

    public LyricsService(AppSettings settings)
    {
        _settings = settings;
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
            // Cap responses so a hostile/misconfigured endpoint can't exhaust memory.
            MaxResponseContentBufferSize = 5 * 1024 * 1024,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JockeyBox/1.0 (karaoke player)");

        _providers = new List<ILyricsProvider>
        {
            new LrcSidecarProvider(),
            new LrcmuxProvider(http),
            new BetterLyricsProvider(http),
            new UnisonProvider(http),
            new SpotifyColorLyricsProvider(http, settings),
            new SpotifyLyricsApiProvider(http, settings),
            new LrclibProvider(http),
        };

        SourceNames = _providers.Select(p => p.Name).ToArray();
        SourceIds = _providers.Select(p => p.Id).ToArray();

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JockeyBox", "lyrics");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Provider display names, in the priority order the chain tries them.</summary>
    public IReadOnlyList<string> SourceNames { get; }

    /// <summary>Stable provider ids matching <see cref="SourceNames"/> one-for-one (same order).</summary>
    public IReadOnlyList<string> SourceIds { get; }

    /// <summary>
    /// Resolve lyrics for a track; null if no provider had anything.
    /// </summary>
    /// <param name="preferredSourceId">
    /// Optional provider id (see <see cref="SourceIds"/>) to try on its own first.
    /// That lookup deliberately bypasses the disk cache so a document from another
    /// provider can never be returned for an explicit choice; a fresh hit is cached
    /// under its own source id. If the preferred provider yields nothing the normal
    /// chain runs and the fallback is visible as
    /// <c>document.SourceId != preferredSourceId</c>.
    /// </param>
    public async Task<LyricsDocument?> GetLyricsAsync(
        TrackEntry track,
        CancellationToken ct,
        string? preferredSourceId = null)
    {
        // An explicit source choice must be honoured before anything cached.
        var preferred = FindProvider(preferredSourceId);
        if (preferred != null)
        {
            var chosen = await SafeGetAsync(preferred, track, ct);
            if (chosen != null)
            {
                if (preferred.Id != SidecarSourceId) WriteCache(track, chosen);
                return chosen;
            }
            // Preferred source had nothing: fall through to the normal chain.
        }

        return await ResolveDefaultChainAsync(track, ct);
    }

    /// <summary>
    /// Every provider that has something for this track, in priority order
    /// (non-null only). Used by the lyrics panel's "pick another source" picker;
    /// results are live network calls, so call it on demand rather than on every
    /// track change.
    /// </summary>
    public async Task<IReadOnlyList<LyricsDocument>> GetCandidatesAsync(TrackEntry track, CancellationToken ct)
    {
        var results = new List<LyricsDocument>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            var doc = await SafeGetAsync(provider, track, ct);
            if (doc == null) continue;

            results.Add(doc);
            // Sidecar files are live/editable, so they are never cached.
            if (provider.Id != SidecarSourceId) WriteCache(track, doc);
        }
        return results;
    }

    /// <summary>Default chain: sidecar, then cache, then live providers in priority order.</summary>
    private async Task<LyricsDocument?> ResolveDefaultChainAsync(TrackEntry track, CancellationToken ct)
    {
        // 1. Sidecar files are intentionally NOT cached: users edit them live.
        var sidecarProvider = FindProvider(SidecarSourceId);
        if (sidecarProvider != null)
        {
            var sidecar = await SafeGetAsync(sidecarProvider, track, ct);
            if (sidecar != null) return sidecar;
        }

        // 2. Legacy cache entry (key without a source id) written by older builds.
        var legacy = ReadCache(track, sourceId: null);
        if (legacy != null && !ShouldBypassCache(legacy)) return legacy;

        // 3. Per-source cache entries, highest-priority provider first.
        foreach (var provider in _providers)
        {
            if (provider.Id == SidecarSourceId) continue;
            var cached = ReadCache(track, provider.Id);
            if (cached != null && !ShouldBypassCache(cached)) return cached;
        }

        // 4. Live chain.
        foreach (var provider in _providers)
        {
            if (provider.Id == SidecarSourceId) continue;
            ct.ThrowIfCancellationRequested();
            var doc = await SafeGetAsync(provider, track, ct);
            if (doc != null)
            {
                WriteCache(track, doc);
                // Keep the pre-source-id key populated so an older build (or a
                // downgrade) still finds this document.
                WriteCache(track, doc, legacyKey: true);
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
        bool isLrclib = string.Equals(cached.SourceId, "lrclib", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cached.Source, "LRCLIB", StringComparison.OrdinalIgnoreCase);
        return spotifyConfigured && isLrclib;
    }

    /// <summary>Provider lookup by stable id; null for unknown/absent ids.</summary>
    private ILyricsProvider? FindProvider(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _providers.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One provider, one chance: any failure returns null so the chain continues.
    /// Real cancellation still propagates; an HttpClient timeout (which surfaces
    /// as TaskCanceledException) does not abort the remaining providers.
    /// </summary>
    private static async Task<LyricsDocument?> SafeGetAsync(
        ILyricsProvider provider, TrackEntry track, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await provider.GetLyricsAsync(track, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller cancelled - stop the whole lookup
        }
        catch
        {
            return null; // broken/unreachable provider must not break the chain
        }
    }

    /// <summary>
    /// Cache file for a track, optionally scoped to one provider. With
    /// <paramref name="sourceId"/> null the historical key is produced verbatim,
    /// which is what keeps pre-upgrade cache files readable.
    /// </summary>
    private string CachePath(TrackEntry track, string? sourceId)
    {
        var key = $"{track.Artist}|{track.Title}|{(int)track.Duration.TotalSeconds}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(sourceId))
            key += "|" + sourceId.Trim().ToLowerInvariant();

        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDir, hash + ".json");
    }

    private LyricsDocument? ReadCache(TrackEntry track, string? sourceId)
    {
        try
        {
            var path = CachePath(track, sourceId);
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<LyricsDocument>(File.ReadAllText(path));
            return doc == null ? null : BackfillSourceId(doc);
        }
        catch { return null; }
    }

    private void WriteCache(TrackEntry track, LyricsDocument doc, bool legacyKey = false)
    {
        try
        {
            var sourceId = legacyKey ? null : (string.IsNullOrWhiteSpace(doc.SourceId) ? null : doc.SourceId);
            if (!legacyKey && sourceId == null) return; // cannot key it safely
            File.WriteAllText(CachePath(track, sourceId), JsonSerializer.Serialize(doc));
        }
        catch { /* cache is best-effort */ }
    }

    /// <summary>
    /// Cache files written before per-source ids existed carry only the display
    /// name; recover the id so callers can compare against <see cref="SourceIds"/>.
    /// </summary>
    private LyricsDocument BackfillSourceId(LyricsDocument doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.SourceId)) return doc;

        var match = _providers.FirstOrDefault(
            p => string.Equals(p.Name, doc.Source, StringComparison.OrdinalIgnoreCase));
        if (match == null) return doc;

        return new LyricsDocument
        {
            Synced = doc.Synced,
            Lines = doc.Lines,
            Source = doc.Source,
            SourceId = match.Id,
        };
    }
}
