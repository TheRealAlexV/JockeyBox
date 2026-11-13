using System.IO;
using JockeyBox.Library;
using JockeyBox.Tools;

namespace JockeyBox.Data;

/// <summary>
/// The one prune policy a scan applies to both stores: a file the scan did not
/// see is retired from <c>library.db</c> (authoritative, one transaction) and
/// then from <c>library.json</c> (the tag cache) in that order. If the database
/// delete fails the cache is left alone too, so the two never disagree about
/// what the library is for longer than one pass.
///
/// <para>Kept out of the view model so the policy - including the unreachable
/// folder guard - is exercised directly by the tests with a sandbox home and a
/// fixture database.</para>
/// </summary>
public static class LibraryPrune
{
    /// <summary>
    /// True when every configured library folder is on disk right now. A prune
    /// is only safe when it is: an unreachable folder (unplugged drive, offline
    /// share) scans as empty, which is indistinguishable from a library where
    /// every file was deleted.
    /// </summary>
    public static bool RootsReachable(params string?[] roots)
    {
        var any = false;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!Directory.Exists(root)) return false;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Run the policy for one scan result. <paramref name="tracks"/> is null when
    /// the database is switched off, in which case only the cache is pruned.
    /// Returns how many rows left <c>tracks</c>.
    /// </summary>
    public static int Apply(IEnumerable<string?> presentPaths, TagCache cache, TrackRepository? tracks)
    {
        var present = presentPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int pruned;
        try
        {
            pruned = tracks?.PruneExcept(present) ?? 0;
        }
        catch (Exception ex)
        {
            // The database is the store that owns referential integrity: if it
            // did not prune, the cache must not either.
            Log.Warn($"scan: track prune failed ({ex.Message}); keeping library.json entries this pass");
            return 0;
        }

        if (pruned > 0) Log.Info($"scan: pruned {pruned} vanished track(s) from library.db");

        cache.Prune(present);
        cache.Save();
        return pruned;
    }
}
