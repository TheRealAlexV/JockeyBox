using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using JockeyBox.Settings;

namespace JockeyBox.Views.Shell;

/// <summary>
/// The shell's dock column: one registered panel at a time, a splitter on its inner
/// edge, and content that outlives every state change.
///
/// <para><b>Why content survives.</b> Closing collapses the column to zero width but
/// keeps the panel's element in <see cref="_live"/>, and popping out hands that same
/// element to a <see cref="PanelWindow"/>. Rebuilding a pane instead would reset scroll
/// position, selection and any running playback the pane owns, so a close/open or a
/// pop-out/pop-in would look like the operator hit refresh.</para>
///
/// <para><b>Who owns what.</b> The host does not own windows: <see cref="PoppedOut"/>
/// reports the request and <c>MainWindow</c> creates, remembers and closes the
/// <see cref="PanelWindow"/>. The host only gives its element up
/// (<see cref="DetachContent"/>) and takes it back (<see cref="ReturnContent"/>).</para>
/// </summary>
public partial class DockPanelHost : UserControl
{
    /// <summary>
    /// Width the centre of the shell needs before the dock may take anything: the rail
    /// and the two deck cards plus the transport. A dock wider than the window minus
    /// this leaves the centre a strip, so the ceiling is derived, never fixed - the
    /// same rule the old library-only clamp used, made generic for every panel.
    /// </summary>
    private const double CentreFloor = 855;

    /// <summary>Content per panel, kept whether the panel is docked, closed or popped out.</summary>
    private readonly Dictionary<ShellPanelId, FrameworkElement> _live = new();

    /// <summary>
    /// The panels this host may show, keyed by id. Registered by whoever builds the
    /// host rather than read from a static list, so "the dock holds these panes" is a
    /// property of this instance: a test can hand it two trivial panes, and phase 3
    /// adds MIX by adding one entry to the production list rather than by editing
    /// the host.
    /// </summary>
    private readonly Dictionary<ShellPanelId, PanelDefinition> _panels = new();

    /// <summary>
    /// Panels currently out in their own window. Recorded here because the dock has
    /// no content for them: without it a second pop-out would rebuild a pane that a
    /// window already holds, or silently steal it back into the column.
    /// </summary>
    private readonly HashSet<ShellPanelId> _poppedOut = new();

    private AppSettings? _settings;

    /// <summary>Production: the XAML-created instance shows the real panel set.</summary>
    public DockPanelHost()
        : this(PanelCatalog.Registered)
    {
    }

    /// <summary>Every panel this host may show. XAML keeps the parameterless form.</summary>
    public DockPanelHost(IEnumerable<PanelDefinition> definitions)
    {
        InitializeComponent();
        foreach (var definition in definitions) _panels[definition.Id] = definition;
    }

    /// <summary>The panel on show, or null when the dock is closed.</summary>
    public ShellPanelId? Current { get; private set; }

    /// <summary>True while a panel occupies the column.</summary>
    public bool IsOpen => Current != null;

    /// <summary>Raised when the operator asks for the current panel in its own window.</summary>
    public event EventHandler<ShellPanelId>? PoppedOut;

    /// <summary>
    /// Hand the host the settings object it reads and writes widths in. Separate from
    /// the constructor because XAML creates the control, and the window already owns
    /// one settings instance that must not be reloaded (a second load would drop
    /// unsaved changes).
    /// </summary>
    public void Bind(AppSettings settings) => _settings = settings;

    /// <summary>Every panel this host can show.</summary>
    public IEnumerable<PanelDefinition> Registered => _panels.Values;

    /// <summary>The definition for a panel, whether or not it is registered.</summary>
    public PanelDefinition? Definition(ShellPanelId id) =>
        _panels.TryGetValue(id, out var definition) ? definition : null;

    /// <summary>Open a panel, or replace the one on show. Content is built once, on first use.</summary>
    public void Show(ShellPanelId id)
    {
        if (!_panels.TryGetValue(id, out var definition)) return;

        // A panel out in its own window owns its content: reparenting it back into the
        // column would empty the window while the operator was looking at it. Ask the
        // window layer to focus what it already has instead.
        if (_poppedOut.Contains(id))
        {
            PoppedOut?.Invoke(this, id);
            return;
        }

        if (!_live.TryGetValue(id, out var content))
        {
            content = definition.Create();
            _live[id] = content;
        }

        PanelContent.Reparent(content, ContentHost);

        Caption.Text = definition.Title;
        AutomationProperties.SetName(this, definition.AccessibleName);
        PopOutButton.Visibility = definition.AllowsPopOut ? Visibility.Visible : Visibility.Collapsed;

        Current = id;
        Visibility = Visibility.Visible;
        ApplyWidth(definition);
    }

    /// <summary>Close the panel if it is the one on show, otherwise show it.</summary>
    public void Toggle(ShellPanelId id)
    {
        if (Current == id) Close();
        else Show(id);
    }

    /// <summary>Collapse the column, keeping the content alive and its width remembered.</summary>
    public void Close()
    {
        Current = null;
        Visibility = Visibility.Collapsed;
        Width = 0;
    }

    /// <summary>Ask for the current panel in its own window. The window itself is the listener's job.</summary>
    public void PopOutCurrent()
    {
        // An already-popped panel answers again rather than being rebuilt: the window
        // layer may have lost the event (a second press of the button, a restart of the
        // window) and focusing the surface it holds is cheaper than a second copy.
        if (Current is { } id && _panels.TryGetValue(id, out var docked) && docked.AllowsPopOut)
        {
            PoppedOut?.Invoke(this, id);
            return;
        }

        if (Current is null && _poppedOut.Count > 0)
        {
            foreach (var popped in _poppedOut)
            {
                if (_panels.TryGetValue(popped, out var definition) && definition.AllowsPopOut)
                {
                    PoppedOut?.Invoke(this, popped);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Take the live element out of the dock so a window can show it. Not in the
    /// original sketch (which only described the return trip): the window has to hold
    /// the same element, and nothing else can hand it over without a second copy.
    /// </summary>
    public FrameworkElement? DetachContent(ShellPanelId id)
    {
        if (!_live.TryGetValue(id, out var content)) return null;

        if (ReferenceEquals(ContentHost.Content, content)) ContentHost.Content = null;
        _poppedOut.Add(id);
        if (Current == id) Close();
        return content;
    }

    /// <summary>Take a panel's content back from its window and reopen the dock on it.</summary>
    public void ReturnContent(ShellPanelId id, FrameworkElement content)
    {
        _live[id] = content;
        _poppedOut.Remove(id);
        Show(id);
    }

    /// <summary>The live element for a panel, if it has ever been built.</summary>
    public T? ContentOf<T>(ShellPanelId id) where T : FrameworkElement =>
        _live.TryGetValue(id, out var content) ? content as T : null;

    /// <summary>
    /// The height the shell's body ended up with. Set from the window's layout pass
    /// because the shell scrolls: a star row measured at infinite height would leave
    /// the panel stretching to nothing.
    /// </summary>
    public void SetBodyHeight(double height)
    {
        // NaN is the "auto" default and every comparison against NaN is false, so the
        // guard has to treat it as "not set yet" rather than "already correct" - the
        // same trap the window's own copy of this height had before it moved here.
        if (height <= 0) return;
        if (double.IsNaN(Height) || Math.Abs(Height - height) > 0.5) Height = height;
    }

    /// <summary>
    /// Keep the remembered width inside what the window can afford. Called from the
    /// layout pass: the width is the operator's choice and is never recomputed, only
    /// clamped down when the window itself is too narrow to hold it.
    /// </summary>
    public void FitToWindow()
    {
        if (Current is not { } id) return;
        if (_panels.TryGetValue(id, out var definition)) ApplyWidth(definition);
    }

    /// <summary>
    /// Client width of the shell the dock sits in, or NaN to read it from the owning
    /// window. Set directly by a host-level test: the clamp's whole job is arithmetic
    /// against this number, and a test that had to build a <c>Window</c> to supply it
    /// would be exercising WPF's layout rather than the rule.
    /// </summary>
    internal double AvailableWidth { get; set; } = double.NaN;

    private double WindowWidth =>
        double.IsNaN(AvailableWidth) ? Window.GetWindow(this)?.ActualWidth ?? 0 : AvailableWidth;

    private void ApplyWidth(PanelDefinition definition)
    {
        Width = ClampWidth(RequestedWidth(definition), definition);
    }

    private double RequestedWidth(PanelDefinition definition)
    {
        if (_settings != null
            && _settings.PanelLayout.TryGetValue(definition.Id.Key(), out var entry)
            && entry.Width > 0)
        {
            return entry.Width;
        }
        return definition.DefaultWidth;
    }

    private double ClampWidth(double wanted, PanelDefinition definition)
    {
        double windowWidth = WindowWidth;
        double ceiling = windowWidth > 0
            ? Math.Max(definition.MinWidth, windowWidth - CentreFloor)
            : definition.DefaultWidth;
        return Math.Max(definition.MinWidth, Math.Min(wanted, ceiling));
    }

    private void PersistWidth()
    {
        if (_settings == null || Current is not { } id) return;

        var entry = _settings.Panel(id);
        if (Math.Abs(entry.Width - Width) < 0.5) return;
        entry.Width = Width;
        _settings.Save();
    }

    private void OnSplitterDrag(object sender, DragDeltaEventArgs e)
    {
        if (Current is not { } id) return;
        if (!_panels.TryGetValue(id, out var definition)) return;

        // Dragging right narrows the dock: the splitter is on the inner edge, so the
        // delta is subtracted from the width rather than added to it.
        Width = ClampWidth(Width - e.HorizontalChange, definition);
        e.Handled = true;
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => PersistWidth();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPopOutClick(object sender, RoutedEventArgs e) => PopOutCurrent();
}
