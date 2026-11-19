using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Write a cache file exactly where the service reads it, reproducing the
    /// on-disk key (artist|title|seconds[|sourceId], MD5-named) so a test can seed
    /// a pre-upgrade source-less entry and a per-source one side by side.
    /// </summary>
    private void Seed(TrackEntry track, LyricsDocument doc, string? sourceId)
    {
        var key = $"{track.Artist}|{track.Title}|{(int)track.Duration.TotalSeconds}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(sourceId))
            key += "|" + sourceId.Trim().ToLowerInvariant();
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
    public async Task WhenEveryCandidateOverruns_TheClosestOneIsKeptAndSaysWhy()
    {
        var track = Headlights();
        Seed(track, Timed("lrcmux", "lrcmux", 51.047, 96.0, 217.750), "lrcmux");
        Seed(track, Timed("lrclib", "LRCLIB", 27.570, 90.0, 200.0), "lrclib");

        var lookup = await Service(
            new SilentProvider("lrcmux", "lrcmux"),
            new SilentProvider("lrclib", "LRCLIB")).GetLyricsAsync(track, CancellationToken.None);

        // Showing nothing would be worse: keep the smallest overrun (200 s, 28 s
        // past the end) and let the status line admit the timings run past the file.
        Assert.NotNull(lookup.Document);
        Assert.Equal("lrclib", lookup.Document!.SourceId);
        Assert.False(string.IsNullOrWhiteSpace(lookup.MissStatus));
        Assert.Contains("past the end", lookup.MissStatus!, StringComparison.OrdinalIgnoreCase);
    }
}
