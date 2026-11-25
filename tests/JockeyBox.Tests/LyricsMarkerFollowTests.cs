using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The instrumental marker is a row of glyph words tiled across the gap
/// (<see cref="InstrumentalGap"/>). Word-follow already steps through them, but
/// line mode - the default - used to leave the row static. What is pinned here:
/// line mode steps one glyph at a time through a marker, the first glyph lights
/// on the same tick the marker is entered, leaving for a sung line clears the
/// row, word mode's marker stepping is untouched, and a sung line in line mode
/// still carries no per-word highlight at all.
///
/// <para>
/// A real <see cref="MainViewModel"/> on <see cref="UiThreadCollection"/>, no
/// window, driven by an explicit playhead: time is passed to
/// <c>UpdateLyricHighlight</c>, never slept for. The same
/// <c>JOCKEYBOX_HOME</c> isolation as <see cref="LyricsEditTests"/> keeps the
/// operator's real settings and metadata store out of the run, and the home is
/// seeded with <see cref="TestHome"/>'s silent route so no view model opens a
/// real audio endpoint.
/// </para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class LyricsMarkerFollowTests : IDisposable
{
    private readonly string? _previousHome;
    private readonly string _home;

    public LyricsMarkerFollowTests()
    {
        _previousHome = Environment.GetEnvironmentVariable("JOCKEYBOX_HOME");
        string root = OperatingSystem.IsWindows() ? @"C:\Dev" : Path.GetTempPath();
        _home = Path.Combine(root, "jb-marker-follow", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
        // A bare home reads as "no routing choice saved yet", which makes
        // BuildRoute fall back to the system default output: every view model then
        // opens a real WASAPI endpoint and render thread (8 s per test). The same
        // silent route the module initializer seeds keeps the devices shut.
        TestHome.WriteSilentSettings(_home);
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOCKEYBOX_HOME", _previousHome);
        try
        {
            // The seeded settings file is read-only so a stray save cannot rewrite it.
            File.SetAttributes(Path.Combine(_home, "settings.json"), FileAttributes.Normal);
            Directory.Delete(_home, recursive: true);
        }
        catch { /* a save timer may still hold it */ }
    }

    // ---- 1. line mode steps the glyphs through the gap ---------------------

    [Fact]
    public void LineMode_StepsMarkerGlyphsOneAtATimeInsideTheGap()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            Load(vm, wordMode: false);

            var marker = Marker(vm);

            // First glyph.
            Tick(vm, marker.Words[0].Start + TimeSpan.FromMilliseconds(100),
                v => Assert.Equal(0, LitGlyphIndex(v)));

            // Advance past the second glyph's start: exactly one glyph, and it moved.
            Tick(vm, marker.Words[1].Start + TimeSpan.FromMilliseconds(100),
                v => Assert.Equal(1, LitGlyphIndex(v)));

            // Mid-gap is still exactly one current glyph.
            Tick(vm, marker.Words[marker.Words.Count / 2].Start + TimeSpan.FromMilliseconds(100),
                v => Assert.Equal(marker.Words.Count / 2, LitGlyphIndex(v)));
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- 2. entering the marker lights the first glyph immediately ---------

    [Fact]
    public void EnteringTheMarker_LightsTheFirstGlyphOnThatTick()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            Load(vm, wordMode: false);
            var marker = Marker(vm);

            // Still inside the sung line before the marker: nothing lit.
            Tick(vm, TimeSpan.FromMilliseconds(3_000), AssertNoWordsLit);

            // One tick later the playhead is on the marker's own start.
            Tick(vm, marker.Time, v => Assert.Equal(0, LitGlyphIndex(v)));
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- 3. leaving the marker leaves no glyph lit ------------------------

    [Fact]
    public void LeavingTheMarkerForASungLine_LeavesNoGlyphCurrent()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            Load(vm, wordMode: false);
            var marker = Marker(vm);

            // Light the last glyph of the stretch.
            var last = marker.Words.Count - 1;
            Tick(vm, marker.Words[last].Start + TimeSpan.FromMilliseconds(100),
                v => Assert.Equal(last, LitGlyphIndex(v)));

            // Cross into the sung line that ends the gap.
            var sung = vm.LyricLines.Last(l => !l.IsInstrumental);
            Tick(vm, sung.Time, v =>
            {
                Assert.False(v.CurrentLyricLine!.IsInstrumental);
                AssertNoWordsLit(v);
                Assert.Equal(-1, CurrentWordIndex(v));
            });
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- 4. word mode's marker stepping is unchanged ----------------------

    [Fact]
    public void WordMode_LightsTheSameGlyphAtTheSameInstantsAsLineMode()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            Load(vm, wordMode: true);
            var marker = Marker(vm);
            var instants = new[]
            {
                marker.Words[0].Start + TimeSpan.FromMilliseconds(100),
                marker.Words[1].Start + TimeSpan.FromMilliseconds(100),
                marker.Words[marker.Words.Count - 1].Start + TimeSpan.FromMilliseconds(100),
            };

            var wordIndices = LitAtEach(vm, instants);

            // Same view model, flipped to line mode: identical stepping inside the marker.
            UiThread.Run(() => vm.IsWordFollow = false);
            var lineIndices = LitAtEach(vm, instants);

            Assert.Equal(new[] { 0, 1, marker.Words.Count - 1 }, wordIndices);
            Assert.Equal(wordIndices, lineIndices);
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- 5. the change is scoped to markers -------------------------------

    [Fact]
    public void LineMode_StillHasNoPerWordHighlightOnASungLine()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            Load(vm, wordMode: false);

            var sung = vm.LyricLines.First(l => !l.IsInstrumental);
            // Guard the guard: the line really does carry synthesized words.
            Assert.NotEmpty(sung.Words);

            Tick(vm, TimeSpan.FromMilliseconds(3_000), v =>
            {
                Assert.False(v.CurrentLyricLine!.IsInstrumental);
                AssertNoWordsLit(v);
                Assert.Equal(-1, CurrentWordIndex(v));
            });
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    // ---- fixtures ----------------------------------------------------------

    /// <summary>
    /// A sung line at 1 s, a 46 s instrumental gap, then the next sung line at
    /// 47 s. Applying it runs <see cref="InstrumentalGap.Fill"/> and
    /// <see cref="WordTimeline.Synthesize"/> exactly as a real load does, so the
    /// marker's glyph words come from the production tiler.
    /// </summary>
    private static void Load(MainViewModel vm, bool wordMode) => UiThread.Run(() =>
    {
        vm.LoadedTrack = Track();
        vm.IsWordFollow = wordMode;
        Invoke(vm, "ApplyLyricsDocument", Document(), null);
    });

    /// <summary>
    /// Advance the playhead and read the glyph state, both inside one dispatcher
    /// operation. Reading after the invoke returns is a race: the view model starts
    /// a 33 ms UI clock in its constructor, and that clock re-runs
    /// <c>UpdateLyricHighlight</c> with the transport's real (idle, zero) position,
    /// which clears the marker. The clock runs on this same dispatcher thread, so a
    /// write and a read inside one delegate cannot be separated by a tick. The
    /// required read argument is what keeps callers atomic: there is no overload
    /// that ticks alone, so a read on the test thread is not expressible.
    /// </summary>
    private static void Tick(MainViewModel vm, TimeSpan at, Action<MainViewModel> readAndAssert) =>
        UiThread.Run(() =>
        {
            Invoke(vm, "UpdateLyricHighlight", at);
            readAndAssert(vm);
        });

    private static int[] LitAtEach(MainViewModel vm, TimeSpan[] instants)
    {
        var lit = new List<int>(instants.Length);
        foreach (var at in instants) Tick(vm, at, v => lit.Add(LitGlyphIndex(v)));
        return lit.ToArray();
    }

    private static LyricLineViewModel Marker(MainViewModel vm)
    {
        var marker = vm.LyricLines.Single(l => l.IsInstrumental);
        Assert.True(marker.Words.Count >= InstrumentalGap.MinGlyphs);
        return marker;
    }

    /// <summary>
    /// Index of the single current marker glyph, or -1 when none is current.
    /// More than one current word, or a current word on a sung line, is a failure.
    /// </summary>
    private static int LitGlyphIndex(MainViewModel vm)
    {
        var lit = vm.LyricLines
            .SelectMany(l => l.Words.Select((w, i) => (Line: l, Word: w, Index: i)))
            .Where(x => x.Word.IsCurrent)
            .ToList();
        Assert.True(lit.Count <= 1, $"more than one word is current ({lit.Count})");
        if (lit.Count == 0) return -1;
        Assert.True(lit[0].Line.IsInstrumental, "a sung line's word was lit in line mode");
        return lit[0].Index;
    }

    private static void AssertNoWordsLit(MainViewModel vm) =>
        Assert.DoesNotContain(vm.LyricLines, l => l.Words.Any(w => w.IsCurrent));

    private static TrackEntry Track() => new()
    {
        RelativeKey = "covers/marker-song",
        RelativePath = "covers/Marker Song.mp3",
        Title = "Marker Song",
        Artist = "Band",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static LyricsDocument Document() => new()
    {
        Synced = true,
        Source = "Fake Provider",
        SourceId = "fake",
        Lines = new List<LyricLine>
        {
            new(TimeSpan.FromMilliseconds(1_000), "one two three four"),
            new(TimeSpan.FromMilliseconds(47_000), "next line"),
        },
    };

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        typeof(MainViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args.Length == 0 ? null : args);

    private static int CurrentWordIndex(MainViewModel vm) =>
        (int)typeof(MainViewModel)
            .GetField("_currentWordIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;
}
