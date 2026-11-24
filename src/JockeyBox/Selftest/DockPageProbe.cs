using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using JockeyBox.Tools;
using JockeyBox.Views;

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
        }
        finally
        {
            window.Close();
        }
    }

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
        // Dock tabs: the RadioButtons that share a parent with DockTabMixer. The
        // group is found from its first member instead of a key list, so a new
        // DockTab* radio is covered the day it is added.
        foreach (var radio in DockRadios(window))
        {
            string name = "dock:" + (ContentText(radio) ?? radio.Name.ToLowerInvariant());
            pages.Add(new Page(name, () => radio.IsChecked = true));
        }

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
    private static RadioButton[] DockRadios(MainWindow window)
    {
        var mixer = window.FindName("DockTabMixer") as RadioButton
                    ?? throw new InvalidOperationException(
                        "DockTabMixer not found; the dock-page probe cannot enumerate the dock tabs");
        var strip = LogicalTreeHelper.GetParent(mixer) as Panel
                    ?? throw new InvalidOperationException(
                        "DockTabMixer has no panel parent; the dock tabs moved and the probe cannot see them");
        return strip.Children.OfType<RadioButton>().ToArray();
    }

    private static void Neutralize(MainWindow window)
    {
        foreach (var radio in DockRadios(window)) radio.IsChecked = false;
        window.SetLibraryOpen(false);
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
        var size = new Size(window.Width, window.Height);
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
