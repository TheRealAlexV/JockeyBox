using JockeyBox.Sources;

namespace JockeyBox.Downloads;

/// <summary>Where the knowledge that a file came from YouTube came from.</summary>
public enum YouTubeProvenance
{
    /// <summary>The ledger recorded a YouTube playlist for this file.</summary>
    Recorded,

    /// <summary>
    /// The ledger has no playlist, but its source key is an 11-character YouTube
    /// video id. This is a derivation from the identity the ledger already
    /// stores - never from the file name, the tags or the folder.
    /// </summary>
    DerivedFromVideoId,

    /// <summary>
    /// No ledger entry, but an imported YouTube playlist still holds a row for
    /// this file, so its remote identity survives there.
    /// </summary>
    PlaylistRow,
}

/// <summary>
/// One downloaded file that came from YouTube. Carries exactly what the tab
/// needs to list it and hand it to the deck, plus the provenance so the UI can
/// say how it knows.
/// </summary>
public sealed record YouTubeLibraryFile(
    string Path,
    string Title,
    string Artist,
    string VideoId,
    DateTime AddedUtc,
    string? PlaylistName,
    YouTubeProvenance Provenance)
{
    public string OriginText => Provenance switch
    {
        YouTubeProvenance.Recorded => "recorded provenance",
        YouTubeProvenance.PlaylistRow => "from a YouTube playlist",
        _ => "derived from the video id",
    };

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
            if (!string.IsNullOrWhiteSpace(PlaylistName)) parts.Add(PlaylistName);
            parts.Add(AddedUtc == default
                ? OriginText
                : $"{AddedUtc.ToLocalTime():yyyy-MM-dd} \u00b7 {OriginText}");
            return string.Join(" \u00b7 ", parts);
        }
    }

    /// <summary>Row for the deck and the remote-row menus: local file, YouTube identity.</summary>
    public SourceTrack ToSourceTrack() => new()
    {
        Source = MediaSource.YouTube,
        Id = VideoId,
        VideoId = VideoId,
        Title = Title,
        Artist = Artist,
        PlaylistName = PlaylistName,
        LibraryPath = Path,
        State = SourceTrackState.InLibrary,
    };
}

/// <summary>
/// Result of resolving the library's YouTube downloads, with the honesty
/// numbers the tab shows: how many files resolved, how many needed a derivation,
/// and what share of the ledger is YouTube at all.
/// </summary>
public sealed record YouTubeLibraryResult(
    IReadOnlyList<YouTubeLibraryFile> Files,
    int LedgerFiles,
    int YouTubeLedgerFiles,
    int Recorded,
    int Derived,
    int FromPlaylists,
    int Missing)
{
    /// <summary>Share of ledger entries that are YouTube, 0-1.</summary>
    public double LedgerFraction => LedgerFiles == 0 ? 0 : (double)YouTubeLedgerFiles / LedgerFiles;

    public bool IsEmpty => Files.Count == 0;

    /// <summary>One line, safe to render in a small strip.</summary>
    public string Summary
    {
        get
        {
            if (IsEmpty)
                return Missing > 0
                    ? $"No YouTube file is on disk ({Missing} ledger entr{(Missing == 1 ? "y" : "ies")} point at missing files)."
                    : "No YouTube downloads yet.";

            var parts = new List<string>
            {
                $"{Files.Count} file{(Files.Count == 1 ? "" : "s")}",
                $"{Recorded} recorded",
                $"{Derived} derived from the video id",
            };
            if (FromPlaylists > 0) parts.Add($"{FromPlaylists} from playlist rows");
            if (Missing > 0) parts.Add($"{Missing} gone from disk");
            parts.Add($"ledger {YouTubeLedgerFiles}/{LedgerFiles} YouTube ({LedgerFraction:P0})");
            return string.Join(" \u00b7 ", parts);
        }
    }
}

/// <summary>
/// Answers "which local files came from YouTube?" from the store the app
/// already keeps, not from file names or ID3 tags.
///
/// <para>Order of evidence, strongest first: a ledger entry whose
/// <see cref="LedgerEntry.PlaylistSource"/> is YouTube (recorded), then a ledger
/// entry whose source key is an 11-character video id (derived from the identity
/// the ledger already holds), then an imported YouTube playlist row that still
/// names the file. A 22-character source key is a Spotify id and is never
/// treated as YouTube, which is what keeps this filter honest.</para>
///
/// <para>Pure and file-system-free by default so it can be unit-tested; the
/// app passes <see cref="File.Exists(string)"/>.</para>
/// </summary>
public static class YouTubeLibrary
{
    private static readonly char[] IdCharacters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".ToCharArray();

    /// <summary>True when <paramref name="value"/> is the shape of a YouTube video id.</summary>
    public static bool IsVideoId(string? value)
    {
        if (value is not { Length: 11 }) return false;
        foreach (var character in value)
        {
            if (Array.IndexOf(IdCharacters, character) < 0) return false;
        }

        return true;
    }

    public static YouTubeLibraryResult Resolve(
        IEnumerable<LedgerEntry> ledger,
        IEnumerable<UserPlaylist>? playlists = null,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= _ => true;

        var all = ledger.ToList();
        var resolved = new Dictionary<string, YouTubeLibraryFile>(StringComparer.OrdinalIgnoreCase);
        var missing = 0;

        foreach (var entry in all)
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;

            YouTubeProvenance provenance;
            if (entry.PlaylistSource == MediaSource.YouTube)
            {
                provenance = YouTubeProvenance.Recorded;
            }
            else if (entry.PlaylistSource == null && IsVideoId(entry.SourceKey))
            {
                provenance = YouTubeProvenance.DerivedFromVideoId;
            }
            else
            {
                continue;
            }

            if (!fileExists(entry.Path))
            {
                missing++;
                continue;
            }

            resolved[entry.Path] = new YouTubeLibraryFile(
                entry.Path,
                entry.Title,
                entry.Artist,
                IsVideoId(entry.SourceKey) ? entry.SourceKey : "",
                entry.AddedUtc,
                entry.PlaylistName,
                provenance);
        }

        // Playlist rows cover files the ledger never recorded at all.
        var fromPlaylists = 0;
        if (playlists != null)
        {
            foreach (var playlist in playlists)
            {
                if (playlist.Source != MediaSource.YouTube) continue;

                foreach (var track in playlist.Tracks)
                {
                    if (string.IsNullOrWhiteSpace(track.LibraryPath)) continue;
                    if (resolved.ContainsKey(track.LibraryPath!)) continue;
                    if (!fileExists(track.LibraryPath!)) continue;

                    resolved[track.LibraryPath!] = new YouTubeLibraryFile(
                        track.LibraryPath!,
                        track.Title,
                        track.Artist,
                        track.VideoId ?? (IsVideoId(track.Id) ? track.Id : ""),
                        playlist.LastSyncedUtc,
                        playlist.Name,
                        YouTubeProvenance.PlaylistRow);
                    fromPlaylists++;
                }
            }
        }

        var youtubeLedger = all.Count(entry =>
            entry.PlaylistSource == MediaSource.YouTube
            || (entry.PlaylistSource == null && IsVideoId(entry.SourceKey)));

        var files = resolved.Values
            .OrderByDescending(file => file.AddedUtc)
            .ThenBy(file => file.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new YouTubeLibraryResult(
            files,
            all.Count,
            youtubeLedger,
            files.Count(file => file.Provenance == YouTubeProvenance.Recorded),
            files.Count(file => file.Provenance == YouTubeProvenance.DerivedFromVideoId),
            fromPlaylists,
            missing);
    }
}
