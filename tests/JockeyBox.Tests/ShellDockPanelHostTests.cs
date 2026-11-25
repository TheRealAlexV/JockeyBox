using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.Views.Shell;

namespace JockeyBox.Tests;

/// <summary>
/// The dock's contract, asserted on <see cref="DockPanelHost"/> itself.
///
/// <para><b>Why the host and not the window.</b> Every rule here is the host's: one
/// panel at a time, a width per panel, a clamp derived from the window's width, and
/// content that is handed out and taken back rather than rebuilt. Phase 1 could only
/// prove these through a rendered capture, which shows geometry and chrome but cannot
/// tell a re-parented element from an identical-looking fresh one. Two synthetic panes
/// with trivial content make "the same instance" assertable.</para>
///
/// <para><b>No window.</b> These tests never construct a <c>Window</c> and never build
/// <c>MainWindow</c>. The control's XAML names brushes from <c>Themes/Dark.xaml</c>, so
/// it is built on the suite's existing STA dispatcher (<see cref="UiThread"/>) with that
/// dictionary merged - one <c>Application</c> for the process, as every other UI test in
/// this suite already uses, and no NuGet dependency added.</para>
///
/// <para><b>Settings isolation.</b> The width tests write a real settings file, so they
/// redirect <c>JOCKEYBOX_HOME</c> at a temp directory and restore it; the class sits in
/// <see cref="UiThreadCollection"/>, which xUnit runs alone.</para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public class ShellDockPanelHostTests
{
    // The library's real numbers: 545 px opened, 300 px floor. The ceiling is derived
    // (window width - 855), which is the rule under test.
    private const double LibraryWidth = 545;
    private const double LibraryFloor = 300;
    private const double OtherWidth = 420;

    /// <summary>Run on the suite's STA dispatcher with the theme merged.</summary>
    private static T OnUi<T>(Func<T> body) => UiThread.Run(() =>
    {
        var app = Application.Current!;
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/JockeyBox;component/Themes/Dark.xaml", UriKind.Relative),
            });
        }

        return body();
    });

    private static void OnUi(Action body) => OnUi(() => { body(); return true; });

    /// <summary>Two panes the dock can hold, with content it must not rebuild.</summary>
    private sealed class Fixture
    {
        public Border A { get; } = new() { Name = "a-content" };
        public Border B { get; } = new() { Name = "b-content" };
        public int Created { get; private set; }

        public PanelDefinition DefinitionA => new(
            ShellPanelId.Library, "LIBRARY", "Library panel",
            LibraryWidth, LibraryFloor, AllowsPopOut: true,
            () => { Created++; return A; });

        public PanelDefinition DefinitionB => new(
            ShellPanelId.Lyrics, "LYRICS", "Lyrics panel",
            OtherWidth, 260, AllowsPopOut: true,
            () => { Created++; return B; });

        public DockPanelHost NewHost(AppSettings? settings = null, double? availableWidth = null)
        {
            var host = new DockPanelHost(new[] { DefinitionA, DefinitionB });
            if (settings != null) host.Bind(settings);
            if (availableWidth is { } width) host.AvailableWidth = width;
            return host;
        }
    }

    private static string TempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "jb-shell-dock", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public void ShowSecondPanel_LeavesOnlyTheSecondElementInTheSlot()
    {
        OnUi(() =>
        {
            var fixture = new Fixture();
            var host = fixture.NewHost();

            host.Show(ShellPanelId.Library);
            host.Show(ShellPanelId.Lyrics);

            Assert.Same(fixture.B, host.ContentHost.Content);
            Assert.Null(fixture.A.Parent);
            Assert.Equal(ShellPanelId.Lyrics, host.Current);
        });
    }

    [Fact]
    public void Show_SwitchesToThePanelsOwnDefaultWidth()
    {
        OnUi(() =>
        {
            var fixture = new Fixture();
            var host = fixture.NewHost();

            host.Show(ShellPanelId.Library);
            Assert.Equal(LibraryWidth, host.Width);

            host.Show(ShellPanelId.Lyrics);
            Assert.Equal(OtherWidth, host.Width);
        });
    }

    [Fact]
    public void Width_IsClampedToTheDerivedCeilingAndNeverBelowTheFloor()
    {
        OnUi(() =>
        {
            var fixture = new Fixture();

            // 1040 - 855 = 185, below the 300 floor, so the floor wins.
            var narrow = fixture.NewHost(availableWidth: 1040);
            narrow.Show(ShellPanelId.Library);
            Assert.Equal(LibraryFloor, narrow.Width);

            // 1400 - 855 = 545, exactly the library's own default.
            var wide = fixture.NewHost(availableWidth: 1400);
            wide.Show(ShellPanelId.Library);
            Assert.Equal(LibraryWidth, wide.Width);
        });
    }

    [Fact]
    public void PopOutCurrent_RaisesTheIdAndLeavesTheDockEmptyWithoutDestroyingContent()
    {
        OnUi(() =>
        {
            var fixture = new Fixture();
            var host = fixture.NewHost();
            ShellPanelId? raised = null;
            host.PoppedOut += (_, id) => raised = id;

            host.Show(ShellPanelId.Library);
            host.PopOutCurrent();

            Assert.Equal(ShellPanelId.Library, raised);

            // The window layer's next step, reproduced: take the element, keep it.
            var element = host.DetachContent(ShellPanelId.Library);

            Assert.NotNull(element);
            Assert.Null(host.ContentHost.Content);
            Assert.False(host.IsOpen);
            Assert.Same(fixture.A, element);
            Assert.Same(element, host.ContentOf<FrameworkElement>(ShellPanelId.Library));
            Assert.Equal(1, fixture.Created);
        });
    }

    [Fact]
    public void ReturnContent_PutsTheSameReferenceBackAndReopensTheDock()
    {
        OnUi(() =>
        {
            var fixture = new Fixture();
            var host = fixture.NewHost();

            host.Show(ShellPanelId.Library);
            var element = host.DetachContent(ShellPanelId.Library)!;
            Assert.Null(host.ContentHost.Content);

            host.ReturnContent(ShellPanelId.Library, element);

            // Reference equality is the assertion: a reconstructed pane would satisfy
            // IsNotNull and fail this.
            Assert.Same(element, host.ContentHost.Content);
            Assert.Same(fixture.A, host.ContentHost.Content);
            Assert.True(host.IsOpen);
            Assert.Equal(ShellPanelId.Library, host.Current);
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(1, fixture.Created);
        });
    }

    [Fact]
    public void PoppingAnAlreadyPoppedPanel_RaisesAgainInsteadOfBuildingASecondContent()
    {
        OnUi(() =>
        {
            var fixture = new Fixture();
            var host = fixture.NewHost();
            var raised = new List<ShellPanelId>();
            host.PoppedOut += (_, id) => raised.Add(id);

            host.Show(ShellPanelId.Library);
            host.PopOutCurrent();
            var element = host.DetachContent(ShellPanelId.Library)!;

            // Nothing is docked now; the press has to reach the window that holds it.
            host.PopOutCurrent();

            Assert.Equal(new[] { ShellPanelId.Library, ShellPanelId.Library }, raised);
            Assert.Same(element, host.ContentOf<FrameworkElement>(ShellPanelId.Library));
            Assert.Equal(1, fixture.Created);
        });
    }

    [Fact]
    public void Width_SurvivesSaveAndReload_AndIsClampedOnLoad()
    {
        string home = TempHome();
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            OnUi(() =>
            {
                var fixture = new Fixture();
                var settings = new AppSettings();
                var host = fixture.NewHost(settings, availableWidth: 1400);
                host.Show(ShellPanelId.Library);

                // The operator's drag: width then the thumb's completion, which is what
                // persists it.
                host.Width = 600;
                host.Splitter.RaiseEvent(
                    new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

                Assert.Equal(600, AppSettings.TryLoad()!.Panel(ShellPanelId.Library).Width);

                // A fresh host reads it back from disk rather than from memory.
                var reloaded = AppSettings.TryLoad()!;
                var reopened = fixture.NewHost(reloaded, availableWidth: 1400);
                reopened.Show(ShellPanelId.Library);
                Assert.Equal(600, reopened.Width);

                // Outside [floor, ceiling] a stored width is clamped, never trusted:
                // 1400 - 855 = 545 is the ceiling here.
                reloaded.Panel(ShellPanelId.Library).Width = 5000;
                reloaded.Save();
                var tooWide = fixture.NewHost(AppSettings.TryLoad()!, availableWidth: 1400);
                tooWide.Show(ShellPanelId.Library);
                Assert.Equal(LibraryWidth, tooWide.Width);

                var wideSettings = AppSettings.TryLoad()!;
                wideSettings.Panel(ShellPanelId.Library).Width = 100;
                wideSettings.Save();
                var tooNarrow = fixture.NewHost(AppSettings.TryLoad()!, availableWidth: 1400);
                tooNarrow.Show(ShellPanelId.Library);
                Assert.Equal(LibraryFloor, tooNarrow.Width);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SettingsFileWrittenBeforePanelLayoutExisted_LoadsWithEveryKeyAndAnEmptyPanelLayout()
    {
        string home = TempHome();
        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            // The shape of a settings.json from before the shell's panels existed: every
            // key is real, PanelLayout is simply absent.
            File.WriteAllText(AppSettings.SettingsPath, """
                {
                  "MusicDirectory": "C:\\Music",
                  "OutputDelayMs": 175,
                  "DownloadBitrateKbps": 256,
                  "YouTubeSearchResults": 40
                }
                """);

            var loaded = AppSettings.TryLoad();

            Assert.NotNull(loaded);
            Assert.Equal(@"C:\Music", loaded!.MusicDirectory);
            Assert.Equal(175, loaded.OutputDelayMs);
            Assert.Equal(256, loaded.DownloadBitrateKbps);
            Assert.Equal(40, loaded.YouTubeSearchResults);
            Assert.Empty(loaded.PanelLayout);

            // A host on that file opens the library at its own default, not at a stored
            // width that was never written.
            OnUi(() =>
            {
                var host = new Fixture().NewHost(loaded, availableWidth: 1400);
                host.Show(ShellPanelId.Library);
                Assert.Equal(LibraryWidth, host.Width);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previous);
            try { Directory.Delete(home, recursive: true); } catch { /* best effort */ }
        }
    }
}
