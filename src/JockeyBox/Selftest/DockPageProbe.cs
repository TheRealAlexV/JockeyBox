using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using JockeyBox.Audio.Graph;
using JockeyBox.Lyrics;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using JockeyBox.Views.Shell;
using JockeyBox.YouTube;

namespace JockeyBox.Selftest;

/// <summary>
/// Constructs the real main window and realises every page it owns - each document
/// tab, each dock tab, the library pane and each rail flyout - failing on any
/// exception raised while a page comes up.
///
/// <para><b>Why.</b> A <c>StaticResource</c> inside a <see cref="ControlTemplate"/>
/// is resolved when the template is <em>applied</em>, not when the XAML is parsed.
/// A template declared above the style it names therefore builds clean, passes every
/// test that never realises it, and throws
/// <c>XamlParseException: Cannot find resource named '...'</c> the first time the
/// page that uses it is shown. Commit <c>7b687b4</c> put <c>StripCell</c> above
/// <c>RackCaption</c> and the dock STRIPS page died on first display; the build was
/// 0/0 and the whole suite was green. Nothing in the suite could see it, because
/// nothing ever constructed a page.</para>
///
/// <para><b>Enumeration.</b> The page set is derived from the window's own logical
/// tree, not from a list here: every <see cref="TabItem"/> is a page, every
/// <see cref="RadioButton"/> beside <c>DockTabMixer</c> is a dock page, and every
/// <c>Rail*Toggle</c> is a rail flyout. A tab or flyout added to
/// <c>MainWindow.xaml</c> joins the set without touching this file. Only three
/// anchors are named - the dock tab group's first member and the rail toggle
/// naming convention - and a missing anchor fails loudly rather than shrinking
/// coverage silently.</para>
///
/// <para><b>Isolation.</b> Runs against a throwaway <c>JOCKEYBOX_HOME</c> when the
/// caller has not already sandboxed one, never shows a window on any desktop the
/// operator can see, and touches no audio device, network or Spotify session.</para>
/// </summary>
internal static class DockPageProbe
{
    private const string ThemeDictionary =
        "pack://application:,,,/JockeyBox;component/Themes/Dark.xaml";

    /// <summary>
    /// Realise every page. <paramref name="line"/> receives one line per page so a
    /// red run names the page that died.
    /// </summary>
    public static void Check(Action<string> line)
    {
        RunOnUiThread(() => CaptureWindow(line));
    }

    // ------------------------------------------------------------------ pages

    private static void CaptureWindow(Action<string> line)
    {
        using var home = SandboxHome();
        if (home.Sandboxed)
            line($"  dock-page home: {home.Path}");

        EnsureTheme();

        var window = new MainWindow { Width = 1600, Height = 1000 };
        var content = window.Content as FrameworkElement
                      ?? throw new InvalidOperationException("main window has no content to lay out");

        // One bound tab header (QUEUE) resolves on the dispatcher, not in the
        // constructor. Flush the bind queue without laying anything out, so a page
        // can be named for what it says instead of "unnamed-1".
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

        try
        {
            var pages = Enumerate(window);

            // Nothing is shown or presented: a page is realised by laying the
            // content out, so no desktop, no focus and no HWND are involved. The
            // default dock page is unset first, or the very first layout pass would
            // realise it before it has a name and the failure would be attributed to
            // whichever page happened to be first.
            Neutralize(window);

            foreach (var page in pages)
            {
                try
                {
                    page.Activate();
                    Settle(window, content);
                }
                catch (Exception ex)
                {
                    line($"  dock-page {page.Name}: FAILED");
                    throw Fail(page.Name, ex);
                }

                line($"  dock-page {page.Name}: ok");
            }

            CapturePanels(window, line);
            CaptureDialogs(line);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ dialogs

    /// <summary>
    /// The windows <c>MainWindow</c> opens. Unlike the tabs and flyouts these are not
    /// reachable from its logical tree, so there is nothing to walk and the list is
    /// written out. They carry the same defect class: a <c>StaticResource</c> inside a
    /// <c>ControlTemplate</c> is resolved when the template is applied, and nothing
    /// applied one of these until an operator clicked. Each is built from a null or
    /// empty view model, laid out, and dropped - nothing is shown.
    /// </summary>
    private static void CaptureDialogs(Action<string> line)
    {
        var settings = AppSettings.Load();
        var google = new GoogleOAuthService();
        var api = new YouTubeDataApi(google);

        // The diagnostics window owns its engine's lifetime; it is never started, so
        // no device is opened.
        using var audio = new AudioEngine();

        var dialogs = new (string Name, Func<Window> Create)[]
        {
            ("dialog:renametrack", () => new RenameTrackDialog(@"C:\Music\Artist - Title.mp3")),
            ("dialog:movetrack", () => new MoveTrackDialog(@"Artist\Album\Artist - Title.mp3")),
            ("dialog:settings", () => new SettingsWindow(settings)),
            ("dialog:diagnostics", () => new DiagnosticsWindow(audio, settings)),
            ("dialog:spotify-setup", () => new SpotifySetupDialog(settings, new SpotifyOAuthService(settings))),
            ("dialog:youtube-auth", () => new YouTubeAuthDialog(google, api)),
            ("dialog:youtube-search", () => new YouTubeSearchWindow(SearchViewModel(settings))),
        };

        var timings = DialogTimings;
        foreach (var (name, create) in dialogs)
        {
            var clock = Stopwatch.StartNew();
            Window? dialog = null;
            try
            {
                dialog = create();
                var face = dialog.Content as FrameworkElement
                           ?? throw new InvalidOperationException("dialog has no content to lay out");
                Settle(dialog, face);
            }
            catch (Exception ex)
            {
                line($"  dock-page {name}: FAILED");
                throw Fail(name, ex);
            }
            finally
            {
                // Never shown, so there is no HWND to release; Close on an unshown
                // window is still the documented teardown and must not mask the
                // failure above.
                try { dialog?.Close(); }
                catch (InvalidOperationException) { }
            }

            clock.Stop();
            line(timings
                ? $"  dock-page {name}: ok ({clock.ElapsedMilliseconds} ms)"
                : $"  dock-page {name}: ok");
        }
    }

    private static YouTubeSearchViewModel SearchViewModel(AppSettings settings) =>
        new(settings, new StreamPreviewTransport(new PreviewStreamPlayer(settings)), "dock-page probe");

    /// <summary>
    /// Set <c>JOCKEYBOX_DIALOG_TIMINGS=1</c> to print how long each dialog took. The
    /// dialog pass is the expensive part of this case in testhost (window and font
    /// work that the app process pays once at startup), and the cost has to be
    /// attributable without a rebuild.
    /// </summary>
    private static bool DialogTimings =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOCKEYBOX_DIALOG_TIMINGS"));

    private static IReadOnlyList<Page> Enumerate(MainWindow window)
    {
        var pages = new List<Page>();

        // Document tabs: every TabItem in the window is a page, whatever tab strip
        // owns it. Nested tab controls (the console's JOBS/LOG) are included, which
        // is why this is a walk and not a lookup of one TabControl.
        int unnamed = 0;
        foreach (var tab in Descendants(window).OfType<TabItem>().ToArray())
        {
            string name = "tab:" + (TabLabel(tab) ?? $"unnamed-{++unnamed}");
            pages.Add(new Page(name, () => tab.IsSelected = true));
        }
        // Dock tabs are not in the window any more: they are the page strip of the
        // MIX panel, a XAML root of its own. CapturePanels walks each registered
        // panel and selects the radios it finds there.

        // Rail flyouts: the toggles that open each section. Naming convention
        // (Rail<Section>Toggle) rather than a list, so a new section is covered
        // once its toggle exists.
        var rail = Descendants(window).OfType<ToggleButton>()
            .Where(t => t.Name.StartsWith("Rail", StringComparison.Ordinal)
                        && t.Name.EndsWith("Toggle", StringComparison.Ordinal))
            .ToArray();
        if (rail.Length == 0)
            throw new InvalidOperationException(
                "no Rail*Toggle found; the rail flyouts moved and the probe cannot see them");

        foreach (var toggle in rail)
        {
            string section = toggle.Name[4..^6].ToLowerInvariant();
            pages.Add(new Page("flyout:" + section,
                () => toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))));
        }

        // The library pane is not a tab: it is a column the rail opens.
        pages.Add(new Page("library-pane", () => window.SetLibraryOpen(true)));

        if (pages.Count == 0)
            throw new InvalidOperationException("main window exposed no pages to construct");

        // Two strips can carry a tab with the same caption (the console's JOBS tab
        // beside the dock's JOBS page). Number the repeats so a red run still points
        // at one element instead of an ambiguous name.
        return pages
            .GroupBy(page => page.Name, StringComparer.Ordinal)
            .SelectMany(group => group.Select((page, index) =>
                index == 0 ? page : page with { Name = $"{page.Name}#{index + 1}" }))
            .ToArray();
    }

    /// <summary>
    /// The dock's page selectors: every <see cref="RadioButton"/> in the strip that
    /// owns <c>DockTabMixer</c>. Anchored on the one member whose name the code
    /// behind must keep (it is the default page), then derived - a new dock tab is a
    /// new radio and needs no edit here.
    /// </summary>
    private static RadioButton[] DockRadios(FrameworkElement root)
    {
        // A panel is its own XAML root, so its names live in its own namescope:
        // walk the tree instead of asking the window for them. A panel without a
        // page strip (LIB, LYRIC) simply has no inner pages.
        var mixer = Descendants(root).OfType<RadioButton>()
            .FirstOrDefault(radio => radio.Name == "DockTabMixer");
        if (mixer is null) return Array.Empty<RadioButton>();

        var strip = LogicalTreeHelper.GetParent(mixer) as Panel
                    ?? throw new InvalidOperationException(
                        "DockTabMixer has no panel parent; the dock tabs moved and the probe cannot see them");
        return strip.Children.OfType<RadioButton>().ToArray();
    }

    private static void Neutralize(MainWindow window)
    {
        window.SetLibraryOpen(false);
    }

    // ------------------------------------------------------------------ panels

    /// <summary>
    /// Every shell panel <see cref="PanelCatalog"/> registers, built from the catalog
    /// itself.
    ///
    /// <para><b>The class of bug this covers.</b> A panel is a XAML root of its own:
    /// it is constructed before it has a parent, so a <c>StaticResource</c> it names
    /// resolves at application scope or not at all. Phase 3 moved MIX out of the
    /// window while its styles stayed in <c>MainWindow.xaml</c>'s
    /// <c>Window.Resources</c>, and the panel threw
    /// <c>XamlParseException: Cannot find resource named 'RackCaption'</c> on
    /// construction. Phase 4 adds four more panels of the same shape, which is why
    /// this enumerates the catalog rather than a list written here.</para>
    ///
    /// <para>Each panel is also walked for its own page radios (MIX's STRIPS /
    /// SOUNDBOARD / CUES / JOBS), because a page only realises when it is selected:
    /// the failure that killed STRIPS on first display lives in a page, not in the
    /// panel's default view.</para>
    /// </summary>
    private static void CapturePanels(MainWindow window, Action<string> line)
    {
        var registered = PanelCatalog.Registered;
        if (registered.Count == 0)
            throw new InvalidOperationException(
                "PanelCatalog registers no panel; the probe has nothing to build");

        foreach (var definition in registered)
        {
            string name = "panel:" + definition.Id.ToString().ToLowerInvariant();

            FrameworkElement panel;
            try
            {
                panel = definition.Create();
            }
            catch (Exception ex)
            {
                line($"  dock-page {name}: FAILED");
                throw Fail(name, ex);
            }

            // In the dock the panel inherits the console's view model; give it the
            // same one here so a page fails for its own reason, not for a missing
            // context.
            panel.DataContext ??= window.DataContext;
            Realize(panel);
            RealizeTemplates(panel, name);
            line($"  dock-page {name}: ok");

            foreach (var radio in DockRadios(panel))
            {
                string page = name + ":" + (ContentText(radio) ?? radio.Name.ToLowerInvariant());
                try
                {
                    radio.IsChecked = true;
                    Realize(panel);
                }
                catch (Exception ex)
                {
                    line($"  dock-page {page}: FAILED");
                    throw Fail(page, ex);
                }

                line($"  dock-page {page}: ok");
            }
        }
    }

    /// <summary>
    /// Load every <c>ItemTemplate</c> the panel declares, once.
    ///
    /// <para><b>Why this is not redundant with <see cref="Realize"/>.</b> A template
    /// only parses when its control first produces an item, and the interesting lists
    /// in this shell are empty in a probe: the strip rack waits for a selected strip,
    /// the device combos wait for a device. Deleting <c>RackGroupTitle</c> from app
    /// scope therefore still reported <c>panel:sources: ok</c> - the same
    /// "builds clean, fails on first real show" hole the probe exists to close.
    /// <see cref="DataTemplate.LoadContent"/> resolves the template's own keys against
    /// the same dictionaries a real item would, without inventing a data context.</para>
    /// </summary>
    private static void RealizeTemplates(FrameworkElement panel, string name)
    {
        foreach (var control in Descendants(panel).OfType<ItemsControl>())
        {
            if (control.ItemTemplate is not { } template) continue;
            try
            {
                template.LoadContent();
            }
            catch (Exception ex)
            {
                throw Fail(name + ":template", ex);
            }
        }
    }

    /// <summary>
    /// Lay a detached panel out at a plausible dock size. Measure applies templates,
    /// and a <c>StaticResource</c> inside a template is resolved on apply - so an
    /// unresolvable key surfaces here, not on the operator's first click.
    /// </summary>
    private static void Realize(FrameworkElement panel)
    {
        var size = new Size(520, 760);
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        panel.UpdateLayout();
    }

    private static string? TabLabel(TabItem tab)
    {
        if (tab.Header is string text && !string.IsNullOrWhiteSpace(text))
            return text.Trim().ToLowerInvariant();

        if (tab.Header is DependencyObject header)
        {
            string? inner = header is TextBlock self && !string.IsNullOrWhiteSpace(self.Text)
                ? self.Text
                : Descendants(header).OfType<TextBlock>()
                    .Select(block => block.Text)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(inner)) return inner.Trim().ToLowerInvariant();
        }

        return string.IsNullOrWhiteSpace(tab.Name) ? null : tab.Name.ToLowerInvariant();
    }

    private static string? ContentText(ContentControl control) =>
        control.Content as string is { Length: > 0 } text ? text.Trim().ToLowerInvariant() : null;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject node)
            {
                yield return node;
                foreach (var descendant in Descendants(node)) yield return descendant;
            }
        }
    }

    private sealed record Page(string Name, Action Activate);

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// Force a synchronous measure/arrange pass. Template application happens in
    /// measure, so an unresolvable StaticResource inside a template surfaces here
    /// and not three frames later on the dispatcher.
    /// </summary>
    private static void Settle(Window window, FrameworkElement content)
    {
        // Dialogs declare their own size; a Window that declares none measures to
        // zero and would realise nothing, so the fallback is a size every dialog in
        // this app fits inside.
        var size = new Size(
            window.Width  > 0 ? window.Width  : 1000,
            window.Height > 0 ? window.Height : 800);
        content.Measure(size);
        content.Arrange(new Rect(size));
        window.UpdateLayout();
    }

    private static Exception Fail(string page, Exception ex)
    {
        var detail = new System.Text.StringBuilder($"{page}: {ex.GetType().Name}: {ex.Message}");
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            detail.Append($" ---> {inner.GetType().Name}: {inner.Message}");
        return new SelftestRunner.CaseFailed(detail.ToString());
    }

    // ------------------------------------------------------------------ thread

    /// <summary>
    /// Run on the thread that owns <see cref="Application"/>: WPF styles are
    /// thread-affine, so the window and its resources must be built on the same
    /// thread. In the app process that is the main STA thread; under the test gate
    /// it is the suite's shared UI thread. A bare process with no Application gets
    /// its own, built the same way the capture harnesses build theirs.
    /// </summary>
    private static void RunOnUiThread(Action body)
    {
        var app = Application.Current;
        if (app is not null)
        {
            if (app.Dispatcher.CheckAccess())
            {
                body();
                return;
            }

            ExceptionDispatchInfo? failure = null;
            app.Dispatcher.Invoke(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            failure?.Throw();
            return;
        }

        ExceptionDispatchInfo? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                CreateBareApplication();
                body();
            }
            catch (Exception ex) { thrown = ExceptionDispatchInfo.Capture(ex); }
        })
        {
            IsBackground = true,
            Name = "jockeybox-dock-pages",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("dock-page probe did not finish within 2 minutes");
        thrown?.Throw();
    }

    private static void CreateBareApplication()
    {
        if (Application.Current is null)
        {
            _ = new Application
            {
                // Nothing here shows a window the operator keeps: a close must not
                // end whatever host this runs in.
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
        }
    }

    private static void EnsureTheme()
    {
        var app = Application.Current!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (app.Resources.Contains("AccentABrush")) return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(ThemeDictionary),
        });
    }

    // ------------------------------------------------------------------ home

    /// <summary>
    /// A throwaway home for the window's view model, unless the caller already
    /// sandboxed one (as <c>--selftest</c> does: it refuses the live profile).
    /// </summary>
    private static HomeLease SandboxHome()
    {
        string previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable) ?? "";
        bool live = previous.Length == 0
                    || string.Equals(Trim(previous), Trim(AppPaths.LiveHome), StringComparison.OrdinalIgnoreCase);
        if (!live) return HomeLease.None;

        string path = Path.Combine(Path.GetTempPath(), "jb-dockpages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, path);
        return new HomeLease(previous, path);
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private sealed class HomeLease : IDisposable
    {
        public static readonly HomeLease None = new(null, null);

        private readonly string? _previous;
        private bool _disposed;

        public HomeLease(string? previous, string? path)
        {
            _previous = previous;
            Path = path;
        }

        public string? Path { get; }

        public bool Sandboxed => Path is not null;

        public void Dispose()
        {
            if (_disposed || Path is null) return;
            _disposed = true;
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _previous);
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
