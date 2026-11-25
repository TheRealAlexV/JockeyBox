using System.IO;
using System.Threading;
using System.Windows.Threading;
using JockeyBox.Activity;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The operator's complaint was "the activity log isn't showing ALL activities;
/// it didn't show several of the karaoke tracks being made". These tests pin the
/// two things that made karaoke invisible: it wrote nothing to the log, and its
/// refusals were silent. Each test captures <see cref="Log.LineWritten"/> rather
/// than the console, because that event is the single route into the LOG list.
/// </summary>
// Drives UiThread: it samples the process's one Application and dispatcher, the
// same static UiThreadCollection exists to serialise. Without this it ran beside
// the view-model suites on that single dispatcher.
[Collection(UiThreadCollection.Name)]
public sealed class ActivityLogTests
{
    [Fact]
    public void KaraokeCreation_WritesALogLine()
    {
        using var log = new LogCapture();
        using var service = NewService(engineJob: "Song.karaoke.mp3");

        var track = NewTrack("Creation Log Song");
        Assert.True(service.Enqueue(track, KaraokeStemMode.Instrumental));

        WaitUntil(() => log.Lines.Any(l => l.Contains("Karaoke created:")),
                  TimeSpan.FromSeconds(10));

        Assert.Contains(log.Lines, l => l.Contains("Karaoke created:") && l.Contains("Creation Log Song"));
    }

    [Fact]
    public void KaraokeFailure_LogsTheReason()
    {
        using var log = new LogCapture();
        using var service = NewService(engineJob: null, fail: "separator exploded");

        var track = NewTrack("Failure Log Song");
        Assert.True(service.Enqueue(track, KaraokeStemMode.Instrumental));

        WaitUntil(() => log.Lines.Any(l => l.Contains("Karaoke failed:")),
                  TimeSpan.FromSeconds(10));

        Assert.Contains(log.Lines, l => l.Contains("Karaoke failed:") && l.Contains("separator exploded"));
    }

    [Fact]
    public void FailedFinish_CarriesTheErrorOntoTheRow()
    {
        // Run on the UI thread so the feed's dispatcher marshalling is inline and
        // the assertion cannot race a message loop this test does not own.
        UiThread.Run(() =>
        {
            var feed = new ActivityFeed(10);
            var row = feed.Start(
                ActivityKind.Karaoke,
                "karaoke:error",
                "Error Song",
                source: "karaoke",
                outputPath: @"C:\Music\Error Song.karaoke.mp3");

            feed.Finish(row, DownloadState.Failed, "boom", "boom");

            Assert.Equal(DownloadState.Failed, row.State);
            Assert.True(row.IsFinished);
            Assert.Equal("boom", row.Error);
            Assert.Equal("boom", row.Detail);
        });
    }

    [Fact]
    public void EnqueueWithoutMusicPath_LogsTheRefusal()
    {
        using var log = new LogCapture();
        using var service = NewService(engineJob: null);

        var track = new TrackEntry { RelativeKey = "no-music", RelativePath = "no-music.mp3" };

        Assert.False(service.Enqueue(track, KaraokeStemMode.Instrumental));
        Assert.Contains(log.Lines, l => l.Contains("Karaoke skipped: no music path") && l.Contains("no-music"));
    }

    [Fact]
    public void EnqueueWithoutOutputPath_LogsTheRefusal()
    {
        using var log = new LogCapture();
        var settings = new AppSettings { MusicDirectory = @"C:\Music", KaraokeDirectory = "" };
        using var service = new SeparationService(settings, Dispatcher.CurrentDispatcher);

        // Empty (not null) MusicPath, and no karaoke root to mirror into: no
        // output file can be named, which is the second silent refusal.
        var track = new TrackEntry
        {
            RelativeKey = "no-output",
            RelativePath = "no-output.mp3",
            MusicPath = "",
            Title = "No Output Song",
        };

        Assert.False(service.Enqueue(track, KaraokeStemMode.Instrumental));
        Assert.Contains(log.Lines, l => l.Contains("Karaoke skipped: no output path") && l.Contains("No Output Song"));
    }

    [Fact]
    public void EnqueueingTheSameJobTwice_LogsThatItIsAlreadyQueued()
    {
        using var log = new LogCapture();
        using var service = NewService(engineJob: null);

        var track = NewTrack("Duplicate Log Song");
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RenderOverride = (_, _) => release.Task;

        Assert.True(service.Enqueue(track, KaraokeStemMode.Instrumental));
        Assert.False(service.Enqueue(track, KaraokeStemMode.Instrumental));

        Assert.Contains(log.Lines, l => l.Contains("Karaoke already queued:") && l.Contains("Duplicate Log Song"));
        release.TrySetResult(@"C:\Music\Duplicate.karaoke.mp3");
    }

    [Theory]
    [InlineData(KaraokeDenial.NoTrack)]
    [InlineData(KaraokeDenial.NoMusicPath)]
    [InlineData(KaraokeDenial.ModeAlreadyExists)]
    [InlineData(KaraokeDenial.NoKaraokeFolder)]
    [InlineData(KaraokeDenial.EngineNotInstalled)]
    public void EveryGateRefusal_LogsSomething(KaraokeDenial expected)
    {
        using var log = new LogCapture();

        // The per-mode gate reads the disk, so that one row needs a real pair.
        var temp = expected == KaraokeDenial.ModeAlreadyExists
            ? Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "jb-gate-" + Guid.NewGuid().ToString("N")))
            : null;

        try
        {
            var track = NewTrack("Gate Song");
            switch (expected)
            {
                case KaraokeDenial.NoTrack:
                    track = null;
                    break;
                case KaraokeDenial.NoMusicPath:
                    track = new TrackEntry { RelativeKey = "gate", RelativePath = "gate.mp3", Title = "Gate Song" };
                    break;
                case KaraokeDenial.ModeAlreadyExists:
                    var source = Path.Combine(temp!.FullName, "Gate Song.mp3");
                    File.WriteAllText(source, "");
                    File.WriteAllText(Path.Combine(temp.FullName, "Gate Song.karaoke.mp3"), "");
                    track!.MusicPath = source;
                    break;
            }

            var denial = KaraokeGate.Check(
                track,
                expected == KaraokeDenial.NoKaraokeFolder ? "" : @"C:\K",
                engineInstalled: expected != KaraokeDenial.EngineNotInstalled);

            Assert.Equal(expected, denial);
            Assert.Contains(log.Lines, l => l.Contains("Karaoke skipped: "));
        }
        finally
        {
            temp?.Delete(recursive: true);
        }
    }

    [Fact]
    public void Gate_RefusesOnlyTheModeWhoseFileExists()
    {
        // One render on disk is one file: the other two modes are still free.
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "jb-gate-" + Guid.NewGuid().ToString("N")));
        try
        {
            var source = Path.Combine(temp.FullName, "Song.mp3");
            File.WriteAllText(source, "");
            File.WriteAllText(Path.Combine(temp.FullName, "Song.karaoke.mp3"), "");

            var track = NewTrack("Song");
            track.MusicPath = source;

            Assert.Equal(KaraokeDenial.ModeAlreadyExists,
                KaraokeGate.Check(track, @"C:\K", engineInstalled: true, KaraokeStemMode.Instrumental));
            Assert.Equal(KaraokeDenial.Allowed,
                KaraokeGate.Check(track, @"C:\K", engineInstalled: true, KaraokeStemMode.InstrumentalKeepBacking));
            Assert.Equal(KaraokeDenial.Allowed,
                KaraokeGate.Check(track, @"C:\K", engineInstalled: true, KaraokeStemMode.VocalsOnly));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeRow_CarriesEveryFieldItWasGiven()
    {
        var started = new DateTimeOffset(2026, 11, 12, 20, 15, 0, TimeSpan.Zero);
        var row = new ActivityRow
        {
            Id = "karaoke:Artist - Song\u0001Instrumental",
            Kind = ActivityKind.Karaoke,
            Source = "karaoke",
            Title = "Artist - Song",
            Started = started,
            OutputPath = @"C:\Music\Artist - Song.karaoke.mp3",
            Error = "ffmpeg exited 1",
        };
        row.Percent = 100;
        row.Detail = "done";

        var text = ActivityFeed.DescribeRow(row);

        Assert.Contains(row.Id, text);
        Assert.Contains("Karaoke", text);
        Assert.Contains("karaoke", text);
        Assert.Contains("Artist - Song", text);
        Assert.Contains("2026-11-12 20:15:00", text);
        Assert.Contains("100%", text);
        Assert.Contains("done", text);
        Assert.Contains(@"C:\Music\Artist - Song.karaoke.mp3", text);
        Assert.Contains("ffmpeg exited 1", text);
    }

    [Fact]
    public void Start_CarriesSourceAndOutputPathOntoTheRow()
    {
        var feed = new ActivityFeed(10);
        var row = feed.Start(
            ActivityKind.Karaoke,
            "karaoke:x",
            "X",
            source: "karaoke",
            outputPath: @"C:\Music\X.karaoke.mp3");

        Assert.Equal("karaoke", row.Source);
        Assert.Equal(@"C:\Music\X.karaoke.mp3", row.OutputPath);
        Assert.Equal(ActivityKind.Karaoke, row.Kind);
    }

    // ---- helpers ------------------------------------------------------------

    private static TrackEntry NewTrack(string title) => new()
    {
        RelativeKey = "Tests/" + title,
        RelativePath = "Tests/" + title + ".mp3",
        MusicPath = @"C:\Music\Tests\" + title + ".mp3",
        Title = title,
    };

    private static SeparationService NewService(string? engineJob, string? fail = null)
    {
        var settings = new AppSettings
        {
            MusicDirectory = @"C:\Music",
            KaraokeDirectory = @"C:\Music",
        };
        var service = new SeparationService(settings, Dispatcher.CurrentDispatcher);
        if (fail is not null)
        {
            service.RenderOverride = (_, _) => throw new InvalidOperationException(fail);
        }
        else if (engineJob is not null)
        {
            service.RenderOverride = (job, _) => Task.FromResult(
                Path.Combine(Path.GetDirectoryName(job.MusicPath) ?? "", engineJob));
        }
        return service;
    }

    /// <summary>
    /// Run a real message loop until <paramref name="done"/> holds. The service
    /// posts its completion with <c>Dispatcher.InvokeAsync</c> onto the dispatcher
    /// it was constructed with (this thread's), so the test has to pump it; a
    /// sleep alone would leave that post queued forever.
    /// </summary>
    private static void WaitUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(15),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                if (done() || DateTime.UtcNow >= deadline) frame.Continue = false;
            },
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly List<string> _lines = new();
        private readonly object _gate = new();

        public LogCapture() => Log.LineWritten += OnLine;

        public IReadOnlyList<string> Lines
        {
            get { lock (_gate) return _lines.ToList(); }
        }

        private void OnLine(string line)
        {
            lock (_gate) _lines.Add(line);
        }

        public void Dispose() => Log.LineWritten -= OnLine;
    }
}
