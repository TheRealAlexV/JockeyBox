using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The pair load is the one load path that does not go through
/// <c>LoadTrack</c>, so it has to point the lyrics header at its own track and
/// request the lyrics itself. Both are silent when they regress: the header
/// keeps the previous song's key and the panel stays blank or stale.
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class KaraokePairLyricsTests
{
    [Fact]
    public void KaraokePair_LoadsTheLyricsForThePairTrack()
    {
        string music = TestAudio.WriteConstantWav(0.20f);
        string karaoke = TestAudio.WriteConstantWav(0.50f);
        var cache = TempCacheDir();
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            // Before the load, not after it: a pair load issued while both channels
            // report no output fails, and the pair release guard then drops the pair
            // on the next UI tick. This test used to prime only inside WaitFor, 5 ms
            // after LoadKaraokePair, so it ran against a pair that had already been
            // released and passed anyway because nothing it asserted was undone by
            // the release. PairLoaded below is what makes that observable.
            TestAudio.PrimeOutput(vm);
            UseFakeLyrics(vm, cache);
            var pair = Track("pair/song", music, karaoke);

            UiThread.Run(() => Invoke(vm, "LoadKaraokePair", pair, AudioVariant.Karaoke));

            Assert.Same(pair, vm.LoadedTrack);
            Assert.Equal(pair.RelativeKey, Private<string>(vm, "CurrentLyricsKey"));
            Assert.True(WaitFor(vm, () => vm.LyricLines.Count > 0),
                        "the pair load must fetch its own lyrics, not keep the previous track's");
            Assert.True(vm.IsKaraokePairLoaded,
                        "the pair must survive its own load: " +
                        $"A={vm.Transport.DeckA.State} ({vm.Transport.DeckA.Error}), " +
                        $"B={vm.Transport.DeckB.State} ({vm.Transport.DeckB.Error})");
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(karaoke);
            TryDelete(cache);
        }
    }

    [Fact]
    public async Task LyricSearch_PrefillsFromTheSelectedTrackWhenNothingIsLoaded()
    {
        var cache = TempCacheDir();
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UseFakeLyrics(vm, cache);
            var selected = Track("selected/other", null, null);

            UiThread.Run(() =>
            {
                vm.LoadedTrack = null;
                vm.SelectedTrack = selected;
            });

            // The picker's fields must come from a real track, not from whatever
            // was typed for a previous song. The command returns at its first
            // await, after the prefill and before the providers answer.
            var search = UiThread.Run(() => (Task)Invoke(vm, "TryOtherSourcesAsync")!);

            Assert.True(vm.IsLyricsSourcePickerOpen);
            Assert.Equal(selected.Title, vm.LyricsQueryTitle);
            Assert.Equal(selected.Artist, vm.LyricsQueryArtist);
            Assert.DoesNotContain("Load a track first", vm.LyricsSourceHint ?? "");

            await search.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
            TryDelete(cache);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private sealed class FakeProvider : ILyricsProvider
    {
        public FakeProvider(LyricsDocument? document) => _document = document;

        private readonly LyricsDocument? _document;

        public string Id => "fake";
        public string Name => "Fake Provider";

        public Task<LyricsDocument?> GetLyricsAsync(TrackEntry track, CancellationToken ct)
            => Task.FromResult(_document);
    }

    private static LyricsDocument Synced(params (int Ms, string Text)[] lines) => new()
    {
        Synced = true,
        Source = "Fake Provider",
        SourceId = "fake",
        Lines = lines
            .Select(l => new LyricLine(TimeSpan.FromMilliseconds(l.Ms), l.Text))
            .ToList(),
    };

    private static TrackEntry Track(string key, string? music, string? karaoke) => new()
    {
        RelativeKey = key,
        RelativePath = (music ?? karaoke)!,
        Title = "Song",
        Artist = "Artist",
        MusicPath = music,
        KaraokePath = karaoke,
    };

    private static void UseFakeLyrics(MainViewModel vm, string cacheDir)
    {
        var service = new LyricsService(
            new AppSettings(), cacheDir, new ILyricsProvider[] { new FakeProvider(Synced((1000, "one two three"))) });
        typeof(MainViewModel)
            .GetField("_lyricsService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, service);
    }

    private static string TempCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jb-pair-lyrics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
    }

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        typeof(MainViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args.Length == 0 ? null : args);

    private static T? Private<T>(MainViewModel vm, string name) =>
        (T?)typeof(MainViewModel)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm);

    private static bool WaitFor(MainViewModel vm, Func<bool> until, int milliseconds = 10_000)
    {
        for (int i = 0; i < milliseconds / 5; i++)
        {
            PumpDecks(vm);
            if (until()) return true;
            Thread.Sleep(5);
        }

        PumpDecks(vm);
        return until();
    }

    private static void PumpDecks(MainViewModel vm)
    {
        vm.Transport.DeckA.SetOutputAvailable(true);
        vm.Transport.DeckB.SetOutputAvailable(true);
        vm.Transport.Engine.DeckA.Commands.Drain();
        vm.Transport.Engine.DeckB.Commands.Drain();
    }
}
