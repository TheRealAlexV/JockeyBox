using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Metadata;
using JockeyBox.Settings;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Operator line-text edits. What is pinned here is the four things that regress
/// silently and one that would regress loudly (an edited lyric landing in the shared
/// cache and leaking onto every other file with the same tags and duration):
/// the rendered text, the untouched timing, the source guard, the highlight reset
/// and the round-trip through <c>track-metadata.json</c>.
///
/// <para>
/// The view-model tests run on <see cref="UiThreadCollection"/> (a real WPF
/// view model, no window) and repoint <c>JOCKEYBOX_HOME</c> at a throwaway
/// directory for their lifetime, so nothing touches the operator's
/// <c>%APPDATA%\JockeyBox</c>. The metadata store is driven through its public
/// surface; only the private document/playhead entry points are reached by
/// reflection, the way <c>KaraokePairTests</c> reaches <c>LoadTrack</c>.
/// </para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class LyricsEditTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _home;
    private readonly string _cacheDir;

    public LyricsEditTests()
    {
        _previousHome = Environment.GetEnvironmentVariable("JOCKEYBOX_HOME");

        // Windows runs keep the sandbox under C:\Dev; the tests only ever run on
        // Windows, the fallback just keeps a Unix `dotnet test` honest.
        string root = OperatingSystem.IsWindows() ? @"C:\Dev" : Path.GetTempPath();
        _home = Path.Combine(root, "jb-lyrics-edit", Guid.NewGuid().ToString("n"));
        _cacheDir = Path.Combine(_home, "lyrics");
        Directory.CreateDirectory(_cacheDir);

        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch { /* a save timer may still hold it */ }
    }

    // ---- fixtures ----------------------------------------------------------

    private static TrackEntry Track() => new()
    {
        RelativeKey = "covers/song",
        RelativePath = "covers/Song.mp3",
        Title = "Song",
        Artist = "Band",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static LyricsDocument Synced(params (int Ms, string Text)[] lines) => new()
    {
        Synced = true,
        Source = "Fake Provider",
        SourceId = "fake",
        Lines = lines.Select(l => new LyricLine(TimeSpan.FromMilliseconds(l.Ms), l.Text)).ToList(),
    };

    private static LyricsDocument SyncedFrom(string sourceId, params (int Ms, string Text)[] lines) => new()
    {
        Synced = true,
        Source = "Fake Provider",
        SourceId = sourceId,
        Lines = lines.Select(l => new LyricLine(TimeSpan.FromMilliseconds(l.Ms), l.Text)).ToList(),
    };

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        typeof(MainViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args.Length == 0 ? null : args);

    private static int CurrentWordIndex(MainViewModel vm) =>
        (int)typeof(MainViewModel)
            .GetField("_currentWordIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private sealed class FakeProvider : ILyricsProvider
    {
        private readonly LyricsDocument? _document;

        public FakeProvider(LyricsDocument? document) => _document = document;

        public string Id => "fake";
        public string Name => "Fake Provider";

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
        {
            LyricsTrace.Note(Id, _document == null ? "returned nothing" : "found");
            return Task.FromResult(_document);
        }
    }

    // ---- 1. rendered text changes, the shared cache does not ---------------

    [Fact]
    public async Task LineEdit_ReplacesRenderedText_AndLeavesTheLyricsCacheByteIdentical()
    {
        var track = Track();
        var document = Synced((1000, "wrong line one"), (4000, "line two"));
        var service = new LyricsService(
            new AppSettings(), _cacheDir, new ILyricsProvider[] { new FakeProvider(document) });

        var lookup = await service.GetLyricsAsync(track, CancellationToken.None);
        Assert.NotNull(lookup.Document);

        // One cache entry per source, keyed artist|title|seconds|sourceId and
        // shared by every file with these tags and this length.
        var before = Directory.GetFiles(_cacheDir)
            .ToDictionary(f => f, File.ReadAllBytes);
        Assert.NotEmpty(before);

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() =>
            {
                vm.LoadedTrack = track;
                Invoke(vm, "ApplyLyricsDocument", lookup.Document, null);
            });
            Assert.Equal("wrong line one", vm.LyricLines[0].Text);

            UiThread.Run(() => vm.SetLyricLineText(new[] { 0 }, "operator line one"));

            Assert.Equal("operator line one", vm.LyricLines[0].Text);
            // The whole point: edited text must never reach the cache, or it shows
            // up on any other file with the same artist|title|seconds|sourceId.
            var after = Directory.GetFiles(_cacheDir)
                .ToDictionary(f => f, File.ReadAllBytes);
            Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
            foreach (var (file, bytes) in before)
                Assert.Equal(bytes, after[file]);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- 2. timings stay put, words re-space inside the same window --------

    [Fact]
    public void LineEdit_KeepsLineTime_AndRespansWordsInsideTheUnchangedWindow()
    {
        var doc = new LyricsDocument
        {
            Synced = true,
            Source = "Fake",
            SourceId = "fake",
            Lines = new List<LyricLine>
            {
                new(TimeSpan.FromMilliseconds(1000), "one two three four"),
                new(TimeSpan.FromMilliseconds(5000), "next line"),
            },
        };
        WordTimeline.Synthesize(doc);
        Assert.Equal(4, doc.Lines[0].Words!.Count);

        var edited = LyricEdits.Apply(doc, new[]
        {
            new LyricLineEdit { TimeMs = 1000, Text = "one two three four five six", SourceId = "fake" },
        });

        var line = edited!.Lines[0];
        Assert.Equal(TimeSpan.FromMilliseconds(1000), line.Time);
        Assert.True(line.IsSynthetic);
        Assert.Equal(6, line.Words!.Count);
        Assert.All(line.Words, w => Assert.True(w.IsSynthetic));

        var windowEnd = TimeSpan.FromMilliseconds(5000);
        Assert.All(line.Words, w =>
        {
            Assert.True(w.Start >= line.Time, "word starts before the line");
            Assert.True(w.End <= windowEnd, "word runs past the next line");
            Assert.True(w.End > w.Start, "word has no duration");
        });
        for (int i = 1; i < line.Words.Count; i++)
        {
            Assert.True(line.Words[i].Start >= line.Words[i - 1].End, "word spans overlap");
            Assert.True(line.Words[i].Start >= line.Words[i - 1].Start, "word spans are out of order");
        }

        // The last line has no next line to bound it: the existing last-line
        // window applies, and the edit does not move the line's own time.
        var last = LyricEdits.Apply(doc, new[]
        {
            new LyricLineEdit { TimeMs = 5000, Text = "a b", SourceId = "fake" },
        })!.Lines[1];
        Assert.Equal(TimeSpan.FromMilliseconds(5000), last.Time);
        Assert.Equal(2, last.Words!.Count);
        Assert.True(last.Words[^1].End
                    <= TimeSpan.FromMilliseconds(5000 + WordTimeline.LastLineWindowMs));

        // Every line other than the edited one is untouched.
        Assert.Equal("next line", edited.Lines[1].Text);
    }

    // ---- 3. another source's timing is not this line ----------------------

    [Fact]
    public void EditAuthoredAgainstAnotherSource_DoesNotApply_AndStaysInTheStore()
    {
        using var store = TrackMetadataStore.Open(Path.Combine(_home, "track-metadata.json"));
        store.SetLyricEdits("covers/song", new[]
        {
            new LyricLineEdit { TimeMs = 1000, Text = "edited against lrclib", SourceId = "lrclib" },
        });

        var fromOther = SyncedFrom("spotify-color", (1000, "spotify text"));
        var applied = LyricEdits.Apply(fromOther, store.GetLyricEdits("covers/song"));

        Assert.Same(fromOther, applied);
        Assert.Equal("spotify text", applied!.Lines[0].Text);
        // Kept for when that source comes back, never pruned by a mismatch.
        Assert.Single(store.GetLyricEdits("covers/song"));

        // And it does apply to the source it was written against.
        var fromOriginal = SyncedFrom("lrclib", (1000, "lrclib text"));
        var appliedThere = LyricEdits.Apply(fromOriginal, store.GetLyricEdits("covers/song"))!;
        Assert.Equal("edited against lrclib", appliedThere.Lines[0].Text);
    }

    // ---- 4. the word highlight does not survive the rebuild ---------------

    [Fact]
    public void LineEdit_ResetsTheWordHighlight()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            // Write and read in one dispatcher operation: the view model's 33 ms UI
            // clock re-runs UpdateLyricHighlight with the idle transport position,
            // and it can run on the UI thread between the write and a read taken
            // here on the test thread.
            int lit = UiThread.Run(() =>
            {
                vm.LoadedTrack = Track();
                vm.IsWordFollow = true;
                Invoke(vm, "ApplyLyricsDocument", Synced((1000, "one two three four"), (6000, "next")), null);
                // A playhead inside the first line's window lights one word.
                Invoke(vm, "UpdateLyricHighlight", TimeSpan.FromMilliseconds(1500));
                return CurrentWordIndex(vm);
            });
            Assert.NotEqual(-1, lit);

            UiThread.Run(() => vm.SetLyricLineText(new[] { 0 }, "edited one two three"));

            Assert.Equal(-1, CurrentWordIndex(vm));
            Assert.DoesNotContain(vm.LyricLines, l => l.Words.Any(w => w.IsCurrent));
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- 5. the store round-trip, old file included -----------------------

    [Fact]
    public void Edits_SurviveASaveAndReload_AndAnOldFileWithoutTheListLoads()
    {
        string path = Path.Combine(_home, "track-metadata.json");
        // A file written before this feature: no lyricEdits property at all.
        File.WriteAllText(path,
            "{\"version\":1,\"tracks\":[{\"key\":\"a/song\",\"title\":\"Song\",\"artist\":\"Band\"}]}");

        using (var store = TrackMetadataStore.Open(path))
        {
            Assert.Empty(store.GetLyricEdits("a/song"));
            store.SetLyricEdits("a/song", new[]
            {
                new LyricLineEdit { TimeMs = 1200, Text = "operator line", SourceId = "lrclib" },
                new LyricLineEdit { TimeMs = 400, Text = "earlier line", SourceId = "lrclib" },
            });
            store.Save();
        }

        using (var reopened = TrackMetadataStore.Open(path))
        {
            var edits = reopened.GetLyricEdits("a/song");
            Assert.Equal(2, edits.Count);
            // Stored sorted by line time, so the sheet reads in order.
            Assert.Equal(400, edits[0].TimeMs);
            Assert.Equal("earlier line", edits[0].Text);
            Assert.Equal(1200, edits[1].TimeMs);
            Assert.Equal("operator line", edits[1].Text);
            Assert.Equal("lrclib", edits[1].SourceId);
        }

        Assert.Contains("lyricEdits", File.ReadAllText(path));
    }

    // ---- 6. a marker line is not text, so it cannot be edited --------------

    [Fact]
    public void SetLyricLineText_OnAnInstrumentalMarker_WritesNoEdit()
    {
        // The panel renders marker rows next to sung ones, so a selection can
        // include one. An edit written at a marker's time would be an orphan:
        // LyricLineEdit.FindLine matches real lines and would never apply it.
        var doc = Synced((2000, "hold on"), (7000, InstrumentalGap.Marker), (42000, "next verse"));
        doc.Lines[1] = doc.Lines[1] with { IsInstrumental = true };

        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() =>
            {
                vm.LoadedTrack = Track();
                Invoke(vm, "ApplyLyricsDocument", doc, null);
            });
            Assert.True(vm.LyricLines[1].IsInstrumental);

            UiThread.Run(() => vm.SetLyricLineText(new[] { 1 }, "operator text"));

            // The marker's rendered text is untouched...
            Assert.Equal(InstrumentalGap.Marker, vm.LyricLines[1].Text);

            // ...and nothing reached the store.
            var store = (TrackMetadataStore)typeof(MainViewModel)
                .GetField("_trackMeta", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm)!;
            Assert.Empty(store.GetLyricEdits(Track().RelativeKey));
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }
}
