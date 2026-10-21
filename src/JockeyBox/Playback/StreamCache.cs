using System.Globalization;
using System.IO;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Playback;

/// <summary>
/// On-disk LRU cache for streamed source audio (%APPDATA%\JockeyBox\stream-cache).
///
/// A stream can stop being playable at any moment (yt-dlp URL expiry, network
/// drop), so every streamed track is downloaded once to this folder and played
/// from there. The cache is capped by total size; when a new download pushes it
/// over the cap the oldest-by-write-time files are deleted first.
///
/// Nothing in here may throw at a streaming call site: eviction and cleanup
/// failures are logged and ignored so playback is never blocked by housekeeping.
/// </summary>
public sealed class StreamCache
{
    /// <summary>Extensions the cache recognises on disk.</summary>
    private static readonly string[] SupportedExtensions = { ".m4a", ".mp3", ".webm", ".opus" };

    private const long BytesPerMb = 1024L * 1024L;
    private const long BytesPerGb = 1024L * BytesPerMb;

    private readonly object _gate = new();

    /// <summary>%APPDATA%\JockeyBox\stream-cache - created on demand.</summary>
    public static string DefaultDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JockeyBox", "stream-cache");

    /// <summary>Create a cache capped at <paramref name="limitMb"/> megabytes.</summary>
    public StreamCache(int limitMb)
    {
        LimitBytes = Math.Max(0, limitMb) * BytesPerMb;
    }

    /// <summary>The size cap in bytes; eviction targets this bound. Settable so a
    /// new cap in Settings takes effect without a restart.</summary>
    public long LimitBytes { get; set; }

    /// <summary>Sum of the sizes of every cached audio file.</summary>
    public long TotalBytes()
    {
        lock (_gate)
        {
            var total = 0L;
            foreach (var file in EnumerateFiles())
            {
                try { total += new FileInfo(file.FullName).Length; }
                catch (Exception ex) { Log.Error($"Could not measure stream cache file '{file.FullName}'", ex); }
            }
            return total;
        }
    }

    /// <summary>Number of cached audio files.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var _ in EnumerateFiles()) count++;
                return count;
            }
        }
    }

    /// <summary>
    /// Cache path for a video id, e.g. "dQw4w9WgXcQ.m4a". The id is sanitised so
    /// it can never escape the cache directory.
    /// </summary>
    public string PathFor(string videoId, string extension = "m4a")
    {
        var id = SourceMatcher.SanitizeFileName(videoId ?? "");
        var ext = NormalizeExtension(extension);
        var path = Path.Combine(DefaultDir, id + ext);

        try { Directory.CreateDirectory(DefaultDir); }
        catch (Exception ex) { Log.Error($"Could not create stream cache directory '{DefaultDir}'", ex); }

        return path;
    }

    /// <summary>True when any supported extension is already cached for this id.</summary>
    public bool TryGet(string videoId, out string path)
    {
        foreach (var extension in SupportedExtensions)
        {
            var candidate = PathFor(videoId, extension);
            try
            {
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not probe stream cache file '{candidate}'", ex);
            }
        }

        path = "";
        return false;
    }

    /// <summary>Refresh a file's write time so it is evicted last (LRU bookkeeping).</summary>
    public void Touch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not touch stream cache file '{path}'", ex);
        }
    }

    /// <summary>
    /// Delete the oldest-by-write-time files until the cache fits
    /// <see cref="LimitBytes"/>. Returns the number of files deleted; never throws.
    /// </summary>
    public int Evict()
    {
        try
        {
            lock (_gate)
            {
                if (LimitBytes <= 0) return ClearLocked();

                var files = new List<FileInfo>();
                var total = 0L;
                foreach (var file in EnumerateFiles())
                {
                    try
                    {
                        file.Refresh();
                        total += file.Length;
                        files.Add(file);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not inspect stream cache file '{file.FullName}'", ex);
                    }
                }

                if (total <= LimitBytes || files.Count == 0) return 0;

                var deleted = 0;
                foreach (var file in files.OrderBy(f => LastWriteUtc(f)))
                {
                    if (total <= LimitBytes) break;

                    long length;
                    try { length = file.Length; }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not measure stream cache file '{file.FullName}'", ex);
                        continue;
                    }

                    try
                    {
                        File.Delete(file.FullName);
                        total -= length;
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        // A file in use is skipped; the next eviction pass retries it.
                        Log.Error($"Could not evict stream cache file '{file.FullName}'", ex);
                    }
                }

                if (deleted > 0) Log.Info($"Stream cache evicted {deleted} file(s), now {TotalBytesLocked()} bytes");
                return deleted;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stream cache eviction failed", ex);
            return 0;
        }
    }

    /// <summary>Delete every cached audio file. Never throws.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _ = ClearLocked();
        }
    }

    /// <summary>Human-readable usage line, e.g. "312 MB / 2.0 GB &#183; 18 files".</summary>
    public string StatsText()
    {
        long used;
        int count;
        lock (_gate)
        {
            used = TotalBytesLocked();
            count = 0;
            foreach (var _ in EnumerateFiles()) count++;
        }

        var separator = " \u00b7 ";
        return $"{FormatSize(used)} / {FormatSize(LimitBytes)}{separator}{count.ToString(CultureInfo.InvariantCulture)} files";
    }

    // ---- internals --------------------------------------------------------

    private int ClearLocked()
    {
        var deleted = 0;
        foreach (var file in EnumerateFiles())
        {
            try
            {
                File.Delete(file.FullName);
                deleted++;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not delete stream cache file '{file.FullName}'", ex);
            }
        }
        return deleted;
    }

    private long TotalBytesLocked()
    {
        var total = 0L;
        foreach (var file in EnumerateFiles())
        {
            try { total += file.Length; }
            catch (Exception ex) { Log.Error($"Could not measure stream cache file '{file.FullName}'", ex); }
        }
        return total;
    }

    private static DateTime LastWriteUtc(FileInfo file)
    {
        try { return file.LastWriteTimeUtc; }
        catch (Exception ex)
        {
            Log.Error($"Could not read the write time of '{file.FullName}'", ex);
            return DateTime.MinValue;
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles()
    {
        FileInfo[] files;
        try
        {
            if (!Directory.Exists(DefaultDir)) return Array.Empty<FileInfo>();
            files = new DirectoryInfo(DefaultDir).GetFiles();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not enumerate the stream cache directory '{DefaultDir}'", ex);
            return Array.Empty<FileInfo>();
        }

        return files.Where(f => SupportedExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeExtension(string? extension)
    {
        var ext = (extension ?? "").Trim();
        if (ext.Length == 0) return ".m4a";
        if (!ext.StartsWith('.')) ext = "." + ext;
        return SourceMatcher.SanitizeFileName(ext);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= BytesPerGb)
            return (bytes / (double)BytesPerGb).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= BytesPerMb)
            return (bytes / (double)BytesPerMb).ToString("0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024)
            return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
