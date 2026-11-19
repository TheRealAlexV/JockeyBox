using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JockeyBox.Library;
using JockeyBox.Settings;
using JockeyBox.Tools;

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
    /// <summary>
    /// A user-pinned query for one track (null keeps the file tag). Providers are
    /// keyed on artist+title, so a cover credited to another artist can only be
    /// resolved by searching the tags of the song the operator actually means.
    /// </summary>
    public sealed record LyricsQuery(string? Title, string? Artist);

    /// <summary>
    /// Result of one lookup. <see cref="Status"/> is null on a hit (the caller keeps
    /// its existing success wording) and describes what was searched on a miss.
    /// </summary>
    public sealed record LyricsLookup(LyricsDocument? Document, string? MissStatus);

    /// <summary>Provider id of the local .lrc sidecar (never cached - users edit it live).</summary>
    public const string SidecarSourceId = "lrc";

    private static List<ILyricsProvider> BuildDefaultProviders(HttpClient http, AppSettings settings) => new()
    {
        new LrcSidecarProvider(),
        new LrcmuxProvider(http),
        new BetterLyricsProvider(http),
        new UnisonProvider(http),
        new SpotifyColorLyricsProvider(http, settings),
        new SpotifyLyricsApiProvider(http, settings),
        new LrclibProvider(http),
    };

    private readonly List<ILyricsProvider> _providers;
    private readonly AppSettings _settings;
    private readonly string _cacheDir;

    public LyricsService(AppSettings settings)
        : this(settings, cacheDir: null, providers: null)
    {
    }

    /// <summary>
    /// Test/embedding seam: an explicit provider list and cache directory. Passing
    /// null for either keeps the production default (the seven-provider chain in
    /// <see cref="BuildDefaultProviders"/>, cache under %APPDATA%).
    /// </summary>
    internal LyricsService(
        AppSettings settings,
        string? cacheDir = null,
        IReadOnlyList<ILyricsProvider>? providers = null)
    {
        _settings = settings;
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
            // Cap responses so a hostile/misconfigured endpoint can't exhaust memory.
            MaxResponseContentBufferSize = 5 * 1024 * 1024,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JockeyBox/1.0 (karaoke player)");

        _providers = providers?.ToList() ?? BuildDefaultProviders(http, settings);

        SourceNames = _providers.Select(p => p.Name).ToArray();
        SourceIds = _providers.Select(p => p.Id).ToArray();

        _cacheDir = cacheDir ?? Path.Combine(AppPaths.Home, "lyrics");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Provider display names, in the priority order the chain tries them.</summary>
    public IReadOnlyList<string> SourceNames { get; }

    /// <summary>Stable provider ids matching <see cref="SourceNames"/> one-for-one (same order).</summary>
    public IReadOnlyList<string> SourceIds { get; }

    /// <summary>
    /// Resolve lyrics for a track; <c>Document</c> is null if no provider had
    /// anything, and <c>MissStatus</c> then names the query and the providers tried.
    /// </summary>
    /// <param name="preferredSourceId">
    /// Optional provider id (see <see cref="SourceIds"/>) to try on its own first.
    /// That lookup deliberately bypasses the disk cache so a document from another
    /// provider can never be returned for an explicit choice; a fresh hit is cached
    /// under its own source id. If the preferred provider yields nothing the normal
    /// chain runs and the fallback is visible as
    /// <c>document.SourceId != preferredSourceId</c>.
    /// </param>
    /// <param name="query">
    /// User-pinned title/artist (see <see cref="LyricsQuery"/>). When set, every
    /// provider is asked with those strings instead of the file tags and the cache
    /// is keyed on them too: the whole point is that the file-tag cache entry is
    /// the wrong answer, so it must not win.
    /// </param>
    public async Task<LyricsLookup> GetLyricsAsync(
        TrackEntry track,
        CancellationToken ct,
        string? preferredSourceId = null,
        LyricsQuery? query = null)
    {
        using var trace = LyricsTrace.Begin();

        var effective = ApplyQuery(track, query);

        // An explicit source choice must be honoured before anything cached.
        var preferred = FindProvider(preferredSourceId);
        if (preferred != null)
        {
            var chosen = await SafeGetAsync(preferred, effective, ct);
            if (chosen != null)
            {
                if (FitsFile(chosen, effective.Duration, "preferred", out _))
                {
                    if (preferred.Id != SidecarSourceId) WriteCache(effective, chosen);
                    return new LyricsLookup(chosen, null);
                }
                // The pinned source returned another edit's timeline: let the chain
                // look for one that fits rather than showing this one.
            }
            // Preferred source had nothing: fall through to the normal chain.
        }

        var (doc, caveat) = await ResolveDefaultChainAsync(effective, ct);
        if (doc != null) return new LyricsLookup(doc, caveat);

        var miss = DescribeMiss(track, query, trace.Notes);
        ProviderLog.WarnOnce(
            "miss|" + MissKey(track, query),
            $"lyrics miss: title=\"{effective.Title}\" artist=\"{effective.Artist}\" "
            + $"album=\"{track.Album}\" duration={(int)track.Duration.TotalSeconds}s "
            + $"source={preferredSourceId ?? "auto"} -> {DescribeNotes(trace.Notes)}");
        return new LyricsLookup(null, miss);
    }

    /// <summary>
    /// The track with any pinned query applied. Duration is dropped: the file's
    /// length belongs to the cover, not to the song being searched, and every
    /// provider that filters on duration would reject the right answer for it.
    /// The audio paths go too, so the local .lrc sidecar cannot shadow the search.
    /// </summary>
    private static TrackEntry ApplyQuery(TrackEntry track, LyricsQuery? query)
    {
        if (query == null) return track;

        var title = string.IsNullOrWhiteSpace(query.Title) ? track.Title : query.Title!.Trim();
        var artist = string.IsNullOrWhiteSpace(query.Artist) ? track.Artist : query.Artist!.Trim();
        if (title == track.Title && artist == track.Artist) return track;

        return new TrackEntry
        {
            RelativeKey = track.RelativeKey,
            RelativePath = track.RelativePath,
            Title = title,
            Artist = artist,
            Album = "",
        };
    }

    /// <summary>Short name shown in the status line, e.g. "lrcmux", "Spotify", "LRCLIB".</summary>
    internal static string DisplayName(string providerId) => providerId switch
    {
        "lrc" => "local .lrc",
        "lrcmux" => "lrcmux",
        "betterlyrics" => "Better Lyrics",
        "unison" => "Unison",
        "spotify-color" => "Spotify",
        "spotify-api" => "Spotify API",
        "lrclib" => "LRCLIB",
        _ => providerId,
    };

    private static string DescribeNotes(IReadOnlyList<(string Provider, string Outcome)> notes)
    {
        if (notes.Count == 0) return "no provider answered";
        return string.Join(", ", notes.Select(n => $"{DisplayName(n.Provider)}: {n.Outcome}"));
    }

    /// <summary>"Providers that were actually asked", for the status line.</summary>
    private static string DescribeTried(IReadOnlyList<(string Provider, string Outcome)> notes)
    {
        var names = notes.Count > 0
            ? notes.Select(n => DisplayName(n.Provider)).ToArray()
            : new[] { "lrcmux", "Better Lyrics", "Unison", "Spotify", "LRCLIB" };
        return string.Join(", ", names);
    }

    private static string DescribeMiss(
        TrackEntry track, LyricsQuery? query, IReadOnlyList<(string Provider, string Outcome)> notes)
    {
        var title = string.IsNullOrWhiteSpace(query?.Title) ? track.Title : query!.Title!.Trim();
        var artist = string.IsNullOrWhiteSpace(query?.Artist) ? track.Artist : query!.Artist!.Trim();

        var who = string.IsNullOrWhiteSpace(artist) ? $"\"{title}\"" : $"\"{title}\" by {artist}";
        return $"No lyrics for {who} (also tried: {DescribeTried(notes)})";
    }

    private static string MissKey(TrackEntry track, LyricsQuery? query) =>
        $"{query?.Artist ?? track.Artist}|{query?.Title ?? track.Title}|{track.RelativeKey}";

    /// <summary>
    /// Ask every provider once and report what each said, without the cache and
    /// without the priority chain stopping at the first hit. This is the live
    /// network evidence for "which source has Sweater Weather": the candidate
    /// list hides the misses, and the miss log only exists when everything fails.
    /// </summary>
    internal async Task<IReadOnlyList<(string Provider, string Outcome)>> ProbeAsync(
        TrackEntry track, CancellationToken ct)
    {
        var results = new List<(string Provider, string Outcome)>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            using var trace = LyricsTrace.Begin();
            var doc = await SafeGetAsync(provider, track, ct);

            string outcome;
            if (doc != null)
                outcome = $"found ({doc.Lines.Count} lines, {(doc.Synced ? "synced" : "unsynced")})";
            else if (trace.Notes.Count > 0)
                outcome = trace.Notes[0].Outcome;
            else
                outcome = "returned nothing";

            results.Add((provider.Name, outcome));
        }
        return results;
    }

    /// <summary>
    /// Every provider that has something for this track, in priority order
    /// (non-null only). Used by the lyrics panel's "pick another source" picker;
    /// results are live network calls, so call it on demand rather than on every
    /// track change.
    /// </summary>
    public async Task<IReadOnlyList<LyricsDocument>> GetCandidatesAsync(
        TrackEntry track,
        CancellationToken ct,
        LyricsQuery? query = null)
    {
        var effective = ApplyQuery(track, query);
        var results = new List<LyricsDocument>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            var doc = await SafeGetAsync(provider, effective, ct);
            if (doc == null) continue;

            results.Add(doc);
            // Sidecar files are live/editable, so they are never cached.
            if (provider.Id != SidecarSourceId) WriteCache(effective, doc);
        }
        return results;
    }

    /// <summary>Default chain: sidecar, then cache, then live providers in priority order.</summary>
    private async Task<(LyricsDocument? Document, string? Caveat)> ResolveDefaultChainAsync(
        TrackEntry track, CancellationToken ct)
    {
        var duration = track.Duration;
        // Documents whose timeline runs past the end of the audio, kept so the
        // closest one can be shown if nothing fits.
        var overrunning = new List<(LyricsDocument Document, TimeSpan Overrun)>();

        // Returns the document when its timeline can belong to this file; otherwise
        // records how far past the end it runs and returns null.
        LyricsDocument? Accept(LyricsDocument document, string origin)
        {
            if (FitsFile(document, duration, origin, out var overrun)) return document;

            overrunning.Add((document, overrun));
            return null;
        }

        // 1. Sidecar files are intentionally NOT cached: users edit them live.
        var sidecarProvider = FindProvider(SidecarSourceId);
        if (sidecarProvider != null)
        {
            var sidecar = await SafeGetAsync(sidecarProvider, track, ct);
            if (sidecar != null && Accept(sidecar, "sidecar") is { } fittingSidecar)
                return (fittingSidecar, null);
        }

        // 2. Per-source cache entries, highest-priority provider first. A
        //    source-specific entry beats the source-less legacy key below: every
        //    build wrote the legacy key for whichever provider answered first, so it
        //    can hold a different provider's document.
        foreach (var provider in _providers)
        {
            if (provider.Id == SidecarSourceId) continue;
            var cached = ReadCache(track, provider.Id);
            if (cached != null && !ShouldBypassCache(cached)
                && Accept(cached, "cache") is { } fittingCached)
                return (fittingCached, null);
        }

        // 3. Legacy cache entry (key without a source id) written by older builds.
        var legacy = ReadCache(track, sourceId: null);
        if (legacy != null && !ShouldBypassCache(legacy) && Accept(legacy, "legacy cache") is { } fittingLegacy)
            return (fittingLegacy, null);

        // 4. Live chain.
        foreach (var provider in _providers)
        {
            if (provider.Id == SidecarSourceId) continue;
            ct.ThrowIfCancellationRequested();
            var doc = await SafeGetAsync(provider, track, ct);
            if (doc == null) continue;
            if (Accept(doc, "live") is not { } fitting) continue;

            WriteCache(track, fitting);
            // Keep the pre-source-id key populated so an older build (or a
            // downgrade) still finds this document.
            WriteCache(track, fitting, legacyKey: true);
            return (fitting, null);
        }

        // Nothing fits. Showing nothing would be worse than showing a timeline that
        // is a little long, so keep the closest one and admit it in the status text.
        if (overrunning.Count > 0)
        {
            var (document, overrun) = overrunning.OrderBy(c => c.Overrun).First();
            var caveat =
                $"timings run {overrun.TotalSeconds:0.#}s past the end of this file "
                + $"({LastLineTime(document).TotalSeconds:0.#}s of lyrics on a "
                + $"{duration.TotalSeconds:0.#}s track)";
            Log.Warn($"lyrics: no candidate fits the {duration.TotalSeconds:0.#}s file; "
                + $"keeping the closest ({document.SourceId}, +{overrun.TotalSeconds:0.#}s) - {caveat}");
            return (document, caveat);
        }

        return (null, null);
    }

    /// <summary>
    /// True when the document's timeline can belong to a file of this length. Logs
    /// the measured span either way and traces a rejection, so a wrong-edition
    /// timeline shows up whichever path produced it (sidecar, cache, live chain or
    /// the preferred provider's early return).
    /// </summary>
    private static bool FitsFile(
        LyricsDocument document, TimeSpan duration, string origin, out TimeSpan overrun)
    {
        var first = document.Lines.Count == 0 ? TimeSpan.Zero : document.Lines[0].Time;
        var last = LastLineTime(document);
        overrun = last - duration;
        // Zero duration means "unknown" (a pinned query drops it, see ApplyQuery),
        // so there is nothing to measure against and the document passes.
        var fits = duration <= TimeSpan.Zero || overrun <= OverrunTolerance(duration);

        Log.Info($"lyrics candidate: origin={origin} source={document.SourceId} "
            + $"span={Span(first)}-{Span(last)} duration={duration.TotalSeconds:0.###}s "
            + $"overrun={overrun.TotalSeconds:+0.###;-0.###;0}s {(fits ? "fits" : "REJECTED")}");

        if (!fits)
        {
            // Feeds the "lyrics miss" line, which used to name the source and never
            // its span: that omission is why a wrong-edit timeline looked like an
            // offset problem for two rounds of investigation.
            LyricsTrace.Note(
                string.IsNullOrWhiteSpace(document.SourceId) ? "cache" : document.SourceId,
                $"timeline {Span(first)}-{Span(last)} runs {overrun.TotalSeconds:0.#}s "
                + $"past the {duration.TotalSeconds:0.#}s file");
        }

        return fits;
    }

    /// <summary>
    /// Smallest overrun that still counts as a different edit of the song. A
    /// trailing credit line or a slightly different master legitimately lands a
    /// second or two past the audio, and rejecting those would be worse than the
    /// bug this guards against: 5% of the track (and never less than 5 s) covers
    /// that slack while 46 s on a 172 s file does not come close.
    /// </summary>
    internal static TimeSpan OverrunTolerance(TimeSpan duration) => TimeSpan.FromTicks(
        Math.Max(TimeSpan.FromSeconds(5).Ticks, (long)(duration.Ticks * 0.05)));

    /// <summary>Latest line time in the document (zero when it has no lines).</summary>
    internal static TimeSpan LastLineTime(LyricsDocument document)
    {
        var last = TimeSpan.Zero;
        foreach (var line in document.Lines)
            if (line.Time > last) last = line.Time;
        return last;
    }

    private static string Span(TimeSpan time) =>
        time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

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
