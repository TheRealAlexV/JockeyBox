using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JockeyBox.Audio.Beat;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Transition;
using JockeyBox.Playback;
using JockeyBox.Settings;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Auto-mix is the requirement that a transition beats-matches rather than
/// crossfades. These cases exercise the decision in <see cref="TransitionScheduler"/>
/// without a device: the decks carry only the two facts the planner reads (path and
/// duration), and the grids come from a stub provider.
/// </summary>
public sealed class AutoMixTests
{
    private sealed class Grids : IBeatGridProvider
    {
        private readonly Dictionary<string, BeatGrid> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public Grids Add(string path, BeatGrid grid)
        {
            _byPath[path] = grid;
            return this;
        }

        public BeatGrid? For(string? path) =>
            path != null && _byPath.TryGetValue(path, out var grid) ? grid : null;
    }

    internal static BeatGrid Measured(double bpm, double firstBeat = 0.25, int beats = 64) => new(
        bpm, firstBeat,
        Enumerable.Range(0, beats).Select(i => firstBeat + i * 60.0 / bpm).ToArray(),
        bpm - 0.5, bpm + 0.5, 0.9);

    /// <summary>Give a deck the two planner inputs without opening a file.</summary>
    private static void Prime(DeckSource deck, string path, double durationSeconds)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(DeckSource).GetField("_durationSeconds", flags)!.SetValue(deck, durationSeconds);

        var backing = typeof(DeckSource).GetField("<TrackPath>k__BackingField", flags);
        if (backing != null) backing.SetValue(deck, path);
        else typeof(DeckSource).GetProperty("TrackPath", flags)!.SetValue(deck, path);
    }

    private static (AudioEngine Engine, TransitionScheduler Scheduler) Build(IBeatGridProvider grids)
    {
        var engine = new AudioEngine();
        var scheduler = new TransitionScheduler(engine, grids)
        {
            Mode = TransitionMode.AutoMix,
            FadeSeconds = 4.0,
            TempoRangePercent = 8.0,
        };
        return (engine, scheduler);
    }

    [Fact]
    public void BeatMatchedTransitionPicksTheRatioAndLocksTheBeat()
    {
        const string from = @"C:\Music\outgoing.mp3";
        const string to = @"C:\Music\incoming.mp3";
        var grids = new Grids().Add(from, Measured(128.0)).Add(to, Measured(132.0));

        var (engine, scheduler) = Build(grids);
        Prime(engine.DeckA, from, 300);
        Prime(engine.DeckB, to, 280);

        var plan = scheduler.Begin(engine.SlotA, engine.SlotB, atTrackEnd: false);

        Assert.NotNull(plan);
        Assert.True(plan!.BeatLocked);
        Assert.Equal(128.0 / 132.0, plan.Ratio, 4);
        Assert.NotEqual(1.0, plan.Ratio);

        // The incoming deck starts on its own grid (first beat, advanced by whole
        // bars), never at 0.
        var incoming = grids.For(to)!;
        Assert.Equal(incoming.SuggestedStart(280), plan.StartPositionSeconds, 3);
        Assert.True(plan.StartPositionSeconds >= incoming.FirstBeatSeconds);

        Assert.Contains("auto-mix", plan.Reason);
        Assert.Contains("ratio", plan.Reason);
    }

    [Fact]
    public void EqualTempiLockTheBeatAtRatioOne()
    {
        const string from = @"C:\Music\outgoing.mp3";
        const string to = @"C:\Music\incoming.mp3";
        var grids = new Grids().Add(from, Measured(124.0)).Add(to, Measured(124.0));

        var (engine, scheduler) = Build(grids);
        Prime(engine.DeckA, from, 240);
        Prime(engine.DeckB, to, 240);

        var plan = scheduler.Begin(engine.SlotA, engine.SlotB, atTrackEnd: false);

        Assert.NotNull(plan);
        Assert.True(plan!.BeatLocked);
        Assert.Equal(1.0, plan.Ratio, 4);
        Assert.True(plan.StartPositionSeconds > 0);
    }

    [Fact]
    public void MissingGridFallsBackToAPlainCrossfade()
    {
        const string from = @"C:\Music\outgoing.mp3";
        const string to = @"C:\Music\incoming.mp3";
        var grids = new Grids().Add(from, Measured(128.0)); // incoming never analysed

        var (engine, scheduler) = Build(grids);
        Prime(engine.DeckA, from, 300);
        Prime(engine.DeckB, to, 280);

        var plan = scheduler.Begin(engine.SlotA, engine.SlotB, atTrackEnd: false);

        Assert.NotNull(plan);
        Assert.False(plan!.BeatLocked);
        Assert.Equal(1.0, plan.Ratio, 4);
        Assert.Equal(0, plan.StartPositionSeconds, 4);
        Assert.Contains("no BPM data", plan.Reason);
    }

    [Fact]
    public void TempoMismatchOutsideTheRangeIsRefused()
    {
        const string from = @"C:\Music\outgoing.mp3";
        const string to = @"C:\Music\incoming.mp3";
        var grids = new Grids().Add(from, Measured(128.0)).Add(to, Measured(150.0));

        var (engine, scheduler) = Build(grids);
        Prime(engine.DeckA, from, 300);
        Prime(engine.DeckB, to, 280);

        var plan = scheduler.Begin(engine.SlotA, engine.SlotB, atTrackEnd: false);

        Assert.NotNull(plan);
        Assert.False(plan!.BeatLocked);
        Assert.Equal(1.0, plan.Ratio, 4);
        Assert.Contains("tempo mismatch", plan.Reason);
    }

    [Fact]
    public void SyntheticGridIsNotUsedForBeatMatching()
    {
        const string from = @"C:\Music\outgoing.mp3";
        const string to = @"C:\Music\incoming.mp3";
        var grids = new Grids()
            .Add(from, new BeatGrid(128, 0)) // bpm only, no measured beats
            .Add(to, Measured(128.0));

        var (engine, scheduler) = Build(grids);
        Prime(engine.DeckA, from, 300);
        Prime(engine.DeckB, to, 280);

        var plan = scheduler.Begin(engine.SlotA, engine.SlotB, atTrackEnd: false);

        Assert.NotNull(plan);
        Assert.False(plan!.BeatLocked);
        Assert.Contains("not measured", plan.Reason);
    }

    [Fact]
    public void RatioIsAppliedToTheIncomingDeckSoDriftIsZero()
    {
        // At the edge of the tempo range the fade cannot be longer than the drift
        // budget allows; that cap is what keeps a mix from smearing.
        var outgoing = Measured(128.0);
        var incoming = Measured(128.0 * 1.08);

        var match = TempoMatcher.Match(outgoing, incoming, 8.0);

        Assert.True(match.Matched);
        Assert.Equal(1.0 / 1.08, match.Ratio, 4);

        // Retuning the incoming deck to the outgoing tempo makes the drift zero, so
        // the whole drift budget is available and the fade is not shortened.
        Assert.Equal(TempoMatcher.MaxDriftBeats, match.FadeBeats, 4);
    }

    [Fact]
    public void OctaveErrorIsCollapsedBeforeMatching()
    {
        var outgoing = Measured(128.0);
        var incoming = Measured(64.0); // analyser reported half tempo

        var match = TempoMatcher.Match(outgoing, incoming, 8.0);

        Assert.True(match.Matched);
        Assert.Equal(1.0, match.Ratio, 4);
    }
}
