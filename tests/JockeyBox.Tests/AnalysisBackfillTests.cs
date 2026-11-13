using System.IO;
using System.Text.Json;
using JockeyBox.Audio.Beat;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Tools;
using Xunit.Abstractions;

namespace JockeyBox.Tests;

/// <summary>
/// Analysis already on disk must reach the stores that answer "what is this
/// track's tempo and key", and the paths that produce new analysis must write it
/// down.
///
/// <para>The defect these pin: a grid measured months ago lived only in
/// <c>beat-grids.json</c>, the scan put it in a row on screen, and neither
/// <c>library.json</c>'s <c>Bpm</c>/<c>Key</c> nor the <c>tracks</c> tempo
/// columns ever received it. No analyser runs here - that is the point.</para>
///
/// <para>Every fixture runs against a throwaway <c>JOCKEYBOX_HOME</c>.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class AnalysisBackfillTests : IDisposable
{
    private const string Alpha = @"C:\Music\Alpha.mp3";
    private const string Beta = @"C:\Music\Beta.mp3";
    private const string Gamma = @"C:\Music\Gamma.mp3";
    private const string Ghost = @"C:\Music\Ghost.mp3";

    private readonly string? _previousHome;
    private readonly string _home;
    private readonly string _music;
    private readonly ITestOutputHelper _output;

    public AnalysisBackfillTests(ITestOutputHelper output)
    {
        _output = output;
        _previousHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        _home = Path.Combine(Path.GetTempPath(), "jb-backfill-home-" + Guid.NewGuid().ToString("n"));
        _music = Path.Combine(Path.GetTempPath(), "jb-backfill-music-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_music);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _previousHome);
        foreach (var dir in new[] { _home, _music })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static BeatGrid Grid(double bpm, double firstBeat = 0.412, double confidence = 0.83,
        string? key = null, double keyConfidence = 0, string? keyReason = null) => new(
        bpm, firstBeat,
        Enumerable.Range(0, 16).Select(i => firstBeat + i * 60.0 / bpm).ToArray(),
        bpmP10: bpm - 1.4, bpmP90: bpm + 1.1, confidence: confidence,
        key: key, keyConfidence: keyConfidence, keyAnalysed: true, keyReason: keyReason);

    private void WriteLibrary(params CachedTags[] entries)
    {
        var named = new Dictionary<string, CachedTags>(StringComparer.OrdinalIgnoreCase)
        {
            [Alpha] = entries[0],
            [Beta] = entries[1],
            [Gamma] = entries[2],
        };
        File.WriteAllText(Path.Combine(_home, "library.json"),
            JsonSerializer.Serialize(named, new JsonSerializerOptions { WriteIndented = false }));
    }

    private static CachedTags Cached(string title, double tagBpm = 0, string tagKey = "") =>
        new() { Version = 2, Title = title, Bpm = tagBpm, Key = tagKey, DurationMs = 180_000 };

    private static TrackRecord Row(string path, string title) => new()
    {
        Path = path,
        Title = title,
        SizeBytes = 100,
    };

    [Fact]
    public void Run_FillsBothStoresFromTheGridsAlreadyOnDisk()
    {
        WriteLibrary(Cached("Alpha"), Cached("Beta", tagBpm: 128, tagKey: "Am"), Cached("Gamma"));

        using var fixture = new TestDatabase();
        fixture.Tracks.UpsertMany(new[] { Row(Alpha, "Alpha"), Row(Beta, "Beta"), Row(Gamma, "Gamma") });

        var document = Path.Combine(_home, "beat-grids.json");
        using var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(Alpha, Grid(124.0, 0.412, 0.83, key: "8A", keyConfidence: 0.71));  // no tag: analysis fills it
        store.Save(Beta, Grid(174.0, 0.100, 0.70, key: "5B", keyConfidence: 0.60));  // tags win both fields
        store.Save(Ghost, Grid(90.0, 0.0, 0.40, key: "12A"));                        // grid for a lost file
        store.Flush();

        var tags = new TagCache();
        var (bpmJson, keyJson, rows) = AnalysisBackfill.Run(store, tags, fixture.Database);

        Assert.Equal(1, bpmJson);   // Alpha only: Beta's tag already answered, Gamma has no grid
        Assert.Equal(1, keyJson);   // Alpha only, for the same reason
        Assert.Equal(2, rows);      // Alpha and Beta have rows; Ghost has none

        var reloaded = new TagCache();
        Assert.Equal(124.0, reloaded.BpmOf(Alpha), 3);
        Assert.Equal(128.0, reloaded.BpmOf(Beta), 3);
        Assert.Equal(0.0, reloaded.BpmOf(Gamma), 3);

        var cache = JsonLibrary(_home);
        Assert.Equal("8A", cache[Alpha].Key);     // the analysed key reached the durable store
        Assert.Equal("Am", cache[Beta].Key);      // an authored TKEY is never displaced by an estimate
        Assert.Equal("", cache[Gamma].Key);
        Assert.False(cache.ContainsKey(Ghost));   // no entry created for a path the library lost

        var alpha = Assert.IsType<TrackRecord>(fixture.Tracks.GetByPath(Alpha));
        Assert.Equal(124.0, alpha.Bpm, 3);
        Assert.Equal(122.6, alpha.BpmP10, 3);
        Assert.Equal(125.1, alpha.BpmP90, 3);
        Assert.Equal(0.83, alpha.BpmConfidence, 3);
        Assert.Equal(412, alpha.FirstBeatMs);

        // An authored tempo reaches the column too, not the estimator's octave.
        var beta = Assert.IsType<TrackRecord>(fixture.Tracks.GetByPath(Beta));
        Assert.Equal(128.0, beta.Bpm, 3);

        // Nothing ran twice: the markers stop the pass, and the values are the same
        // ones, so even an unmarked repeat changes nothing.
        Assert.Equal((0, 0, 0), AnalysisBackfill.Run(store, tags, fixture.Database));
        Assert.Equal((0, 0, 0), AnalysisBackfill.Run(store, tags, null));
        Assert.Equal(124.0, new TagCache().BpmOf(Alpha), 3);

        _output.WriteLine($"home={AppPaths.Home}");
        _output.WriteLine($"grids={store.Count} bpmJson={bpmJson} keyJson={keyJson} rows={rows}");
        _output.WriteLine($"alpha={alpha.Bpm:0.###}/{cache[Alpha].Key} beta={beta.Bpm:0.###}/{cache[Beta].Key}");
    }

    [Fact]
    public void Run_WithTheDatabaseOff_StillWritesLibraryJson()
    {
        WriteLibrary(Cached("Alpha"), Cached("Beta"), Cached("Gamma"));

        var document = Path.Combine(_home, "beat-grids.json");
        using var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(Alpha, Grid(124.0, key: "8A", keyConfidence: 0.71));
        store.Flush();

        var (bpmJson, keyJson, rows) = AnalysisBackfill.Run(store, new TagCache(), db: null);

        Assert.Equal(1, bpmJson);
        Assert.Equal(1, keyJson);
        Assert.Equal(0, rows);

        var cache = JsonLibrary(_home);
        Assert.Equal(124.0, cache[Alpha].Bpm, 3);
        Assert.Equal("8A", cache[Alpha].Key);
    }

    [Fact]
    public void Scan_WritesTheCachedGridTempoAndKeyIntoTheLibraryJson()
    {
        // The scan is the other half: a track whose file states no tempo or key
        // must have the analysed ones written into the cache it is about to save.
        // Without this the values exist only in the row the UI built.
        var file = Path.Combine(_music, "Alpha.mp3");
        File.WriteAllBytes(file, Array.Empty<byte>());

        var document = Path.Combine(_home, "beat-grids.json");
        using var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(file, Grid(124.0, key: "8A", keyConfidence: 0.71));
        store.Flush();

        var tags = new TagCache();
        var scanned = new LibraryScanner(tags).Scan(_music, "");
        Assert.Single(scanned);
        Assert.Equal(124.0, scanned[0].Bpm, 3);
        Assert.Equal("8A", scanned[0].Key);
        tags.Save();

        var reloaded = new TagCache();
        Assert.Equal(124.0, reloaded.BpmOf(file), 3);
        Assert.Equal("8A", JsonLibrary(_home)[file].Key);

        _output.WriteLine($"home={AppPaths.Home}");
        _output.WriteLine($"scanned={file} bpm={reloaded.BpmOf(file):0.###} key={JsonLibrary(_home)[file].Key}");
    }

    [Fact]
    public void GridCarriesTheKeyReasonAndAnEntryWithoutOneStillLoads()
    {
        var document = Path.Combine(_home, "beat-grids.json");
        using (var store = new BeatGridStore(document, mirror: (_, _) => true))
        {
            store.Save(Alpha, Grid(124.0, keyReason: "ambiguous (best Am corr 0.41 margin 0.02)"));
            store.Flush();
        }

        using var reopened = new BeatGridStore(document, mirror: (_, _) => true);
        Assert.Equal("ambiguous (best Am corr 0.41 margin 0.02)", reopened.Get(Alpha)!.KeyReason);

        // An entry written before the field existed has no keyReason key at all:
        // it must still load, and read as "analysed, reason unknown".
        var json = File.ReadAllText(document);
        File.WriteAllText(document, json.Replace("\"keyReason\":", "\"legacyReason_\":"));
        Assert.DoesNotContain("\"keyReason\"", File.ReadAllText(document));

        using var legacy = new BeatGridStore(document, mirror: (_, _) => true);
        var grid = legacy.Get(Alpha);
        Assert.NotNull(grid);
        Assert.Null(grid!.KeyReason);
        Assert.True(grid.KeyAnalysed);
    }

    [Fact]
    public void KeyNote_ReportsTheReasonRatherThanOneBareNoKey()
    {
        Assert.Equal("key 8A (0.71)", BpmAnalysisService.KeyNote(Grid(124.0, key: "8A", keyConfidence: 0.71)));
        Assert.Equal("no key: ambiguous (best Am corr 0.41 margin 0.02)",
            BpmAnalysisService.KeyNote(Grid(124.0, keyReason: "ambiguous (best Am corr 0.41 margin 0.02)")));
        Assert.Equal("key stage failed: RuntimeError: codec exploded",
            BpmAnalysisService.KeyNote(Grid(124.0, keyReason: "key stage failed: RuntimeError: codec exploded")));

        // An entry from before the reason field still says "no key", and a track
        // that predates key estimation still says "key n/a": neither is a failure.
        Assert.Equal("no key", BpmAnalysisService.KeyNote(new BeatGrid(124.0, 0, keyAnalysed: true)));
        Assert.Equal("key n/a", BpmAnalysisService.KeyNote(new BeatGrid(124.0, 0)));
    }

    private static Dictionary<string, CachedTags> JsonLibrary(string home)
    {
        var path = Path.Combine(home, "library.json");
        if (!File.Exists(path)) return new Dictionary<string, CachedTags>(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize<Dictionary<string, CachedTags>>(File.ReadAllText(path))!
               ?? new Dictionary<string, CachedTags>(StringComparer.OrdinalIgnoreCase);
    }
}
