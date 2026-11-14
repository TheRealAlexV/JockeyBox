using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// Lyrics lookup is keyed on the file's artist+title, which is wrong for exactly
/// the case the operator hits: a cover credited to another artist. These tests pin
/// the three behaviours that make that recoverable - a pinned query reaches the
/// provider, a pinned query never reads the file-tag cache entry, and a miss says
/// what was searched instead of only "no lyrics".
///
/// <para>These tests repoint <c>JOCKEYBOX_HOME</c> at a private directory for their
/// whole lifetime, so they belong to the home collection: xUnit runs that collection
/// without overlapping any other collection, which is what keeps a home flip out of
/// another test's two reads of the same path.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public class LyricsQueryTests : IDisposable
{
    private readonly string _home;
    private readonly HomeLease _lease;
    private readonly string _cacheDir;

    public LyricsQueryTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "jb-lyrics-query", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        _cacheDir = Path.Combine(_home, "lyrics");
        Directory.CreateDirectory(_cacheDir);

        _lease = HomeLease.Enter(_home, nameof(LyricsQueryTests));
    }

    public void Dispose()
    {
        _lease.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { /* test cleanup */ }
    }

    private static TrackEntry Cover() => new()
    {
        RelativeKey = "covers/sweater-weather",
        RelativePath = "covers/Sweater Weather.mp3",
        Title = "Sweater Weather",
        Artist = "Myles Smith",
        Duration = TimeSpan.FromSeconds(201),
    };

    /// <summary>Returns one canned line and records every query it was asked.</summary>
    private sealed class RecordingProvider : ILyricsProvider
    {
        private readonly LyricsDocument? _document;

        public RecordingProvider(LyricsDocument? document) => _document = document;

        public string Id => "fake";
        public string Name => "Fake Provider";

        public List<(string Artist, string Title, double Seconds)> Queries { get; } = new();

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
        {
            Queries.Add((track.Artist, track.Title, track.Duration.TotalSeconds));
            // Real providers report their outcome the same way; the miss line has
            // to show it even for this fake.
            LyricsTrace.Note(Id, _document == null ? "returned nothing" : "found");
            return Task.FromResult(_document);
        }
    }

    private static LyricsDocument Doc(string text) => LyricsDocument.Unsynced(
        new[] { text }, "Fake Provider", "fake");

    private LyricsService Service(RecordingProvider provider) =>
        new(new AppSettings(), _cacheDir, new ILyricsProvider[] { provider });

    [Fact]
    public async Task PinnedQuery_ReachesProvider_InsteadOfFileTags()
    {
        var provider = new RecordingProvider(Doc("the original"));
        var service = Service(provider);

        var lookup = await service.GetLyricsAsync(Cover(), CancellationToken.None,
            preferredSourceId: null,
            query: new LyricsService.LyricsQuery("Sweater Weather", "The Neighbourhood"));

        Assert.NotNull(lookup.Document);
        var sent = Assert.Single(provider.Queries);
        Assert.Equal("The Neighbourhood", sent.Artist);
        Assert.Equal("Sweater Weather", sent.Title);
        // Duration is dropped with an override: the file's length belongs to the
        // cover and would reject the original's document upstream.
        Assert.Equal(0, sent.Seconds);
    }

    [Fact]
    public async Task PinnedQuery_DoesNotReadTheFileTagCacheEntry()
    {
        var cached = new RecordingProvider(Doc("wrong cover document"));
        var first = Service(cached);
        var withoutQuery = await first.GetLyricsAsync(Cover(), CancellationToken.None);
        Assert.NotNull(withoutQuery.Document);

        // A second service over the same cache directory would serve the file-tag
        // entry; with a pinned query the provider has to be asked again.
        var pinned = new RecordingProvider(Doc("the original"));
        var second = Service(pinned);
        var lookup = await second.GetLyricsAsync(Cover(), CancellationToken.None,
            preferredSourceId: null,
            query: new LyricsService.LyricsQuery(null, "The Neighbourhood"));

        Assert.NotNull(lookup.Document);
        Assert.Equal("the original", Assert.Single(lookup.Document!.Lines).Text);
        Assert.Single(pinned.Queries);
    }

    [Fact]
    public async Task Miss_LogsOneLine_NamingTheSearchedQuery()
    {
        var provider = new RecordingProvider(null);
        var service = Service(provider);
        var warnings = new List<string>();
        void OnLine(string line)
        {
            if (line.Contains("lyrics miss", StringComparison.Ordinal)) warnings.Add(line);
        }

        Log.LineWritten += OnLine;
        try
        {
            var track = Cover();
            var track2 = Cover();

            var lookup = await service.GetLyricsAsync(track, CancellationToken.None);
            // Same track again: the miss is already explained, so no second line.
            await service.GetLyricsAsync(track2, CancellationToken.None);

            Assert.Null(lookup.Document);
            Assert.NotNull(lookup.MissStatus);
            Assert.Contains("Sweater Weather", lookup.MissStatus);
            Assert.Contains("Myles Smith", lookup.MissStatus);
            Assert.Contains("also tried", lookup.MissStatus);

            var line = Assert.Single(warnings);
            Assert.Contains("artist=\"Myles Smith\"", line);
            Assert.Contains("title=\"Sweater Weather\"", line);
            // Each provider that was asked reports its own outcome in the same line.
            Assert.Contains("fake: returned nothing", line);
        }
        finally
        {
            Log.LineWritten -= OnLine;
        }
    }

    [Fact]
    public void MetadataStore_RoundTripsPinnedQuery_AndClearsBackToTags()
    {
        var path = Path.Combine(_home, "track-metadata.json");

        using (var store = TrackMetadataStore.Open(path))
        {
            store.SetLyricsQuery("covers/Sweater Weather.mp3", "Sweater Weather", "The Neighbourhood");
            store.SetLyricsSource("covers/Sweater Weather.mp3", "lrclib");
            store.Save();
        }

        // A fresh store is what a restart looks like: nothing is in memory.
        using (var reopened = TrackMetadataStore.Open(path))
        {
            var record = reopened.Get("covers/Sweater Weather.mp3");
            Assert.NotNull(record);
            Assert.Equal("Sweater Weather", record!.LyricsTitle);
            Assert.Equal("The Neighbourhood", record.LyricsArtist);
            Assert.Equal("lrclib", record.LyricsSourceId);

            var onDisk = File.ReadAllText(path);
            Assert.Contains("\"lyricsTitle\"", onDisk);
            Assert.Contains("\"lyricsArtist\"", onDisk);

            reopened.SetLyricsQuery("covers/Sweater Weather.mp3", null, null);
            var cleared = reopened.Get("covers/Sweater Weather.mp3");
            Assert.NotNull(cleared);
            Assert.Null(cleared!.LyricsTitle);
            Assert.Null(cleared.LyricsArtist);
            // Clearing the query must not clear the provider pin.
            Assert.Equal("lrclib", cleared.LyricsSourceId);
        }
    }
}
