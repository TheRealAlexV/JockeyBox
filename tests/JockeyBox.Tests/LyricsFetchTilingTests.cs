using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Where word spans are built. Providers must hand back the document exactly as
/// parsed: the load-time pass (<c>MainViewModel</c>: <see cref="InstrumentalGap.Fill"/>
/// then <see cref="WordTimeline.Synthesize"/>) is the only place that tiles a
/// line-only document, so it tiles against the instrumental marker. A provider
/// that synthesises at fetch time freezes the tiling at the raw next-line width
/// and the marker is inserted into a document that already claims word timings.
/// <para>
/// The Headlights shape below is the reported failure: a line at 62.480 s and
/// the next at 109.030 s. The gap is 46.55 s, so <see cref="InstrumentalGap"/>
/// puts the marker at 67.480 s; the last word must end there, not at 109.030 s.
/// </para>
/// </summary>
public sealed class LyricsFetchTilingTests : IDisposable
{
    private const int LineStartMs = 62_480;
    private const int MarkerMs = 67_480; // line start + InstrumentalGap.SungCapMs
    private const int NextLineMs = 109_030;
    private const string TenWords = "And I know the headlights are gonna find me tonight";

    private readonly string _cacheDir;

    public LyricsFetchTilingTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "jb-fetch-tiling", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- 1. fetch time: the provider hands back what it parsed -----------

    [Fact]
    public async Task LineOnlyProviderDocument_ArrivesWithoutWordSpans()
    {
        var doc = await FetchFromLrcmuxAsync();

        // Tiling belongs to the load-time pass; a document tiled here already
        // reports HasWordTimings, so that pass skips it and never sees the marker.
        Assert.False(doc.HasWordTimings);
    }

    // ---- 2. load time: the marker bounds the last word --------------------

    [Fact]
    public async Task HeadlightsShape_LoadTimePass_EndsTheLinesWordsAtTheMarker()
    {
        var doc = await FetchFromLrcmuxAsync();

        // Exactly what MainViewModel does after the fetch returns.
        var loaded = LoadTimePass(doc);

        var line = loaded.Lines.Single(l => l.Time == TimeSpan.FromMilliseconds(LineStartMs));
        var words = line.Words;
        Assert.NotNull(words);
        Assert.Equal(10, words!.Count);

        // The whole line is still covered, from its own start...
        Assert.Equal(LineStartMs, (int)words[0].Start.TotalMilliseconds);

        // ...but the last word stops at the marker. On a fetch-time tiling this
        // is 109030: ten words stretched across the entire 46.55 s gap.
        Assert.Equal(MarkerMs, (int)words[^1].End.TotalMilliseconds);
    }

    // ---- 3. the poisoned cache entries are no longer reachable -----------

    [Fact]
    public async Task PreBumpCacheEntry_IsNotRead_AndIsLeftOnDisk()
    {
        var track = Track();

        // The key exactly as 12f770c built it: artist|title|seconds.
        var preBump = $"{track.Artist}|{track.Title}|{(int)track.Duration.TotalSeconds}".ToLowerInvariant();
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(preBump)));
        var poisonedPath = Path.Combine(_cacheDir, hash + ".json");
        File.WriteAllText(poisonedPath, JsonSerializer.Serialize(CachedDocument()));

        var service = new LyricsService(
            new AppSettings(), _cacheDir, new ILyricsProvider[] { new FakeProvider(LiveDocument()) });

        var lookup = await service.GetLyricsAsync(track, CancellationToken.None);

        // The old entry holds a fetch-time tiled document; it must not win.
        Assert.Equal("Live", lookup.Document?.Source);
        // A version segment, not a purge: nothing the operator owns is deleted.
        Assert.True(File.Exists(poisonedPath));
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// The load-time pass once the document is in hand
    /// (<c>MainViewModel</c>:3684-3696). Mirror, not a call: the view model needs
    /// a window and a playhead to reach that path. The guard is <c>Synced</c>
    /// only: the marker Fill inserts carries glyph words, so a document-wide
    /// HasWordTimings test would skip every sung line.
    /// </summary>
    private static LyricsDocument LoadTimePass(LyricsDocument doc)
    {
        doc = InstrumentalGap.Fill(doc);
        if (doc.Synced)
            WordTimeline.Synthesize(doc, TimeSpan.FromSeconds(180));
        return doc;
    }

    private static async Task<LyricsDocument> FetchFromLrcmuxAsync()
    {
        // Line-level payload only: no "words" arrays anywhere.
        var json = $$"""
            {
              "lines": [
                { "text": "the intro line", "start": 1000, "end": 4000 },
                { "text": "{{TenWords}}", "start": {{LineStartMs}}, "end": {{NextLineMs}} },
                { "text": "the final line", "start": {{NextLineMs}}, "end": 113030 }
              ]
            }
            """;

        using var http = new HttpClient(new StaticHandler(json));
        var provider = new LrcmuxProvider(http);

        var doc = await provider.GetLyricsAsync(Track(), CancellationToken.None);
        Assert.NotNull(doc);
        return doc!;
    }

    private static TrackEntry Track() => new()
    {
        RelativeKey = "headlights",
        RelativePath = "headlights.mp3",
        Title = "Headlights",
        Artist = "Test Artist",
        Duration = TimeSpan.FromSeconds(180),
    };

    /// <summary>A document as a poisoned cache entry would hold it: tiled at fetch width.</summary>
    private static LyricsDocument CachedDocument() => new()
    {
        Synced = true,
        Source = "Cached",
        SourceId = "fake",
        Lines = new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "old line"),
            new(TimeSpan.FromSeconds(4), "old line two"),
        },
    };

    private static LyricsDocument LiveDocument() => new()
    {
        Synced = true,
        Source = "Live",
        SourceId = "fake",
        Lines = new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "line one"),
            new(TimeSpan.FromSeconds(4), "line two"),
        },
    };

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StaticHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeProvider : ILyricsProvider
    {
        private readonly LyricsDocument _document;
        public FakeProvider(LyricsDocument document) => _document = document;

        public string Id => "fake";
        public string Name => "Fake Provider";

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct) =>
            Task.FromResult<LyricsDocument?>(_document);
    }
}
