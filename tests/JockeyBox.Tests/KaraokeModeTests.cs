using JockeyBox.Library;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Karaoke mode's decision logic, asserted as the pure function the ViewModel
/// calls on every load: given what a deck is about to play, whether its stem is
/// on disk, whether the mode is on and what the separation queue already says,
/// what happens? The rules under test are the whole feature contract - when to
/// queue a separation, when to wait, when to fall back to the mix, and that a
/// build with no separator (or a track with no original) never queues anything.
/// </summary>
public sealed class KaraokeModeTests
{
    private const string Music = @"C:\Music\Artist\Album\Song.mp3";
    private const string Stem = @"C:\Music\Artist\Album\Song.karaoke.flac";

    [Fact]
    public void Mode_off_loads_the_pair_and_queues_nothing()
    {
        var plan = KaraokeStemPlanner.Decide(Music, null, karaokeModeEnabled: false,
            SeparationState.None, canSeparate: true);

        Assert.Equal(KaraokeStemAction.PlayOriginal, plan.Action);
        Assert.False(plan.Enqueues);
        Assert.False(plan.WantsKaraoke);
        Assert.Equal("", plan.Status);
    }

    [Fact]
    public void Mode_off_keeps_an_existing_stem_on_the_normal_path()
    {
        var plan = KaraokeStemPlanner.Decide(Music, Stem, karaokeModeEnabled: false,
            SeparationState.None);

        Assert.Equal(KaraokeStemAction.PlayOriginal, plan.Action);
        Assert.False(plan.Enqueues);
    }

    [Fact]
    public void Stem_on_disk_plays_immediately()
    {
        var plan = KaraokeStemPlanner.Decide(Music, Stem, karaokeModeEnabled: true,
            SeparationState.None);

        Assert.Equal(KaraokeStemAction.PlayKaraoke, plan.Action);
        Assert.True(plan.WantsKaraoke);
        Assert.False(plan.Enqueues);
    }

    [Fact]
    public void Missing_stem_with_a_separator_queues_one_separation()
    {
        var plan = KaraokeStemPlanner.Decide(Music, null, karaokeModeEnabled: true,
            SeparationState.None, canSeparate: true);

        Assert.Equal(KaraokeStemAction.QueueSeparation, plan.Action);
        Assert.True(plan.Enqueues);
        Assert.False(plan.WantsKaraoke);

        // One short line, naming the track, that says the mix plays meanwhile.
        Assert.Contains("Song", plan.Status);
        Assert.Contains("original mix", plan.Status);
        Assert.DoesNotContain("\n", plan.Status);
    }

    [Theory]
    [InlineData(SeparationState.Queued)]
    [InlineData(SeparationState.Running)]
    public void A_stem_already_being_made_is_waited_for_not_queued_twice(SeparationState state)
    {
        var plan = KaraokeStemPlanner.Decide(Music, null, karaokeModeEnabled: true,
            state, canSeparate: true);

        Assert.Equal(KaraokeStemAction.WaitForSeparation, plan.Action);
        Assert.False(plan.Enqueues);
        Assert.Contains("still being made", plan.Status);
    }

    [Fact]
    public void A_failed_separation_falls_back_to_the_mix_and_is_not_retried()
    {
        var plan = KaraokeStemPlanner.Decide(Music, null, karaokeModeEnabled: true,
            SeparationState.Failed, canSeparate: true);

        Assert.Equal(KaraokeStemAction.PlayOriginalNoStem, plan.Action);
        Assert.False(plan.Enqueues);
        Assert.False(plan.WantsKaraoke);
        Assert.Contains("original mix", plan.Status);
    }

    [Fact]
    public void A_missing_separator_falls_back_to_the_mix()
    {
        var plan = KaraokeStemPlanner.Decide(Music, null, karaokeModeEnabled: true,
            SeparationState.None, canSeparate: false);

        Assert.Equal(KaraokeStemAction.PlayOriginalNoStem, plan.Action);
        Assert.False(plan.Enqueues);
        Assert.Contains("original mix", plan.Status);
    }

    [Fact]
    public void A_karaoke_only_row_has_no_original_to_separate()
    {
        // A row whose only file is "Song.karaoke.flac": no primary stem, so
        // there is nothing to run a separator over and nothing to wait for.
        var plan = KaraokeStemPlanner.Decide(null, null, karaokeModeEnabled: true,
            SeparationState.None, canSeparate: true);

        Assert.Equal(KaraokeStemAction.PlayOriginal, plan.Action);
        Assert.False(plan.Enqueues);
    }

    [Fact]
    public void A_fresh_queue_entry_beats_a_stale_failed_row_state()
    {
        // The stem landed on disk while the row still said Failed: the file wins,
        // so a retry that succeeded is not hidden by the old state.
        var plan = KaraokeStemPlanner.Decide(Music, Stem, karaokeModeEnabled: true,
            SeparationState.Failed);

        Assert.Equal(KaraokeStemAction.PlayKaraoke, plan.Action);
    }

    [Fact]
    public void Mode_off_never_queues_even_with_everything_missing()
    {
        bool[] separators = { true, false };
        foreach (var canSeparate in separators)
        {
            var plan = KaraokeStemPlanner.Decide(Music, null, karaokeModeEnabled: false,
                SeparationState.Queued, canSeparate);

            Assert.Equal(KaraokeStemAction.PlayOriginal, plan.Action);
            Assert.False(plan.Enqueues);
        }
    }
}
