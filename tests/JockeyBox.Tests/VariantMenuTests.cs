using System;
using System.IO;
using System.Linq;
using JockeyBox.Library;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Which takes a track has, and what the Play / Add to Queue submenus make of
/// them. The both-renders case is the one that used to collapse: the entries were
/// validated against a single partner path, so the second instrumental could never
/// match and the deck's switcher had one item.
///
/// <para>No deck and no window: the enumeration is disk plus the model, and the
/// item composition is a static function, so both are checkable on their own.</para>
/// </summary>
public sealed class VariantMenuTests : IDisposable
{
    private readonly string _folder;

    public VariantMenuTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "jb-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Song(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private TrackEntry Track(string music) => new()
    {
        RelativeKey = "a/song",
        RelativePath = music,
        Title = "Song",
        Artist = "Artist",
        MusicPath = music,
    };

    [Fact]
    public void EveryTakeOnDisk_HasItsOwnPath()
    {
        var source = Song("Song.mp3");
        Song("Song.karaoke.mp3");
        Song("Song.backing.mp3");
        Song("Song.vocals.mp3");

        var variants = Track(source).Variants;

        Assert.Equal(4, variants.Count);
        Assert.Equal(
            new[] { AudioVariant.Original, AudioVariant.Karaoke, AudioVariant.Backing, AudioVariant.Vocals },
            variants.Present.Select(slot => slot.Variant).ToArray());

        // The two instrumental renders are two files. This is the pair the old
        // deck menu could not tell apart.
        Assert.Equal(Path.Combine(_folder, "Song.karaoke.mp3"),
            variants.PathFor(AudioVariant.Karaoke));
        Assert.Equal(Path.Combine(_folder, "Song.backing.mp3"),
            variants.PathFor(AudioVariant.Backing));
    }

    [Fact]
    public void TheLedgerIsReadFromDisk_NotCached()
    {
        var source = Song("Live.mp3");
        var track = Track(source);
        Assert.Single(track.Variants.Present);

        Song("Live.karaoke.mp3");

        // A render that lands while the row is on screen appears on the next read.
        Assert.Equal(2, track.Variants.Count);
        Assert.True(track.Variants.Has(AudioVariant.Karaoke));
        Assert.Null(track.Variants.PathFor(AudioVariant.Vocals));
    }

    [Fact]
    public void EverySlotHasAnIcon_AndAbsentTakesHaveNone()
    {
        var source = Song("Icons.mp3");
        Song("Icons.karaoke.mp3");

        var slots = Track(source).Variants.All;

        Assert.Equal(4, slots.Count);
        Assert.Equal(VariantGlyphs.Karaoke, slots.Single(s => s.Variant == AudioVariant.Karaoke).Icon);
        Assert.Equal("", slots.Single(s => s.Variant == AudioVariant.Vocals).Icon);
        Assert.False(slots.Single(s => s.Variant == AudioVariant.Vocals).Exists);
    }

    [Fact]
    public void PlaySubmenu_OneItemPerTakeThatExists_InStableOrder()
    {
        var source = Song("Menu.mp3");
        Song("Menu.karaoke.mp3");
        Song("Menu.backing.mp3");

        var items = TrackMenuCapabilities.BuildVariantItems(
            Track(source).Variants, _ => new StubCommand());

        Assert.Equal(new[] { "Original", "Karaoke", "Keep backing" },
            items.Select(item => item.Header).ToArray());
        Assert.Equal(VariantGlyphs.Karaoke, items[1].Icon);
        Assert.All(items, item => Assert.NotNull(item.Command));

        // A take that is not on disk is absent, so it cannot look available.
        Assert.DoesNotContain(items, item => item.Variant == AudioVariant.Vocals);
    }

    [Fact]
    public void QueueSubmenu_OffersOnlyWhatTheQueueCanCarry()
    {
        var source = Song("Queue.mp3");
        Song("Queue.karaoke.mp3");
        Song("Queue.backing.mp3");
        Song("Queue.vocals.mp3");

        var items = TrackMenuCapabilities.BuildQueueVariantItems(
            Track(source).Variants, _ => new StubCommand());

        // The queue stores one pair row per song and its mate is resolved by
        // PlaybackQueueItem.FromKaraokePair, which cannot carry a named take.
        Assert.Equal(new[] { "Original", "Karaoke" }, items.Select(item => item.Header).ToArray());
    }

    [Fact]
    public void SingleTakeTrack_KeepsTheFlatMenuItems()
    {
        var source = Song("Plain.mp3");
        var caps = new TrackMenuCapabilities
        {
            Play = new StubCommand(),
            AddToQueue = new StubCommand(),
        };

        caps.SetVariantItems(
            TrackMenuCapabilities.BuildVariantItems(Track(source).Variants, _ => new StubCommand()),
            TrackMenuCapabilities.BuildQueueVariantItems(Track(source).Variants, _ => new StubCommand()));

        Assert.False(caps.HasVariantChoice);
        Assert.True(caps.ShowFlatPlay);
        Assert.False(caps.ShowVariantPlay);
        Assert.True(caps.ShowFlatQueue);
        Assert.False(caps.ShowVariantQueue);
    }

    [Fact]
    public void BothInstrumentalRenders_OpenTheSubmenu()
    {
        var source = Song("Both.mp3");
        Song("Both.karaoke.mp3");
        Song("Both.backing.mp3");
        var variants = Track(source).Variants;
        var caps = new TrackMenuCapabilities
        {
            Play = new StubCommand(),
            AddToQueue = new StubCommand(),
        };

        caps.SetVariantItems(
            TrackMenuCapabilities.BuildVariantItems(variants, _ => new StubCommand()),
            TrackMenuCapabilities.BuildQueueVariantItems(variants, _ => new StubCommand()));

        Assert.True(caps.HasVariantChoice);
        Assert.False(caps.ShowFlatPlay);
        Assert.True(caps.ShowVariantPlay);
        Assert.Equal(3, caps.PlayVariants.Count);
    }

    [Fact]
    public void NoRowAtAll_LeavesEveryListEmpty()
    {
        var caps = new TrackMenuCapabilities { Play = new StubCommand(), AddToQueue = new StubCommand() };

        caps.SetVariantItems(
            TrackMenuCapabilities.BuildVariantItems(VariantAvailability.None, _ => new StubCommand()),
            TrackMenuCapabilities.BuildQueueVariantItems(VariantAvailability.None, _ => new StubCommand()));

        Assert.Empty(caps.PlayVariants);
        Assert.Empty(caps.QueueVariants);
        Assert.True(caps.ShowFlatPlay);
    }

    private sealed class StubCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
