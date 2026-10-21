using System.IO;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Playback;

/// <summary>
/// Turns a remote track into something the deck engine can open.
///
/// Order of preference:
///   1. a file already attached to the track (the browser knows it),
///   2. the on-disk library index (a file the user already owns, even when
///      JockeyBox never downloaded it),
///   3. the download ledger (same track, downloaded earlier),
///   4. the stream cache (played earlier, still warm),
///   5. a fresh best-audio fetch into the stream cache.
///
/// The last step is what makes "click a Spotify track and hear it" work without
/// a permanent download: the audio lands in a bounded LRU cache and is handed to
/// Deck B like any local file, so device routing, delay and lyrics all behave.
/// </summary>
public sealed class StreamResolver
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(150);

    private readonly DownloadQueue _queue;
    private readonly StreamCache _cache;
    private readonly LibraryLedger _ledger;
    private readonly AppSettings _settings;
    private readonly LibraryIndex _library;

    public StreamResolver(DownloadQueue queue, StreamCache cache, LibraryLedger ledger,
                          AppSettings settings, LibraryIndex? library = null)
    {
        _queue = queue;
        _cache = cache;
        _ledger = ledger;
        _settings = settings;
        _library = library ?? LibraryIndex.Empty;
    }

    /// <summary>True when the track can be played without touching the network.</summary>
    public bool IsAvailableOffline(SourceTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.LibraryPath) && File.Exists(track.LibraryPath))
            return true;
        if (_library.Contains(track))
            return true;
        if (_ledger.FindPathByLibraryKey(track.Artist, track.Title) != null)
            return true;
        return _cache.TryGet(CacheKey(track), out _);
    }

    /// <summary>
    /// Resolve a playable local path, downloading into the cache when needed.
    /// Returns null when the fetch failed or was cancelled.
    /// </summary>
    public async Task<string?> ResolveAsync(SourceTrack track, CancellationToken ct)
    {
        // 1. Known file.
        if (!string.IsNullOrWhiteSpace(track.LibraryPath) && File.Exists(track.LibraryPath))
            return track.LibraryPath;

        // 2. Already in the library folder, whatever put it there.
        var inLibrary = _library.Resolve(track);
        if (inLibrary != null)
        {
            track.State = SourceTrackState.InLibrary;
            track.LibraryPath = inLibrary;
            Log.Debug($"library hit for {track.DisplayName}");
            return inLibrary;
        }

        // 3. Same track, already downloaded into the library.
        var ledgered = _ledger.FindPathByLibraryKey(track.Artist, track.Title);
        if (ledgered != null)
        {
            track.State = SourceTrackState.InLibrary;
            track.LibraryPath = ledgered;
            return ledgered;
        }

        // 4. Warm cache.
        var key = CacheKey(track);
        if (_cache.TryGet(key, out var cached))
        {
            _cache.Touch(cached);
            Log.Debug($"stream cache hit for {track.DisplayName}");
            return cached;
        }

        // 5. Fetch into the cache.
        Log.Info($"streaming {track.DisplayName} (cache miss)");
        var item = _queue.Enqueue(track, DownloadKind.Stream);
        var completion = item.Completion!;

        using var timeout = new CancellationTokenSource(FetchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            var finished = await Task.WhenAny(
                completion.Task,
                Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

            if (finished != completion.Task)
            {
                ct.ThrowIfCancellationRequested();
                Log.Warn($"streaming timed out for {track.DisplayName}");
                return null;
            }

            var path = await completion.Task.ConfigureAwait(false);
            if (path == null) Log.Warn($"streaming failed for {track.DisplayName}: {item.Error}");
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Cache files are keyed on the YouTube id when known, else the source id.</summary>
    private static string CacheKey(SourceTrack track) =>
        !string.IsNullOrWhiteSpace(track.VideoId) ? track.VideoId! : $"{track.Source}_{track.Id}";
}
