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
/// One cue on the karaoke pair: a slot only, because a pair action hits both
/// decks at once. Slot 0 is the pair's ephemeral quick cue, 1..8 the saved pads.
/// This is deliberately not a third <see cref="PlaybackDeck"/>: the deck enum
/// stays two-valued, and a pair is not a deck.
/// </summary>
public readonly record struct PairCueAction(int Slot)
{
    public bool IsQuick => Slot == 0;

    /// <summary>"P1".."P8"/"PQ" — short enough for a log line or a key caption.</summary>
    public string Label => $"P{(IsQuick ? "Q" : Slot.ToString())}";
}

/// <summary>
/// The pair cue id scheme: "cue.p.1".."cue.p.8" and "cue.p.q".
///
/// <para><b>Default chords</b> are on the family that is free on this rig: deck A
/// owns <c>Ctrl+Shift</c>, deck B owns <c>Ctrl+Alt+Shift</c>, so the pair takes
/// <c>Ctrl+Alt+1..8</c> / <c>Ctrl+Alt+Q</c> and collides with neither.</para>
///
/// <para>Ids are data, like the per-deck family: nothing here is hand-written
/// per slot.</para>
/// </summary>
public static class PairCueHotkeys
{
    /// <summary>Prefix that keeps pair cue ids distinct from the per-deck ones.</summary>
    public const string Prefix = "cue.p.";

    /// <summary>Every pair cue action: saved slots first, quick cue last.</summary>
    public static IReadOnlyList<PairCueAction> Actions { get; } = BuildActions();

    private static IReadOnlyList<PairCueAction> BuildActions()
    {
        var list = new List<PairCueAction>(9);
        for (int slot = 1; slot <= 8; slot++) list.Add(new PairCueAction(slot));
        list.Add(new PairCueAction(0));
        return list;
    }

    /// <summary>Canonical id for a pair action: "cue.p.3", "cue.p.q".</summary>
    public static string Id(PairCueAction action) =>
        Prefix + (action.IsQuick ? "q" : action.Slot.ToString());

    /// <summary>Parse "cue.p.3" / "cue.p.quick". Slot aliases: q, quick, 0.</summary>
    public static bool TryParseId(string? id, out PairCueAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(id)) return false;

        string text = id.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string slotText = text[Prefix.Length..];
        int slot;
        if (slotText.Equals("q", StringComparison.OrdinalIgnoreCase)
            || slotText.Equals("quick", StringComparison.OrdinalIgnoreCase)
            || slotText == "0")
            slot = 0;
        else if (!int.TryParse(slotText, out slot) || slot is < 1 or > 8)
            return false;

        action = new PairCueAction(slot);
        return true;
    }

    /// <summary>The shipped chord for an action: Ctrl+Alt plus the digit, or Q.</summary>
    public static string DefaultChord(PairCueAction action) =>
        $"Ctrl+Alt+{(action.IsQuick ? "Q" : action.Slot.ToString())}";

    /// <summary>Every pair action mapped to its default chord.</summary>
    public static Dictionary<string, string> Defaults()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions) map[Id(action)] = DefaultChord(action);
        return map;
    }

    /// <summary>
    /// The live pair plan: shipped defaults with any stored overrides layered on
    /// top, same rules as <see cref="CueHotkeys.Resolve"/> (unknown ids dropped,
    /// an empty chord stays deliberately unbound).
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

    /// <summary>
    /// True when a chord is safe to register system-wide: at least one modifier,
    /// or a function key on its own. A bare letter or digit would swallow that
    /// key everywhere on the desktop, including this app's own text boxes.
    /// </summary>
    public static bool HasRequiredModifier(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) return true;

        string normalised = GlobalHotkeyService.Normalize(chord);
        if (normalised.Contains('+')) return true;

        string key = normalised.ToUpperInvariant();
        return key.Length >= 2
            && key[0] == 'F'
            && int.TryParse(key[1..], out int fn)
            && fn is >= 1 and <= 24;
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
    /// <summary>Raised when a registered chord fires, with the id it was bound under.</summary>
    event Action<string>? Triggered;

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
            if (!TryResolveId(pair.Key, out string id, out string label)) continue;
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;

            if (!CueHotkeys.HasRequiredModifier(pair.Value))
            {
                string refusal = "needs a modifier (Ctrl/Alt/Shift/Win) unless the key is an F-key";
                _failures[id] = refusal;
                Warn($"cue hotkey {pair.Value} for {id} ({label}): refused, {refusal}");
                continue;
            }

            if (_registrar.TryBind(id, pair.Value, out string? error))
            {
                _live[id] = pair.Value;
                continue;
            }

            string message = error ?? "registration failed";
            _failures[id] = message;
            Warn($"cue hotkey {pair.Value} for {id} ({label}): {message}");
        }
    }

    /// <summary>
    /// Canonical id and caption for either cue family. The planner hands this
    /// class one merged dictionary, so a pair id has to resolve here too.
    /// </summary>
    private static bool TryResolveId(string key, out string id, out string label)
    {
        if (CueHotkeys.TryParseId(key, out var cue))
        {
            id = CueHotkeys.Id(cue);
            label = cue.Label;
            return true;
        }

        if (PairCueHotkeys.TryParseId(key, out var pair))
        {
            id = PairCueHotkeys.Id(pair);
            label = pair.Label;
            return true;
        }

        id = string.Empty;
        label = string.Empty;
        return false;
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

/// <summary>
/// One cue input surface: the binder plus its trigger subscription, able to go
/// through stop and attach again.
///
/// <para>This is the piece that used to leak. After <c>StopCueHotkeys</c> the
/// view model held a null binder, so the attach that follows a settings reload
/// bound nothing and logged nothing: every cue key stayed dead until restart.
/// Attaching is now self-sufficient, and a second attach rebinds the same
/// plan instead of stacking a second subscription.</para>
/// </summary>
public sealed class CueHotkeyInput : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoFailures =
        new Dictionary<string, string>();

    private readonly IHotkeyRegistrar _registrar;
    private readonly Action<CueAction> _fire;
    private readonly Action<PairCueAction>? _firePair;
    private CueHotkeyBinder? _binder;
    private bool _subscribed;

    public CueHotkeyInput(IHotkeyRegistrar registrar, Action<CueAction> fire,
                          Action<PairCueAction>? firePair = null)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _fire = fire ?? throw new ArgumentNullException(nameof(fire));
        _firePair = firePair;
    }

    /// <summary>Live registrations, 0 before the first attach and after a detach.</summary>
    public int Bound => _binder?.Count ?? 0;

    /// <summary>Actions that could not be registered, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Failures => _binder?.Failures ?? NoFailures;

    /// <summary>Register (or re-register) the plan and subscribe to triggers.</summary>
    public void Attach(IReadOnlyDictionary<string, string> plan)
    {
        _binder ??= new CueHotkeyBinder(_registrar);
        _binder.Bind(plan);

        if (_subscribed) return;
        _registrar.Triggered += OnTriggered;
        _subscribed = true;
    }

    /// <summary>Drop every registration and the subscription; attach can follow.</summary>
    public void Detach()
    {
        _binder?.UnbindAll();

        if (!_subscribed) return;
        _registrar.Triggered -= OnTriggered;
        _subscribed = false;
    }

    public void Dispose() => Detach();

    private void OnTriggered(string id)
    {
        if (CueHotkeys.TryParseId(id, out var action))
        {
            _fire(action);
            return;
        }

        if (_firePair != null && PairCueHotkeys.TryParseId(id, out var pair)) _firePair(pair);
    }
}
