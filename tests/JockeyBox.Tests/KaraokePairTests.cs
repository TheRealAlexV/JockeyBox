using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The karaoke pair: a per-playback action that loads the instrumental on deck A
/// and the original on deck B. These tests pin the two things that regress
/// silently - the deck titles (a pair must not outlive its own load) and the
/// queue's two-row unit (a mate must never start on its own).
/// </summary>
[Collection(nameof(UiThreadCollection))]
public sealed class KaraokePairTests
{
    // ---- the two required regression tests ---------------------------------

    [Fact]
    public void PairThenNormalLoad_RestoresDeckTitles_AndClearsTheLock()
    {
        string music = TestAudio.WriteConstantWav(0.20f);
        string karaoke = TestAudio.WriteConstantWav(0.50f);
        string other = TestAudio.WriteConstantWav(0.30f);
        try
        {
            var vm = UiThread.Run(() => new MainViewModel());
            var pair = Track("a/song", music, karaoke);
            var normal = Track("a/other", other, null);

            UiThread.Run(() => Invoke(vm, "LoadKaraokePair", pair, AudioVariant.Karaoke));
            Assert.True(PairLoaded(vm));
            Assert.Equal("DECK: KARAOKE", vm.DeckA.Title);
            Assert.Equal("DECK: ORIGINAL", vm.DeckB.Title);

            // An ordinary load is the release choke point: it runs first, so the
            // lock and both titles are back before the new track lands.
            UiThread.Run(() => Invoke(vm, "LoadTrack", normal, false, (PlaybackDeck?)null, true));
            Assert.False(PairLoaded(vm));
            Assert.Equal("DECK A", vm.DeckA.Title);
            Assert.Equal("DECK B", vm.DeckB.Title);
        }
        finally
        {
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(karaoke);
            TestAudio.TryDelete(other);
        }
    }

    [Fact]
    public void RestoreTitle_CalledTwice_StillYieldsTheBaseTitles()
    {
        var vm = UiThread.Run(() => new MainViewModel());

        vm.DeckA.SetKaraokeTitle("DECK: KARAOKE");
        vm.DeckA.RestoreTitle();
        vm.DeckA.RestoreTitle();

        Assert.Equal("DECK A", vm.DeckA.Title);
        Assert.Equal("DECK B", vm.DeckB.Title);
    }

    [Fact]
    public void KaraokePair_StartsBothDecks_WithTheLockEngaged()
    {
        string music = TestAudio.WriteConstantWav(0.20f, seconds: 30);
        string karaoke = TestAudio.WriteConstantWav(0.50f, seconds: 30);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Capture(string line) => lines.Enqueue(line);

        Log.LineWritten += Capture;
        try
        {
            var vm = UiThread.Run(() =>
            {
                var model = new MainViewModel();
                model.InitializeDeckUi();
                return model;
            });

            try
            {
                var pair = Track("a/song", music, karaoke);

                // One karaoke load arms the link and the lock, then starts both
                // decks. It used to call PlayDeck(A), which bypasses Target.
                UiThread.Run(() => Invoke(vm, "LoadKaraokePair", pair, AudioVariant.Karaoke));

                bool both = false;
                for (int i = 0; i < 4000 && !both; i++)
                {
                    // The fake output path the other transport tests use, re-asserted
                    // each pass: no engine thread runs here, so the transport keeps
                    // reporting the route as gone and a load would fail with
                    // "no output configured".
                    UiThread.Run(() =>
                    {
                        vm.Transport.DeckA.SetOutputAvailable(true);
                        vm.Transport.DeckB.SetOutputAvailable(true);
                    });
                    vm.Transport.Engine.DeckA.Commands.Drain();
                    vm.Transport.Engine.DeckB.Commands.Drain();
                    // Let the marshalled state notifications run: the channel posts
                    // them to the UI dispatcher, which no test thread pumps by itself.
                    UiThread.Run(() => { });
                    both = vm.Transport.DeckA.State == TransportState.Playing
                        && vm.Transport.DeckB.State == TransportState.Playing;
                    if (!both) Thread.Sleep(5);
                }

                Assert.True(both,
                    $"both decks play: A={vm.Transport.DeckA.State} ({vm.Transport.DeckA.Error}), " +
                    $"B={vm.Transport.DeckB.State} ({vm.Transport.DeckB.Error})");
                Assert.Equal(TransportTarget.Both, vm.Transport.Target);
                Assert.True(vm.Transport.SyncActive, "the pair engages the master/slave lock");

                // One UI tick, the same one the window's timer runs: a deck reaching
                // Playing raises no channel event, so the settled line is written by
                // the tick that drives the readouts. No timer here, so drive it once.
                UiThread.Run(() => Invoke(vm, "OnUiTick"));

                // The same line that lands in jockeybox.log proves it without the UI.
                var pairText = string.Join("\n", lines.Where(line => line.Contains("karaoke pair")));
                var loadedFlag = ReadField(vm, "_karaokePairLoaded");
                var loggedFlag = ReadField(vm, "_pairStartLogged");
                Assert.True(pairText.Contains("both decks Playing") && pairText.Contains("sync=True"),
                    $"the pair's log lines: {pairText}\n" +
                    $"pairLoaded={loadedFlag} pairStartLogged={loggedFlag} " +
                    $"A={vm.Transport.DeckA.State} B={vm.Transport.DeckB.State} " +
                    $"target={vm.Transport.Target} sync={vm.Transport.SyncActive}");
            }
            finally
            {
                UiThread.Run(() => vm.Dispose());
            }
        }
        finally
        {
            Log.LineWritten -= Capture;
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(karaoke);
        }
    }

    // ---- settings migration -------------------------------------------------

    [Fact]
    public void SettingsJson_WithTheRetiredKaraokeFlag_StillLoads()
    {
        // The retired flag is an unknown member now: the loader ignores it instead
        // of throwing, so an old settings.json keeps working with no migration.
        const string json = """
        {
          "KaraokeModeEnabled": true,
          "OutputDelayMs": 120
        }
        """;

        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(settings);
        Assert.Equal(120, settings!.OutputDelayMs);
    }

    // ---- the gate -----------------------------------------------------------

    [Fact]
    public void HasKaraokePairOnDisk_BothFilesPresent_IsTrue()
    {
        string music = TestAudio.WriteConstantWav(0.2f);
        string karaoke = TestAudio.WriteConstantWav(0.5f);
        try
        {
            Assert.True(MainViewModel.HasKaraokePairOnDisk(Track("a/song", music, karaoke)));
        }
        finally
        {
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(karaoke);
        }
    }

    [Fact]
    public void HasKaraokePairOnDisk_NoOriginal_IsFalse()
    {
        string karaoke = TestAudio.WriteConstantWav(0.5f);
        try
        {
            // The karaoke file alone is a normal track, not a pair.
            Assert.False(MainViewModel.HasKaraokePairOnDisk(Track("a/song", null, karaoke)));
        }
        finally { TestAudio.TryDelete(karaoke); }
    }

    [Fact]
    public void HasKaraokePairOnDisk_FlaggedButMissing_IsFalse()
    {
        string music = TestAudio.WriteConstantWav(0.2f);
        try
        {
            var track = Track("a/song", music, Path.Combine(Path.GetTempPath(), "jb-nope.karaoke.wav"));
            Assert.False(MainViewModel.HasKaraokePairOnDisk(track));
        }
        finally { TestAudio.TryDelete(music); }
    }

    [Fact]
    public void HasKaraokePairOnDisk_UnpairedSiblingOnDisk_IsTrue()
    {
        string music = TestAudio.WriteConstantWav(0.2f);
        // "song.karaoke.wav" beside "song.wav" is the naming rule, with no library
        // row flagging it.
        string sibling = KaraokeNaming.KaraokePathFor(music)!;
        File.Copy(music, sibling, overwrite: true);
        try
        {
            Assert.True(MainViewModel.HasKaraokePairOnDisk(Track("a/song", music, null)));
            Assert.False(MainViewModel.HasKaraokePairOnDisk(null));
        }
        finally
        {
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(sibling);
        }
    }

    // ---- the queue's two-row unit -------------------------------------------

    [Fact]
    public void FromKaraokePair_MakesTwoRows_SharingAKey_OnBothDecks()
    {
        var track = Track("a/song", @"C:\m\song.mp3", @"C:\m\song.karaoke.mp3");

        var (karaoke, original) = PlaybackQueueItem.FromKaraokePair(track);

        Assert.Equal(KaraokePairRole.Karaoke, karaoke.PairRole);
        Assert.Equal(KaraokePairRole.Original, original.PairRole);
        Assert.Equal(karaoke.PairKey, original.PairKey);
        Assert.False(string.IsNullOrWhiteSpace(karaoke.PairKey));
        Assert.Equal(PlaybackDeck.A, karaoke.DeckTarget);
        Assert.Equal(PlaybackDeck.B, original.DeckTarget);

        // Composite keys, so the Key+Kind dedup cannot fold a pair row into a
        // normal row for the same song.
        Assert.NotEqual(track.RelativeKey, karaoke.Key);
        Assert.NotEqual(karaoke.Key, original.Key);
    }

    [Fact]
    public void QueueStore_RoundTripsThePairFields()
    {
        var (karaoke, original) = PlaybackQueueItem.FromKaraokePair(
            Track("a/song", @"C:\m\song.mp3", @"C:\m\song.karaoke.mp3"));

        using (var store = new PlaybackQueueStore())
        {
            store.SaveNow(new[]
            {
                new PlaybackQueueRecord
                {
                    Kind = karaoke.Kind, Key = karaoke.Key, Title = karaoke.Title,
                    Artist = karaoke.Artist, Deck = "A",
                    Pair = karaoke.PairKey, Role = "karaoke",
                },
                new PlaybackQueueRecord
                {
                    Kind = original.Kind, Key = original.Key, Title = original.Title,
                    Artist = original.Artist, Deck = "B",
                    Pair = original.PairKey, Role = "original",
                },
            });
        }

        var records = new PlaybackQueueStore().Load();
        var back = records.Where(r => r.Pair == karaoke.PairKey).ToList();

        Assert.Equal(2, back.Count);
        Assert.Contains(back, r => r.Role == "karaoke" && r.Deck == "A");
        Assert.Contains(back, r => r.Role == "original" && r.Deck == "B");
    }

    [Fact]
    public void QueueRecord_WithoutPair_RestoresAsANormalRow()
    {
        var track = Track("a/song", @"C:\m\song.mp3", @"C:\m\song.karaoke.mp3");
        var record = new PlaybackQueueRecord
        {
            Kind = PlaybackQueueItem.LibraryKind,
            Key = track.RelativeKey,
            Title = track.Title,
            Deck = "B",
        };

        var item = PlaybackQueueItem.FromRecord(record, track);

        Assert.Equal(KaraokePairRole.None, item.PairRole);
        Assert.Null(item.PairKey);
        Assert.False(item.IsKaraokePair);
        Assert.Equal(PlaybackDeck.B, item.DeckTarget);
    }

    [Fact]
    public async Task StartingAPair_MarksBothRowsPlayed_AndNeverStartsTheMate()
    {
        string music = TestAudio.WriteConstantWav(0.20f);
        string karaoke = TestAudio.WriteConstantWav(0.50f);
        try
        {
            var vm = UiThread.Run(() => new MainViewModel());
            var pair = Track("a/song", music, karaoke);
            var (karaokeRow, originalRow) = PlaybackQueueItem.FromKaraokePair(pair);
            vm.Queue.Add(karaokeRow);
            vm.Queue.Add(originalRow);

            await (Task)UiThread.Run(() => Invoke(vm, "StartQueueItemAsync", karaokeRow, false))!;

            Assert.True(karaokeRow.IsPlayed);
            Assert.True(originalRow.IsPlayed);
            Assert.Same(karaokeRow, vm.Queue[vm.CurrentIndex]);
            Assert.Equal("DECK: KARAOKE", vm.DeckA.Title);
            Assert.Equal("DECK: ORIGINAL", vm.DeckB.Title);

            // A request for the original row starts the pair, never deck B alone.
            await (Task)UiThread.Run(() => Invoke(vm, "StartQueueItemAsync", originalRow, false))!;
            Assert.Same(karaokeRow, vm.Queue[vm.CurrentIndex]);
        }
        finally
        {
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(karaoke);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static TrackEntry Track(string key, string? music, string? karaoke) => new()
    {
        RelativeKey = key,
        RelativePath = (music ?? karaoke)!,
        Title = "Song",
        Artist = "Artist",
        MusicPath = music,
        KaraokePath = karaoke,
    };

    private static object? Invoke(MainViewModel vm, string name, params object?[] args) =>
        typeof(MainViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, args.Length == 0 ? null : args);

    private static bool PairLoaded(MainViewModel vm) =>
        (bool)typeof(MainViewModel)
            .GetField("_karaokePairLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private static object? ReadField(MainViewModel vm, string name) =>
        typeof(MainViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm);
}
