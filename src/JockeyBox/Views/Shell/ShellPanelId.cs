namespace JockeyBox.Views.Shell;

/// <summary>
/// Every pane the shell can dock. The set is closed on purpose: a pane is a place,
/// not a plugin, and each one has a rail toggle, a width and a memory of its own.
///
/// <para>The names double as the keys of <see cref="Settings.AppSettings.PanelLayout"/>,
/// so a panel id must never be renamed: the string in settings.json is the id.
/// <see cref="Library"/> is the only one wired in phase 1; phases 3 and 4 register
/// the rest in <see cref="PanelCatalog"/>.</para>
/// </summary>
public enum ShellPanelId
{
    Library,
    Lyrics,
    Mix,
    Routing,
    Sources,
    Virtual,
    Devices,
}

/// <summary>Settings keys for the panel set.</summary>
public static class ShellPanelIdNames
{
    /// <summary>The settings.json key this panel's layout is stored under.</summary>
    public static string Key(this ShellPanelId id) => id.ToString();
}
