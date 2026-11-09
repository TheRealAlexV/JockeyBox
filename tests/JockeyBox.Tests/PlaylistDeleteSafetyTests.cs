using System.IO;
using JockeyBox.Data;
using JockeyBox.Sources;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.YouTube;

namespace JockeyBox.Tests;

/// <summary>
/// Data-safety regressions for playlist deletion. Two independent reviews found the
/// same class of bug twice: a write queued before a delete resurrecting the row, and
/// a delete-absent prune running against a graph that was not the database's. These
/// assert what a caller observes (row gone from SQLite and from a cold load), not
/// internal fields.
/// </summary>
[Collection(nameof(JockeyBoxHomeCollection))]
public sealed class PlaylistDeleteSafetyTests : IDisposable
{
    private readonly string _home;
    private readonly string? _previousHome;

    public PlaylistDeleteSafetyTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        _home = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _previousHome);
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
    public void DeferredWriteQueuedBeforeADelete_DoesNotResurrectTheDeletedPlaylist()
    {
        using var fixture = new TestDatabase();
        using var store = new PlaylistStore(fixture.Database);

        var keeper = store.Create("keeper");
        var doomed = store.Create("doomed");
        store.AddTracks(doomed, new[] { new SourceTrack { Id = "doomed-1", Title = "D1", Artist = "A" } });
        store.Flush();

        // A deferred mutation clones its snapshot *before* the delete and parks it on
        // the write queue: exactly the ordering that used to re-insert the row.
        store.AddTracks(keeper, new[] { new SourceTrack { Id = "kept-1", Title = "K1", Artist = "B" } });

        Assert.True(store.Delete(doomed));
        store.Flush(); // drain whatever was queued before and after the delete

        Assert.Equal(0, RowCount(fixture, "playlists", doomed.Id));
        Assert.Equal(0, RowCount(fixture, "playlist_items", doomed.Id));

        using var reloaded = new PlaylistStore(fixture.Database);
        Assert.DoesNotContain(reloaded.Playlists, p => p.Id == doomed.Id);
        Assert.Contains(reloaded.Playlists, p => p.Id == keeper.Id);
    }

    [Fact]
    public void DeleteWhileTheStoreRunsOffJson_IsStillRemovedFromSqlite()
    {
        using var fixture = new TestDatabase();
        using var store = new PlaylistStore(fixture.Database);

        // Fresh database: the store takes the file branch. Its graph is *not* a read
        // of the database, which is the state where the prune must stay switched off.
        Assert.Equal("json", store.LoadSource);

        var doomed = store.Create("doomed");
        var keeper = store.Create("keeper");
        store.Flush(); // the seed and the mirror both reach SQLite

        Assert.True(store.Delete(doomed));
        store.Flush();

        using var reloaded = new PlaylistStore(fixture.Database);
        Assert.DoesNotContain(reloaded.Playlists, p => p.Id == doomed.Id);
        Assert.Contains(reloaded.Playlists, p => p.Id == keeper.Id);
    }

    [Fact]
    public void FossilChannelUrlRow_IsDeletable_AndDoesNotComeBack()
    {
        using var fixture = new TestDatabase();
        using var store = new PlaylistStore(fixture.Database);

        // The row the whole change exists for: a channel page stored as a playlist id.
        var fossil = store.Create("@kavinsky", MediaSource.YouTube, "https://www.youtube.com/@kavinsky");
        store.Flush();

        Assert.True(YouTubeViewModel.IsBrokenYouTubeSource(fossil.SourceId));
        Assert.True(store.Delete(fossil));
        store.Flush();

        using var reloaded = new PlaylistStore(fixture.Database);
        Assert.DoesNotContain(reloaded.Playlists, p => p.Id == fossil.Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/@kavinsky", true)]
    [InlineData("@kavinsky", true)]
    [InlineData("   @kavinsky   ", true)]
    [InlineData("https://www.youtube.com/channel/UC1234567890", true)]
    [InlineData("https://www.youtube.com/c/SomeName", true)]
    [InlineData("https://www.youtube.com/user/SomeName", true)]
    [InlineData("WLabc123456", false)]
    [InlineData("PLabc123456", false)]
    [InlineData("https://www.youtube.com/playlist?list=PLabc123&si=xyz", false)]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", false)]
    [InlineData("", false)]
    public void RepairGuard_OnlyNamesTheChannelShapes(string sourceId, bool broken) =>
        Assert.Equal(broken, YouTubeViewModel.IsBrokenYouTubeSource(sourceId));

    [Theory]
    [InlineData("PL")]
    [InlineData("UU")]
    [InlineData("OL")]
    [InlineData("RD")]
    [InlineData("LL")]
    [InlineData("FL")]
    [InlineData("WL")]
    [InlineData("TL")]
    [InlineData("PU")]
    [InlineData("SP")]
    [InlineData("EC")]
    [InlineData("VL")]
    public void EveryPrefixTheImporterAccepts_IsAPlaylistToTheRepairToo(string prefix)
    {
        var id = prefix + "abc123";

        Assert.True(YouTubeImportParser.TryParse(id, out var target, out _));
        Assert.NotNull(target);
        Assert.True(target!.IsPlaylist);

        // The repair must never remove or detach a row this app can import.
        Assert.False(YouTubeViewModel.IsBrokenYouTubeSource(id));
    }

    [Theory]
    [InlineData("https://www.youtube.com/@kavinsky")]
    [InlineData("https://www.youtube.com/channel/UC1234567890")]
    [InlineData("https://www.youtube.com/c/SomeName")]
    [InlineData("https://www.youtube.com/user/SomeName")]
    public void TryParsePlaylistId_RejectsChannelPages(string input) =>
        Assert.False(YouTubeApi.TryParsePlaylistId(input, out var id) && id.Length > 0);

    [Fact]
    public void TryParsePlaylistId_AcceptsAListUrlWithExtraParameters()
    {
        const string url = "https://music.youtube.com/watch?v=abc&list=PLabc123&index=4&si=xyz";

        Assert.True(YouTubeApi.TryParsePlaylistId(url, out var id));
        Assert.Equal("PLabc123", id);
    }

    private static int RowCount(TestDatabase fixture, string table, string playlistId) =>
        fixture.Database.Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE " +
                              (table == "playlists" ? "id" : "playlist_id") + " = @id;";
            cmd.Parameters.AddWithValue("@id", playlistId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
}
