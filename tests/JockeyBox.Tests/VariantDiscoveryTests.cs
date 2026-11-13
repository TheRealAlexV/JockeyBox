using System.IO;
using JockeyBox.Library;

namespace JockeyBox.Tests;

/// <summary>
/// <see cref="KaraokeNaming.DiscoverVariants"/>: the submenu is built from what is
/// on disk, so every subset of the four takes must report exactly itself.
/// </summary>
public sealed class VariantDiscoveryTests
{
    [Fact]
    public void Original_only()
    {
        using var files = new VariantFolder();
        var source = files.Create("Song.mp3");

        Assert.Equal(new[] { AudioVariant.Original }, KaraokeNaming.DiscoverVariants(source));
    }

    [Fact]
    public void Original_and_a_legacy_karaoke_render()
    {
        using var files = new VariantFolder();
        var source = files.Create("Song.mp3");
        files.Create("Song.karaoke.mp3");   // pre-split instrumental, still valid

        Assert.Equal(
            new[] { AudioVariant.Original, AudioVariant.Karaoke },
            KaraokeNaming.DiscoverVariants(source));
    }

    [Fact]
    public void Plain_instrumental_and_keep_backing_are_listed_separately()
    {
        using var files = new VariantFolder();
        var source = files.Create("Song.flac");
        files.Create("Song.karaoke.mp3");
        files.Create("Song.backing.mp3");

        Assert.Equal(
            new[] { AudioVariant.Original, AudioVariant.Karaoke, AudioVariant.Backing },
            KaraokeNaming.DiscoverVariants(source));
    }

    [Fact]
    public void All_four_takes_are_reported_in_a_stable_order()
    {
        using var files = new VariantFolder();
        var source = files.Create("Song.mp3");
        files.Create("Song.karaoke.mp3");
        files.Create("Song.backing.mp3");
        files.Create("Song.vocals.mp3");

        Assert.Equal(
            new[] { AudioVariant.Original, AudioVariant.Karaoke, AudioVariant.Backing, AudioVariant.Vocals },
            KaraokeNaming.DiscoverVariants(source));
    }

    [Fact]
    public void Backing_and_vocals_without_an_instrumental_still_report_themselves()
    {
        using var files = new VariantFolder();
        var source = files.Create("Song.mp3");
        files.Create("Song.backing.flac");
        files.Create("Song.vocals.mp3");

        Assert.Equal(
            new[] { AudioVariant.Original, AudioVariant.Backing, AudioVariant.Vocals },
            KaraokeNaming.DiscoverVariants(source));
    }

    [Fact]
    public void A_missing_original_is_not_listed()
    {
        using var files = new VariantFolder();
        var source = Path.Combine(files.Directory, "Song.mp3");
        files.Create("Song.karaoke.mp3");

        Assert.Equal(new[] { AudioVariant.Karaoke }, KaraokeNaming.DiscoverVariants(source));
    }

    [Fact]
    public void A_track_with_nothing_on_disk_reports_nothing()
    {
        using var files = new VariantFolder();

        Assert.Empty(KaraokeNaming.DiscoverVariants(Path.Combine(files.Directory, "Song.mp3")));
        Assert.Empty(KaraokeNaming.DiscoverVariants(""));
        Assert.Empty(KaraokeNaming.DiscoverVariants(null));
    }

    /// <summary>A temp folder that is deleted on dispose, with the files a test needs.</summary>
    private sealed class VariantFolder : IDisposable
    {
        public VariantFolder()
        {
            Directory = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public string Create(string fileName)
        {
            var path = Path.Combine(Directory, fileName);
            File.WriteAllBytes(path, Array.Empty<byte>());
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch { /* temp cleanup must never fail a test */ }
        }
    }
}
