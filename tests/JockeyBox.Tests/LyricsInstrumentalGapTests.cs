using System;
using System.Collections.Generic;
using System.Linq;
using JockeyBox.Lyrics;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Instrumental stretches are shown as a marker line on every synced track. The
/// marker is inserted before <see cref="WordTimeline.Synthesize"/> so the sung
/// line before the gap stops stretching its words across it.
/// </summary>
public class LyricsInstrumentalGapTests
{
    private static LyricsDocument Doc(params (int Seconds, string Text)[] lines) => new()
    {
        Synced = true,
        Source = "test",
        SourceId = "test",
        Lines = lines
            .Select(l => new LyricLine(TimeSpan.FromSeconds(l.Seconds), l.Text))
            .ToList(),
    };

    private static LyricLineEdit EditAt(int ms) =>
        new() { TimeMs = ms, Text = "edited", SourceId = "test" };

    [Fact]
    public void FortySecondGap_MarkerSitsFiveSecondsAfterTheLine_AndEndsAtTheNextLyric()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse")));

        Assert.Equal(3, doc.Lines.Count);
        var marker = doc.Lines[1];
        Assert.True(marker.IsInstrumental);
        Assert.Equal(TimeSpan.FromSeconds(7), marker.Time);
        Assert.Equal(InstrumentalGap.Marker, marker.Text);

        // The marker's own span runs to the next sung line, so it covers the gap.
        var (startMs, endMs) = LyricSectionSelector.LineSpan(doc, 1);
        Assert.Equal(7_000, startMs);
        Assert.Equal(42_000, endMs);
    }

    [Fact]
    public void SixSecondGap_GetsNoMarker()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (8, "next verse")));

        Assert.Equal(2, doc.Lines.Count);
        Assert.DoesNotContain(doc.Lines, l => l.IsInstrumental);
    }

    [Fact]
    public void PreGapLineWords_EndAtTheMarker_NotAtTheNextLyricLine()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on tight"), (42, "next verse")));

        WordTimeline.Synthesize(doc);

        var words = doc.Lines[0].Words;
        Assert.NotNull(words);
        Assert.Equal(TimeSpan.FromSeconds(7), words![^1].End);
        // The marker itself carries no words: the synthesiser must not tile the
        // symbols across the gap.
        Assert.Null(doc.Lines[1].Words);
    }

    [Fact]
    public void LongIntro_MarkerSitsAtZero()
    {
        var doc = InstrumentalGap.Fill(Doc((12, "first line"), (20, "second line")));

        Assert.Equal(3, doc.Lines.Count);
        Assert.True(doc.Lines[0].IsInstrumental);
        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(12), doc.Lines[1].Time);
    }

    [Fact]
    public void UnsyncedDocument_GetsNoMarker()
    {
        var doc = LyricsDocument.Unsynced(new[] { "line one", "line two" }, "test");
        var filled = InstrumentalGap.Fill(doc);

        Assert.DoesNotContain(filled.Lines, l => l.IsInstrumental);
        Assert.Equal(doc.Lines.Count, filled.Lines.Count);
    }

    [Fact]
    public void ApplyingTwice_DoesNotDoubleInsert()
    {
        var once = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse")));
        var twice = InstrumentalGap.Fill(once);

        Assert.Equal(3, twice.Lines.Count);
        Assert.Single(twice.Lines.Where(l => l.IsInstrumental));
    }

    [Fact]
    public void Fill_LeavesTheInputDocumentAlone()
    {
        var doc = Doc((2, "hold on"), (42, "next verse"));
        InstrumentalGap.Fill(doc);

        Assert.Equal(2, doc.Lines.Count);
        Assert.DoesNotContain(doc.Lines, l => l.IsInstrumental);
    }

    [Fact]
    public void FindLine_NeverReturnsAMarker()
    {
        var lines = new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(10), InstrumentalGap.Marker) { IsInstrumental = true },
            new(TimeSpan.FromSeconds(10), "real line"),
        };

        Assert.Equal(1, LyricEdits.FindLine(lines, EditAt(10_000)));
    }

    [Fact]
    public void LineSpan_EndsOnTheNextLyricLine_NotOnAMarker()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse")));

        var (startMs, endMs) = LyricSectionSelector.LineSpan(doc, 0);

        Assert.Equal(2_000, startMs);
        Assert.Equal(42_000, endMs);
    }
}
