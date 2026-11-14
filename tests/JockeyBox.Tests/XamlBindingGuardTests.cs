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
        string xaml = File.ReadAllText(MainWindowXaml());

        foreach (var name in new[] { "MonitorEnabled", "MonitorAGain", "MonitorBGain", "MonitorSyncEnabled" })
        {
            Assert.Contains($"Binding {name}", xaml);
            Assert.NotNull(typeof(MainViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
        }

        // The engine carries the bus itself; MVSYNC is a property of the sliders,
        // so it is the one name that must stop at the view model.
        foreach (var name in new[] { "MonitorEnabled", "MonitorAGain", "MonitorBGain" })
            Assert.NotNull(typeof(JockeyBox.Audio.Graph.AudioEngine).GetProperty(name));

        // The deck header is a single TextBlock bound to Title; the prefix is part
        // of that string and DeckPrefix is what composes it.
        Assert.Contains("{Binding Title}", xaml);
        Assert.NotNull(typeof(DeckViewModel).GetProperty("Title"));
        Assert.NotNull(typeof(DeckViewModel).GetProperty("DeckPrefix"));
    }

    // ---- helpers -----------------------------------------------------------

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
