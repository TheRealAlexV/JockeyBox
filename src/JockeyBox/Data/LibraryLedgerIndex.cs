using System;
using System.Collections.Generic;
using System.IO;
using JockeyBox.Downloads;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Data;

/// <summary>
/// One artist+title → file map over the download <see cref="LibraryLedger"/>.
///
/// <para><see cref="LibraryLedger.FindPathByLibraryKey"/> walks every entry (and
/// normalises two strings per entry, under a lock) because the ledger is keyed by
/// remote id, not by title. A playlist switch re-derives the state of every row,
/// so a 5 000-row list paid that walk 5 000 times - that is what made switching
/// playlists feel slow. The index is built once per pass (a few hundred entries,
/// ~1 ms) and each row then costs one dictionary hit.</para>
///
/// <para>Same answer as the linear walk: the first entry with a matching library
/// key wins, and a path that is no longer on disk does not count.</para>
/// </summary>
public sealed class LibraryLedgerIndex
{
    private readonly Dictionary<string, string> _byKey;

    private LibraryLedgerIndex(Dictionary<string, string> byKey) => _byKey = byKey;

    /// <summary>Snapshot the ledger's current entries. Never throws.</summary>
    public static LibraryLedgerIndex Build(LibraryLedger ledger)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in ledger.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;

                var key = SourceMatcher.LibraryKey(entry.Artist, entry.Title);
                if (key.Length == 0) continue;

                // First wins, exactly like the linear scan it replaces.
                byKey.TryAdd(key, entry.Path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"library index build failed ({ex.Message}); rows use the ledger walk");
        }

        return new LibraryLedgerIndex(byKey);
    }

    /// <summary>Entries the index can answer for.</summary>
    public int Count => _byKey.Count;

    /// <summary>
    /// The ledger's path for artist+title, but only while the file is still on
    /// disk. Null when the pair is unknown or the file is gone.
    /// </summary>
    public string? FindPath(string? artist, string? title)
    {
        if (!_byKey.TryGetValue(SourceMatcher.LibraryKey(artist, title), out var path)) return null;

        try
        {
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not probe ledger path '{path}': {ex.Message}");
            return null;
        }
    }
}
