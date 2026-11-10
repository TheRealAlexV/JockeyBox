using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JockeyBox.Audio.Beat;

namespace JockeyBox.Tests;

/// <summary>
/// The library pass on a real collection is hours of analyser time, so the decision
/// about when it may take another track is the difference between a DJ application
/// and a space heater. These cases pin that decision: playback blocks the pass, the
/// track a deck is about to play jumps it, and a cached track is never handed over.
///
/// <para>The policy takes its facts as arguments — is audio playing, is this the
/// imminent track, what is next in the library — so the decision is exercised here
/// with no audio device and no analyser process.</para>
/// </summary>
public sealed class BpmQueuePolicyTests
{
    private sealed class FakeClock
    {
        public long Ms;
        public long Now() => Ms;
    }

    [Fact]
    public void PlayingDeckTakesNoLibraryTrack()
    {
        var policy = new BpmQueuePolicy();

        Assert.Null(policy.Take(audioPlaying: true, imminent: null, library: "older.flac"));
    }

    [Fact]
    public void StoppedDeckTakesTheNextQueuedTrackAfterTheResumeDelay()
    {
        var clock = new FakeClock();
        var policy = new BpmQueuePolicy(clock.Now);

        // Playback runs, then stops.
        Assert.Null(policy.Take(true, null, "a.flac"));
        Assert.Null(policy.Take(false, null, "a.flac"));   // just stopped: still quiet

        clock.Ms += (long)policy.ResumeDelay.TotalMilliseconds;

        Assert.Equal("b.flac", policy.Take(false, null, "b.flac"));
    }

    [Fact]
    public void ImminentTrackJumpsTheQueueWhilePlaybackIsActive()
    {
        var policy = new BpmQueuePolicy();

        Assert.Equal("next-up.flac",
            policy.Take(audioPlaying: true, imminent: "next-up.flac", library: "queued-hours-ago.flac"));
    }

    [Fact]
    public void CachedTrackIsNeverHandedToTheAnalyser()
    {
        using var track = new TempTrack();
        var store = new FakeStore(has: _ => true);

        using var service = new BpmAnalysisService(store, audioPlaying: () => false);

        service.Enqueue(track.Path);
        service.EnqueueAll(new[] { track.Path });

        Assert.Equal(0, service.QueuedCount);
    }

    [Fact]
    public void UnknownTrackIsQueued()
    {
        using var track = new TempTrack();
        var store = new FakeStore(has: _ => false);

        using var service = new BpmAnalysisService(store, audioPlaying: () => true);

        service.EnqueueAll(new[] { track.Path });

        Assert.Equal(1, service.QueuedCount);
    }

    [Fact]
    public void LibraryPassStaysQueuedWhilePlaybackIsActive()
    {
        using var track = new TempTrack();
        var store = new FakeStore(has: _ => false);

        // Playing: the worker polls but must take nothing.
        using var service = new BpmAnalysisService(store, audioPlaying: () => true);
        service.EnqueueAll(new[] { track.Path });

        Thread.Sleep(1500);

        Assert.Equal(1, service.QueuedCount);   // still waiting, not consumed
    }

    /// <summary>
    /// The case above cannot tell "the worker slept" from "the worker spun": a tight
    /// spin also leaves the count at 1, because TakeNext returns before dequeuing.
    /// The gate is asked once per policy consultation, so counting the predicate counts
    /// the consultations — no hook in the service, and a spin turns this into thousands.
    /// </summary>
    [Fact]
    public void HeldBackPassPollsTheGateInsteadOfSpinning()
    {
        using var track = new TempTrack();
        var store = new FakeStore(has: _ => false);
        int consultations = 0;

        using var service = new BpmAnalysisService(store, audioPlaying: () =>
        {
            Interlocked.Increment(ref consultations);
            return true;
        });
        service.EnqueueAll(new[] { track.Path });

        Thread.Sleep(1500);

        int seen = Volatile.Read(ref consultations);

        Assert.Equal(1, service.QueuedCount);        // nothing taken
        Assert.InRange(seen, 2, 10);                 // ~2 per second from the 500 ms wait
    }

    /// <summary>
    /// A held-back pass has to come back on its own. Nothing here nudges it after the
    /// predicate flips: no new enqueue, no signal, no user action — a pass that needed
    /// one of those would stall a library for as long as the DJ leaves it alone.
    /// </summary>
    [Fact]
    public void HeldBackPassResumesAfterPlaybackStopsWithoutAnotherEnqueue()
    {
        using var track = new TempTrack();
        var store = new FakeStore(has: _ => false);
        var policy = new BpmQueuePolicy();           // for ResumeDelay only
        int playing = 1;

        using var service = new BpmAnalysisService(store,
            audioPlaying: () => Volatile.Read(ref playing) == 1);
        service.EnqueueAll(new[] { track.Path });

        Thread.Sleep(300);
        Assert.Equal(1, service.QueuedCount);        // held back while playing

        Volatile.Write(ref playing, 0);              // playback stops; nothing else happens

        long deadline = Environment.TickCount64 +
                        (long)(policy.ResumeDelay + TimeSpan.FromSeconds(1.5)).TotalMilliseconds;
        while (service.QueuedCount != 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(50);

        Assert.Equal(0, service.QueuedCount);        // taken on its own, within ResumeDelay
    }

    // ------------------------------------------------------------------ fixtures

    private sealed class FakeStore : IBeatGridStore
    {
        private readonly Func<string, bool> _has;

        public FakeStore(Func<string, bool>? has = null) => _has = has ?? (_ => false);

        public BeatGrid? Get(string path) => null;
        public void Save(string path, BeatGrid grid) { }
        public bool Has(string path) => _has(path);
        public bool NeedsKeyPass(string path) => false;
        public void MarkTagKey(string path) { }
        public int Count => 0;
    }

    /// <summary>A real (empty) file, because the queue refuses paths that are not there.</summary>
    private sealed class TempTrack : IDisposable
    {
        public TempTrack()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n") + ".wav");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllBytes(Path, Array.Empty<byte>());
        }

        public string Path { get; }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
