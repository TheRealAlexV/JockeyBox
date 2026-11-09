using System.IO;
using JockeyBox.Data;

namespace JockeyBox.Tests;

/// <summary>
/// A throwaway database in its own temp directory. Unknown at compile time on
/// purpose: <c>Dispose</c> removes the -wal/-shm siblings too.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly TrackRepository _tracks;
    private readonly PlaylistRepository _playlists;

    public TestDatabase()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(Directory);
        Database = new JockeyBoxDatabase(System.IO.Path.Combine(Directory, "library.db"));
        Database.Initialize();

        // One repository instance per fixture: the scan-generation stamp lives
        // on the instance, exactly as it does in the app.
        _tracks = new TrackRepository(Database);
        _playlists = new PlaylistRepository(Database);
    }

    public string Directory { get; }

    public JockeyBoxDatabase Database { get; }

    public TrackRepository Tracks => _tracks;

    public PlaylistRepository Playlists => _playlists;

    /// <summary>A path inside the temp directory; existence is the test's business.</summary>
    public string PathFor(string relative) =>
        System.IO.Path.Combine(Directory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Database.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test run.
        }
    }
}
