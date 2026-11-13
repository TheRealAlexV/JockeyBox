using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Switching among the three resident takes (primary, karaoke variant, vocals-only)
/// without a re-point: a scheduled gain ramp over readers that stay open, plus the
/// raw media clock a cue system must read. No device and no engine loop.
/// </summary>
public sealed class StemSwitchTests
{
    private const int Rate = 48_000;
    private const int Frames = 480;
    private const double BlockSeconds = Frames / (double)Rate;

    /// <summary>Peak of the last of <paramref name="blocks"/> rendered blocks.</summary>
    private static float RenderPeak(DeckSource deck, int blocks = 12)
    {
        var buffer = new float[Frames * 2];
        for (int i = 0; i < blocks; i++) deck.Read(buffer, 0, buffer.Length);
        return Samples.Peak(buffer, 0, buffer.Length);
    }

    [Fact]
    public async Task Switching_takes_never_moves_the_media_clock_more_than_one_block()
    {
        // Three different takes, so a switch that seeked or re-pointed a reader would
        // show up as a jump in the clock as well as a click.
        string primary = TestAudio.WriteConstantWav(0.5f);
        string karaoke = TestAudio.WriteConstantWav(0.25f);
        string vocals = TestAudio.WriteConstantWav(0.125f);
        try
        {
            using var deck = new DeckSource(Rate, 2);
            deck.SetKaraokeGain(1f);
            await deck.LoadAsync(primary, karaoke, vocalsPath: vocals);

            var buffer = new float[Frames * 2];
            double previous = deck.MediaPositionSeconds;
            double maxStep = 0;

            void ReadBlocks(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    deck.Read(buffer, 0, buffer.Length);
                    double now = deck.MediaPositionSeconds;
                    maxStep = Math.Max(maxStep, now - previous);
                    previous = now;
                }
            }

            ReadBlocks(10);

            // Every switch happens at the same absolute frame here, because none of
            // them consumes or rewinds a frame: that is the whole claim.
            deck.SetStemMode(StemKind.Vocals, 250);
            ReadBlocks(40);
            deck.SetStemMode(StemKind.Karaoke, 250);
            ReadBlocks(40);
            deck.SetStemMode(StemKind.Original, 250);
            ReadBlocks(40);

            // No block may advance the clock by more than one block, and 130 blocks of
            // output must be 130 blocks of media: no seek, no rewind, no double count.
            Assert.True(maxStep <= BlockSeconds + 1e-6,
                $"media clock jumped {maxStep:F6}s in one block (block is {BlockSeconds:F6}s)");

            double expected = 130 * BlockSeconds;
            Assert.InRange(deck.MediaPositionSeconds, expected - BlockSeconds, expected + BlockSeconds);
        }
        finally
        {
            TestAudio.TryDelete(primary);
            TestAudio.TryDelete(karaoke);
            TestAudio.TryDelete(vocals);
        }
    }

    [Fact]
    public async Task Stem_weights_ramp_across_blocks_instead_of_stepping()
    {
        string primary = TestAudio.WriteConstantWav(0.5f);
        string karaoke = TestAudio.WriteConstantWav(0.25f);
        string vocals = TestAudio.WriteConstantWav(0.125f);
        try
        {
            using var deck = new DeckSource(Rate, 2);
            deck.SetKaraokeGain(1f);
            await deck.LoadAsync(primary, karaoke, vocalsPath: vocals);

            var buffer = new float[Frames * 2];
            for (int i = 0; i < 5; i++) deck.Read(buffer, 0, buffer.Length);

            // 200 ms at 48 kHz is 9600 frames: 20 blocks of ramp.
            deck.SetStemMode(StemKind.Vocals, 200);

            var vocalsWeight = new List<float>();
            for (int i = 0; i < 26; i++)
            {
                deck.Read(buffer, 0, buffer.Length);
                vocalsWeight.Add(deck.StemWeights.Vocals);
            }

            Assert.True(vocalsWeight[0] < 0.05f, $"ramp started at {vocalsWeight[0]:F3}, not at 0");
            Assert.Contains(vocalsWeight, w => w > 0.05f && w < 0.95f);

            for (int i = 1; i < vocalsWeight.Count; i++)
            {
                Assert.True(vocalsWeight[i] >= vocalsWeight[i - 1] - 1e-6f,
                    $"weight fell at block {i}: {vocalsWeight[i - 1]:F4} -> {vocalsWeight[i]:F4}");
            }

            Assert.True(vocalsWeight[^1] > 0.99f,
                $"ramp did not finish inside 260 ms, last weight {vocalsWeight[^1]:F3}");
        }
        finally
        {
            TestAudio.TryDelete(primary);
            TestAudio.TryDelete(karaoke);
            TestAudio.TryDelete(vocals);
        }
    }

    [Fact]
    public async Task Three_readers_sum_only_where_the_mode_gain_is_non_zero()
    {
        string primary = TestAudio.WriteConstantWav(0.5f);
        string karaoke = TestAudio.WriteConstantWav(0.25f);
        string vocals = TestAudio.WriteConstantWav(0.125f);
        try
        {
            using var deck = new DeckSource(Rate, 2);
            deck.SetKaraokeGain(1f);
            deck.SetVocalsGain(1f);
            await deck.LoadAsync(primary, karaoke, vocalsPath: vocals);

            deck.SetStemMode(StemKind.Original, 0);
            Assert.Equal(0.5f, RenderPeak(deck), 2);

            deck.SetStemMode(StemKind.Vocals, 0);
            Assert.Equal(0.125f, RenderPeak(deck), 3);

            deck.SetStemMode(StemKind.Karaoke, 0);
            Assert.Equal(0.25f, RenderPeak(deck), 3);

            // Mix sums primary plus karaoke at its own gain (vocals stay silent),
            // then normalises by the gains present so two takes sum to unity
            // instead of 1.5: (0.5 + 0.25) / (1 + 1) = 0.375. The 1:1 balance
            // between the takes is unchanged; only the absolute level is.
            deck.SetStemMode(StemKind.Mix, 0);
            Assert.Equal(0.375f, RenderPeak(deck), 3);
        }
        finally
        {
            TestAudio.TryDelete(primary);
            TestAudio.TryDelete(karaoke);
            TestAudio.TryDelete(vocals);
        }
    }

    [Fact]
    public async Task Media_position_ignores_the_main_bus_output_delay()
    {
        string primary = TestAudio.WriteConstantWav(0.5f);
        try
        {
            var engine = new AudioEngine();
            engine.OutputDelayMs = 120;
            await engine.DeckA.LoadAsync(primary);

            var buffer = new float[Frames * 2];
            for (int i = 0; i < 20; i++) engine.DeckA.Read(buffer, 0, buffer.Length);

            double media = engine.DeckA.MediaPositionSeconds;
            double audible = engine.AudibleSeconds(engine.DeckA);

            // At 1.0x the stretcher's group delay is zero, so the whole gap is the
            // configured output delay: the raw clock does not subtract any of it.
            Assert.InRange(media - audible, 0.119, 0.121);

            // And the audible readout still subtracts it, unchanged by this work.
            Assert.InRange(engine.DeckA.PositionSeconds - audible, 0.119, 0.121);
        }
        finally
        {
            TestAudio.TryDelete(primary);
        }
    }

    [Fact]
    public async Task Three_readers_per_deck_leave_headroom_in_the_block_budget()
    {
        // The promise is that the clock holds under load, so the cost of six
        // resident readers (three per deck) has to stay well inside a block. This
        // measures the steady-state render cost only: no device, no disk.
        string primary = TestAudio.WriteConstantWav(0.5f, seconds: 20);
        string karaoke = TestAudio.WriteConstantWav(0.25f, seconds: 20);
        string vocals = TestAudio.WriteConstantWav(0.125f, seconds: 20);
        try
        {
            var engine = new AudioEngine();
            var a = new float[Frames * 2];
            var b = new float[Frames * 2];
            const int blocks = 4000;

            double Measure()
            {
                engine.DeckA.SetKaraokeGain(0.5f);
                engine.DeckA.SetVocalsGain(1f);
                engine.DeckB.SetKaraokeGain(0.5f);
                engine.DeckB.SetVocalsGain(1f);
                engine.DeckA.SetStemMode(StemKind.Mix, 0);
                engine.DeckB.SetStemMode(StemKind.Mix, 0);

                for (int i = 0; i < 200; i++) { engine.DeckA.Read(a, 0, a.Length); engine.DeckB.Read(b, 0, b.Length); }

                var clock = Stopwatch.StartNew();
                for (int i = 0; i < blocks; i++)
                {
                    engine.DeckA.Read(a, 0, a.Length);
                    engine.DeckB.Read(b, 0, b.Length);
                }
                clock.Stop();
                return clock.Elapsed.TotalMilliseconds / blocks;
            }

            // Today's deck: primary plus one karaoke sub-stem.
            await engine.DeckA.LoadAsync(primary, karaoke);
            await engine.DeckB.LoadAsync(primary, karaoke);
            double twoReadersMs = Measure();

            // The design: a third resident reader on each deck.
            await engine.DeckA.LoadAsync(primary, karaoke, vocalsPath: vocals);
            await engine.DeckB.LoadAsync(primary, karaoke, vocalsPath: vocals);
            double threeReadersMs = Measure();

            double perBlockMs = threeReadersMs;
            double realtimeShare = perBlockMs / (BlockSeconds * 1000.0);

            // The whole render budget for two decks is one block; keep the decks' own
            // readers to a quarter of it so the bus, mics and soundboard still fit.
            Assert.True(perBlockMs < BlockSeconds * 1000.0 * 0.25,
                $"two decks x three readers cost {perBlockMs:F3} ms/block " +
                $"({realtimeShare:P0} of realtime); budget is {BlockSeconds * 250:F2} ms");

            // Publish the numbers where the harness log can pick them up.
            try
            {
                Directory.CreateDirectory(@"C:\Dev\shots");
                File.WriteAllText(@"C:\Dev\shots\jb-stem-cpu.txt",
                    $"two decks, two readers each:   {twoReadersMs:F4} ms/block\n" +
                    $"two decks, three readers each: {threeReadersMs:F4} ms/block " +
                    $"({realtimeShare:P2} of realtime, {blocks} blocks)\n");
            }
            catch (Exception) { }
        }
        finally
        {
            TestAudio.TryDelete(primary);
            TestAudio.TryDelete(karaoke);
            TestAudio.TryDelete(vocals);
        }
    }
}
