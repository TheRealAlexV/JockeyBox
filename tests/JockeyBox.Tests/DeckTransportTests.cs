using System;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Playback;
using Xunit;
using Xunit.Abstractions;

namespace JockeyBox.Tests;

/// <summary>
/// The A-B link and deck sync on <see cref="DualDeckTransport"/>: one command
/// driving both decks, an absolute play/pause toggle for the pair, the karaoke
/// load selecting the pair by itself, and the slave following the master's
/// media clock.
///
/// <para>A real engine render thread is what normally drains each deck's command
/// queue, and its absence is exactly what makes these assertions readable: the
/// rig drains the queues by hand (one drain = one render block's worth of
/// commands), so state changes land when the test asks for them instead of at
/// the mercy of an unpaced offline loop that would consume a whole file in
/// microseconds. No device is opened; the rig tells the channels an output
/// exists, which is the only thing a transport command waits for.</para>
/// </summary>
public sealed class DeckTransportTests
{
    /// <summary>One render block at 48 kHz, in seconds: the deck pair's sync budget.</summary>
    private const double BlockSeconds = 480.0 / 48_000.0;

    private readonly ITestOutputHelper _output;

    public DeckTransportTests(ITestOutputHelper output) => _output = output;

    private sealed class Rig : IDisposable
    {
        private readonly DeviceService _devices = new();

        public Rig()
        {
            Transport = new DualDeckTransport(
                new DeckChannel("Deck A", _devices),
                new DeckChannel("Deck B", _devices));
        }

        public DualDeckTransport Transport { get; }

        public DeckChannel A => Transport.DeckA;

        public DeckChannel B => Transport.DeckB;

        /// <summary>
        /// Tell both channels an output exists (otherwise every transport command
        /// is refused as a no-op) and run each deck's queued render commands, the
        /// way the engine's render thread would at the top of a block.
        /// </summary>
        public void Pump()
        {
            A.SetOutputAvailable(true);
            B.SetOutputAvailable(true);
            Transport.Engine.DeckA.Commands.Drain();
            Transport.Engine.DeckB.Commands.Drain();
        }

        /// <summary>
        /// Pump until <paramref name="until"/> holds. The cap is generous on
        /// purpose: opening the engine's route and decoding a file compete with
        /// the rest of the suite for the machine, and a tight cap turns a slow
        /// machine into a false failure.
        /// </summary>
        public bool PumpUntil(Func<bool> until, int milliseconds = 60_000)
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

        public void LoadBoth(string path, string? karaoke = null)
        {
            Transport.LoadOnDeck(PlaybackDeck.A, path, karaoke);
            Transport.LoadOnDeck(PlaybackDeck.B, path, karaoke);
            Assert.True(WaitLoaded(A), "deck A loaded");
            Assert.True(WaitLoaded(B), "deck B loaded");
        }

        /// <summary>Both decks in <paramref name="state"/>.</summary>
        public bool IsPair(TransportState state) =>
            A.State == state && B.State == state;

        /// <summary>
        /// Wait until the deck has really opened its file. <see cref="DeckChannel.IsLoaded"/>
        /// flips as soon as the path is accepted; the duration is only published
        /// once the decode has finished, which is the point a command can play.
        /// </summary>
        public bool WaitLoaded(DeckChannel deck) =>
            PumpUntil(() => deck.IsLoaded && deck.Duration > TimeSpan.Zero);

        public void Dispose() => Transport.Dispose();
    }

    [Fact]
    public void Both_target_plays_and_pauses_every_loaded_deck()
    {
        string path = TestAudio.WriteRampWav(seconds: 20);
        try
        {
            using var rig = new Rig();
            rig.LoadBoth(path);

            rig.Transport.Target = TransportTarget.Both;

            rig.Transport.Play();
            Assert.True(rig.PumpUntil(() => rig.IsPair(TransportState.Playing)),
                $"play in Both left A={rig.A.State} B={rig.B.State}");

            rig.Transport.Pause();
            rig.Pump();
            Assert.Equal(TransportState.Paused, rig.A.State);
            Assert.Equal(TransportState.Paused, rig.B.State);

            rig.Transport.Stop();
            rig.Pump();
            Assert.Equal(TransportState.Stopped, rig.A.State);
            Assert.Equal(TransportState.Stopped, rig.B.State);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public void Toggle_play_pause_in_both_is_absolute_not_per_deck()
    {
        string path = TestAudio.WriteRampWav(seconds: 20);
        try
        {
            using var rig = new Rig();
            rig.LoadBoth(path);

            rig.Transport.Target = TransportTarget.Both;

            // A rolling, B cued: a per-deck flip would pause A and play B.
            rig.Transport.PlayDeck(PlaybackDeck.A);
            Assert.True(rig.PumpUntil(() => rig.A.State == TransportState.Playing),
                $"deck A did not start: {rig.A.State}");
            Assert.NotEqual(TransportState.Playing, rig.B.State);

            rig.Transport.TogglePlayPause();
            Assert.True(rig.PumpUntil(() => rig.IsPair(TransportState.Playing)),
                $"toggle with A rolling left A={rig.A.State} B={rig.B.State}");

            // Both rolling: the toggle takes the pair down, not one of them.
            rig.Transport.TogglePlayPause();
            Assert.True(rig.PumpUntil(() => rig.IsPair(TransportState.Paused)),
                $"toggle with the pair rolling left A={rig.A.State} B={rig.B.State}");

            // Both parked: the toggle starts the pair.
            rig.Transport.TogglePlayPause();
            Assert.True(rig.PumpUntil(() => rig.IsPair(TransportState.Playing)),
                $"toggle with the pair parked left A={rig.A.State} B={rig.B.State}");
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public void Load_on_deck_selects_both_only_for_a_karaoke_pair()
    {
        string primary = TestAudio.WriteRampWav(seconds: 5);
        string karaoke = TestAudio.WriteRampWav(seconds: 5, frequency: 5);
        try
        {
            using var rig = new Rig();

            rig.Transport.LoadOnDeck(PlaybackDeck.A, primary, karaoke);
            Assert.Equal(TransportTarget.Both, rig.Transport.Target);

            // An ordinary load is "this track, on this deck": back to Active.
            rig.Transport.LoadOnDeck(PlaybackDeck.B, primary, null);
            Assert.Equal(TransportTarget.Active, rig.Transport.Target);

            Assert.True(rig.WaitLoaded(rig.A), "deck A loaded");
            Assert.True(rig.WaitLoaded(rig.B), "deck B loaded");

            // The fields are independent: nothing above touched the active deck.
            Assert.Equal(PlaybackDeck.A, rig.Transport.ActiveDeck);
        }
        finally
        {
            TestAudio.TryDelete(primary);
            TestAudio.TryDelete(karaoke);
        }
    }

    [Fact]
    public void Engaging_sync_aligns_the_slave_and_master_seeks_carry_it()
    {
        string path = TestAudio.WriteRampWav(seconds: 20);
        try
        {
            using var rig = new Rig();
            rig.LoadBoth(path);

            rig.A.Seek(TimeSpan.FromSeconds(4));
            rig.Pump();
            Assert.True(Math.Abs(rig.A.MediaPositionSeconds - 4.0) < BlockSeconds,
                $"master seek landed at {rig.A.MediaPositionSeconds:F3}s");

            rig.Transport.SyncActive = true;
            rig.Pump();

            double offset = Math.Abs(rig.A.MediaPositionSeconds - rig.B.MediaPositionSeconds);
            _output.WriteLine($"slave offset after engage: {offset * 1000:F2} ms (one block = {BlockSeconds * 1000:F2} ms)");
            Assert.True(offset <= BlockSeconds, $"slave offset after engage: {offset * 1000:F1} ms");

            // A seek on the master after engaging drags the slave with it, by the
            // same amount, still within a block.
            rig.Transport.Target = TransportTarget.Active;
            rig.Transport.Seek(TimeSpan.FromSeconds(9));
            rig.Pump();

            Assert.True(Math.Abs(rig.A.MediaPositionSeconds - 9.0) < BlockSeconds,
                $"master seek landed at {rig.A.MediaPositionSeconds:F3}s");

            offset = Math.Abs(rig.A.MediaPositionSeconds - rig.B.MediaPositionSeconds);
            _output.WriteLine($"slave offset after a master seek: {offset * 1000:F2} ms");
            Assert.True(offset <= BlockSeconds, $"slave offset after a master seek: {offset * 1000:F1} ms");
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public void Shorter_slave_clamps_at_its_end_instead_of_looping()
    {
        string master = TestAudio.WriteRampWav(seconds: 20);
        string shortSlave = TestAudio.WriteRampWav(seconds: 2, frequency: 5);
        try
        {
            using var rig = new Rig();

            rig.Transport.LoadOnDeck(PlaybackDeck.A, master, null);
            rig.Transport.LoadOnDeck(PlaybackDeck.B, shortSlave, null);
            Assert.True(rig.WaitLoaded(rig.A), "deck A loaded");
            Assert.True(rig.WaitLoaded(rig.B), "deck B loaded");

            double slaveDuration = rig.B.Duration.TotalSeconds;
            Assert.True(slaveDuration is > 1.9 and < 2.1, $"slave duration {slaveDuration:F2}s");

            rig.Transport.SyncActive = true;
            rig.Pump();

            // The master runs well past the slave's end.
            rig.Transport.Seek(TimeSpan.FromSeconds(12));
            rig.Pump();

            Assert.True(Math.Abs(rig.A.MediaPositionSeconds - 12.0) < BlockSeconds);

            // Parked at its own end, silent - not wrapped back to the head.
            Assert.True(rig.B.MediaPositionSeconds >= slaveDuration - 2 * BlockSeconds,
                $"slave at {rig.B.MediaPositionSeconds:F3}s of {slaveDuration:F3}s");
            Assert.True(rig.B.MediaPositionSeconds <= slaveDuration,
                $"slave ran past its end: {rig.B.MediaPositionSeconds:F3}s");
        }
        finally
        {
            TestAudio.TryDelete(master);
            TestAudio.TryDelete(shortSlave);
        }
    }

    [Fact]
    public void Fan_out_with_an_empty_second_deck_never_throws()
    {
        string path = TestAudio.WriteRampWav(seconds: 20);
        try
        {
            using var rig = new Rig();

            rig.Transport.LoadOnDeck(PlaybackDeck.A, path, null);
            Assert.True(rig.WaitLoaded(rig.A), "deck A loaded");
            Assert.False(rig.B.IsLoaded);

            rig.Transport.Target = TransportTarget.Both;

            // Every fan-out path, against a deck with nothing on it.
            rig.Transport.Play();
            rig.Pump();
            rig.Transport.TogglePlayPause();
            rig.Pump();
            rig.Transport.Seek(TimeSpan.FromSeconds(3));
            rig.Pump();
            rig.Transport.SkipForward(5);
            rig.Pump();
            rig.Transport.SkipBack(2);
            rig.Pump();
            rig.Transport.SetTargetVolume(0.8);
            rig.Transport.SetTargetTempo(1.1);
            rig.Transport.SyncActive = true;
            rig.Transport.SyncActive = false;
            rig.Transport.Pause();
            rig.Pump();
            rig.Transport.Stop();
            rig.Pump();

            // The loaded deck still took its commands; the empty one stayed empty.
            Assert.True(rig.A.IsLoaded);
            Assert.False(rig.B.IsLoaded);
            Assert.Null(rig.B.Error);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }
}
