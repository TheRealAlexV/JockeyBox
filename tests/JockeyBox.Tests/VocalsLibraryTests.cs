using System;
using System.IO;
using System.Linq;
using JockeyBox.Library;
using NAudio.Wave;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The vocals-only render ("&lt;Song&gt;.vocals.mp3") is reverse karaoke: it is
/// played on its own deck, so it must reach the list as an ordinary row and must
/// never become the <see cref="TrackEntry.KaraokePath"/> of its source. Attaching
/// it there would load the vocals on deck A of a karaoke pair, i.e. play the
/// vocals as if they were the instrumental (Library/DerivedVocals).
/// </summary>
public sealed class VocalsLibraryTests : IDisposable
{
    private const string SourceName = "Song.wav";
    private const string KaraokeName = "Song.karaoke.wav";
    private const string VocalsName = "Song.vocals.wav";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "jb-vocals-" + Guid.NewGuid().ToString("n"));

    private readonly TagCache _tags = new();

    public VocalsLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp folder */ }
    }

    // ---- requirement 1: the sidecar stays the .karaoke file ---------------

    [Fact]
    public void BothFilesPresent_KaraokeIsTheSidecar_AndVocalsNeverIs()
    {
        string music = WriteTrack(SourceName, "Fernando", "ABBA", "Arrival");
        string karaoke = WriteTrack(KaraokeName, "Fernando", "ABBA", "Arrival");
        WriteTrack(VocalsName, "Fernando", "ABBA", "Arrival");

        var rows = Scan();

        var pair = Assert.Single(rows, r => r.RelativeKey == "Song");
        Assert.Equal(music, pair.MusicPath);
        Assert.Equal(karaoke, pair.KaraokePath);

        // Nothing in the scan treats the vocals render as an instrumental.
        Assert.DoesNotContain(rows, r => IsVocals(r.KaraokePath));
        Assert.Equal(2, rows.Count);
    }

    // ---- requirement 2: its own row, titled and artist-tagged -------------

    [Fact]
    public void VocalsFile_BecomesItsOwnOrdinaryRow_NextToItsSource()
    {
        WriteTrack(SourceName, "Fernando", "ABBA", "Arrival");
        WriteTrack(KaraokeName, "Fernando", "ABBA", "Arrival");
        string vocals = WriteTrack(VocalsName, "Fernando", "ABBA", "Arrival");

        var rows = Scan();
        var row = Assert.Single(rows, r => r.MusicPath == vocals);

        Assert.Equal("Song.vocals", row.RelativeKey);          // its own identity
        Assert.Null(row.KaraokePath);                           // not a sidecar
        Assert.Equal("Fernando (Vocals)", row.Title);           // source title + suffix
        Assert.Equal("ABBA", row.Artist);                       // the source's artist
        Assert.Equal("Arrival", row.Album);                     // inherited like any track

        // Two rows: the pair (the music file plus its .karaoke sidecar) and the
        // vocals file as a row of its own.
        var keys = rows.Select(r => r.RelativePath).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Contains("Song.vocals.wav", keys);
        Assert.True(RelativeDistance(keys, "Song.wav", "Song.vocals.wav") <= 1,
                    $"vocals row not adjacent to its source: {string.Join(", ", keys)}");
    }

    // ---- requirement 5: a rescan is idempotent ----------------------------

    [Fact]
    public void Rescan_KeepsOneVocalsRow_AndNeverAttachesIt()
    {
        WriteTrack(SourceName, "Fernando", "ABBA", "Arrival");
        string karaoke = WriteTrack(KaraokeName, "Fernando", "ABBA", "Arrival");
        string vocals = WriteTrack(VocalsName, "Fernando", "ABBA", "Arrival");

        var first = Scan();
        var second = Scan();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(2, second.Count);
        Assert.Single(second, r => r.MusicPath == vocals);
        Assert.Equal("Fernando (Vocals)", Assert.Single(second, r => r.MusicPath == vocals).Title);
        Assert.Equal(karaoke, Assert.Single(second, r => r.RelativeKey == "Song").KaraokePath);
        Assert.DoesNotContain(second, r => IsVocals(r.KaraokePath));
    }

    // ---- requirement 3: no karaoke offer from a derived file --------------

    [Fact]
    public void VocalsRow_NeedsNoKaraoke_AndStaysOutOfTheNeededCount()
    {
        WriteTrack(SourceName, "Fernando", "ABBA", "Arrival");
        WriteTrack(KaraokeName, "Fernando", "ABBA", "Arrival");
        string vocals = WriteTrack(VocalsName, "Fernando", "ABBA", "Arrival");

        var rows = Scan();
        var row = Assert.Single(rows, r => r.MusicPath == vocals);

        Assert.False(row.NeedsKaraoke);
        Assert.Equal("", row.KaraokeStatus);
        Assert.Equal(0, rows.Count(r => r.NeedsKaraoke));
    }

    // ---- a legacy split library: the vocals root file is still a row ------

    [Fact]
    public void SplitLayout_VocalsUnderTheKaraokeRoot_IsARow_NotASidecar()
    {
        string musicRoot = Path.Combine(_root, "music");
        string karaokeRoot = Path.Combine(_root, "karaoke");
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(karaokeRoot);

        WriteTrack(Path.Combine("music", SourceName), "Fernando", "ABBA", "Arrival");
        WriteTrack(Path.Combine("karaoke", KaraokeName), "Fernando", "ABBA", "Arrival");
        string vocals = WriteTrack(Path.Combine("karaoke", VocalsName), "Fernando", "ABBA", "Arrival");

        var rows = Scan(musicRoot, karaokeRoot);

        var row = Assert.Single(rows, r => r.MusicPath == vocals);
        Assert.Null(row.KaraokePath);
        Assert.Equal("Fernando (Vocals)", row.Title);
        Assert.DoesNotContain(rows, r => IsVocals(r.KaraokePath));
    }

    // ---- fixtures ---------------------------------------------------------

    private List<TrackEntry> Scan(string? musicRoot = null, string? karaokeRoot = null)
    {
        musicRoot ??= _root;
        karaokeRoot ??= _root;
        return new LibraryScanner(_tags).Scan(musicRoot, karaokeRoot);
    }

    /// <summary>Write a silent WAV at the given relative path, tagged like a real track.</summary>
    private string WriteTrack(string relativePath, string title, string artist, string album)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        using (var writer = new WaveFileWriter(path, format))
            writer.WriteSamples(new float[4_800 * 2], 0, 4_800 * 2);

        using (var file = TagLib.File.Create(path))
        {
            var tag = file.GetTag(TagLib.TagTypes.Id3v2, true)!;
            tag.Title = title;
            tag.Performers = new[] { artist };
            tag.Album = album;
            file.Save();
        }

        return path;
    }

    private static bool IsVocals(string? path) =>
        path != null && path.EndsWith(".vocals.wav", StringComparison.OrdinalIgnoreCase);

    private static int RelativeDistance(List<string> keys, string a, string b)
    {
        int ia = keys.IndexOf(a);
        int ib = keys.IndexOf(b);
        return ia < 0 || ib < 0 ? int.MaxValue : Math.Abs(ia - ib);
    }
}
