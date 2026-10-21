using System.IO;

namespace JockeyBox.Library;

/// <summary>
/// Watches the Music and Karaoke folders for file changes and raises a single
/// debounced Changed event so the UI can rescan.
/// </summary>
public class LibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Timers.Timer _debounce;

    /// <summary>Raised (on a threadpool thread) after changes settle for 2s.</summary>
    public event EventHandler? Changed;

    public LibraryWatcher()
    {
        _debounce = new System.Timers.Timer(2000) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Watch(params string[] directories)
    {
        StopWatching();
        foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // ride out bulk copies without overflow
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnFsEvent;
            watcher.Deleted += OnFsEvent;
            watcher.Renamed += OnFsEvent;
            watcher.Changed += OnFsEvent;
            watcher.Error += OnFsError; // buffer overflow - treat as "something changed"
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // Restart the debounce window on every event; fire once when quiet.
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnFsError(object sender, ErrorEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void StopWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    public void Dispose()
    {
        StopWatching();
        _debounce.Dispose();
    }
}
