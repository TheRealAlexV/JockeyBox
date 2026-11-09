using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using JockeyBox.Soundboard;
using JockeyBox.Tools;

namespace JockeyBox.Playback;

/// <summary>
/// System-wide hotkeys for soundboard pads (requirement 13).
///
/// <para><b>Why RegisterHotKey and not a keyboard hook.</b> A low-level hook runs
/// for every keystroke in the session and is the first thing anti-cheat and
/// keyboard-privacy tools flag; <c>RegisterHotKey</c> is the documented, per-key
/// mechanism and costs nothing when idle. The trade-off is that a combination
/// already owned by another application cannot be registered, which is reported
/// per binding rather than silently ignored.</para>
///
/// <para>Bindings are parsed from text ("F13", "Ctrl+Shift+1", "NumPad5") so the
/// settings file stays readable and a pad can be re-bound without a rebuild.</para>
/// </summary>
public sealed class GlobalHotkeyService : ISoundboardHotkeyHost, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, string> _idToPad = new();
    private readonly Dictionary<string, int> _padToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _padToText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _source;
    private IntPtr _handle;
    private int _nextId = 0xB000;

    /// <summary>Raised (on the UI thread) when a registered hotkey fires.</summary>
    public event Action<string>? PadTriggered;

    /// <summary>Bindings that could not be registered, keyed by pad id ("already in use" etc).</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>Number of live registrations (diagnostics and tests).</summary>
    public int Count => _padToId.Count;

    /// <summary>True once <see cref="Attach"/> has a window handle to register against.</summary>
    public bool IsAttached => _handle != IntPtr.Zero;

    /// <summary>Text currently registered for a pad, or null.</summary>
    public string? BindingOf(string padId) =>
        _padToText.TryGetValue(padId, out var text) ? text : null;

    /// <summary>
    /// The pad that already owns <paramref name="text"/>, if any. Case/order
    /// insensitive on purpose: "ctrl+1" and "Ctrl+1" are the same chord.
    /// </summary>
    public string? OwnerOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var pair in _padToText)
        {
            if (SameCombo(pair.Value, text)) return pair.Key;
        }

        return null;
    }


    /// <summary>Attach to a window so WM_HOTKEY messages arrive here.</summary>
    public bool Attach(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        _handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        if (_source == null) return false;
        _source.AddHook(WndProc);
        return true;
    }

    /// <summary>Register (or re-register) one pad's binding. Empty text clears it.</summary>
    public bool Bind(string padId, string? text) => TryBind(padId, text, out _);

    /// <summary>
    /// Register (or re-register) one pad's binding, reporting why it failed
    /// instead of throwing. An empty <paramref name="text"/> unbinds and counts as
    /// success. A chord another pad already owns is rejected here, before
    /// <c>RegisterHotKey</c> would report the confusing "another application" for
    /// a collision inside this process.
    /// </summary>
    public bool TryBind(string padId, string? text, out string? error)
    {
        error = null;
        Unbind(padId);

        if (string.IsNullOrWhiteSpace(text)) return true;

        if (_handle == IntPtr.Zero)
        {
            error = "the app window is not ready yet";
            _failures[padId] = error;
            Log.Warn($"hotkey '{text}' for pad {padId}: {error}");
            return false;
        }

        var normalised = Normalize(text);
        var owner = OwnerOf(normalised);
        if (owner != null && !string.Equals(owner, padId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{normalised}' is already bound to another pad";
            _failures[padId] = error;
            Log.Warn($"hotkey '{normalised}' for pad {padId}: {error}");
            return false;
        }

        if (!TryParse(normalised, out uint modifiers, out uint vk, out string parseError))
        {
            error = parseError;
            _failures[padId] = error;
            Log.Warn($"hotkey '{text}' for pad {padId}: {error}");
            return false;
        }

        int id = _nextId++;
        if (!RegisterHotKey(_handle, id, modifiers | ModNoRepeat, vk))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? $"'{normalised}' is already registered by another application"
                : $"registration of '{normalised}' failed (error {code})";
            _failures[padId] = error;
            Log.Warn($"hotkey '{normalised}' for pad {padId}: {error}");
            return false;
        }

        _idToPad[id] = padId;
        _padToId[padId] = id;
        _padToText[padId] = normalised;
        _failures.Remove(padId);
        Log.Info($"hotkey '{normalised}' bound to pad {padId}");
        return true;
    }

    /// <summary>Remove one pad's binding.</summary>
    public void Unbind(string padId)
    {
        _failures.Remove(padId);
        _padToText.Remove(padId);

        if (!_padToId.TryGetValue(padId, out int id)) return;
        try { UnregisterHotKey(_handle, id); } catch { }
        _padToId.Remove(padId);
        _idToPad.Remove(id);
    }

    /// <summary>Remove every binding (settings reload / shutdown).</summary>
    public void UnbindAll()
    {
        foreach (int id in new List<int>(_idToPad.Keys))
        {
            try { UnregisterHotKey(_handle, id); } catch { }
        }
        _idToPad.Clear();
        _padToId.Clear();
        _padToText.Clear();
        _failures.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (!_idToPad.TryGetValue(id, out string? padId)) return IntPtr.Zero;

        handled = true;
        PadTriggered?.Invoke(padId);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Parse "Ctrl+Shift+1", "F13", "Alt+NumPad5", "Space". Modifiers may appear in
    /// any order; the last token is the key.
    /// </summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk, out string error)
    {
        modifiers = 0;
        vk = 0;
        error = string.Empty;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "empty binding";
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool isLast = i == parts.Length - 1;

            if (!isLast)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": modifiers |= ModControl; break;
                    case "shift": modifiers |= ModShift; break;
                    case "alt": modifiers |= ModAlt; break;
                    case "win":
                    case "windows": modifiers |= ModWin; break;
                    default:
                        error = $"unknown modifier '{part}'";
                        return false;
                }
                continue;
            }

            if (!TryParseKey(part, out vk))
            {
                error = $"unknown key '{part}'";
                return false;
            }
        }

        if (vk == 0)
        {
            error = "no key";
            return false;
        }
        return true;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;
        string upper = key.ToUpperInvariant();

        // Function keys F1..F24
        if (upper.Length >= 2 && upper[0] == 'F' && int.TryParse(upper[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        // Single character: A-Z, 0-9
        if (upper.Length == 1)
        {
            char c = upper[0];
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }

        // Numpad digits are spelled "NumPad5"; the other numpad keys live in the
        // shared table so the parser and the formatter agree on every name.
        if (upper.StartsWith("NUMPAD", StringComparison.Ordinal)
            && upper.Length == 7
            && upper[6] is >= '0' and <= '9')
        {
            vk = (uint)(0x60 + (upper[6] - '0'));
            return true;
        }

        // Spelling aliases that users and old config files still carry.
        if (upper is "RETURN") { vk = 0x0D; return true; }
        if (upper is "ESC") { vk = 0x1B; return true; }
        if (upper is "BACK" or "BS") { vk = 0x08; return true; }
        if (upper is "PGUP") { vk = 0x21; return true; }
        if (upper is "PGDN") { vk = 0x22; return true; }

        if (NamedKeys.TryGetValue(key, out uint code))
        {
            vk = code;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Canonical spelling of a chord: modifiers in Ctrl+Alt+Shift+Win order, key
    /// upper-case ("ctrl+shift+1" → "Ctrl+Shift+1"). Unparseable text comes back
    /// trimmed so the caller can still show what the user typed.
    /// </summary>
    public static string Normalize(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return "";
        return TryParse(value, out uint modifiers, out uint vk, out _)
            ? Format(modifiers, vk)
            : value;
    }

    /// <summary>True when both texts describe the same chord.</summary>
    public static bool SameCombo(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Text for the chord the user just pressed, or null when it cannot be a
    /// global hotkey (modifier only, or a key with no virtual-key mapping).
    /// </summary>
    public static string? Describe(ModifierKeys modifiers, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;

        if (!TryVirtualKey(key, out uint vk)) return null;

        uint mods = 0;
        if ((modifiers & ModifierKeys.Control) != 0) mods |= ModControl;
        if ((modifiers & ModifierKeys.Alt) != 0) mods |= ModAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) mods |= ModShift;
        if ((modifiers & ModifierKeys.Windows) != 0) mods |= ModWin;

        return Format(mods, vk);
    }

    /// <summary>WPF key → Win32 virtual key for the keys a soundboard pad can use.</summary>
    public static bool TryVirtualKey(Key key, out uint vk)
    {
        vk = 0;

        if (key is >= Key.A and <= Key.Z) { vk = (uint)('A' + ((int)key - (int)Key.A)); return true; }
        if (key is >= Key.D0 and <= Key.D9) { vk = (uint)('0' + ((int)key - (int)Key.D0)); return true; }
        if (key is >= Key.NumPad0 and <= Key.NumPad9) { vk = (uint)(0x60 + ((int)key - (int)Key.NumPad0)); return true; }
        if (key is >= Key.F1 and <= Key.F24) { vk = (uint)(0x70 + ((int)key - (int)Key.F1)); return true; }

        vk = key switch
        {
            Key.Space => 0x20,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Back => 0x08,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Pause => 0x13,
            Key.Scroll => 0x91,
            Key.CapsLock => 0x14,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            _ => 0,
        };

        return vk != 0;
    }

    private static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(NameOfVk(vk));
        return string.Join("+", parts);
    }

    private static string NameOfVk(uint vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "NumPad" + (vk - 0x60);

        var name = NamedKeys.FirstOrDefault(pair => pair.Value == vk).Key;
        return string.IsNullOrEmpty(name) ? $"0x{vk:X2}" : name;
    }

    /// <summary>Named keys, shared by the parser and the formatter so they cannot drift.</summary>
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Pause"] = 0x13, ["ScrollLock"] = 0x91, ["CapsLock"] = 0x14,
        ["NumPadMultiply"] = 0x6A, ["NumPadAdd"] = 0x6B, ["NumPadSubtract"] = 0x6D,
        ["NumPadDecimal"] = 0x6E, ["NumPadDivide"] = 0x6F,
    };

    public void Dispose()
    {
        UnbindAll();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _handle = IntPtr.Zero;
    }
}
