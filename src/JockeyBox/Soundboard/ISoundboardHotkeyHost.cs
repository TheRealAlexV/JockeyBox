namespace JockeyBox.Soundboard;

/// <summary>
/// What the soundboard needs from the system-wide hotkey layer.
///
/// <para>Kept as an interface so <see cref="ViewModels.SoundboardViewModel"/> can
/// be exercised in tests without a window handle: the real implementation is
/// <see cref="Playback.GlobalHotkeyService"/>, which needs a WPF window and
/// <c>RegisterHotKey</c>; the test double just records bindings.</para>
/// </summary>
public interface ISoundboardHotkeyHost
{
    /// <summary>True once the host can actually register keys (a window exists).</summary>
    bool IsAttached { get; }

    /// <summary>Text currently registered for a pad, or null.</summary>
    string? BindingOf(string padId);

    /// <summary>The pad that already owns <paramref name="text"/>, if any.</summary>
    string? OwnerOf(string? text);

    /// <summary>
    /// Register (or re-register) one pad's chord, reporting failure as text
    /// instead of throwing: a key owned by another application is a status-line
    /// problem, not a crash.
    /// </summary>
    bool TryBind(string padId, string? text, out string? error);

    /// <summary>Remove one pad's registration.</summary>
    void Unbind(string padId);

    /// <summary>Remove every registration (reload / shutdown).</summary>
    void UnbindAll();
}
