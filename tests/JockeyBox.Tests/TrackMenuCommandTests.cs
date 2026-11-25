using System;
using System.IO;
using JockeyBox.Library;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The shared TrackMenu sends no CommandParameter. Every item therefore has to
/// resolve its row from the surface's selection, the way "Play" does, or the
/// click arrives with null and the item silently does nothing - which is exactly
/// what "Make Karaoke Version" did (its wrapper was RelayCommand&lt;object?&gt; fed
/// by a binding nobody had written).
///
/// <para>A right-click is modelled the way MainWindow.xaml.cs does it: the row
/// is selected before the menu opens, so the assertions drive the command with
/// no argument and demand it still acts on that row.</para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class TrackMenuCommandTests
{
    [Theory]
    [InlineData("MakeKaraoke")]
    [InlineData("MakeKaraokeKeepBacking")]
    [InlineData("MakeKaraokeVocalsOnly")]
    public void EveryKaraokeMode_ActsOnTheClickedRow_WithNoParameter(string capsProperty)
    {
        string music = TestAudio.WriteConstantWav(0.20f);
        try
        {
            var vm = UiThread.Run(() => new MainViewModel());
            var track = Track("a/song", music);

            TrackEntry? installAsked = null;
            var settingsAsked = 0;
            var reached = 0;
            vm.EngineInstallRequested += t => { installAsked = t; reached++; };
            vm.SettingsRequested += () => { settingsAsked++; reached++; };

            // Right-click: the row is selected before the menu opens.
            UiThread.Run(() => vm.SelectedTreeNode = new BrowserNode(track.RelativeKey) { Track = track });

            var caps = UiThread.Run(() => vm.TrackMenu);
            var command = capsProperty switch
            {
                "MakeKaraoke" => caps.MakeKaraoke,
                "MakeKaraokeKeepBacking" => caps.MakeKaraokeKeepBacking,
                _ => caps.MakeKaraokeVocalsOnly,
            };
            Assert.NotNull(command);

            UiThread.Run(() => command!.Execute(null));

            // Either prompt proves EnqueueKaraoke got past its first guard, which
            // returns early on a null row (or one without a MusicPath).
            Assert.Equal(1, reached);
            if (installAsked is not null) Assert.Same(track, installAsked);
            else Assert.Equal(1, settingsAsked);
        }
        finally { TestAudio.TryDelete(music); }
    }

    [Fact]
    public void MakeKaraoke_WithNothingSelected_DoesNothing()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        var reached = 0;
        vm.EngineInstallRequested += _ => reached++;
        vm.SettingsRequested += () => reached++;

        var caps = UiThread.Run(() => vm.TrackMenu);
        UiThread.Run(() => caps.MakeKaraoke!.Execute(null));

        Assert.Equal(0, reached);
    }

    [Fact]
    public void LibraryMenu_OffersTheThreeModes_NotTheSingleGesture()
    {
        var caps = UiThread.Run(() => new MainViewModel().TrackMenu);

        Assert.True(caps.CanChooseKaraokeMode);
        Assert.False(caps.CanMakeKaraokeDirectly);

        // Three separate commands: one method wired three times would make every
        // mode render the same take.
        Assert.NotNull(caps.MakeKaraokeKeepBacking);
        Assert.NotNull(caps.MakeKaraokeVocalsOnly);
        Assert.NotSame(caps.MakeKaraoke, caps.MakeKaraokeKeepBacking);
        Assert.NotSame(caps.MakeKaraokeKeepBacking, caps.MakeKaraokeVocalsOnly);
    }

    [Fact]
    public void SurfaceWithoutModes_FallsBackToTheSingleItem()
    {
        // A remote tab supplies only the plain instrumental, so the menu shows one
        // item instead of a submenu with two dead children.
        var caps = new TrackMenuCapabilities { MakeKaraoke = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }) };

        Assert.False(caps.CanChooseKaraokeMode);
        Assert.True(caps.CanMakeKaraokeDirectly);
    }

    [Fact]
    public void HasKaraokePairOnDisk_RefusesTheVocalsTake()
    {
        string music = TestAudio.WriteConstantWav(0.20f);
        string vocals = Path.ChangeExtension(music, ".vocals.mp3");
        try
        {
            File.Copy(music, vocals, overwrite: true);

            // A completed VocalsOnly render stamps KaraokePath too, and that file
            // exists. It must never be handed to a deck as the karaoke stem.
            Assert.False(MainViewModel.HasKaraokePairOnDisk(
                Track("a/song", music, vocals)));

            // The stem beside it still makes a pair.
            string karaoke = Path.ChangeExtension(music, ".karaoke.mp3");
            File.Copy(music, karaoke, overwrite: true);
            try
            {
                Assert.True(MainViewModel.HasKaraokePairOnDisk(Track("a/song", music, karaoke)));
            }
            finally { TestAudio.TryDelete(karaoke); }
        }
        finally
        {
            TestAudio.TryDelete(music);
            TestAudio.TryDelete(vocals);
        }
    }

    private static TrackEntry Track(string key, string? music, string? karaoke = null) => new()
    {
        RelativeKey = key,
        RelativePath = (music ?? karaoke)!,
        Title = "Song",
        Artist = "Artist",
        MusicPath = music,
        KaraokePath = karaoke,
    };
}
