using System;
using System.IO;
using System.Linq;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Separation;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The transport row's link and the deck card's cue bank, headless: no device, no
/// window. The link is driven through a real <see cref="MainViewModel"/> because
/// that is where the switches and the transport meet; the pads through a real deck
/// channel, because the media clock versus the audible clock is the whole point of
/// a cue.
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class DeckUiTests
{
    private sealed class Rig : IDisposable
    {
        private readonly DeviceService _devices = new();

        public Rig() => Transport = new DualDeckTransport(
            new DeckChannel("Deck A", _devices),
            new DeckChannel("Deck B", _devices));

        public DualDeckTransport Transport { get; }

        public DeckChannel A => Transport.DeckA;

        public DeckChannel B => Transport.DeckB;

        public AudioEngine Engine => Transport.Engine;

        /// <summary>
        /// The fake output path the other transport tests use: tell both channels an
        /// output exists (a command is refused as a no-op otherwise) and drain the
        /// render queues by hand, because no engine thread runs in the test host.
        /// </summary>
        public void Pump()
        {
            A.SetOutputAvailable(true);
            B.SetOutputAvailable(true);
            Engine.DeckA.Commands.Drain();
            Engine.DeckB.Commands.Drain();
        }

        public bool PumpUntil(Func<bool> until, int milliseconds = 30_000)
        {
            for (int i = 0; i < milliseconds / 5; i++)
            {
                Pump();
                if (until()) return true;
                Thread.Sleep(5);
            }

            Pump();
            return until();
        }

        public void Dispose() => Transport.Dispose();
    }

    /// <summary>A view model with the row's wiring in place, as the window does it.</summary>
    private static MainViewModel Linked()
        => UiThread.Run(() =>
        {
            var model = new MainViewModel();
            model.InitializeDeckUi();
            return model;
        });

    /// <summary>
    /// Tear a view model down the way the window's closing path does. It owns a
    /// transport, timers and stores, so a test that leaves one behind keeps a
    /// render thread and a device handle alive for the rest of the run.
    /// </summary>
    private static void Drop(MainViewModel vm) => UiThread.Run(() => vm.Dispose());

    [Fact]
    public void TestHost_NeverStartsTheAudioEngine()
    {
        // TestHome seeds a settings file with every bus empty and the "a choice
        // was saved" flag set, so BuildRoute returns a route with no output and
        // Engine.Start() is never reached. This is the guard for that: if the
        // suite ever opens a real WASAPI endpoint again, one test name says so
        // instead of the whole host dying somewhere in the middle of a run.
        var vm = Linked();
        try
        {
            // No device means Engine.Start() was never reached: no WASAPI endpoint
            // opened, no render thread spawned, nothing for a later run to leak.
            Assert.False(vm.Transport.Engine.IsRunning);
        }
        finally
        {
            Drop(vm);
        }
    }

    // ------------------------------------------------------------------ the link

    [Fact]
    public void ABMode_DrivesTheTransportTarget()
    {
        var vm = Linked();
        try
        {
            Assert.Equal(TransportTarget.Active, vm.Transport.Target);

            UiThread.Run(() => vm.IsABMode = true);
            Assert.Equal(TransportTarget.Both, vm.Transport.Target);
            Assert.True(vm.IsSyncAvailable);

            UiThread.Run(() => vm.IsABMode = false);
            Assert.Equal(TransportTarget.Active, vm.Transport.Target);
            Assert.False(vm.IsSyncAvailable);
        }
        finally
        {
            Drop(vm);
        }
    }

    [Fact]
    public void ABMode_FollowsTheTransportWhenALoadSelectsThePair()
    {
        var vm = Linked();
        try
        {
            // A karaoke load sets the target itself; the switch has to report it.
            UiThread.Run(() => vm.Transport.Target = TransportTarget.Both);
            Assert.True(vm.IsABMode);

            UiThread.Run(() => vm.Transport.Target = TransportTarget.Active);
            Assert.False(vm.IsABMode);
        }
        finally
        {
            Drop(vm);
        }
    }

    [Fact]
    public void ABMode_LeavesNeitherDeckSelectedOnItsOwn()
    {
        var vm = Linked();
        try
        {
            // Link off: exactly one deck carries the highlight, the one the
            // transport follows.
            UiThread.Run(() => vm.Transport.ActivateDeck(PlaybackDeck.A));
            Assert.True(vm.IsAOnlySelected);
            Assert.False(vm.IsBOnlySelected);
            Assert.False(vm.IsABMode);

            UiThread.Run(() => vm.Transport.ActivateDeck(PlaybackDeck.B));
            Assert.False(vm.IsAOnlySelected);
            Assert.True(vm.IsBOnlySelected);

            // Link on: neither deck is selected on its own, so the row cannot
            // show B highlighted while A-B also reads as pressed.
            UiThread.Run(() => vm.IsABMode = true);
            Assert.False(vm.IsAOnlySelected);
            Assert.False(vm.IsBOnlySelected);
            Assert.True(vm.IsABMode);

            // Dropping the link hands the highlight back to the deck the
            // transport kept as master, not to the one that was pressed last.
            UiThread.Run(() => vm.IsABMode = false);
            Assert.True(vm.IsAOnlySelected ^ vm.IsBOnlySelected);
            Assert.Equal(vm.IsDeckAActive, vm.IsAOnlySelected);
        }
        finally
        {
            Drop(vm);
        }
    }

    [Fact]
    public void ABMode_TellsTheRowWhenTheTransportSelectsThePair()
    {
        var vm = Linked();
        var raised = new List<string>();
        try
        {
            UiThread.Run(() => vm.Transport.ActivateDeck(PlaybackDeck.A));
            vm.PropertyChanged += (_, e) =>
            {
                lock (raised) raised.Add(e.PropertyName ?? "");
            };

            // A karaoke load flips the transport's target, not the switch.
            UiThread.Run(() => vm.Transport.Target = TransportTarget.Both);

            Assert.True(vm.IsABMode);
            lock (raised)
            {
                Assert.Contains(nameof(vm.IsAOnlySelected), raised);
                Assert.Contains(nameof(vm.IsBOnlySelected), raised);
            }
        }
        finally
        {
            Drop(vm);
        }
    }

    [Fact]
    public void LinkOff_TakesTheThreeSyncSwitchesWithIt()
    {
        var vm = Linked();
        try
        {
            UiThread.Run(() =>
            {
                vm.IsABMode = true;
                vm.IsVolumeSync = true;
                vm.IsTempoSync = true;
                vm.IsABMode = false;
            });

            Assert.False(vm.IsVolumeSync);
            Assert.False(vm.IsTempoSync);
            Assert.False(vm.IsDeckSync);
        }
        finally
        {
            Drop(vm);
        }
    }

    [Fact]
    public void PressingA_DropsTheLinkAndTakesDeckA()
    {
        var vm = Linked();
        try
        {
            UiThread.Run(() =>
            {
                vm.Transport.ActivateDeck(PlaybackDeck.B);
                vm.IsABMode = true;
                vm.SelectDeckACommand.Execute(null);
            });

            Assert.False(vm.IsABMode);
            Assert.Equal(TransportTarget.Active, vm.Transport.Target);
            Assert.Equal(PlaybackDeck.A, vm.Transport.ActiveDeck);
        }
        finally
        {
            Drop(vm);
        }
    }

    // ---------------------------------------------------------------- the guards

    [Fact]
    public void DeckMirror_RefusesTheEchoOfItsOwnWrite()
    {
        int writes = 0;
        DeckMirror? mirror = null;

        // Each setter calls straight back into the mirror, which is exactly what a
        // PropertyChanged echo does. Without the guard the first push would recurse.
        mirror = new DeckMirror(
            value => { writes++; mirror!.Push(0, value); },
            value => { writes++; mirror!.Push(1, value); });

        Assert.True(mirror.Push(0, 0.5));
        Assert.Equal(1, writes);
        Assert.Equal(1, mirror.Passes);
        Assert.False(mirror.IsMirroring);
    }

    [Fact]
    public void VolumeSync_MirrorsBothWaysAndStopsWhenSwitchedOff()
    {
        var vm = Linked();
        try
        {
            UiThread.Run(() =>
            {
                vm.IsABMode = true;
                vm.IsVolumeSync = true;
            });

            UiThread.Run(() => vm.DeckA.Volume = 0.25f);
            Assert.Equal(0.25, vm.DeckB.Volume, 3);

            UiThread.Run(() => vm.DeckB.Volume = 0.75f);
            Assert.Equal(0.75, vm.DeckA.Volume, 3);

            UiThread.Run(() => vm.IsVolumeSync = false);
            UiThread.Run(() => vm.DeckA.Volume = 0.4f);
            Assert.Equal(0.75, vm.DeckB.Volume, 3);
        }
        finally
        {
            Drop(vm);
        }
    }

    // ----------------------------------------------------------------- the cues

    [Fact]
    public void CueCapture_WritesTheMediaClock_NotTheDelayedAudibleClock()
    {
        string path = TestAudio.WriteRampWav(seconds: 20);
        try
        {
            using var rig = new Rig();
            rig.Transport.LoadOnDeck(PlaybackDeck.A, path, null);
            Assert.True(
                rig.PumpUntil(() => rig.A.Duration > TimeSpan.Zero),
                "deck opened the file");

            // 250 ms of output delay: the audible clock is what a cue must not use.
            rig.Engine.OutputDelayMs = 250;

            // Park the playhead at a known media position. No render thread runs in
            // the test host, so Seek posts a command and one Pump drains it: what
            // matters is that the audible clock is 250 ms behind the media clock
            // when the pad is pressed.
            rig.A.Seek(TimeSpan.FromSeconds(6));
            var buffer = new float[480 * 2];
            rig.Pump();
            rig.Engine.DeckA.Read(buffer, 0, buffer.Length);

            double media = rig.A.MediaPositionSeconds;
            double audible = rig.A.Position.TotalSeconds;
            Assert.True(media > 0.2, $"deck advanced (media {media:F3})");
            Assert.True(media - audible > 0.2,
                $"the clocks differ by the output delay (media {media:F3}, audible {audible:F3})");

            var bank = new CueBankViewModel(0,
                () => rig.A.MediaPositionSeconds,
                seconds => rig.A.Seek(TimeSpan.FromSeconds(seconds)),
                () => null);

            bank.PressCommand.Execute(bank.Slots[0]);

            Assert.True(bank.Slots[0].HasPosition);
            long stored = bank.Slots[0].PositionMs;
            Assert.InRange(stored, (long)(media * 1000) - 60, (long)(media * 1000) + 60);
            Assert.True(Math.Abs(stored / 1000.0 - audible) > 0.2,
                $"the cue did not land on the delay-corrected clock (stored {stored} ms, audible {audible:F3} s)");
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public void CuePads_RoundTripThroughTheRepository_AndTheQuickCueStaysInMemory()
    {
        using var db = new TestDatabase();
        var repo = new HotCueRepository(db.Database);

        // Cue rows point at tracks.id, so the track has to exist first.
        var trackPath = db.PathFor("music/song.mp3");
        long trackId = db.Tracks.Upsert(new TrackRecord
        {
            Path = trackPath,
            PathKey = LibraryKeys.PathKey(trackPath),
            Title = "song",
            DurationMs = 200_000,
        });
        Assert.True(trackId > 0);

        var bank = new CueBankViewModel(0, () => 12.5, _ => { }, () => repo);
        var other = new CueBankViewModel(1, () => 12.5, _ => { }, () => repo);

        // Nothing loaded yet: a press still sets the pad, it just cannot be saved.
        bank.PressCommand.Execute(bank.Slots[0]);
        Assert.Equal(12_500, bank.Slots[0].PositionMs);

        // Load a known track id and store slot 2 plus the quick cue.
        bank.SetTrackId(trackId);
        other.SetTrackId(trackId);
        bank.PressCommand.Execute(bank.Slots[1]);
        bank.PressCommand.Execute(bank.QuickCue);

        var saved = repo.Get(trackId, 0);
        Assert.Single(saved);
        Assert.Equal(2, saved[0].Slot);
        Assert.Equal(12_500, saved[0].PositionMs);

        // Deck 1 and the quick cue are separate: no row for either.
        Assert.Empty(repo.Get(trackId, 1));

        // Clearing a saved pad removes its row; the quick cue never had one.
        bank.IsClearing = true;
        bank.PressCommand.Execute(bank.Slots[1]);
        Assert.False(bank.Slots[1].HasPosition);
        Assert.Empty(repo.Get(trackId, 0));

        bank.IsClearing = false;
        // The quick cue is cleared the same way, but it never had a row.
        bank.IsClearing = true;
        bank.PressCommand.Execute(bank.QuickCue);
        Assert.False(bank.QuickCue.HasPosition);
        Assert.Empty(repo.Get(trackId, 0));
    }

    [Fact]
    public void CuePads_JumpToTheStoredPosition()
    {
        double sought = -1;
        var bank = new CueBankViewModel(1, () => 7.25, seconds => sought = seconds, () => null);

        bank.PressCommand.Execute(bank.Slots[3]);   // empty: stores the current position
        Assert.Equal(7_250, bank.Slots[3].PositionMs);
        Assert.Equal(-1, sought);

        bank.PressCommand.Execute(bank.Slots[3]);   // occupied: jumps there
        Assert.Equal(7.25, sought, 3);
    }

    // -------------------------------------------------------------- the variants

    [Fact]
    public void Versions_ListExactlyWhatIsOnDisk()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        try
        {
            string Song(string name) => System.IO.Path.Combine(folder, name);

            var all = Song("Song.mp3");
            File.WriteAllBytes(all, Array.Empty<byte>());
            File.WriteAllBytes(Song("Song.karaoke.mp3"), Array.Empty<byte>());
            File.WriteAllBytes(Song("Song.backing.mp3"), Array.Empty<byte>());
            File.WriteAllBytes(Song("Song.vocals.mp3"), Array.Empty<byte>());

            var options = DeckViewModel.BuildVersions(all);

            // Every render on disk gets its own entry. This is the case that used
            // to collapse: validating Karaoke and Backing against one shared
            // partner path meant only one of them could match, so the other was
            // dropped and the deck's menu went dead.
            Assert.Equal(new[] { "Original", "Karaoke", "Keep backing", "Vocals" },
                options.Where(o => o.IsAvailable).Select(o => o.Label).ToArray());
            Assert.Equal(4, options.Count);

            // Each entry carries the file it plays, and the two instrumentals are
            // two different files.
            var karaokeEntry = options.Single(o => o.Variant == AudioVariant.Karaoke);
            var backingEntry = options.Single(o => o.Variant == AudioVariant.Backing);
            Assert.Equal(Song("Song.karaoke.mp3"), karaokeEntry.Path);
            Assert.Equal(Song("Song.backing.mp3"), backingEntry.Path);
            Assert.NotEqual(karaokeEntry.Path, backingEntry.Path);

            // Keep-backing only, and one version is not a choice.
            var backingOnly = Song("Backing.mp3");
            File.WriteAllBytes(backingOnly, Array.Empty<byte>());
            File.WriteAllBytes(Song("Backing.backing.mp3"), Array.Empty<byte>());

            var backing = DeckViewModel.BuildVersions(backingOnly);
            Assert.Equal(new[] { "Original", "Keep backing" },
                backing.Where(o => o.IsAvailable).Select(o => o.Label).ToArray());

            // Nothing beside the original: one entry, and no second thing to pick,
            // which is what makes the deck's menu read as disabled.
            var alone = Song("Alone.mp3");
            File.WriteAllBytes(alone, Array.Empty<byte>());
            var single = DeckViewModel.BuildVersions(alone);
            var only = Assert.Single(single);
            Assert.Equal("Original", only.Label);
            Assert.True(only.IsAvailable);

            // A path with no file at all cannot even offer the original: the entry
            // is still there so the menu has a header, unavailable and with a reason.
            var missing = DeckViewModel.BuildVersions(Song("Missing.mp3"));
            var none = Assert.Single(missing);
            Assert.False(none.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(none.Reason));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The switcher on a real deck: every render on disk is listed and every row is
    /// reachable (its command is wired), which is a different claim from the list
    /// above - a menu whose rows carry no command renders perfectly and switches
    /// nothing.
    /// </summary>
    [Fact]
    public void VersionSwitcher_OnALoadedDeck_ListsAllFourRowsAndWiresEachOne()
    {
        string music = TestAudio.WriteConstantWav(0.2f);
        string folder = Path.GetDirectoryName(music)!;
        string stem = Path.GetFileNameWithoutExtension(music);
        // The render names the separation pipeline writes, so the deck sees the
        // four files a fully rendered track has.
        string karaoke = Path.Combine(folder, KaraokeNaming.RenderName(KaraokeStemMode.Instrumental, stem));
        string backing = Path.Combine(folder, KaraokeNaming.RenderName(KaraokeStemMode.InstrumentalKeepBacking, stem));
        string vocals = Path.Combine(folder, KaraokeNaming.RenderName(KaraokeStemMode.VocalsOnly, stem));
        File.Copy(music, karaoke, overwrite: true);
        File.Copy(music, backing, overwrite: true);
        File.Copy(music, vocals, overwrite: true);

        try
        {
            var vm = UiThread.Run(() => new MainViewModel());
            UiThread.Run(() => vm.DeckA.SetTrack(music, null, "Song", ""));

            Assert.Equal(4, vm.DeckA.Versions.Count);
            Assert.Equal(
                new[] { "Original", "Karaoke", "Keep backing", "Vocals" },
                vm.DeckA.Versions.Select(v => v.Label).ToArray());
            Assert.All(vm.DeckA.Versions, v => Assert.NotNull(v.SelectCommand));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(music)!, recursive: true); } catch { }
        }
    }

}
