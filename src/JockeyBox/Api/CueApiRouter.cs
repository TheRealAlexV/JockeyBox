using System;
using System.Collections.Generic;
using System.Text.Json;
using JockeyBox.Playback;
using JockeyBox.Tools;

namespace JockeyBox.Api;

/// <summary>
/// The cue/transport surface the HTTP router calls. One interface, implemented
/// over the existing view models, so a deck or cue can only be reached through
/// the same path the UI uses: no parallel play/pause, no second seek.
/// </summary>
public interface ICueActionTarget
{
    CueSurface Snapshot();

    bool Recall(PlaybackDeck deck, int slot, out long positionMs, out string error);

    bool Capture(PlaybackDeck deck, int slot, out long positionMs, out string error);

    void PlayPause(PlaybackDeck deck);

    bool SetABMode(bool enabled, out string error);

    bool SetSync(string name, bool enabled, out string error);
}

/// <summary>One cue slot as the API reports it. Slot 0 is the quick cue.</summary>
public sealed record CueSlotState(int Slot, long PositionMs, bool HasPosition);

/// <summary>One deck's bank: which track the cues belong to, and each slot.</summary>
public sealed record CueDeckState(string Deck, long TrackId, IReadOnlyList<CueSlotState> Slots);

/// <summary>Everything a Stream Deck key needs to render the cue surface.</summary>
public sealed record CueSurface(
    IReadOnlyList<CueDeckState> Decks,
    bool ABMode,
    bool DeckSync,
    bool VolumeSync,
    bool TempoSync);

/// <summary>HTTP status plus the JSON body to write.</summary>
public readonly record struct RouteOutcome(int Status, string Json)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Relaxed escaping keeps the echoed text readable ("unknown slot '9'",
        // "Ctrl+Shift+3") instead of turning quotes and plus signs into \u0027 /
        // \u002B, which also made the refusal text hard to match for a plugin.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RouteOutcome Ok(object payload) => new(200, JsonSerializer.Serialize(payload, Options));

    public static RouteOutcome Reject(int status, string message) =>
        new(status, JsonSerializer.Serialize(new { ok = false, error = message }, Options));

    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// Routes the cue half of the loopback API as plain data: method + path in,
/// status + JSON out. No HttpListener, no WPF, no audio, so every refusal and
/// every "reaches the deck exactly once" claim is testable headlessly.
///
/// <para><b>Routes</b> (all loopback-only; the listener is the boundary):</para>
/// <list type="bullet">
/// <item><c>GET /cues</c> — both banks, the A-B and sync state</item>
/// <item><c>POST /cue/{a|b}/{1-8|q|quick}/recall</c> — jump to a stored cue</item>
/// <item><c>POST /cue/{a|b}/{1-8|q|quick}/capture</c> — store the current media position</item>
/// <item><c>POST /transport/{a|b}/playpause</c> — the deck's own PlayPause command</item>
/// <item><c>POST /transport/ab</c> with <c>{"enabled":true}</c></item>
/// <item><c>POST /transport/sync/{deck|volume|tempo}</c> with <c>{"enabled":true}</c></item>
/// </list>
///
/// <para>Anything else is refused with a 4xx that names what was wrong and a
/// log line, because a silent no-op on stream is indistinguishable from a dead key.</para>
/// </summary>
public sealed class CueApiRouter
{
    private readonly ICueActionTarget _target;
    private readonly Action<string>? _warn;

    public CueApiRouter(ICueActionTarget target, Action<string>? warn = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _warn = warn;
    }

    /// <summary>True when this path belongs to the cue surface (the listener uses it as a filter).</summary>
    public static bool Handles(string path) =>
        path.Equals("/cues", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/cue/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/transport/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Route one request. Never throws; every failure is a 4xx/5xx outcome.</summary>
    public RouteOutcome Handle(string method, string path, string? body)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string verb = (method ?? string.Empty).ToUpperInvariant();

        try
        {
            if (verb == "GET" && segments.Length == 1 && Is(segments[0], "cues"))
                return RouteOutcome.Ok(_target.Snapshot());

            if (segments.Length == 4 && Is(segments[0], "cue") && verb == "POST")
                return HandleCue(segments[1], segments[2], segments[3], method!, path!);

            if (segments.Length >= 2 && Is(segments[0], "transport") && verb == "POST")
                return HandleTransport(segments, method!, path!, body);

            return Reject(404, $"unknown action for {verb} {path}", method, path);
        }
        catch (Exception ex)
        {
            // The caller gets a generic message; the detail stays in the log so a
            // plugin or a browser cannot learn this process's internals.
            Warn($"{method} {path}: {ex.GetType().Name}: {ex.Message}");
            return RouteOutcome.Reject(500, "internal error; see the local api log");
        }
    }

    private RouteOutcome HandleCue(string deckText, string slotText, string action, string method, string path)
    {
        if (!TryDeck(deckText, out var deck))
            return Reject(400, $"unknown deck '{deckText}'; expected a or b", method, path);

        if (!TrySlot(slotText, out int slot))
            return Reject(400, $"unknown slot '{slotText}'; expected 1-8, q or quick", method, path);

        bool recalled;
        long positionMs;
        string error;

        if (Is(action, "recall"))
            recalled = _target.Recall(deck, slot, out positionMs, out error);
        else if (Is(action, "capture"))
            recalled = _target.Capture(deck, slot, out positionMs, out error);
        else
            return Reject(404, $"unknown action '{action}'; expected recall or capture", method, path);

        if (!recalled)
        {
            Warn($"{method} {path}: {error}");
            return RouteOutcome.Reject(409, error);
        }

        return Accept(method, path, new
        {
            ok = true,
            action = action.ToLowerInvariant(),
            deck = DeckName(deck),
            slot,
            positionMs,
        });
    }

    private RouteOutcome HandleTransport(string[] segments, string method, string path, string? body)
    {
        if (segments.Length == 3 && Is(segments[2], "playpause"))
        {
            if (!TryDeck(segments[1], out var deck))
                return Reject(400, $"unknown deck '{segments[1]}'; expected a or b", method, path);

            _target.PlayPause(deck);
            return Accept(method, path, new { ok = true, action = "playpause", deck = DeckName(deck) });
        }

        if (segments.Length == 2 && Is(segments[1], "ab"))
        {
            if (!TryEnabled(body, out bool enabled, out string bodyError))
                return Reject(400, bodyError, method, path);

            if (!_target.SetABMode(enabled, out string error))
            {
                Warn($"{method} {path}: {error}");
                return RouteOutcome.Reject(409, error);
            }

            return Accept(method, path, new { ok = true, action = "ab", enabled });
        }

        if (segments.Length == 3 && Is(segments[1], "sync"))
        {
            string name = segments[2].ToLowerInvariant();
            if (name is not ("deck" or "volume" or "tempo"))
                return Reject(404, $"unknown sync '{segments[2]}'; expected deck, volume or tempo", method, path);

            if (!TryEnabled(body, out bool enabled, out string bodyError))
                return Reject(400, bodyError, method, path);

            if (!_target.SetSync(name, enabled, out string error))
            {
                Warn($"{method} {path}: {error}");
                return RouteOutcome.Reject(409, error);
            }

            return Accept(method, path, new { ok = true, action = "sync", sync = name, enabled });
        }

        string segment = segments.Length > 1 ? segments[^1] : string.Empty;
        return Reject(404, $"unknown transport action '{segment}'", method, path);
    }

    /// <summary>Success is logged too: on stream, "did my key fire?" must be answerable from the log.</summary>
    private static RouteOutcome Accept(string method, string path, object payload)
    {
        Log.Info($"cue api: {method} {path} ok");
        return RouteOutcome.Ok(payload);
    }

    private RouteOutcome Reject(int status, string message, string? method, string? path)
    {
        Warn($"{method} {path}: {message}");
        return RouteOutcome.Reject(status, message);
    }

    private void Warn(string message)
    {
        if (_warn != null) _warn(message);
        else Log.Warn($"cue api: {message}");
    }

    private static bool Is(string text, string expected) =>
        string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);

    private static string DeckName(PlaybackDeck deck) => deck == PlaybackDeck.A ? "a" : "b";

    internal static bool TryDeck(string text, out PlaybackDeck deck)
    {
        deck = PlaybackDeck.A;
        if (Is(text, "a")) return true;
        if (Is(text, "b")) { deck = PlaybackDeck.B; return true; }
        return false;
    }

    internal static bool TrySlot(string text, out int slot)
    {
        slot = 0;
        if (Is(text, "q") || Is(text, "quick")) return true;
        if (int.TryParse(text, out slot) && slot is >= 1 and <= 8) return true;
        slot = 0;
        return false;
    }

    /// <summary><c>{"enabled":true}</c>; a missing or non-boolean field is a refusal, not a toggle.</summary>
    internal static bool TryEnabled(string? body, out bool enabled, out string error)
    {
        enabled = false;
        error = "expected {\"enabled\":true|false}";

        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("enabled", out var value)) return false;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;

            enabled = value.GetBoolean();
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
