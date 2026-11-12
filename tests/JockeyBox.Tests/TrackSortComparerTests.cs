using System;
using System.Collections.Generic;
using System.Linq;
using JockeyBox.Sources;
using JockeyBox.ViewModels;

namespace JockeyBox.Tests;

/// <summary>
/// The shared row order, exercised through the Spotify path where "Date added"
/// lives. The null policy is the interesting part: a row with no date groups last
/// in both directions and ties on it keep arrival order, which is what a bulk-added
/// playlist depends on.
/// </summary>
public sealed class TrackSortComparerTests
{
    private static readonly DateTimeOffset Jan1 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan2 = new(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static SourceTrack Row(string title, DateTimeOffset? addedAt) =>
        new() { Id = title, Title = title, Artist = "artist", AddedAt = addedAt };

    /// <summary>
    /// Production builds the rank from the current list, keyed by row reference
    /// (SpotifyViewModel.ApplySortOrder); this mirrors that map exactly.
    /// </summary>
    private static Dictionary<object, int> Rank(params SourceTrack[] arrival)
    {
        var rank = new Dictionary<object, int>(arrival.Length, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < arrival.Length; i++) rank[arrival[i]] = i;
        return rank;
    }

    private static string[] Order(
        TrackSortField field, bool descending, IReadOnlyDictionary<object, int>? rank,
        params SourceTrack[] rows)
    {
        var copy = rows.ToArray();
        Array.Sort(copy, new TrackSortComparer(field, descending, rank));
        return copy.Select(r => r.Title).ToArray();
    }

    [Fact]
    public void DateAddedAscendingPutsNullsLastOldestFirst()
    {
        var old = Row("old", Jan1);
        var noneA = Row("none-a", null);
        var mid = Row("mid", Jan2);
        var noneB = Row("none-b", null);

        var order = Order(TrackSortField.DateAdded, descending: false,
            Rank(old, noneA, mid, noneB), old, noneA, mid, noneB);

        Assert.Equal(new[] { "old", "mid", "none-a", "none-b" }, order);
    }

    [Fact]
    public void DateAddedDescendingPutsNullsLastNewestFirst()
    {
        var old = Row("old", Jan1);
        var noneA = Row("none-a", null);
        var mid = Row("mid", Jan2);
        var noneB = Row("none-b", null);

        var order = Order(TrackSortField.DateAdded, descending: true,
            Rank(old, noneA, mid, noneB), old, noneA, mid, noneB);

        Assert.Equal(new[] { "mid", "old", "none-a", "none-b" }, order);
    }

    [Fact]
    public void SameDateKeepsArrivalOrderBothDirections()
    {
        var first = Row("first", Jan1);
        var second = Row("second", Jan1);
        var third = Row("third", Jan1);
        var rank = Rank(first, second, third);

        // Sort a reversed copy: only the rank tie-break can restore arrival order.
        var ascending = Order(TrackSortField.DateAdded, descending: false,
            rank, third, second, first);
        var descending = Order(TrackSortField.DateAdded, descending: true,
            rank, third, second, first);

        Assert.Equal(new[] { "first", "second", "third" }, ascending);
        Assert.Equal(new[] { "first", "second", "third" }, descending);
    }

    [Fact]
    public void OnlyTheSpotifyChoicesOfferDateAdded()
    {
        Assert.DoesNotContain(TrackSortChoices.All,
            c => c.Value == TrackSortField.DateAdded);
        Assert.Contains(TrackSortChoices.Spotify,
            c => c.Value == TrackSortField.DateAdded);
    }
}
