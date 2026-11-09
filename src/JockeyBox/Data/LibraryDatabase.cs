using System.IO;

namespace JockeyBox.Data;

/// <summary>
/// Process-wide handle on the durable store. One <see cref="JockeyBoxDatabase"/>
/// serves every consumer (it owns the single writer connection and its
/// semaphore), so this is the entry point the stores and the host share.
///
/// <para>Opening is best-effort: when the profile is read-only or the native
/// SQLite cannot load, <see cref="Current"/> is null and callers keep using the
/// JSON files they already know how to read. The same null is returned when the
/// database is switched off on purpose (see <see cref="JsonOnlyVariable"/>), so
/// every consumer falls back in exactly one way.</para>
/// </summary>
public static class LibraryDatabase
{
    /// <summary>
    /// Support switch: <c>JOCKEYBOX_JSON_ONLY=1</c> in the environment, or a file
    /// named <c>library.db.off</c> in the data directory, keeps the app on the
    /// JSON stores (no database opened). Used to compare the two read paths, and
    /// to get out of a corrupt database without deleting anything.
    /// </summary>
    public const string JsonOnlyVariable = "JOCKEYBOX_JSON_ONLY";

    /// <summary>Marker file that switches the database off (see <see cref="JsonOnlyVariable"/>).</summary>
    public static string JsonOnlyMarker { get; } =
        Path.Combine(JockeyBoxDatabase.DefaultDirectory, "library.db.off");

    /// <summary>
    /// Why the database is off, or null when it is wanted. Evaluated once: the
    /// switch is a startup decision, not something a running app re-reads.
    /// </summary>
    public static string? DisabledReason { get; } = ReadDisabledReason();

    /// <summary>True when the app is deliberately running on the JSON stores.</summary>
    public static bool JsonOnly => DisabledReason is not null;

    private static readonly Lazy<JockeyBoxDatabase?> Shared = new(
        JockeyBoxDatabase.TryOpenDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared database, or null when it is unavailable or switched off.</summary>
    public static JockeyBoxDatabase? Current => JsonOnly ? null : Shared.Value;

    /// <summary>True when a database could be opened.</summary>
    public static bool IsAvailable => Current is not null;

    /// <summary>Repository over <see cref="Current"/>, or null.</summary>
    public static TrackRepository? Tracks => Current is { } db ? new TrackRepository(db) : null;

    /// <summary>Repository over <see cref="Current"/>, or null.</summary>
    public static PlaylistRepository? Playlists => Current is { } db ? new PlaylistRepository(db) : null;

    /// <summary>
    /// The library as the database last saw it, published once at startup and
    /// rebuilt after a scan/import. Serves membership and remote-row resolution
    /// before the disk scan has finished; empty when the database is off.
    /// </summary>
    public static LibrarySnapshotStore Snapshot { get; } = new();

    private static string? ReadDisabledReason()
    {
        try
        {
            if (File.Exists(JsonOnlyMarker)) return $"marker {JsonOnlyMarker}";
        }
        catch (Exception)
        {
            // An unreadable marker must not stop the database from opening.
        }

        var value = Environment.GetEnvironmentVariable(JsonOnlyVariable);
        if (value is { Length: > 0 } && value != "0" &&
            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return $"{JsonOnlyVariable}={value}";
        }

        return null;
    }
}
