using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using JockeyBox.Selftest;
using JockeyBox.Settings;
using Xunit;

namespace JockeyBox.Tests;

public class LyricAlignmentTests
{
    [Fact]
    public void ParseAlignmentOutput_ParsesLinesAndWordsCorrectly()
    {
        var sampleJson = """
        {
          "status": "ok",
          "lines": [
            {
              "time_ms": 12150,
              "text": "I figure it's out of your hands",
              "words": [
                { "start_ms": 12150, "end_ms": 12300, "text": "I" },
                { "start_ms": 12310, "end_ms": 12520, "text": "figure" }
              ]
            }
          ]
        }
        """;

        var doc = LyricAlignmentService.ParseAlignmentOutput(sampleJson);
        Assert.NotNull(doc);
        Assert.True(doc.Synced);
        Assert.Equal(LyricAlignmentService.AlignedSourceId, doc.SourceId);
        Assert.Equal(LyricAlignmentService.AlignedSourceName, doc.Source);
        Assert.Single(doc.Lines);

        var line = doc.Lines[0];
        Assert.Equal(TimeSpan.FromMilliseconds(12150), line.Time);
        Assert.Equal("I figure it's out of your hands", line.Text);
        Assert.NotNull(line.Words);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal("I", line.Words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(12150), line.Words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(12300), line.Words[0].End);
        Assert.Equal("figure", line.Words[1].Text);
    }

    [Fact]
    public void ParseAlignmentOutput_HandlesErrorStatus()
    {
        var sampleErrorJson = """
        {
          "status": "error",
          "error": "Failed to align lyrics."
        }
        """;

        var doc = LyricAlignmentService.ParseAlignmentOutput(sampleErrorJson);
        Assert.Null(doc);
    }

    [Fact]
    public void LyricAlignCli_Requested_DetectsFlag()
    {
        Assert.True(LyricAlignCli.Requested(new[] { "--align-lyrics", "path/to/song.mp3" }));
        Assert.True(LyricAlignCli.Requested(new[] { "--other", "val", "--align-lyrics=Artist|Title" }));
        Assert.False(LyricAlignCli.Requested(new[] { "--selftest" }));
    }

    [Fact]
    public void Enqueue_RejectsMissingFile()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "jb-test-align-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        try
        {
            var store = TrackMetadataStore.Open(Path.Combine(tempHome, "meta.json"));
            var settings = new AppSettings();
            var lyricsService = new LyricsService(settings, tempHome);
            using var service = new LyricAlignmentService(store, lyricsService);

            var track = new TrackEntry
            {
                MusicPath = Path.Combine(tempHome, "nonexistent.mp3"),
                RelativeKey = "nonexistent",
                RelativePath = "nonexistent.mp3",
                Title = "Test",
                Artist = "Test"
            };

            var doc = new LyricsDocument
            {
                Synced = false,
                Source = "test",
                Lines = new() { new LyricLine(TimeSpan.Zero, "Hello world") }
            };

            var enqueued = service.Enqueue(track, doc);
            Assert.False(enqueued);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }
}
