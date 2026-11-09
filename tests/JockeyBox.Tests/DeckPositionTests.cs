using System.Collections.Concurrent;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Tests;

/// <summary>
/// The deck clock's promise is ratio-invariance: the playhead counts media
/// consumed, not wall clock, so a 3-minute file ends after 3 minutes of media at
/// 0.92× or 1.08× alike. The UI only ever sees an atomically published snapshot.
/// </summary>
public class DeckPositionTests
{
    private const int Rate = 48_000;
    private const int Block = 480; // the engine's block size

    /// <summary>Pulls whole blocks, exactly as the render loop does.</summary>
    private static void AdvanceMedia(DeckPosition position, double ratio, int blocks)
    {
        for (int i = 0; i < blocks; i++) position.Advance(Block, ratio);
    }

    // ------------------------------------------------------- Ratio invariance

    [Theory]
    [InlineData(0.92)]
    [InlineData(1.0)]
    [InlineData(1.08)]
    public void Advance_ReachesTheEndAtTheFileEnd_RegardlessOfTempoRatio(double ratio)
    {
        const double duration = 60.0;
        var position = new DeckPosition(Rate);
        position.SetTrack(duration);

        // 60 s of media is 60 s * ratio * sampleRate engine frames.
        double mediaPerBlock = Block / (ratio * Rate);
        int blocks = (int)Math.Round(duration / mediaPerBlock);

        // One block short of consuming the file: still playing.
        AdvanceMedia(position, ratio, blocks - 1);
        Assert.False(position.ReachedEnd(), $"ratio {ratio} ended early");
        Assert.InRange(position.SourceSeconds, duration - mediaPerBlock - 1e-6, duration);

        // The next block is the last one of media.
        position.Advance(Block, ratio);
        Assert.True(position.ReachedEnd(), $"ratio {ratio} did not reach the end");
        Assert.InRange(position.SourceSeconds, duration - 1e-6, duration + 1e-6);
        Assert.InRange(position.OutputFrames, blocks * (long)Block - Block, blocks * (long)Block);
    }

    [Fact]
    public void ReachedEnd_IsNeverObservedBeforeTheFileEnd_AndAtMostOneBlockAfter()
    {
        // The real contract, stated without a flattering duration: ReachedEnd is
        // only ever evaluated between blocks, and its tolerance is 5 ms, so the
        // first true reading lands in [duration - 5 ms, duration + one block of
        // media]. 10.003 s is deliberately not a whole number of blocks at any of
        // the ratios below, so the quantisation is actually exercised.
        const double duration = 10.003;

        foreach (double ratio in new[] { 0.92, 1.0, 1.08 })
        {
            var position = new DeckPosition(Rate);
            position.SetTrack(duration);

            double step = Block / (ratio * Rate); // one block of media
            int guard = 0;
            while (!position.ReachedEnd() && guard++ < 100_000) position.Advance(Block, ratio);

            Assert.True(position.ReachedEnd(), $"ratio {ratio} never ended");
            Assert.InRange(position.SourceSeconds, duration - 0.005 - 1e-9, duration - 0.005 + step + 1e-9);
        }
    }

    [Fact]
    public void Advance_WithZeroOrNegativeFrames_DoesNothing()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(60);

        position.Advance(0, 1.0);
        position.Advance(-Block, 1.0);

        Assert.Equal(0.0, position.SourceSeconds, 15);
        Assert.Equal(0L, position.OutputFrames);
    }

    [Fact]
    public void Advance_WithNonPositiveRatio_FallsBackToUnityInsteadOfDividingByZero()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(60);

        position.Advance(Block, 0.0);
        Assert.False(double.IsNaN(position.SourceSeconds) || double.IsInfinity(position.SourceSeconds));
        Assert.Equal(Block / (double)Rate, position.SourceSeconds, 12);

        position.Advance(Block, -2.0);
        Assert.Equal(2 * Block / (double)Rate, position.SourceSeconds, 12);
    }

    [Fact]
    public void AnInvalidSampleRate_FallsBackToFortyEightKilohertz()
    {
        var position = new DeckPosition(0);
        Assert.Equal(48_000, position.SampleRate);

        position.Advance(48_000, 1.0);
        Assert.Equal(1.0, position.SourceSeconds, 12);
    }

    [Fact]
    public void ATempoChange_OnlyAffectsFramesReadAfterIt()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(60);

        AdvanceMedia(position, 1.0, 100); // 1 s of media
        double before = position.SourceSeconds;

        position.Advance(Block, 1.08);
        Assert.Equal(before, position.SourceSeconds - Block / (1.08 * Rate), 12);

        position.Advance(Block, 1.0);
        Assert.Equal(before + Block / (1.08 * Rate) + Block / (double)Rate, position.SourceSeconds, 12);
    }

    // -------------------------------------------------------------- Pausing

    [Fact]
    public void APausedDeck_NeverMovesItsSourcePosition()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(60);
        position.Advance(Block, 1.0);

        double frozen = position.SourceSeconds;
        long frozenFrames = position.OutputFrames;

        // A paused render block calls nothing: no Advance, no state change.
        // Read it a thousand times to prove reading is side-effect free.
        for (int i = 0; i < 1_000; i++)
        {
            Assert.Equal(frozen, position.SourceSeconds, 15);
            Assert.Equal(frozenFrames, position.OutputFrames);
            Assert.Equal(frozen, position.Snapshot.SourceSeconds, 15);
        }

        // Resuming continues from where it stopped, it does not jump.
        position.Advance(Block, 1.0);
        Assert.Equal(frozen + Block / (double)Rate, position.SourceSeconds, 12);
    }

    // ----------------------------------------------------------------- Seek

    [Fact]
    public void Seek_ResetsBothCounters()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(180);
        AdvanceMedia(position, 1.0, 100);

        position.Seek(42.0);

        Assert.Equal(42.0, position.SourceSeconds, 15);
        Assert.Equal(0L, position.OutputFrames);
        Assert.Equal(42.0, position.Snapshot.SourceSeconds, 15);
        Assert.Equal(0L, position.Snapshot.OutputFrames);

        position.Advance(Block, 1.0);
        Assert.Equal(Block, position.OutputFrames);
    }

    [Fact]
    public void Seek_ClampsAtZero()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(180);
        AdvanceMedia(position, 1.0, 10);

        position.Seek(-5.0);
        Assert.Equal(0.0, position.SourceSeconds, 15);
        Assert.Equal(0.0, position.Snapshot.SourceSeconds, 15);

        position.Seek(-1e12);
        Assert.Equal(0.0, position.SourceSeconds, 15);
    }

    [Fact]
    public void RemainingSeconds_IsNeverNegative()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(1.0);
        position.Seek(5.0);
        Assert.Equal(0.0, position.RemainingSeconds, 15);
    }

    [Fact]
    public void ReachedEnd_IsFalseBeforeATrackIsLoaded()
    {
        var position = new DeckPosition(Rate);
        position.Seek(0);
        Assert.False(position.ReachedEnd());

        position.Advance(Rate * 10, 1.0);
        Assert.False(position.ReachedEnd()); // duration 0 == no track
    }

    [Fact]
    public void SetTrack_RestartsTheClockForTheNewFile()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(60);
        AdvanceMedia(position, 1.0, 200);
        Assert.True(position.SourceSeconds > 0);

        position.SetTrack(30);

        Assert.Equal(30.0, position.DurationSeconds, 15);
        Assert.Equal(0.0, position.SourceSeconds, 15);
        Assert.Equal(0L, position.OutputFrames);
        Assert.False(position.ReachedEnd());
    }

    // ------------------------------------------------------------ Snapshot

    [Fact]
    public void Snapshot_ReadConcurrently_IsNeverATornPairAndFramesNeverRegress()
    {
        // The writer advances at ratio 1.0, so a *consistent* snapshot always
        // satisfies SourceSeconds * SampleRate == OutputFrames exactly. If the
        // seconds and the frame counter ever came from different publishes, that
        // relation would break - which is precisely the tearing this test hunts.
        var position = new DeckPosition(Rate);
        position.SetTrack(60 * 60);

        var failures = new ConcurrentQueue<string>();
        int readCount = 0;
        Exception? writerError = null;

        var writer = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 5_000; i++) position.Advance(Block, 1.0);
            }
            catch (Exception ex) { writerError = ex; }
        }) { IsBackground = true };

        var reader = new Thread(() =>
        {
            long previousFrames = -1;
            double previousSeconds = -1;
            for (int i = 0; i < 10_000; i++)
            {
                PositionSnapshot snap = position.Snapshot;
                readCount++;

                if (snap is null) { failures.Enqueue("null snapshot"); continue; }
                if (snap.SourceSeconds < 0 || snap.SourceSeconds > position.DurationSeconds + 1)
                    failures.Enqueue($"SourceSeconds out of range: {snap.SourceSeconds}");
                if (snap.OutputFrames < 0)
                    failures.Enqueue($"negative OutputFrames: {snap.OutputFrames}");
                if (snap.OutputFrames < previousFrames)
                    failures.Enqueue($"frames regressed {previousFrames} -> {snap.OutputFrames}");
                if (snap.SourceSeconds < previousSeconds)
                    failures.Enqueue($"seconds regressed {previousSeconds} -> {snap.SourceSeconds}");

                double expected = snap.OutputFrames / (double)Rate;
                if (Math.Abs(snap.SourceSeconds - expected) > 1e-9)
                    failures.Enqueue($"torn pair: {snap.SourceSeconds} s vs {snap.OutputFrames} frames");

                previousFrames = snap.OutputFrames;
                previousSeconds = snap.SourceSeconds;
            }
        });

        writer.Start();
        reader.Start();
        Assert.True(reader.Join(TimeSpan.FromSeconds(20)), "reader thread hung");
        Assert.True(writer.Join(TimeSpan.FromSeconds(20)), "writer thread hung");
        if (writerError is not null) failures.Enqueue("writer: " + writerError);

        Assert.Equal(10_000, readCount);
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)));
    }

    [Fact]
    public void Snapshot_SurvivesConcurrentSeeks_WithoutPublishingImpossibleValues()
    {
        // Seeks legitimately reset OutputFrames, so monotonicity cannot hold
        // across them; what must hold is that no published pair is out of range,
        // non-finite, or carries a negative frame count.
        //
        // Note: SourceSeconds is *not* clamped to DurationSeconds - Advance keeps
        // counting past the end of the file and ReachedEnd is only a query - so the
        // upper bound here is deliberately loose (a seek resets it to < 60 s and
        // only a bounded run of advances follows).
        var position = new DeckPosition(Rate);
        position.SetTrack(60);

        var failures = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();

        var writer = new Thread(() =>
        {
            var random = new Random(2024);
            while (!stop.IsCancellationRequested)
            {
                position.Advance(Block, 0.92 + random.NextDouble() * 0.16);
                if (random.Next(200) == 0) position.Seek(random.NextDouble() * 60.0);
            }
        }) { IsBackground = true };

        writer.Start();
        for (int i = 0; i < 10_000; i++)
        {
            PositionSnapshot snap = position.Snapshot;
            if (snap is null) { failures.Enqueue("null snapshot"); continue; }
            if (snap.SourceSeconds < 0 || double.IsNaN(snap.SourceSeconds) || double.IsInfinity(snap.SourceSeconds))
                failures.Enqueue($"invalid SourceSeconds: {snap.SourceSeconds}");
            if (snap.OutputFrames < 0)
                failures.Enqueue($"negative OutputFrames: {snap.OutputFrames}");
            if (snap.SourceSeconds > position.DurationSeconds + 30)
                failures.Enqueue($"SourceSeconds implausibly far past the file: {snap.SourceSeconds}");
        }
        stop.Cancel();
        Assert.True(writer.Join(TimeSpan.FromSeconds(10)), "writer thread hung");

        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)));
    }

    [Fact]
    public void Snapshot_IsAWholeValuePublishedByReferenceSwap()
    {
        // The record is a reference type on purpose: reading it is one reference
        // load, so a pair published together is always observed together.
        var position = new DeckPosition(Rate);
        position.SetTrack(120);
        position.Advance(Block, 1.0);

        PositionSnapshot first = position.Snapshot;
        PositionSnapshot second = position.Snapshot;

        Assert.Same(first, second);
        Assert.Equal(position.SourceSeconds, first.SourceSeconds, 15);
        Assert.Equal(position.OutputFrames, first.OutputFrames);
        // SeekBaseOutputFrames is currently always 0 - the field is published but
        // never advanced anywhere in DeckPosition, so it is *not* asserted here.
    }

    [Fact]
    public void Snapshot_HistoryIsInternallyConsistent()
    {
        var position = new DeckPosition(Rate);
        position.SetTrack(60 * 60);
        var seen = new List<PositionSnapshot>(10_000);

        for (int i = 0; i < 10_000; i++)
        {
            position.Advance(i % 480 + 1, 1.0);
            seen.Add(position.Snapshot);
        }

        long last = -1;
        foreach (PositionSnapshot snap in seen)
        {
            Assert.True(snap.OutputFrames >= last, "frames regressed with no seek in between");
            last = snap.OutputFrames;
            Assert.InRange(snap.SourceSeconds * Rate, snap.OutputFrames - 1.0, snap.OutputFrames + 1.0);
        }
    }
}
