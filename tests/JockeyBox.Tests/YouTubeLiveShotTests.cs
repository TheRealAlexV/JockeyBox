using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JockeyBox.Downloads;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using JockeyBox.YouTube;

namespace JockeyBox.Tests;

/// <summary>
/// Live proof for the YouTube tab, run by hand on the Windows host:
///
/// <code>
/// set JOCKEYBOX_HOME=C:\Dev\jb-sandbox\data
/// set JOCKEYBOX_YT_LIVE_SHOT=1
/// set JOCKEYBOX_YT_SHOT_DIR=C:\Dev\jb-yt\shots
/// dotnet test ... --filter FullyQualifiedName~YouTubeLiveShotTests
/// </code>
///
/// <para><b>Why a gated test.</b> The parser is proved offline in
/// <see cref="YouTubeImportTests"/>; this one proves the whole path - real yt-dlp,
/// real network, the real queue - and renders the real view headlessly through
/// <see cref="RenderTargetBitmap"/>, so no window is ever shown to the operator.
/// It is skipped unless <c>JOCKEYBOX_YT_LIVE_SHOT=1</c>, so the normal suite
/// never depends on the network.</para>
/// </summary>
public sealed class YouTubeLiveShotTests
{
    private const string PlaylistUrl =
        "https://www.youtube.com/playlist?list=PL6NdkXsPL07Il2hEQGcLI4dg_LTg7xA2L";

    private const string VideoUrl = "https://www.youtube.com/watch?v=5NV6Rdv1a3I";

    /// <summary>A normal 5-minute video, found with a real yt-dlp search; not in the ledger.</summary>
    private const string ProgressVideoId = "A2VpR8HahKc";

    private const string ProgressVideoUrl = "https://www.youtube.com/watch?v=" + ProgressVideoId;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("JOCKEYBOX_YT_LIVE_SHOT") == "1";

    [Fact]
    public void Live_PlaylistListing_SingleVideo_And_DownloadedView()
    {
        if (!Enabled) return; // opt-in only: the default suite stays offline

        var shotDir = Environment.GetEnvironmentVariable("JOCKEYBOX_YT_SHOT_DIR")
                      ?? Path.Combine(Path.GetTempPath(), "jb-yt-shots");
        Directory.CreateDirectory(shotDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run(shotDir);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(6)), "live YouTube run timed out");

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Run(string shotDir)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Await continuations must come back to this STA thread, or the view's
        // command bindings are touched from a pool thread and WPF throws.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var app = new App();
        app.InitializeComponent(); // Themes/Dark.xaml: the view's StaticResources

        var settings = AppSettings.Load();
        var ledger = new LibraryLedger();
        var store = new PlaylistStore(null); // JSON only: nothing is written to library.db
        var queue = new DownloadQueue(settings, ledger, new StreamCache(2048));

        var played = new System.Collections.Generic.List<SourceTrack>();
        var vm = new YouTubeViewModel(settings, store, queue, ledger, track =>
        {
            played.Add(track);
            return System.Threading.Tasks.Task.CompletedTask;
        });

        var view = new YouTubeView { DataContext = vm };
        var window = new Window
        {
            Content = view,
            Width = 1400,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -6000,
            Top = -6000,
        };
        window.Show();
        Pump(400);

        // ---- 1. playlist listing -----------------------------------------
        vm.ImportUrl = PlaylistUrl;
        var load = vm.LoadImporterCommand.ExecuteAsync(null);
        PumpUntil(() => load.IsCompleted, 120_000);
        Assert.True(load.IsCompletedSuccessfully, $"playlist load failed: {load.Exception}");
        Assert.Null(load.Exception);

        Assert.Equal(23, vm.ImporterRows.Count);
        Assert.Equal("PLAYLIST", vm.ImportHeader);
        Assert.Contains("Lofi Girl", vm.ImportTitle);
        Assert.Equal("Download selected", vm.DownloadSelectedText);
        Assert.True(vm.ShowDownloadAll);
        Assert.Equal("not downloaded", vm.ImporterRows[0].StateText);
        Assert.Equal("lofi hip hop radio", vm.ImporterRows[0].Title[..18]);
        Capture(shotDir, "yt-playlist-listing.png");

        // ---- 2. queue one row, watch real progress, cancel ----------------
        // The Lofi playlist is 24/7 live radio: yt-dlp reports "Downloading" but
        // never a byte percentage. Progress is proved below with a normal video,
        // so here only the queue wiring is asserted.
        var first = vm.ImporterRows[0];
        first.IsSelected = true;
        var download = vm.DownloadSelectedCommand.ExecuteAsync(null);
        PumpUntil(() => download.IsCompleted, 30_000);
        Assert.Single(queue.Items);
        var item = queue.Items[0];
        Assert.Same(first.Track, item.Track);

        // Let the worker pick it up and report progress; the row reads it live.
        PumpUntil(() => item.Percent >= 1 || item.IsFinished, 45_000);
        Console.WriteLine($"queue(live stream): state={item.State} percent={item.Percent:0.0} " +
                          $"row={first.StateText} output={item.OutputPath}");
        Assert.Contains(first.State, new[]
        {
            YouTubeImportRowState.Queued, YouTubeImportRowState.Downloading,
            YouTubeImportRowState.Downloaded, YouTubeImportRowState.Failed,
        });
        Capture(shotDir, "yt-queue-progress.png");

        queue.CancelAll();
        Pump(600);
        Assert.Contains(item.State, new[] { DownloadState.Cancelled, DownloadState.Failed, DownloadState.Done });

        // ---- 2b. a normal video: real percent, then a full download ------
        // Hygiene so every run exercises the queue: drop what an earlier run of
        // this test downloaded. Sandbox home only.
        foreach (var stale in ledger.Entries.Where(e => e.SourceKey == ProgressVideoId).ToList())
        {
            try
            {
                if (File.Exists(stale.Path)) File.Delete(stale.Path);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"could not delete {stale.Path}: {ex.Message}");
            }

            ledger.Remove(stale.SourceKey);
        }
        ledger.Save();

        vm.ImportUrl = ProgressVideoUrl;
        var normal = vm.LoadImporterCommand.ExecuteAsync(null);
        PumpUntil(() => normal.IsCompleted, 120_000);
        Assert.Null(normal.Exception);

        var streaming = Assert.Single(vm.ImporterRows);
        Assert.Equal("not downloaded", streaming.StateText);
        Assert.Equal("Download video", vm.DownloadSelectedText);
        streaming.IsSelected = true;
        var normalDownload = vm.DownloadSelectedCommand.ExecuteAsync(null);
        PumpUntil(() => normalDownload.IsCompleted, 30_000);

        var progressItem = queue.Items.Single(i => i.Track.Id == streaming.VideoId);
        PumpUntil(() => progressItem.Percent >= 2 || progressItem.IsFinished, 90_000);
        Console.WriteLine($"queue(normal video): state={progressItem.State} " +
                          $"percent={progressItem.Percent:0.0} row={streaming.StateText}");
        Assert.True(progressItem.Percent > 0 || progressItem.IsFinished,
            $"no progress observed: state={progressItem.State} percent={progressItem.Percent}");
        Capture(shotDir, "yt-queue-progress-video.png");

        // A 5-minute audio file finishes on this line, which also proves the
        // ledger records provenance for a fresh download.
        PumpUntil(() => progressItem.IsFinished, 240_000);
        Console.WriteLine($"queue(normal video) finished: state={progressItem.State} " +
                          $"percent={progressItem.Percent:0.0} output={progressItem.OutputPath}");

        if (progressItem.State == DownloadState.Done)
        {
            Assert.Equal(YouTubeImportRowState.Downloaded, streaming.State);
            Assert.True(File.Exists(progressItem.OutputPath), progressItem.OutputPath);
            var recorded = ledger.TryGet(streaming.VideoId);
            Assert.NotNull(recorded);
            Assert.Equal(progressItem.OutputPath, recorded!.Path);
            Assert.Equal(MediaSource.YouTube, recorded.PlaylistSource);
        }
        else
        {
            queue.CancelAll();
            Pump(800);
            Assert.Equal(DownloadState.Cancelled, progressItem.State);
            // The PriorState rule: a cancelled row goes back to where it was.
            Assert.Equal(YouTubeImportRowState.NotDownloaded, streaming.State);
            Assert.Equal(SourceTrackState.New, streaming.Track.State);
        }

        // The cancelled live-stream row also went back to its prior state.
        Assert.Equal(SourceTrackState.New, first.Track.State);
        queue.Dispose();

        // ---- 3. single video: one row, already on disk from the ledger ----
        vm.ImportUrl = VideoUrl;
        var single = vm.LoadImporterCommand.ExecuteAsync(null);
        PumpUntil(() => single.IsCompleted, 120_000);
        Assert.Null(single.Exception);

        var row = Assert.Single(vm.ImporterRows);
        Assert.Equal("VIDEO", vm.ImportHeader);
        Assert.Contains("Get Lucky", row.Title);           // real title from yt-dlp
        Assert.Equal("Daft Punk", row.Artist);
        Assert.Equal("Download video", vm.DownloadSelectedText);
        Assert.False(vm.ShowDownloadAll);
        Assert.Equal("downloaded", row.StateText);          // ledger says it is on disk
        Capture(shotDir, "yt-single-video.png");

        // ---- 4. downloaded view -------------------------------------------
        vm.ImporterRows.Clear(); // close the listing so the downloaded list owns the pane
        vm.CloseImporterCommand.Execute(null);
        vm.ShowDownloadedCommand.Execute(null);
        Pump(400);

        Assert.True(vm.IsDownloadedView);
        Assert.True(vm.DownloadedFiles.Count >= 3, $"{vm.DownloadedFiles.Count} files");
        Assert.Contains("YouTube", vm.DownloadedSummary);
        Assert.All(vm.DownloadedFiles, f => Assert.True(File.Exists(f.Path), f.Path));
        Assert.Contains(vm.DownloadedFiles, f => f.Path.Contains("Get Lucky", StringComparison.OrdinalIgnoreCase));
        Capture(shotDir, "yt-downloaded.png");

        // Play honours the library path: the callback receives a YouTube track.
        var target = vm.DownloadedFiles[0];
        vm.SelectedDownloadedFile = target;
        var play = vm.PlayDownloadedCommand.ExecuteAsync(null);
        PumpUntil(() => play.IsCompleted, 15_000);
        Assert.Single(played);
        Assert.Equal(MediaSource.YouTube, played[0].Source);
        Assert.Equal(target.Path, played[0].LibraryPath);

        // ---- 5. bad link: one inline line, no listing, no dialog ----------
        vm.ShowSearchCommand.Execute(null);
        vm.ImportUrl = "https://www.youtube.com/@Kavinsky";
        var bad = vm.LoadImporterCommand.ExecuteAsync(null);
        PumpUntil(() => bad.IsCompleted, 10_000);
        Assert.False(vm.HasImportListing);
        Assert.Equal(YouTubeImportParser.ChannelMessage, vm.ImportError);
        Assert.False(vm.CanRetryImport);
        Capture(shotDir, "yt-bad-link.png");

        window.Close();
        app.Shutdown();
    }

    // ---- headless render (same mechanics as Selftest/UiShot) --------------

    private static void Capture(string directory, string name)
    {
        const int width = 1400;
        const int height = 900;
        Pump(250);

        if (Application.Current?.Windows.Count > 0
            && Application.Current.Windows[0].Content is FrameworkElement content)
        {
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            Pump(150);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Save(bitmap, Path.Combine(directory, name));
        }
    }

    private static void Save(RenderTargetBitmap bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"uishot: {path}");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Pump the dispatcher until <paramref name="done"/>, so await continuations run.</summary>
    private static void PumpUntil(Func<bool> done, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs) Pump(50);
    }
}
