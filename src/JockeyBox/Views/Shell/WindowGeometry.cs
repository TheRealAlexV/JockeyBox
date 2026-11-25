using System.Windows;

namespace JockeyBox.Views.Shell;

/// <summary>
/// Putting a window back where it was, and refusing to put it somewhere nobody can
/// reach. A rectangle saved while a second monitor was attached would otherwise open
/// off-screen on a laptop dock, which reads as "the panel did not open".
///
/// <para>Shared by the lyrics pop-out and the panel windows so the screen test has one
/// copy; it was private to the old lyrics window until the panels needed the same
/// rule.</para>
/// </summary>
internal static class WindowGeometry
{
    /// <summary>Restore a saved rectangle, but only where a sliver of it is still reachable.</summary>
    public static void Restore(
        Window window,
        double? left,
        double? top,
        double? width,
        double? height,
        double fallbackWidth,
        double fallbackHeight)
    {
        if (width is > 200) window.Width = width.Value;
        if (height is > 150) window.Height = height.Value;

        if (left is null || top is null) return;

        var candidate = new Rect(
            left.Value,
            top.Value,
            double.IsNaN(window.Width) ? fallbackWidth : window.Width,
            double.IsNaN(window.Height) ? fallbackHeight : window.Height);

        if (!IsOnAnyScreen(candidate)) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = candidate.Left;
        window.Top = candidate.Top;
    }

    /// <summary>
    /// A sliver of the title bar has to be reachable, not the whole rectangle:
    /// partially off screen is a legitimate placement. Copied verbatim from the old
    /// lyrics window, which owned the only copy until the panels needed the same rule;
    /// that window is gone and both paths call this one now.
    /// </summary>
    public static bool IsOnAnyScreen(Rect candidate)
    {
        var screens = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var screensTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        return candidate.Left >= SystemParameters.VirtualScreenLeft - 8
               && candidate.Top >= SystemParameters.VirtualScreenTop - 8
               && candidate.Left + 80 <= screens
               && candidate.Top + 32 <= screensTop;
    }
}
