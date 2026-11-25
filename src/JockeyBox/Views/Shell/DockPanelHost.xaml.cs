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

    private AppSettings? _settings;

    public DockPanelHost()
    {
        InitializeComponent();
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

    /// <summary>Open a panel, or replace the one on show. Content is built once, on first use.</summary>
    public void Show(ShellPanelId id)
    {
        if (!PanelCatalog.IsRegistered(id)) return;

        PanelDefinition definition = PanelCatalog.Get(id);
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
        if (Current is not { } id) return;
        if (!PanelCatalog.Get(id).AllowsPopOut) return;
        PoppedOut?.Invoke(this, id);
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
        if (Current == id) Close();
        return content;
    }

    /// <summary>Take a panel's content back from its window and reopen the dock on it.</summary>
    public void ReturnContent(ShellPanelId id, FrameworkElement content)
    {
        _live[id] = content;
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
        ApplyWidth(PanelCatalog.Get(id));
    }

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
        double windowWidth = Window.GetWindow(this)?.ActualWidth ?? 0;
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

        // Dragging right narrows the dock: the splitter is on the inner edge, so the
        // delta is subtracted from the width rather than added to it.
        Width = ClampWidth(Width - e.HorizontalChange, PanelCatalog.Get(id));
        e.Handled = true;
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => PersistWidth();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPopOutClick(object sender, RoutedEventArgs e) => PopOutCurrent();
}
