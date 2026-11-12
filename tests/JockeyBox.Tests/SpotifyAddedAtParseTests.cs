using System.Text.Json;
using JockeyBox.Spotify;

namespace JockeyBox.Tests;

/// <summary>
/// `added_at` lives on the playlist/saved-track entry, beside `item`/`track`,
/// never inside the track object. Three envelopes reach the same reader, so all
/// three are pinned here, plus the nulls the schema allows: Spotify documents
/// `added_at` as nullable for very old playlists, so a miss must read as unknown
/// rather than throwing or inventing a date.
/// </summary>
public sealed class SpotifyAddedAtParseTests
{
    private static readonly DateTimeOffset Expected = new(2024, 3, 1, 12, 34, 56, TimeSpan.Zero);

    /// <summary>Mirror of production: the entry is `items[n]`, the reader gets the entry.</summary>
    private static DateTimeOffset? Entry(string pageJson)
    {
        using var doc = JsonDocument.Parse(pageJson);
        return SpotifyApi.ReadAddedAt(doc.RootElement.GetProperty("items")[0]);
    }

    [Fact]
    public void PlaylistItemsEntryCarriesAddedAtBesideItem()
    {
        var value = Entry("""
            {"items":[
              {"added_at":"2024-03-01T12:34:56Z","added_by":{"id":"u"},"is_local":false,
               "item":{"id":"t","name":"Song","type":"track","is_local":false,"duration_ms":1000}}
            ]}
            """);

        Assert.Equal(Expected, value);
    }

    [Fact]
    public void LegacyTracksEntryCarriesAddedAtBesideTrack()
    {
        var value = Entry("""
            {"items":[
              {"added_at":"2024-03-01T12:34:56Z",
               "track":{"id":"t","name":"Song","type":"track","is_local":false,"duration_ms":1000}}
            ]}
            """);

        Assert.Equal(Expected, value);
    }

    [Fact]
    public void LikedSongsEntryCarriesAddedAtBesideTrack()
    {
        var value = Entry("""
            {"items":[
              {"added_at":"2024-03-01T12:34:56Z",
               "track":{"id":"t","name":"Song","type":"track","is_local":false,"duration_ms":1000}}
            ]}
            """);

        Assert.Equal(Expected, value);
    }

    /// <summary>RoundtripKind leaves a stated offset alone instead of rewriting it to UTC.</summary>
    [Fact]
    public void NonUtcOffsetIsPreserved()
    {
        var value = Entry("""
            {"items":[{"added_at":"2024-03-01T14:34:56+02:00","track":{"id":"t"}}]}
            """);

        Assert.NotNull(value);
        Assert.Equal(TimeSpan.FromHours(2), value!.Value.Offset);
        Assert.Equal(Expected, value.Value);
    }

    [Theory]
    [InlineData("""{"items":[{"track":{"id":"t"}}]}""")]                              // property absent
    [InlineData("""{"items":[{"added_at":null,"track":{"id":"t"}}]}""")]                // JSON null
    [InlineData("""{"items":[{"added_at":"","track":{"id":"t"}}]}""")]                  // empty string
    [InlineData("""{"items":[{"added_at":"   ","track":{"id":"t"}}]}""")]               // whitespace
    [InlineData("""{"items":[{"added_at":"not-a-date","track":{"id":"t"}}]}""")]        // unparseable
    [InlineData("""{"items":[{"added_at":1730000000,"track":{"id":"t"}}]}""")]          // wrong JSON type
    public void MissingOrUnusableAddedAtIsNull(string pageJson) => Assert.Null(Entry(pageJson));

    /// <summary>A non-object entry (null item in the array) must not throw either.</summary>
    [Fact]
    public void NonObjectEntryIsNull()
    {
        using var doc = JsonDocument.Parse("""{"items":[null]}""");
        Assert.Null(SpotifyApi.ReadAddedAt(doc.RootElement.GetProperty("items")[0]));
    }
}
