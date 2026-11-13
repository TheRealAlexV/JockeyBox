using System.IO;
using System.Text.Json;
using JockeyBox.Audio.Beat;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Tools;
using Xunit.Abstractions;

namespace JockeyBox.Tests;

/// <summary>
/// Analysis already on disk must reach the two stores that answer "what is this
/// track's tempo", and the paths that produce new analysis must write it down.
///
/// <para>The defect these pin: a tempo measured months ago lived only in
/// <c>beat-grids.json</c>, the scan put it in a row on screen, and neither
/// <c>library.json</c>'s <c>Bpm</c> nor the <c>tracks</c> columns ever received it.
/// No analyser runs here - that is the point.</para>
///
/// <para>Every fixture runs against a throwaway <c>JOCKEYBOX_HOME</c>.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class BpmBackfillTests : IDisposable
{
    private const string Alpha = @"C:\Music\Alpha.mp3";
    private const string Beta = @"C:\Music\Beta.mp3";
    private const string Gamma = @"C:\Music\Gamma.mp3";
    private const string Ghost = @"C:\Music\Ghost.mp3";

    private readonly string? _previousHome;
    private readonly string _home;
    private readonly string _music;
    private readonly ITestOutputHelper _output;

    public BpmBackfillTests(ITestOutputHelper output)
    {
        _output = output;
        _previousHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        _home = Path.Combine(Path.GetTempPath(), "jb-bpm-home-" + Guid.NewGuid().ToString("n"));
        _music = Path.Combine(Path.GetTempPath(), "jb-bpm-music-" + Guid.NewGuid().ToString("n"));
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

    private static BeatGrid Grid(double bpm, double firstBeat = 0.412, double confidence = 0.83) => new(
        bpm, firstBeat,
        Enumerable.Range(0, 16).Select(i => firstBeat + i * 60.0 / bpm).ToArray(),
        bpmP10: bpm - 1.4, bpmP90: bpm + 1.1, confidence: confidence);

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

    private static CachedTags Cached(string title, double tagBpm) =>
        new() { Version = 2, Title = title, Bpm = tagBpm, DurationMs = 180_000 };

    private static TrackRecord Row(string path, string title) => new()
    {
        Path = path,
        Title = title,
        SizeBytes = 100,
    };

    [Fact]
    public void Run_FillsBothStoresFromTheGridsAlreadyOnDisk()
    {
        WriteLibrary(Cached("Alpha", 0), Cached("Beta", 128), Cached("Gamma", 0));

        using var fixture = new TestDatabase();
        fixture.Tracks.UpsertMany(new[] { Row(Alpha, "Alpha"), Row(Beta, "Beta"), Row(Gamma, "Gamma") });

        var document = Path.Combine(_home, "beat-grids.json");
        using var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(Alpha, Grid(124.0, 0.412, 0.83));      // no tag: analysis fills it
        store.Save(Beta, Grid(174.0, 0.100, 0.70));       // tag 128 wins, estimate must not
        store.Save(Ghost, Grid(90.0, 0.0, 0.40));         // grid for a file the library lost
        store.Flush();

        var tags = new TagCache();
        var (json, rows) = BpmBackfill.Run(store, tags, fixture.Database);

        Assert.Equal(1, json);   // Alpha only: Beta's tag already answered, Gamma has no grid
        Assert.Equal(2, rows);   // Alpha and Beta have rows; Ghost has none

        var reloaded = new TagCache();
        Assert.Equal(124.0, reloaded.BpmOf(Alpha), 3);
        Assert.Equal(128.0, reloaded.BpmOf(Beta), 3);
        Assert.Equal(0.0, reloaded.BpmOf(Gamma), 3);
        Assert.Equal(0.0, reloaded.BpmOf(Ghost), 3);
        Assert.DoesNotContain(Ghost, JsonKeys(_home), StringComparer.OrdinalIgnoreCase);

        var alpha = Assert.IsType<TrackRecord>(fixture.Tracks.GetByPath(Alpha));
        Assert.Equal(124.0, alpha.Bpm, 3);
        Assert.Equal(122.6, alpha.BpmP10, 3);
        Assert.Equal(125.1, alpha.BpmP90, 3);
        Assert.Equal(0.83, alpha.BpmConfidence, 3);
        Assert.Equal(412, alpha.FirstBeatMs);

        // An authored tempo reaches the column too, not the estimator's octave.
        var beta = Assert.IsType<TrackRecord>(fixture.Tracks.GetByPath(Beta));
        Assert.Equal(128.0, beta.Bpm, 3);

        // Nothing ran twice: the marker stops the pass, and the values are the same
        // ones, so even an unmarked repeat changes nothing.
        Assert.Equal((0, 0), BpmBackfill.Run(store, tags, fixture.Database));
        Assert.Equal((0, 0), BpmBackfill.Run(store, tags, null));
        Assert.Equal(124.0, new TagCache().BpmOf(Alpha), 3);

        _output.WriteLine($"home={AppPaths.Home}");
        _output.WriteLine($"grids={store.Count} json={json} rows={rows}");
        _output.WriteLine($"alpha={alpha.Bpm:0.###} beta={beta.Bpm:0.###}");
    }

    [Fact]
    public void Run_WithTheDatabaseOff_StillWritesLibraryJson()
    {
        WriteLibrary(Cached("Alpha", 0), Cached("Beta", 0), Cached("Gamma", 0));

        var document = Path.Combine(_home, "beat-grids.json");
        using var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(Alpha, Grid(124.0));
        store.Flush();

        var (json, rows) = BpmBackfill.Run(store, new TagCache(), db: null);

        Assert.Equal(1, json);
        Assert.Equal(0, rows);
        Assert.Equal(124.0, new TagCache().BpmOf(Alpha), 3);
    }

    [Fact]
    public void Scan_WritesTheCachedGridTempoIntoTheLibraryJson()
    {
        // The scan is the other half: a track whose file states no tempo must have
        // the analysed one written into the cache it is about to save. Without
        // this the number exists only in the row the UI built.
        var file = Path.Combine(_music, "Alpha.mp3");
        File.WriteAllBytes(file, Array.Empty<byte>());

        var document = Path.Combine(_home, "beat-grids.json");
        using var store = new BeatGridStore(document, mirror: (_, _) => true);
        store.Save(file, Grid(124.0));
        store.Flush();

        var tags = new TagCache();
        var scanned = new LibraryScanner(tags).Scan(_music, "");
        Assert.Single(scanned);
        Assert.Equal(124.0, scanned[0].Bpm, 3);
        tags.Save();

        var reloaded = new TagCache();
        Assert.Equal(124.0, reloaded.BpmOf(file), 3);
        _output.WriteLine($"home={AppPaths.Home}");
        _output.WriteLine($"scanned={file} bpm={reloaded.BpmOf(file):0.###}");
    }

    private static string[] JsonKeys(string home)
    {
        var path = Path.Combine(home, "library.json");
        if (!File.Exists(path)) return Array.Empty<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }
}
