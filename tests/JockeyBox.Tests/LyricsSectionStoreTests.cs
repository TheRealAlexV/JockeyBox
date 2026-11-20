using System;
using System.IO;
using System.Linq;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The section store's overlap policy, which is the only place a stamp the
/// operator asked for can be refused.
///
/// <para>
/// A stamp fully contained in an existing section used to be rejected, and the
/// operator met that with a real file: one section [62480, 109030] labelled
/// "line 8" covered 46.5 s, because <see cref="LyricSectionSelector.Span"/> ends
/// at the next non-marker line and skips markers, so a stamp on the line before
/// an instrumental swallowed the whole gap. Every later stamp inside that window
/// was refused. Contained means "carve it out", not "refuse": that section is
/// replaced by its two remainders plus the new span.
/// </para>
///
/// <para>
/// A genuine partial overlap - starts inside, runs past the end - is still
/// refused, and with the same reason. Relaxing the reject condition instead
/// would let <see cref="LyricOffsetTimeline.NormalizeWithReport"/> drop the
/// nested span and report "displaced by an earlier section", which tells the
/// operator nothing about what to do.
/// </para>
/// </summary>
public sealed class LyricsSectionStoreTests : IDisposable
{
    private const string Key = "covers/song";

    private readonly string _dir;
    private readonly TrackMetadataStore _store;

    public LyricsSectionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jb-tests", "sections-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _store = TrackMetadataStore.Open(Path.Combine(_dir, "track-metadata.json"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a save timer may hold the file */ }
    }

    // ---- contained: split, do not refuse ----------------------------------

    [Fact]
    public void StampInsideASection_SplitsItIntoBothRemainders()
    {
        Assert.True(_store.AddSection(Key, 62_480, 109_030, 0, "line 8").Added);

        var result = _store.AddSection(Key, 70_000, 80_000, 500, "line 9");

        Assert.True(result.Added, result.RejectReason);

        var sections = _store.GetSections(Key);
        Assert.Equal(3, sections.Count);

        // The new stamp, and the container's two halves carrying the container's
        // own offset and label on either side of it.
        Assert.Equal(new LyricOffsetSection(62_480, 70_000, 0, "line 8"), sections[0]);
        Assert.Equal(new LyricOffsetSection(70_000, 80_000, 500, "line 9"), sections[1]);
        Assert.Equal(new LyricOffsetSection(80_000, 109_030, 0, "line 8"), sections[2]);

        // And the accepted span is the one the index points at.
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void StampFlushWithTheStart_LeavesOnlyTheRightRemainder()
    {
        Assert.True(_store.AddSection(Key, 10_000, 40_000, 0, "verse").Added);

        var result = _store.AddSection(Key, 10_000, 20_000, 250, "line 2");

        Assert.True(result.Added, result.RejectReason);

        var sections = _store.GetSections(Key);
        Assert.Equal(2, sections.Count);
        Assert.Equal(new LyricOffsetSection(10_000, 20_000, 250, "line 2"), sections[0]);
        Assert.Equal(new LyricOffsetSection(20_000, 40_000, 0, "verse"), sections[1]);
    }

    [Fact]
    public void StampOverTheWholeSection_ReplacesIt()
    {
        Assert.True(_store.AddSection(Key, 10_000, 40_000, 0, "verse").Added);

        var result = _store.AddSection(Key, 10_000, 40_000, 900, "line 2");

        Assert.True(result.Added, result.RejectReason);
        Assert.Equal(new LyricOffsetSection[] { new(10_000, 40_000, 900, "line 2") },
            _store.GetSections(Key));
    }

    // ---- half-open: touching is not overlapping ---------------------------

    [Fact]
    public void StampTouchingASectionBoundary_IsAccepted()
    {
        Assert.True(_store.AddSection(Key, 1_000, 5_000, 0, "verse").Added);

        // [5000, 9000) starts exactly where [1000, 5000) ends: half-open, so the
        // boundary belongs to neither section twice.
        var after = _store.AddSection(Key, 5_000, 9_000, 100, "line 2");
        var before = _store.AddSection(Key, 0, 1_000, 200, "line 1");

        Assert.True(after.Added, after.RejectReason);
        Assert.True(before.Added, before.RejectReason);
        Assert.Equal(new LyricOffsetSection[]
        {
            new(0, 1_000, 200, "line 1"),
            new(1_000, 5_000, 0, "verse"),
            new(5_000, 9_000, 100, "line 2"),
        }, _store.GetSections(Key));
    }

    // ---- genuine partial overlap: still refused ---------------------------

    [Fact]
    public void StampStartingInsideAndRunningPast_IsStillRefused()
    {
        Assert.True(_store.AddSection(Key, 0, 10_000, 0, "verse").Added);

        var result = _store.AddSection(Key, 4_000, 14_000, 300, "line 2");

        Assert.False(result.Added);
        Assert.Equal(-1, result.Index);
        Assert.Equal("overlaps the section at 0-10000 ms; trim or remove that section first",
            result.RejectReason);

        // Nothing was written on the way to the refusal.
        Assert.Equal(new LyricOffsetSection[] { new(0, 10_000, 0, "verse") },
            _store.GetSections(Key));
    }
}
