using System;
using System.Collections.Generic;
using JockeyBox.Tools;

namespace JockeyBox.Playback;

/// <summary>
/// One cue an input surface can fire: a deck and a slot number. Slot 0 is the
/// deck's ephemeral quick cue, 1..8 the saved pads.
/// </summary>
public readonly record struct CueAction(PlaybackDeck Deck, int Slot)
{
    public bool IsQuick => Slot == 0;

    /// <summary>"A1".."A8"/"AQ" — short enough for a log line or a key caption.</summary>
    public string Label => $"{(Deck == PlaybackDeck.A ? "A" : "B")}{(IsQuick ? "Q" : Slot.ToString())}";
}

/// <summary>
/// The cue/action identifier scheme shared by hotkeys and the HTTP surface.
///
/// <para><b>Ids are data.</b> Eighteen recall actions ("cue.a.1".."cue.a.8",
/// "cue.a.q", same for B) are generated from <see cref="Actions"/>; nothing in
/// this feature has eighteen hand-written handlers or eighteen named fields.</para>
///
/// <para><b>Default chords.</b> F13–F24 are conventionally free on Windows, but
/// they are <i>not</i> free in this app: <c>SoundboardViewModel.NextFreeHotkey</c>
/// hands them to soundboard pads, and the rig's adv-ss already owns F20. So the
/// defaults use two-modifier chords on the digit row, which the window's own
/// bindings (Ctrl+Q, Ctrl+Shift+Left/Right) and the soundboard both ignore:</para>
/// <list type="bullet">
/// <item>Deck A: Ctrl+Shift+1..8, quick cue Ctrl+Shift+Q</item>
/// <item>Deck B: Ctrl+Alt+Shift+1..8, quick cue Ctrl+Alt+Shift+Q</item>
/// </list>
/// </summary>
public static class CueHotkeys
{
    /// <summary>Prefix that keeps cue ids distinguishable from soundboard pad ids.</summary>
    public const string Prefix = "cue.";

    /// <summary>Every cue action, deck A then B, saved slots first.</summary>
    public static IReadOnlyList<CueAction> Actions { get; } = BuildActions();

    private static IReadOnlyList<CueAction> BuildActions()
    {
        var list = new List<CueAction>(18);
        foreach (var deck in new[] { PlaybackDeck.A, PlaybackDeck.B })
        {
            for (int slot = 1; slot <= 8; slot++) list.Add(new CueAction(deck, slot));
            list.Add(new CueAction(deck, 0));
        }

        return list;
    }

    /// <summary>Canonical id for an action: "cue.a.3", "cue.b.q".</summary>
    public static string Id(CueAction action) =>
        Prefix + (action.Deck == PlaybackDeck.A ? "a" : "b") + "." + (action.IsQuick ? "q" : action.Slot.ToString());

    /// <summary>Parse "cue.a.3" / "cue.b.quick". Slot aliases: q, quick, 0.</summary>
    public static bool TryParseId(string? id, out CueAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(id)) return false;

        string text = id.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var parts = text[Prefix.Length..].Split('.');
        if (parts.Length != 2) return false;

        PlaybackDeck deck;
        if (parts[0].Equals("a", StringComparison.OrdinalIgnoreCase)) deck = PlaybackDeck.A;
        else if (parts[0].Equals("b", StringComparison.OrdinalIgnoreCase)) deck = PlaybackDeck.B;
        else return false;

        string slotText = parts[1];
        int slot;
        if (slotText.Equals("q", StringComparison.OrdinalIgnoreCase)
            || slotText.Equals("quick", StringComparison.OrdinalIgnoreCase)
            || slotText == "0")
            slot = 0;
        else if (!int.TryParse(slotText, out slot) || slot is < 1 or > 8)
            return false;

        action = new CueAction(deck, slot);
        return true;
    }

    /// <summary>The shipped chord for one action; never null.</summary>
    public static string DefaultChord(CueAction action)
    {
        string suffix = action.IsQuick ? "Q" : action.Slot.ToString();
        return action.Deck == PlaybackDeck.A ? $"Ctrl+Shift+{suffix}" : $"Ctrl+Alt+Shift+{suffix}";
    }

    /// <summary>Every action mapped to its default chord.</summary>
    public static Dictionary<string, string> Defaults()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions) map[Id(action)] = DefaultChord(action);
        return map;
    }

    /// <summary>
    /// The live plan: shipped defaults with the operator's settings layered on
    /// top. Unknown ids are dropped rather than registered as dead keys; an empty
    /// chord is kept as "deliberately unbound" so clearing a key in settings
    /// really clears it instead of reviving the default.
    /// </summary>
    public static SortedDictionary<string, string> Resolve(IReadOnlyDictionary<string, string>? stored)
    {
        var plan = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions) plan[Id(action)] = DefaultChord(action);

        if (stored == null) return plan;

        foreach (var pair in stored)
        {
            if (!TryParseId(pair.Key, out var action)) continue;
            plan[Id(action)] = pair.Value?.Trim() ?? string.Empty;
        }

        return plan;
    }
}

/// <summary>
/// The slice of <see cref="GlobalHotkeyService"/> the cue binder needs. Keeping
/// it an interface lets the conflict path be tested headlessly with a fake
/// (RegisterHotKey itself cannot be exercised in CI).
/// </summary>
public interface IHotkeyRegistrar
{
    bool TryBind(string actionId, string? chord, out string? error);

    void Unbind(string actionId);
}

/// <summary>
/// Registers the cue plan through an <see cref="IHotkeyRegistrar"/> and keeps
/// the outcome visible: a chord another application owns is recorded and logged
/// with the combination, and the rest of the plan still binds.
///
/// <para>Teardown is part of the contract: <see cref="UnbindAll"/> runs before
/// every rebind, because a settings change that leaves the old registrations
/// alive makes the old chords fire the new actions.</para>
/// </summary>
public sealed class CueHotkeyBinder : IDisposable
{
    private readonly IHotkeyRegistrar _registrar;
    private readonly Action<string>? _warn;
    private readonly Dictionary<string, string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    public CueHotkeyBinder(IHotkeyRegistrar registrar, Action<string>? warn = null)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _warn = warn;
    }

    /// <summary>Actions that could not be registered, keyed by action id.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>Number of live registrations.</summary>
    public int Count => _live.Count;

    /// <summary>The chord currently registered for an action, or null.</summary>
    public string? BindingOf(CueAction action) =>
        _live.TryGetValue(CueHotkeys.Id(action), out var chord) ? chord : null;

    /// <summary>
    /// Replace every cue registration with <paramref name="plan"/>. Successes go
    /// to <see cref="Count"/>; failures to <see cref="Failures"/> and the log.
    /// </summary>
    public void Bind(IReadOnlyDictionary<string, string> plan)
    {
        UnbindAll();

        foreach (var pair in plan)
        {
            if (!CueHotkeys.TryParseId(pair.Key, out var action)) continue;
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;

            string id = CueHotkeys.Id(action);
            if (_registrar.TryBind(id, pair.Value, out string? error))
            {
                _live[id] = pair.Value;
                continue;
            }

            string message = error ?? "registration failed";
            _failures[id] = message;
            Warn($"cue hotkey {pair.Value} ({action.Label}): {message}");
        }
    }

    /// <summary>Drop every cue registration this binder owns.</summary>
    public void UnbindAll()
    {
        foreach (string id in _live.Keys) _registrar.Unbind(id);
        _live.Clear();
        _failures.Clear();
    }

    public void Dispose() => UnbindAll();

    private void Warn(string message)
    {
        if (_warn != null) _warn(message);
        else Log.Warn(message);
    }
}
