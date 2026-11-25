namespace JockeyBox.Settings;

/// <summary>
/// What the shell remembers about one panel: the width the operator dragged the dock
/// to, where the panel's own window was left, and whether that window stays on top.
///
/// <para>A class rather than a struct so <see cref="AppSettings.Panel"/> can hand out a
/// reference the caller writes through; serialised as a nested object inside
/// settings.json under <c>PanelLayout</c>.</para>
/// </summary>
public sealed class PanelLayoutEntry
{
    /// <summary>Docked width in pixels, or 0 when the operator has never dragged it.</summary>
    public double Width { get; set; }

    /// <summary>Saved window left edge in screen pixels, or null.</summary>
    public double? WindowLeft { get; set; }

    /// <summary>Saved window top edge in screen pixels, or null.</summary>
    public double? WindowTop { get; set; }

    /// <summary>Saved window width in pixels, or null.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Saved window height in pixels, or null.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Whether the panel's own window stays above other windows.</summary>
    public bool Topmost { get; set; }
}
