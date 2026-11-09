using System.IO;

namespace JockeyBox.Data;

/// <summary>
/// Identity of <c>playlists.json</c> on disk: last write time XOR file length.
///
/// <para>Stable across processes (unlike <see cref="HashCode"/>, which is seeded
/// per run), so it can be persisted in <c>schema_info</c> to answer the only
/// question that matters for external edits: "is the database still the mirror
/// of the JSON file?". Zero means the file is absent or unreadable.</para>
/// </summary>
public static class PlaylistFileStamp
{
    public const string FileName = "playlists.json";

    /// <summary>Stamp of <c>&lt;directory&gt;/playlists.json</c>.</summary>
    public static long Read(string directory) => Of(Path.Combine(directory, FileName));

    /// <summary>Stamp of one file; 0 when missing or unreadable.</summary>
    public static long Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length * 2654435761L) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
