using System.IO;
using System.Text.Json;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Tools;
using Xunit.Abstractions;

namespace JockeyBox.Tests;

/// <summary>
/// One scan result, one prune policy, both stores. A file the scan no longer
/// sees must leave <c>library.json</c> (the tag cache) and <c>library.db</c>
/// (the tracks table) in the same pass - and must not leave anything when the
/// folder was merely unreachable.
///
/// <para>Every fixture runs against a throwaway <c>JOCKEYBOX_HOME</c> and a
/// throwaway music tree, never the operator's profile.</para>
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class LibraryPruneTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly string _music;
    private readonly ITestOutputHelper _output;

    public LibraryPruneTests(ITestOutputHelper output)
    {
        _output = output;
        _previousHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        _home = Path.Combine(Path.GetTempPath(), "jb-prune-home-" + Guid.NewGuid().ToString("n"));
        _music = Path.Combine(Path.GetTempPath(), "jb-prune-music-" + Guid.NewGuid().ToString("n"));
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

    private string File(string name)
    {
        var path = Path.Combine(_music, name);
        System.IO.File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    /// <summary>
    /// One sandbox scan, exactly as the app runs it: scan, save the tag cache,
    /// mirror the json into the database. Returns the scanned rows.
    /// </summary>
    private static List<TrackEntry> Index(string home, string music, TagCache tags, TestDatabase fixture)
    {
        var rows = new LibraryScanner(tags).Scan(music, "");
        tags.Save();
        new LegacyImporter(fixture.Database).Run(home, force: true);
        return rows;
    }

    private static List<string> Paths(List<TrackEntry> rows) => rows
        .SelectMany(t => new[] { t.MusicPath, t.KaraokePath })
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p!)
        .ToList();

    private static string[] JsonKeys(string home)
    {
        var json = System.IO.File.ReadAllText(Path.Combine(home, "library.json"));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.Keys.ToArray();
    }

    [Fact]
    public void Rescan_RetiresAVanishedFile_FromBothStoresInOnePass()
    {
        var keep = File("Keep.mp3");
        var stale = File("Cavinsky - Cameo.mp3");
        File("Third.mp3");

        using var fixture = new TestDatabase();
        var tags = new TagCache();

        var first = Index(_home, _music, tags, fixture);
        Assert.Equal(3, first.Count);
        Assert.Equal(3, fixture.Tracks.Count());
        Assert.Contains(stale, JsonKeys(_home), StringComparer.OrdinalIgnoreCase);

        // The file is replaced (a different extension) and the old one is gone.
        System.IO.File.Delete(stale);

        var second = new LibraryScanner(tags).Scan(_music, "");
        Assert.Equal(2, second.Count);

        // Same policy the view model runs after a scan: db first, cache second.
        var pruned = LibraryPrune.Apply(Paths(second), tags, fixture.Tracks);

        Assert.Equal(1, pruned);
        Assert.Equal(2, fixture.Tracks.Count());
        Assert.Null(fixture.Tracks.GetByPath(stale));
        Assert.NotNull(fixture.Tracks.GetByPath(keep));

        var cached = JsonKeys(_home);
        Assert.Equal(2, cached.Length);
        Assert.DoesNotContain(stale, cached, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(keep, cached, StringComparer.OrdinalIgnoreCase);

        _output.WriteLine($"home={AppPaths.Home}");
        _output.WriteLine($"music={_music}");
        _output.WriteLine($"deleted={stale}");
        _output.WriteLine($"library.db rows: {string.Join(" | ", fixture.Tracks.All().Select(t => t.Path))}");
        _output.WriteLine($"library.json keys: {string.Join(" | ", cached)}");
    }

    [Fact]
    public void Rescan_WithTheFolderUnreachable_EmptiesNeitherStore()
    {
        File("One.mp3");
        File("Two.mp3");

        using var fixture = new TestDatabase();
        var tags = new TagCache();

        var first = Index(_home, _music, tags, fixture);
        Assert.Equal(2, first.Count);

        // Drive unplugged / share offline: the folder is gone, not the files.
        var offline = _music + "-offline";
        Directory.Move(_music, offline);
        try
        {
            var second = new LibraryScanner(tags).Scan(_music, "");
            Assert.Empty(second);

            // The view model consults exactly this guard before pruning.
            Assert.False(LibraryPrune.RootsReachable(_music));
            if (LibraryPrune.RootsReachable(_music))
                LibraryPrune.Apply(Paths(second), tags, fixture.Tracks);

            Assert.Equal(2, fixture.Tracks.Count());
            Assert.Equal(2, JsonKeys(_home).Length);

            _output.WriteLine($"home={AppPaths.Home}");
            _output.WriteLine($"music (moved away)={_music}");
            _output.WriteLine($"roots reachable={LibraryPrune.RootsReachable(_music)}");
            _output.WriteLine($"library.db rows: {string.Join(" | ", fixture.Tracks.All().Select(t => t.Path))}");
            _output.WriteLine($"library.json keys: {string.Join(" | ", JsonKeys(_home))}");
        }
        finally
        {
            Directory.Move(offline, _music);
        }
    }

    [Fact]
    public void Prune_TakesTheTracksChildrenAndKeepsPlaylistMembership()
    {
        using var fixture = new TestDatabase();

        var keepId = fixture.Tracks.Upsert(new TrackRecord { Path = fixture.PathFor("music/keep.mp3"), Title = "Keep" });
        var goneId = fixture.Tracks.Upsert(new TrackRecord { Path = fixture.PathFor("music/gone.mp3"), Title = "Gone" });
        Assert.True(keepId > 0 && goneId > 0);

        new HotCueRepository(fixture.Database).Set(goneId, 0, 1, 5_000);

        fixture.Database.WithWriter(conn =>
        {
            JockeyBoxDatabase.Execute(conn, "INSERT INTO playlists (id, name) VALUES ('p1', 'Main Set');");
            JockeyBoxDatabase.Execute(conn,
                "INSERT INTO playlist_items (playlist_id, position, source_key, title) " +
                "VALUES ('p1', 1024, 'music/gone', 'Gone');");
            JockeyBoxDatabase.Execute(conn,
                $"INSERT INTO track_lyrics (key, track_id, title) VALUES ('music/gone', {goneId}, 'Gone');");
        });

        var pruned = fixture.Tracks.PruneExcept(new[] { fixture.PathFor("music/keep.mp3") });

        Assert.Equal(1, pruned);
        Assert.Equal(1, fixture.Tracks.Count());
        Assert.NotNull(fixture.Tracks.GetById(keepId));

        // hot_cues is ON DELETE CASCADE: a cue is a timestamp in audio that is gone.
        Assert.Equal(0, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM hot_cues;"))));

        // track_lyrics keeps the document, detached from the vanished row.
        Assert.Equal(0, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM track_lyrics WHERE track_id IS NOT NULL;"))));
        Assert.Equal(1, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM track_lyrics;"))));

        // playlist_items has no FK to tracks by design: membership survives.
        Assert.Equal(1, fixture.Database.Read(conn => Convert.ToInt32(
            JockeyBoxDatabase.Scalar(conn, "SELECT COUNT(*) FROM playlist_items;"))));
    }
}
