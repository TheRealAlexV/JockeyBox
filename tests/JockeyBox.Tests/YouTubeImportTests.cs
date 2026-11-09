using System.IO;
using System.Text.Json;
using JockeyBox.Downloads;
using JockeyBox.Sources;
using JockeyBox.YouTube;

namespace JockeyBox.Tests;

/// <summary>
/// The YouTube tab's importer: link parsing, the yt-dlp listing parser, and the
/// "which local files came from YouTube" resolution.
///
/// The listing fixtures are verbatim shapes from <c>yt-dlp -J</c> (a watch
/// response and a <c>--flat-playlist</c> response with two dead entries), so the
/// parser is proved against the real payload without a network call.
/// </summary>
public sealed class YouTubeImportTests
{
    private const string WatchJson = """
    {
      "id": "5NV6Rdv1a3I",
      "title": "Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams, Nile Rodgers",
      "uploader": "Daft Punk",
      "uploader_id": "@daftpunk",
      "duration": 248.0,
      "thumbnail": "https://i.ytimg.com/vi/5NV6Rdv1a3I/maxresdefault.jpg",
      "webpage_url": "https://www.youtube.com/watch?v=5NV6Rdv1a3I",
      "_type": "video"
    }
    """;

    private const string PlaylistJson = """
    {
      "_type": "playlist",
      "id": "PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D",
      "title": "Synthwave Essentials",
      "uploader": "Nightdrive",
      "playlist_count": 5,
      "entries": [
        {
          "_type": "url",
          "id": "MV_3Dpw-BRY",
          "title": "The Midnight - Sunset",
          "duration": 292.0,
          "uploader": "The Midnight",
          "thumbnails": [{"url": "https://i.ytimg.com/vi/MV_3Dpw-BRY/hq.jpg"}]
        },
        {
          "_type": "url",
          "id": "jSyQ3Xh2Z4Y",
          "title": "Kavinsky - Nightcall",
          "duration": 258.0,
          "uploader": "Kavinsky"
        },
        {"_type": "url", "id": "aaaaaaaaaaa", "title": "[Private video]", "duration": null},
        {
          "_type": "url",
          "id": "bbbbbbbbbbb",
          "title": "Gunship - Tech Noir",
          "duration": 331.0,
          "uploader": "Gunship"
        },
        {"_type": "url", "id": "ccccccccccc", "title": "[Deleted video]", "duration": null}
      ]
    }
    """;

    // ---- link parsing -----------------------------------------------------

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=5NV6Rdv1a3I", "5NV6Rdv1a3I")]
    [InlineData("https://youtu.be/5NV6Rdv1a3I", "5NV6Rdv1a3I")]
    [InlineData("https://music.youtube.com/watch?v=5NV6Rdv1a3I&feature=share", "5NV6Rdv1a3I")]
    [InlineData("https://m.youtube.com/watch?v=5NV6Rdv1a3I&list=PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D", "5NV6Rdv1a3I")]
    [InlineData("https://www.youtube.com/shorts/5NV6Rdv1a3I", "5NV6Rdv1a3I")]
    [InlineData("youtube.com/watch?v=5NV6Rdv1a3I", "5NV6Rdv1a3I")]
    [InlineData("5NV6Rdv1a3I", "5NV6Rdv1a3I")]
    public void TryParse_VideoLinks_YieldOneVideo(string input, string expectedId)
    {
        Assert.True(YouTubeImportParser.TryParse(input, out var target, out var error), error);
        Assert.NotNull(target);
        Assert.Equal(YouTubeImportKind.Video, target!.Kind);
        Assert.Equal(expectedId, target.Id);
        Assert.Equal($"https://www.youtube.com/watch?v={expectedId}", target.Url);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D")]
    [InlineData("https://music.youtube.com/playlist?list=PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D&si=abc")]
    public void TryParse_PlaylistLinks_YieldPlaylist(string input)
    {
        Assert.True(YouTubeImportParser.TryParse(input, out var target, out var error), error);
        Assert.NotNull(target);
        Assert.Equal(YouTubeImportKind.Playlist, target!.Kind);
        Assert.Equal("PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D", target.Id);
        Assert.Equal("https://www.youtube.com/playlist?list=PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D", target.Url);
    }

    [Theory]
    [InlineData("PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D")]
    [InlineData("UUw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D")]
    public void TryParse_BarePlaylistIds_Resolve(string input)
    {
        Assert.True(YouTubeImportParser.TryParse(input, out var target, out _));
        Assert.Equal(YouTubeImportKind.Playlist, target!.Kind);
    }

    [Fact]
    public void TryParse_MixesUpNothing()
    {
        // A bare 11-char id is a video even though a PL id is longer.
        Assert.True(YouTubeImportParser.TryParse("PLw-VjHDlEOgvtnnnqWlTqByAtC7tXBg6D", out var playlist, out _));
        Assert.Equal(YouTubeImportKind.Playlist, playlist!.Kind);

        // "notalink" is shaped like nothing we accept; it must not become a lookup.
        Assert.False(YouTubeImportParser.TryParse("notalink", out _, out var error));
        Assert.Equal(YouTubeImportParser.NotALinkMessage, error);
    }

    [Theory]
    [InlineData("https://www.youtube.com/@daftpunk", YouTubeImportParser.ChannelMessage)]
    [InlineData("https://www.youtube.com/c/DaftPunk", YouTubeImportParser.ChannelMessage)]
    [InlineData("https://www.youtube.com/channel/UC3pDJd9Qk0Udt5z0Yq5aVJw", YouTubeImportParser.ChannelMessage)]
    [InlineData("", YouTubeImportParser.EmptyMessage)]
    [InlineData("   ", YouTubeImportParser.EmptyMessage)]
    [InlineData("https://example.com/watch?v=5NV6Rdv1a3I", YouTubeImportParser.NotALinkMessage)]
    [InlineData("https://www.youtube.com/playlist?list=", YouTubeImportParser.MissingListMessage)]
    public void TryParse_RejectsWithOneSentence(string input, string expected)
    {
        Assert.False(YouTubeImportParser.TryParse(input, out var target, out var error));
        Assert.Null(target);
        Assert.Equal(expected, error);
        Assert.DoesNotContain("\n", error);
    }

    // ---- listing parsing --------------------------------------------------

    [Fact]
    public void ParseListing_SingleVideo_OneEntryWithVideoShape()
    {
        var listing = YtDlp.ParseListing(WatchJson);

        var entry = Assert.Single(listing.Entries);
        Assert.Equal("5NV6Rdv1a3I", entry.VideoId);
        Assert.Contains("Get Lucky", entry.Title);
        Assert.Equal(248.0, entry.DurationSeconds);
        Assert.Equal(0, listing.SkippedCount);
        // A watch response carries the video's own title; the tab uses it as the
        // listing title for the single-video case.
        Assert.Contains("Get Lucky", listing.Title);
    }

    [Fact]
    public void ParseListing_Playlist_KeepsOrderAndCountsSkipped()
    {
        var listing = YtDlp.ParseListing(PlaylistJson);

        Assert.Equal("Synthwave Essentials", listing.Title);
        Assert.Equal(new[] { "MV_3Dpw-BRY", "jSyQ3Xh2Z4Y", "bbbbbbbbbbb" },
            listing.Entries.Select(e => e.VideoId));
        Assert.Equal(2, listing.SkippedCount);
        Assert.True(listing.HasSkipped);
        Assert.Equal(3, listing.Count);
    }

    [Fact]
    public void ParseListing_PlaylistEntriesBecomeTracksInOrder()
    {
        var listing = YtDlp.ParseListing(PlaylistJson);
        var tracks = YouTubeApi.ToSourceTracks(listing.Entries, "Synthwave Essentials");

        Assert.Equal(3, tracks.Count);
        Assert.Equal("Nightcall", tracks[1].Title);
        Assert.Equal("Kavinsky", tracks[1].Artist);
        Assert.Equal(258_000, tracks[1].DurationMs);
        Assert.All(tracks, t =>
        {
            Assert.Equal(MediaSource.YouTube, t.Source);
            Assert.Equal(SourceTrackState.New, t.State);
            Assert.Equal("Synthwave Essentials", t.PlaylistName);
        });
    }

    [Fact]
    public void ParseListing_GarbageJson_ThrowsOneClearMessage()
    {
        var ex = Assert.Throws<YtDlpException>(() => YtDlp.ParseListing("<html>not json</html>"));
        Assert.Contains("not valid JSON", ex.Message);
    }

    // ---- downloaded-from-YouTube resolution -------------------------------

    private static LedgerEntry Entry(
        string key, string path, string title = "T", string artist = "A",
        string? playlist = null, MediaSource? source = null, int daysAgo = 1) =>
        new(key, path, title, artist, DateTime.UtcNow.AddDays(-daysAgo), playlist, source);

    [Fact]
    public void ResolveRecordedProvenanceComesFirst()
    {
        var ledger = new[]
        {
            Entry("5NV6Rdv1a3I", @"C:\Music\Get Lucky.mp3", "Get Lucky", "Daft Punk",
                playlist: "Daft Punk", source: MediaSource.YouTube, daysAgo: 5),
            Entry("4uLU6hMCjMI", @"C:\Music\Cameo.mp3", "Cameo", "Kavinsky", daysAgo: 2),
            Entry("3n3Ppam7vga", @"C:\Music\Spotify Song.mp3", "Spotify Song", "Someone",
                playlist: "Mix", source: MediaSource.Spotify, daysAgo: 1),
        };

        var result = YouTubeLibrary.Resolve(ledger);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(1, result.Recorded);
        Assert.Equal(1, result.Derived);
        Assert.Equal(0, result.FromPlaylists);
        Assert.Equal(0.6667, result.LedgerFraction, 3);

        // Newest first: the derived file outranks the recorded one.
        Assert.Equal(@"C:\Music\Cameo.mp3", result.Files[0].Path);
        Assert.Equal(YouTubeProvenance.DerivedFromVideoId, result.Files[0].Provenance);
        Assert.Equal(YouTubeProvenance.Recorded, result.Files[1].Provenance);
        Assert.Contains("derived from the video id", result.Summary);
    }

    [Fact]
    public void Resolve_SpotifyKeysAreNeverYouTube()
    {
        var ledger = new[]
        {
            // A 22-character Spotify id must not be mistaken for a video id.
            Entry("4uLU6hMCjMIhL6x1Zq8kZ8Q", @"C:\Music\Blinding Lights.mp3",
                playlist: null, source: null),
            // Nor a non-video source key of the wrong length.
            Entry("abc", @"C:\Music\Odd.mp3"),
        };

        var result = YouTubeLibrary.Resolve(ledger);

        Assert.Empty(result.Files);
        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.YouTubeLedgerFiles);
        Assert.Equal("No YouTube downloads yet.", result.Summary);
    }

    [Fact]
    public void Resolve_MissingFilesAreCountedNotListed()
    {
        var ledger = new[]
        {
            Entry("5NV6Rdv1a3I", @"C:\Music\Gone.mp3"),
            Entry("KllnaNOxe0I", @"C:\Music\Here.mp3"),
        };
        Func<string, bool> exists = path => path.EndsWith("Here.mp3", StringComparison.Ordinal);

        var result = YouTubeLibrary.Resolve(ledger, fileExists: exists);

        Assert.Single(result.Files);
        Assert.Equal(1, result.Missing);
        Assert.Contains("gone from disk", result.Summary);
    }

    [Fact]
    public void Resolve_PlaylistRowsCoverFilesTheLedgerNeverRecorded()
    {
        var playlist = new UserPlaylist { Name = "Nightdrive", Source = MediaSource.YouTube };
        playlist.Tracks.Add(new SourceTrack
        {
            Id = "MV_3Dpw-BRY",
            VideoId = "MV_3Dpw-BRY",
            Title = "Sunset",
            Artist = "The Midnight",
            LibraryPath = @"C:\Music\Sunset.mp3",
            State = SourceTrackState.InLibrary,
        });

        var result = YouTubeLibrary.Resolve(
            Array.Empty<LedgerEntry>(), new[] { playlist });

        var file = Assert.Single(result.Files);
        Assert.Equal(YouTubeProvenance.PlaylistRow, file.Provenance);
        Assert.Equal("Nightdrive", file.PlaylistName);
        Assert.Equal(MediaSource.YouTube, file.ToSourceTrack().Source);
        Assert.Equal(SourceTrackState.InLibrary, file.ToSourceTrack().State);
    }

    [Fact]
    public void Resolve_InternalPlaylistsDoNotClaimFileProvenance()
    {
        var internalList = new UserPlaylist { Name = "My set", Kind = PlaylistKind.Internal };
        internalList.Tracks.Add(new SourceTrack
        {
            Id = "5NV6Rdv1a3I",
            Title = "Get Lucky",
            LibraryPath = @"C:\Music\Get Lucky.mp3",
        });

        var result = YouTubeLibrary.Resolve(Array.Empty<LedgerEntry>(), new[] { internalList });

        Assert.Empty(result.Files);
    }

    [Fact]
    public void IsVideoId_MatchesYouTubeShapeOnly()
    {
        Assert.True(YouTubeLibrary.IsVideoId("5NV6Rdv1a3I"));
        Assert.True(YouTubeLibrary.IsVideoId("KllnaNOxe0I"));
        Assert.False(YouTubeLibrary.IsVideoId("5NV6Rdv1a3"));
        Assert.False(YouTubeLibrary.IsVideoId("5NV6Rdv1a3II"));
        Assert.False(YouTubeLibrary.IsVideoId("5NV6Rdv1a3!"));
        Assert.False(YouTubeLibrary.IsVideoId(null));
    }

    [Fact]
    public void Resolve_UsesTheLedgerOnDisk_NotTheFileName()
    {
        // The file name says "Get Lucky" but the ledger says the source key is a
        // Spotify id: nothing here is YouTube.
        var ledger = new[]
        {
            Entry("5NV6Rdv1a3I", @"C:\Music\Get Lucky.mp3", playlist: "@daftpunk",
                source: MediaSource.Spotify),
        };

        var result = YouTubeLibrary.Resolve(ledger);

        // Recorded Spotify provenance wins over the video-id shape: no YouTube row.
        Assert.Empty(result.Files);
    }
}

/// <summary>
/// Provenance belongs to the download, not to a playlist name: a single video
/// queued straight from the importer (no playlist) still has to resolve in the
/// Downloaded view, and a later put that knows no source must not erase it.
/// Serialised with the other temp-home classes.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class YouTubeLedgerProvenanceTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previousHome;

    public YouTubeLedgerProvenanceTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(JockeyBox.Tools.AppPaths.HomeVariable);
        _home = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(JockeyBox.Tools.AppPaths.HomeVariable, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(JockeyBox.Tools.AppPaths.HomeVariable, _previousHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test run.
        }
    }

    [Fact]
    public void Put_WithoutPlaylistName_StillRecordsTheSource()
    {
        var path = Path.Combine(_home, "Get Lucky.mp3");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var ledger = new LibraryLedger();

        // A single video: the importer passes no playlist name at all.
        ledger.Put("5NV6Rdv1a3I", path, "Get Lucky", "Daft Punk",
            playlistName: null, playlistSource: MediaSource.YouTube);

        var recorded = ledger.TryGet("5NV6Rdv1a3I");
        Assert.NotNull(recorded);
        Assert.Equal(MediaSource.YouTube, recorded!.PlaylistSource);

        // Re-downloading with no new source keeps it instead of erasing it.
        ledger.Put("5NV6Rdv1a3I", path, "Get Lucky", "Daft Punk");
        Assert.Equal(MediaSource.YouTube, ledger.TryGet("5NV6Rdv1a3I")!.PlaylistSource);
    }
}
