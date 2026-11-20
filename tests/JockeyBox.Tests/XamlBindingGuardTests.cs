using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using JockeyBox.ViewModels;
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

    [Fact]
    public void EveryBindingPathInTheMainWindowResolvesOnItsDataContext()
    {
        var doc = XDocument.Load(MainWindowXaml());
        var failures = new List<string>();
        int checkedBindings = 0;

        foreach (var element in doc.Descendants())
        {
            Type? context = DataContextType(element);
            if (context == null) continue;

            foreach (var attribute in element.Attributes())
            {
                string? path = BindingPathOf(attribute.Value);
                if (path == null) continue;

                // A DataContext binding re-roots what is inside, so it is checked
                // against the context it replaces, not the one it creates.
                var target = attribute.Name.LocalName == "DataContext"
                    ? OuterContextType(element)
                    : context;
                if (target == null) continue;

                checkedBindings++;
                if (!Resolves(target, path))
                    failures.Add($"\"{{Binding {path}}}\" on {element.Name.LocalName}.{attribute.Name.LocalName} " +
                                 $"does not resolve on {target.Name}");
            }
        }

        Assert.True(checkedBindings > 100,
                    $"only {checkedBindings} bindings were walked; the guard is not reading the window");
        Assert.Empty(failures);
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

    private static string MainWindowXaml()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "JockeyBox.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "src", "JockeyBox", "Views", "MainWindow.xaml");
    }

    /// <summary>
    /// What a binding on this element is bound to, or null when the XAML does not
    /// say (a Style's setters bind to the styled item, which is not a data
    /// context we can name here). A ControlTemplate does not change the data
    /// context - it inherits it - so it is walked through, not returned.
    /// </summary>
    private static Type? DataContextType(XElement element)
    {
        for (var e = element; e != null; e = e.Parent)
        {
            // An explicit DataContext binding re-roots everything inside it
            // (the waveform strips point theirs at DeckA / DeckB).
            if (e.Attribute("DataContext") is { } dataContext)
            {
                var path = BindingPathOf(dataContext.Value);
                if (path == null) return null;
                var outer = e.Parent == null ? typeof(MainViewModel) : DataContextType(e.Parent);
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
        return typeof(MainViewModel);
    }

    private static Type? OuterContextType(XElement element)
        => element.Parent == null ? typeof(MainViewModel) : DataContextType(element.Parent);

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
