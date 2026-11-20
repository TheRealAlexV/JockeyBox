using System;
using System.Collections.Generic;
using System.Linq;
using JockeyBox.Lyrics;
using JockeyBox.ViewModels;
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
        // The synthesiser must not tile the symbols across the gap; the marker
        // builds its own glyph words, one per symbol, before Synthesize runs.
        var marker = doc.Lines[1];
        Assert.True(marker.IsInstrumental);
        Assert.NotNull(marker.Words);
        Assert.Equal(TimeSpan.FromSeconds(7), marker.Words![0].Start);
        Assert.Equal(TimeSpan.FromSeconds(42), marker.Words[^1].End);
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

    // ---- 2. the marker carries glyph words that fill its own window --------

    [Fact]
    public void MarkerWords_TileAcrossTheGap_AndTheLastOneEndsAtTheNextLyric()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse")));
        var marker = doc.Lines[1];

        Assert.NotNull(marker.Words);
        Assert.True(marker.Words!.Count > 1);

        // Tiled from the marker's own start to where singing resumes: the first
        // word opens the window, the last one closes it on the next lyric.
        Assert.Equal(marker.Time, marker.Words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(42), marker.Words[^1].End);

        // One glyph per word, cycling the marker's symbols.
        Assert.All(marker.Words, w => Assert.Equal(1, w.Text.Length));
        for (int i = 0; i < marker.Words.Count; i++)
            Assert.Equal(InstrumentalGap.Marker[i % InstrumentalGap.Marker.Length].ToString(),
                marker.Words[i].Text);
    }

    [Fact]
    public void MarkerWords_CoverTheGapWithoutGapsOrOverlap()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse")));
        var marker = doc.Lines[1];
        var words = marker.Words!;

        // Contiguous and ordered: each word starts exactly where the last ended.
        for (int i = 1; i < words.Count; i++)
            Assert.Equal(words[i - 1].End, words[i].Start);

        // And none of them reaches past the gap.
        Assert.All(words, w =>
        {
            Assert.True(w.Start >= marker.Time, $"word {w.Text} starts before the marker");
            Assert.True(w.End <= TimeSpan.FromSeconds(42), $"word {w.Text} ends past the gap");
            Assert.True(w.End > w.Start);
        });
    }

    [Fact]
    public void MarkerRow_HasWords_SoTheWordLayoutRenders()
    {
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse")));
        var marker = doc.Lines[1];

        var line = new LyricLineViewModel(marker.Time, marker.Text, marker.Words, marker.IsInstrumental);

        // IsInstrumental stays true (the row is still a marker), but the words
        // must survive so the panel renders them and the follower can walk them.
        Assert.True(line.IsInstrumental);
        Assert.True(line.HasWords);
        Assert.Equal(marker.Words!.Count, line.Words.Count);
        Assert.Equal(InstrumentalGap.Marker, line.Text);
    }

    [Fact]
    public void Synthesize_StillSkipsMarkers()
    {
        // A marker built by hand (no words) must not have the symbols tiled by
        // the synthesiser: the marker owns its words, the synthesiser does not.
        var doc = new LyricsDocument
        {
            Synced = true,
            Source = "test",
            SourceId = "test",
            Lines = new List<LyricLine>
            {
                new(TimeSpan.FromSeconds(2), "hold on"),
                new(TimeSpan.FromSeconds(7), InstrumentalGap.Marker) { IsInstrumental = true },
                new(TimeSpan.FromSeconds(42), "next verse"),
            },
        };

        WordTimeline.Synthesize(doc);

        Assert.Null(doc.Lines[1].Words);
        Assert.NotNull(doc.Lines[0].Words);
    }

    [Fact]
    public void SungLineWords_AreUnchangedByTheMarker()
    {
        // The marker only steals the window of the line before it; a line after
        // the gap keeps exactly the words it would get on its own.
        var doc = InstrumentalGap.Fill(Doc((2, "hold on"), (42, "next verse"), (50, "a b c")));
        WordTimeline.Synthesize(doc);

        var after = doc.Lines[2];
        var expected = WordTimeline.BuildLineWords(after.Text,
            TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(50));

        Assert.Equal(expected.Count, after.Words!.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Start, after.Words[i].Start);
            Assert.Equal(expected[i].End, after.Words[i].End);
            Assert.Equal(expected[i].Text, after.Words[i].Text);
        }
    }
}
