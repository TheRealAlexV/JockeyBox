using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A cached lyric document can belong to a different edit of the song: the lrcmux
/// timeline for "Headlights" runs to 217.75 s while the file it is played against
/// is 171.99 s, and every line drifts a little further behind LRCLIB as the track
/// plays, which is a tempo/length mismatch and not an offset bug. These tests pin
/// the rule that makes that recoverable: a document whose last line lands well past
/// the end of the audio loses to one that fits, unless nothing fits.
///
/// <para>Line times are the ones measured from the real cache files, not invented:
/// 51.047-217.750 for the lrcmux document and 27.570-148.860 for LRCLIB against a
/// 171.99 s file.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public class LyricsDurationFitTests : IDisposable
{
    private const double TrackSeconds = 171.99;

    private readonly string _home;
    private readonly HomeLease _lease;
    private readonly string _cacheDir;

    public LyricsDurationFitTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "jb-lyrics-fit", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        _cacheDir = Path.Combine(_home, "lyrics");
        Directory.CreateDirectory(_cacheDir);

        _lease = HomeLease.Enter(_home, nameof(LyricsDurationFitTests));
    }

    public void Dispose()
    {
        _lease.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { /* test cleanup */ }
    }

    /// <summary>The file as the operator has it: 171.99 s of audio.</summary>
    private static TrackEntry Headlights() => new()
    {
        RelativeKey = "Headlights",
        RelativePath = "Headlights.mp3",
        Title = "Headlights",
        Artist = "The Artist",
        Duration = TimeSpan.FromSeconds(TrackSeconds),
    };

    private static TrackEntry Track(double seconds) => new()
    {
        RelativeKey = "track",
        RelativePath = "Track.mp3",
        Title = "Track",
        Artist = "The Artist",
        Duration = TimeSpan.FromSeconds(seconds),
    };

    /// <summary>A synced document owning the given line starts (seconds).</summary>
    private static LyricsDocument Timed(string sourceId, string source, params double[] lineStarts) => new()
    {
        Synced = true,
        Source = source,
        SourceId = sourceId,
        Lines = lineStarts
            .Select(s => new LyricLine(TimeSpan.FromSeconds(s), $"line at {s:0.000}"))
            .ToList(),
    };

    /// <summary>A provider that always hands back the same document.</summary>
    private sealed class FixedProvider : ILyricsProvider
    {
        private readonly LyricsDocument? _document;

        public FixedProvider(string id, string name, LyricsDocument? document)
        {
            Id = id;
            Name = name;
            _document = document;
        }

        public string Id { get; }
        public string Name { get; }

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct) =>
            Task.FromResult(_document);
    }

    /// <summary>A provider that always misses, so the chain can only answer from cache.</summary>
    private sealed class SilentProvider : ILyricsProvider
    {
        public SilentProvider(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
        {
            LyricsTrace.Note(Id, "returned nothing");
            return Task.FromResult<LyricsDocument?>(null);
        }
    }

    /// <summary>
    /// Write a cache file exactly where the service reads it, using the service's
    /// own key builder, so a test can seed a source-less entry and a per-source
    /// one side by side.
    /// </summary>
    private void Seed(TrackEntry track, LyricsDocument doc, string? sourceId)
    {
        var key = LyricsService.CacheKey(track.Artist, track.Title, track.Duration, sourceId);
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        File.WriteAllText(Path.Combine(_cacheDir, hash + ".json"), JsonSerializer.Serialize(doc));
    }

    private LyricsService Service(params ILyricsProvider[] providers) =>
        new(new AppSettings(), _cacheDir, providers);

    [Fact]
    public async Task WrongVersionDocument_IsRejectedInFavourOfOneThatFits()
    {
        var track = Headlights();
        // The cached lrcmux timeline is for a longer edit: last line 217.75 s on a
        // 171.99 s file, 45.76 s past the end.
        Seed(track, Timed("lrcmux", "lrcmux", 51.047, 96.0, 217.750), "lrcmux");
        Seed(track, Timed("lrclib", "LRCLIB", 27.570, 90.0, 148.860), "lrclib");

        var lookup = await Service(
            new SilentProvider("lrcmux", "lrcmux"),
            new SilentProvider("lrclib", "LRCLIB")).GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(lookup.Document);
        Assert.Equal("lrclib", lookup.Document!.SourceId);
        Assert.Equal(TimeSpan.FromSeconds(27.570), lookup.Document.Lines[0].Time);
    }

    [Fact]
    public async Task PreferredProviderDocument_ThatCannotFitTheFile_FallsThroughToTheChain()
    {
        var track = Headlights();
        // The same wrong edit as above, but handed back by the pinned provider's
        // live lookup instead of the cache: an explicit source choice must not skip
        // the duration guard. A fitting document is already cached.
        Seed(track, Timed("lrclib", "LRCLIB", 27.570, 90.0, 148.860), "lrclib");

        var lookup = await Service(
            new FixedProvider("lrcmux", "lrcmux", Timed("lrcmux", "lrcmux", 51.047, 96.0, 217.750)),
            new SilentProvider("lrclib", "LRCLIB"))
            .GetLyricsAsync(track, CancellationToken.None, preferredSourceId: "lrcmux");

        Assert.NotNull(lookup.Document);
        Assert.Equal("lrclib", lookup.Document!.SourceId);
        Assert.Equal(TimeSpan.FromSeconds(27.570), lookup.Document.Lines[0].Time);
    }

    [Fact]
    public async Task DocumentOverrunningByAFewSeconds_IsStillAccepted()
    {
        var track = Headlights();
        // 174.5 s is 2.51 s past the end: a trailing credit line on a slightly
        // different master. Rejecting this would be worse than the bug.
        Seed(track, Timed("lrclib", "LRCLIB", 27.570, 90.0, 174.500), "lrclib");

        var lookup = await Service(
            new SilentProvider("lrclib", "LRCLIB")).GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(lookup.Document);
        Assert.Equal("lrclib", lookup.Document!.SourceId);
        Assert.Equal(TimeSpan.FromSeconds(27.570), lookup.Document.Lines[0].Time);
    }

    [Fact]
    public async Task SourceSpecificCacheEntry_BeatsTheLegacyEntry()
    {
        var track = Headlights();
        // Pre-upgrade builds wrote only the source-less key; both entries fit, so
        // only the ordering can decide.
        Seed(track, Timed("", "Unison", 30.0, 90.0, 150.0), sourceId: null);
        Seed(track, Timed("lrclib", "LRCLIB", 27.570, 90.0, 148.860), "lrclib");

        var lookup = await Service(
            new SilentProvider("lrclib", "LRCLIB")).GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(lookup.Document);
        Assert.Equal("lrclib", lookup.Document!.SourceId);
        Assert.Equal(TimeSpan.FromSeconds(27.570), lookup.Document.Lines[0].Time);
    }

    [Fact]
    public async Task An_overrunning_document_alone_is_not_displayed()
    {
        var track = Headlights();
        // The lrcmux timeline belongs to a longer edit: last line 217.75 s on a
        // 171.99 s file, 45.76 s past the end. No other source answers.
        Seed(track, Timed("lrcmux", "lrcmux", 51.047, 96.0, 217.750), "lrcmux");

        var lookup = await Service(
            new SilentProvider("lrcmux", "lrcmux"),
            new SilentProvider("lrclib", "LRCLIB")).GetLyricsAsync(track, CancellationToken.None);

        // The operator reads these aloud on stage: a timeline that runs past the
        // audio is worse than no timeline, so nothing is displayed and the note
        // names the rejection.
        Assert.Null(lookup.Document);
        Assert.False(string.IsNullOrWhiteSpace(lookup.MissStatus));
        Assert.Contains("past the end", lookup.MissStatus!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("45.8", lookup.MissStatus!);   // the overrun, in seconds
        Assert.Contains("217.8", lookup.MissStatus!);  // last line of the refused document
    }

    [Fact]
    public async Task A_fitting_document_is_preferred_over_an_overrunning_one()
    {
        var track = Track(240.0);
        // The Get Lucky shape: the lrcmux timeline runs to 312.7 s, Better Lyrics
        // stops at 221.4 s and belongs to this edit.
        Seed(track, Timed("lrcmux", "lrcmux", 30.0, 120.0, 312.7), "lrcmux");
        Seed(track, Timed("betterlyrics", "Better Lyrics", 27.5, 120.0, 221.4), "betterlyrics");

        var lookup = await Service(
            new SilentProvider("lrcmux", "lrcmux"),
            new SilentProvider("betterlyrics", "Better Lyrics")).GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(lookup.Document);
        Assert.Equal("betterlyrics", lookup.Document!.SourceId);
        Assert.Null(lookup.MissStatus);
    }

    [Fact]
    public async Task A_document_inside_the_tolerance_is_still_displayed()
    {
        // 200 s file, tolerance max(5 s, 5%) = 10 s. 9.9 s past the end is a
        // trailing credit on a slightly different master, not another edit.
        var track = Track(200.0);
        Seed(track, Timed("lrclib", "LRCLIB", 10.0, 100.0, 209.9), "lrclib");

        var lookup = await Service(
            new SilentProvider("lrclib", "LRCLIB")).GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(lookup.Document);
        Assert.Equal("lrclib", lookup.Document!.SourceId);
        Assert.Null(lookup.MissStatus);
    }
}

/// <summary>
/// The refusal has to reach the panel: no lyric rows, and a status line that says
/// which candidate was rejected and by how much. Drives a real view model on
/// <see cref="UiThreadCollection"/>, no window shown.
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class LyricsRefusalStatusTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly string _cacheDir;

    public LyricsRefusalStatusTests()
    {
        _previousHome = Environment.GetEnvironmentVariable("JOCKEYBOX_HOME");
        string root = OperatingSystem.IsWindows() ? @"C:\Dev" : Path.GetTempPath();
        _home = Path.Combine(root, "jb-lyrics-refusal", Guid.NewGuid().ToString("n"));
        _cacheDir = Path.Combine(_home, "lyrics");
        Directory.CreateDirectory(_cacheDir);
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch { /* a save timer may still hold it */ }
    }

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        typeof(MainViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args.Length == 0 ? null : args);

    [Fact]
    public async Task The_refusal_reason_reaches_the_status_line()
    {
        var track = new TrackEntry
        {
            RelativeKey = "get-lucky",
            RelativePath = "Get Lucky.mp3",
            Title = "Get Lucky",
            Artist = "Daft Punk",
            Duration = TimeSpan.FromSeconds(240.0),
        };
        var refused = new LyricsDocument
        {
            Synced = true,
            Source = "lrcmux",
            SourceId = "lrcmux",
            Lines = new List<LyricLine>
            {
                new(TimeSpan.FromSeconds(30.0), "line one"),
                new(TimeSpan.FromSeconds(312.7), "line past the end"),
            },
        };
        var key = LyricsService.CacheKey(track.Artist, track.Title, track.Duration, "lrcmux");
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        File.WriteAllText(Path.Combine(_cacheDir, hash + ".json"), JsonSerializer.Serialize(refused));

        var service = new LyricsService(
            new AppSettings(), _cacheDir,
            new ILyricsProvider[] { new SilentProvider("lrcmux", "lrcmux") });
        var lookup = await service.GetLyricsAsync(track, CancellationToken.None);
        Assert.Null(lookup.Document);

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() =>
            {
                vm.LoadedTrack = track;
                Invoke(vm, "ApplyLyricsDocument", lookup.Document, lookup.MissStatus);
            });

            Assert.Contains("past the end", vm.LyricsStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("72.7", vm.LyricsStatus);
            Assert.Empty(vm.LyricLines);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    private sealed class SilentProvider : ILyricsProvider
    {
        public SilentProvider(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct) =>
            Task.FromResult<LyricsDocument?>(null);
    }
}
