using System.Reflection;
using System.Text.Json;

namespace JockeyBox.Settings;

/// <summary>
/// Three-way merge between the settings a dialog was built from, the settings on
/// disk right now, and the settings its controls now represent.
///
/// <para><b>Why.</b> A dialog that stamps controls once and then saves every
/// control back over the whole document reverts any setting that changed while it
/// was open - even a change the dialog itself provoked (the engine setup writes
/// <c>PythonPath</c> while the settings window is still up). Saving per field is
/// the fix; binding the whole window is not needed to get it.</para>
///
/// <para><b>Rule.</b> For every property: if the edited state differs from the
/// snapshot, the operator changed it, so the UI value wins. If it is unchanged,
/// the value from disk wins, so an outside change survives. Properties with no
/// control in the dialog are unchanged by definition and therefore carried from
/// disk.</para>
///
/// <para>Pure: takes three objects, returns a new one. No file access, no WPF.</para>
/// </summary>
public static class SettingsMerge
{
    private static readonly PropertyInfo[] Fields = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        // ExtensionData must stay out of the merge. SameValue below returns false
        // for null-vs-non-null, so a dialog-built object with no extension data
        // would be "changed" and its null would overwrite the disk map, deleting
        // every foreign key. Excluding it means unknown members always come from
        // disk, which is what this class promises above for properties the dialog
        // has no control for.
        .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(AppSettings.ExtensionData))
        .ToArray();

    /// <summary>
    /// Merge <paramref name="edited"/> (controls) over <paramref name="current"/>
    /// (disk), using <paramref name="snapshot"/> (what the dialog was built from)
    /// to tell an operator edit from an outside change.
    /// </summary>
    public static AppSettings Merge(AppSettings? snapshot, AppSettings? current, AppSettings? edited)
    {
        // Nothing was edited: hand back what the caller should write.
        if (edited is null) return current?.Clone() ?? snapshot?.Clone() ?? new AppSettings();

        // Without a baseline there is no way to attribute a difference, and
        // without a readable disk there is nothing to carry: keep the window's
        // state. This is the old behaviour, and it never overwrites live settings
        // with defaults because a read failed.
        if (snapshot is null || current is null) return edited.Clone();

        var merged = current.Clone();

        foreach (var field in Fields)
        {
            object? before = field.GetValue(snapshot);
            object? after = field.GetValue(edited);
            if (SameValue(before, after)) continue; // untouched: keep what is on disk
            field.SetValue(merged, after);          // operator edit: the UI wins
        }

        return merged;
    }

    /// <summary>
    /// Value equality that treats a collection rebuilt with the same content as
    /// unchanged. The mic list is re-materialised on every save from the rows the
    /// dialog shows, so reference equality would call it edited every time.
    /// </summary>
    private static bool SameValue(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.GetType().IsPrimitive || a is string || a is Enum) return a.Equals(b);

        try { return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b); }
        catch { return a.Equals(b); }
    }
}
