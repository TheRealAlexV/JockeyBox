using System;
using System.Threading.Tasks;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Karaoke split rendering in <see cref="DeckSource"/>: two stretchers with the
/// same ratio and phase, one playhead. These tests read the deck directly, with no
/// device and no engine loop.
/// </summary>
public sealed class DeckSplitTests
{
    private const int Rate = 48_000;
    private const int Frames = 480;

    [Fact]
    public async Task Split_stems_stay_sample_aligned_through_a_tempo_change()
    {
        // Identical content in both stems: any divergence in segment choice, ratio
        // or phase between the two stretchers shows up as a sample difference.
        string path = TestAudio.WriteRampWav();
        try
        {
            using var deck = new DeckSource(Rate, 2);
            await deck.LoadAsync(path, path);

            deck.SetTempo(1.30);
            deck.KaraokeSplit = true;

            var karaoke = new float[Frames * 2];
            var original = new float[Frames * 2];

            for (int block = 0; block < 20; block++)
            {
                deck.ReadKaraoke(karaoke, 0, karaoke.Length);
                deck.ReadOriginal(original, 0, original.Length);

                if (block == 10) deck.SetTempo(0.80); // mid-stream tempo change

                for (int i = 0; i < karaoke.Length; i++)
                    Assert.Equal(karaoke[i], original[i], 5);
            }

            Assert.True(deck.IsSplitting);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public async Task Split_clock_advances_once_per_block()
    {
        string path = TestAudio.WriteRampWav();
        try
        {
            using var deck = new DeckSource(Rate, 2);
            await deck.LoadAsync(path, path);
            deck.SetTempo(1.25);
            deck.KaraokeSplit = true;

            var karaoke = new float[Frames * 2];
            var original = new float[Frames * 2];

            // First pair also drains the load and enters split mode.
            deck.ReadKaraoke(karaoke, 0, karaoke.Length);
            deck.ReadOriginal(original, 0, original.Length);

            double before = deck.Position.SourceSeconds;
            const int blocks = 20;
            for (int i = 0; i < blocks; i++)
            {
                deck.ReadKaraoke(karaoke, 0, karaoke.Length);
                deck.ReadOriginal(original, 0, original.Length);
            }

            // SetTempo(1.25) means 25% faster: 1.25 source seconds per output second.
            double expected = blocks * (double)Frames / Rate * 1.25;
            double actual = deck.Position.SourceSeconds - before;
            Assert.InRange(actual, expected * 0.98, expected * 1.02);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }

    [Fact]
    public async Task Mix_path_is_untouched_when_karaoke_split_is_off()
    {
        string path = TestAudio.WriteConstantWav(0.25f);
        try
        {
            using var deck = new DeckSource(Rate, 2);
            await deck.LoadAsync(path);
            deck.SetTempo(1.25);

            var buffer = new float[Frames * 2];
            deck.Read(buffer, 0, buffer.Length);

            Assert.False(deck.IsSplitting);
            Assert.Equal(0.25f, Samples.Peak(buffer, 0, buffer.Length), 3);
        }
        finally
        {
            TestAudio.TryDelete(path);
        }
    }
}
