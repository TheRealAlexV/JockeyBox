using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// A WPF binding to a name the view model does not expose fails silently: the
/// control renders and does nothing. These tests read the window's XAML and make
/// that failure loud - a renamed property breaks the suite instead of disabling a
/// control in the field.
/// </summary>
public sealed class XamlBindingGuardTests
{
    private static readonly Assembly AppAssembly = typeof(MainViewModel).Assembly;

    /// <summary>
    /// Every view's data context, named here so a binding can be checked against the
    /// type it actually reads. A new view that is not listed fails the suite instead of
    /// being skipped: this guard exists to break when a binding stops resolving, and one
    /// that silently stops watching a file is worse than no guard at all.
    ///
    /// <para>null means "no inherited context" - a ResourceDictionary has no data
    /// context of its own, so only the bindings inside its typed DataTemplates are
    /// checked, and those name their own type.</para>
    /// </summary>
    private static readonly Dictionary<string, Type?> ViewContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        // The console and the panes it hosts: everything inside them binds to the one
        // MainViewModel the window owns.
        ["MainWindow.xaml"] = typeof(MainViewModel),
        ["LibraryPanel.xaml"] = typeof(MainViewModel),
        ["LyricsPanel.xaml"] = typeof(MainViewModel),
        ["SpotifyView.xaml"] = typeof(SpotifyViewModel),
        ["YouTubeView.xaml"] = typeof(YouTubeViewModel),

        // Views with a context of their own.
        ["PreviewBar.xaml"] = typeof(PreviewBarViewModel),
        ["DiagnosticsWindow.xaml"] = typeof(DiagnosticsViewModel),
        ["YouTubeSearchWindow.xaml"] = typeof(YouTubeSearchViewModel),
        ["SpotifySetupDialog.xaml"] = typeof(SpotifySetupDialog),
        ["YouTubeAuthDialog.xaml"] = typeof(YouTubeAuthDialog),

        // The shell's own chrome. DockPanelHost binds nothing (its caption is set in
        // code from the panel definition); PanelWindow carries the console's view model
        // because it hosts the panel element that binds to it.
        ["DockPanelHost.xaml"] = null,
        ["PanelWindow.xaml"] = typeof(MainViewModel),

        // Dialogs that bind nothing or bind with ElementName only: there is no
        // inherited context to check a path against, so the walk finds none. Listed
        // rather than omitted so a new binding in one of them cannot be added silently -
        // the file still has to be named here either way.
        ["SettingsWindow.xaml"] = null,
        ["SetupWindow.xaml"] = null,
        ["EditLyricLineDialog.xaml"] = null,
        ["MoveTrackDialog.xaml"] = null,
        ["RenameTrackDialog.xaml"] = null,
    };

    [Fact]
    public void EveryBindingPathInEveryViewResolvesOnItsDataContext()
    {
        var failures = new List<string>();
        var unlisted = new List<string>();
        int checkedBindings = 0;
        int files = 0;

        foreach (string file in ViewXamlFiles())
        {
            var doc = XDocument.Load(file);
            string name = Path.GetFileName(file);
            files++;

            // A dictionary is merged into an element tree rather than being one, so it
            // inherits nothing; a view inherits from whatever hosts it, which is the
            // table above.
            bool isDictionary = doc.Root?.Name.LocalName == "ResourceDictionary";
            Type? fallback = null;
            if (!isDictionary)
            {
                if (!ViewContexts.TryGetValue(name, out fallback)) unlisted.Add(name);
            }

            checkedBindings += WalkBindings(doc, fallback, name, failures);
        }

        Assert.Empty(unlisted);
        Assert.True(files >= 10, $"only {files} XAML files were walked; the guard is not reading the views");
        Assert.True(checkedBindings >= 403,
                    $"only {checkedBindings} bindings were walked; the guard is not reading the views");
        Assert.Empty(failures);
    }

    /// <summary>
    /// Walk one file's bindings. Returns how many were attributed to a data context, so
    /// the caller can prove the guard actually read something rather than passing on an
    /// empty tree.
    /// </summary>
    private static int WalkBindings(XDocument doc, Type? fallback, string file, List<string> failures)
    {
        int checkedBindings = 0;

        foreach (var element in doc.Descendants())
        {
            Type? context = DataContextType(element, fallback);
            if (context == null) continue;

            foreach (var attribute in element.Attributes())
            {
                string? path = BindingPathOf(attribute.Value);
                if (path == null) continue;

                // A DataContext binding re-roots what is inside, so it is checked
                // against the context it replaces, not the one it creates.
                var target = attribute.Name.LocalName == "DataContext"
                    ? OuterContextType(element, fallback)
                    : context;
                if (target == null) continue;

                checkedBindings++;
                if (!Resolves(target, path))
                    failures.Add($"{file}: \"{{Binding {path}}}\" on {element.Name.LocalName}.{attribute.Name.LocalName} " +
                                 $"does not resolve on {target.Name}");
            }
        }

        return checkedBindings;
    }

    /// <summary>
    /// The cluster this round is about, asserted by name so the sweep above cannot
    /// pass by walking a window that has stopped binding any of them.
    /// </summary>
    [Fact]
    public void MonitorClusterAndDeckTitleBindingsArePresentAndResolvable()
    {
        var doc = XDocument.Load(MainWindowXaml());
        var bindings = BindingsIn(doc);

        foreach (var name in new[] { "MonitorEnabled", "MonitorAGain", "MonitorBGain", "MonitorSyncEnabled" })
        {
            // A plain path binds on the element's own data context. The cue levels
            // live on the shared deck card, so they re-root to the window with
            // RelativeSource and read DataContext.<name>. Both forms name the same
            // property, and a typo in either matches neither.
            Assert.True(BoundTo(bindings, name).Any(),
                $"no binding to {name} (plain or DataContext-qualified) in the main window");
            Assert.NotNull(typeof(MainViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
        }

        // The cue sliders write the level back, so two-way is part of the contract
        // this guard claims to protect.
        foreach (var name in new[] { "MonitorAGain", "MonitorBGain" })
            Assert.All(BoundTo(bindings, name), b => Assert.Contains("Mode=TwoWay", b.Value));

        // The engine carries the bus itself; MVSYNC is a property of the sliders,
        // so it is the one name that must stop at the view model.
        foreach (var name in new[] { "MonitorEnabled", "MonitorAGain", "MonitorBGain" })
            Assert.NotNull(typeof(JockeyBox.Audio.Graph.AudioEngine).GetProperty(name));

        // The deck header is a single TextBlock bound to Title; the prefix is part
        // of that string and DeckPrefix is what composes it.
        Assert.Contains(bindings, b => b.Path == "Title");
        Assert.NotNull(typeof(DeckViewModel).GetProperty("Title"));
        Assert.NotNull(typeof(DeckViewModel).GetProperty("DeckPrefix"));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Every parsed binding in the document. Comments contribute nothing: an
    /// XComment is not an attribute, so a name quoted in prose cannot satisfy an
    /// assertion here.
    /// </summary>
    private static List<(string Path, string Value)> BindingsIn(XDocument doc)
    {
        var bindings = new List<(string Path, string Value)>();
        foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes()))
        {
            string? path = DataContextPathOf(attribute.Value);
            if (path != null) bindings.Add((path, attribute.Value));
        }
        return bindings;
    }

    /// <summary>
    /// The path of a binding that resolves against a data context, plain
    /// (<c>Binding Name</c>) or re-rooted by RelativeSource (<c>Binding
    /// DataContext.Name, RelativeSource=...</c>). Null for everything else,
    /// including bindings that name their own Source or ElementName.
    /// </summary>
    private static string? DataContextPathOf(string value)
    {
        if (!value.StartsWith("{Binding", StringComparison.Ordinal)) return null;
        var body = value["{Binding".Length..].TrimEnd('}').Trim();
        if (body.Length == 0) return null;

        var arguments = body.Split(',');
        var head = arguments[0].Trim();
        if (head.StartsWith("Path=", StringComparison.Ordinal)) head = head["Path=".Length..].Trim();
        if (head.Length == 0) return null;

        // Source= and ElementName= point at some other object, so such a binding
        // is not a data-context path. Matched at the start of an argument: the
        // plain "Source=" contains-check would also reject RelativeSource=.
        bool NamesAnotherObject(string argument)
        {
            var a = argument.TrimStart();
            return a.StartsWith("Source=", StringComparison.Ordinal)
                || a.StartsWith("ElementName=", StringComparison.Ordinal);
        }

        if (NamesAnotherObject(head) || arguments.Skip(1).Any(NamesAnotherObject)) return null;
        return head;
    }

    /// <summary>
    /// The bindings that name this property, either directly or re-rooted through
    /// the window as <c>DataContext.&lt;name&gt;</c> (a RelativeSource binding on
    /// the shared card). The path is compared whole, so a name that is merely a
    /// prefix of the bound path does not count.
    /// </summary>
    private static IEnumerable<(string Path, string Value)> BoundTo(
        List<(string Path, string Value)> bindings, string name)
        => bindings.Where(b => b.Path == name || b.Path == "DataContext." + name);

    /// <summary>
    /// The LIB tabs that host Spotify and YouTube re-root their context, so everything
    /// inside them resolves on a different view model than the rest of the pane. Pinned
    /// by name: if those tabs move again, or lose their DataContext, this fails instead
    /// of the sweep quietly grading them against MainViewModel - which would report
    /// false failures on a correct change, or false passes on a broken one.
    /// </summary>
    [Fact]
    public void LibraryTabsReRootToTheirOwnViewModels()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "JockeyBox", "Views", "LibraryPanel.xaml"));

        foreach (var (header, expected) in new (string, Type)[]
                 {
                     ("SPOTIFY", typeof(SpotifyViewModel)),
                     ("YOUTUBE", typeof(YouTubeViewModel)),
                 })
        {
            var tab = doc.Descendants()
                .Where(e => e.Name.LocalName == "TabItem")
                .FirstOrDefault(e => (string?)e.Attribute("Header") == header);

            Assert.NotNull(tab);
            var host = tab!.Elements().FirstOrDefault();
            Assert.NotNull(host);
            // The pane's own context is MainViewModel; the tab re-roots it. Passing the
            // fallback is what the real walk does, and the point of the assertion.
            Assert.Equal(expected, DataContextType(host!, typeof(MainViewModel)));
        }
    }

    private static string MainWindowXaml() =>
        Path.Combine(RepoRoot(), "src", "JockeyBox", "Views", "MainWindow.xaml");

    /// <summary>The source tree the test is running against: the folder holding the solution.</summary>
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "JockeyBox.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>
    /// Every view's XAML, not just the main window. The library pane used to live inside
    /// MainWindow.xaml, so a guard that read only that file stopped watching 700 lines
    /// the moment the pane became its own view - a coverage regression caused by the
    /// extraction, and the reason this walk is part of the same commit.
    /// </summary>
    private static IEnumerable<string> ViewXamlFiles()
    {
        string views = Path.Combine(RepoRoot(), "src", "JockeyBox", "Views");
        return Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>
    /// What a binding on this element is bound to, or null when the XAML does not
    /// say (a Style's setters bind to the styled item, which is not a data
    /// context we can name here). A ControlTemplate does not change the data
    /// context - it inherits it - so it is walked through, not returned.
    /// </summary>
    private static Type? DataContextType(XElement element, Type? fallback = null)
    {
        for (var e = element; e != null; e = e.Parent)
        {
            // An explicit DataContext binding re-roots everything inside it
            // (the waveform strips point theirs at DeckA / DeckB).
            if (e.Attribute("DataContext") is { } dataContext)
            {
                var path = BindingPathOf(dataContext.Value);
                if (path == null) return null;
                var outer = DataContextType(e.Parent, fallback);
                return outer == null ? null : ResolveType(outer, path);
            }

            switch (e.Name.LocalName)
            {
                case "Style":
                    return null;                 // setters bind to the styled item
                case "DataTemplate" when e.Attribute("DataType") is { } dataType:
                    return TypeNamed(dataType.Value);
                case "DataTemplate":
                    return null;                 // implicit template: item type unknown
            }
        }
        return fallback;
    }

    private static Type? OuterContextType(XElement element, Type? fallback = null)
        => DataContextType(element.Parent, fallback);

    private static Type? TypeNamed(string xType)
    {
        // "{x:Type vm:DeckViewModel}" -> "DeckViewModel"
        var name = xType.Trim().TrimStart('{').TrimEnd('}');
        int colon = name.LastIndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = name.TrimStart('x', ':');
        return AppAssembly.GetTypes().FirstOrDefault(t => t.Name == name);
    }

    /// <summary>
    /// The property path of a binding, or null when it cannot be attributed to a
    /// data context (an explicit Source or ElementName names someone else).
    /// </summary>
    private static string? BindingPathOf(string value)
    {
        if (!value.StartsWith("{Binding", StringComparison.Ordinal)) return null;
        var body = value["{Binding".Length..].TrimEnd('}').Trim();
        if (body.Length == 0) return null;                      // {Binding} - the item itself
        if (body.Contains("Source=") || body.Contains("ElementName=")) return null;

        int comma = body.IndexOf(',');
        if (comma >= 0) body = body[..comma].Trim();
        if (body.StartsWith("Path=", StringComparison.Ordinal)) body = body["Path=".Length..].Trim();
        return body.Length == 0 ? null : body;
    }

    private static bool Resolves(Type type, string path) => ResolveType(type, path) != null;

    /// <summary>The final property's type, or null when any segment is missing.</summary>
    private static Type? ResolveType(Type type, string path)
    {
        Type current = type;
        foreach (var raw in path.Split('.'))
        {
            var name = raw;
            int bracket = name.IndexOf('[');
            if (bracket >= 0) name = name[..bracket];
            name = name.Trim();
            if (name.Length == 0) return null;

            var member = current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                      ?? (MemberInfo?)current.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (member == null) return null;
            current = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
        }
        return current;
    }
}
