using JockeyBox.Api;
using JockeyBox.Playback;

namespace JockeyBox.Tests;

/// <summary>
/// Records what the router and the listener actually asked for, so "reaches the
/// deck exactly once" is a count, not a claim. A slot in <see cref="Empty"/> is
/// refused the way an empty pad is, which is the 409 path.
/// </summary>
internal sealed class FakeCueTarget : ICueActionTarget
{
    public List<(PlaybackDeck Deck, int Slot)> Recalls { get; } = new();

    public List<(PlaybackDeck Deck, int Slot)> Captures { get; } = new();

    public List<PlaybackDeck> PlayPauses { get; } = new();

    public List<bool> ABMode { get; } = new();

    public List<(string Name, bool Enabled)> Syncs { get; } = new();

    public HashSet<(PlaybackDeck Deck, int Slot)> Empty { get; } = new();

    /// <summary>When set, a recall throws: the router must not leak the message.</summary>
    public bool ThrowOnRecall { get; set; }

    public bool SyncAvailable { get; set; } = true;

    public long PositionMs { get; set; } = 42_000;

    public CueSurface Snapshot() => new(
        new[]
        {
            new CueDeckState("a", 7, new[] { new CueSlotState(1, 1000, true) }),
            new CueDeckState("b", 0, new[] { new CueSlotState(1, 0, false) }),
        },
        ABMode: SyncAvailable,
        DeckSync: false,
        VolumeSync: false,
        TempoSync: false);

    public bool Recall(PlaybackDeck deck, int slot, out long positionMs, out string error)
    {
        if (ThrowOnRecall) throw new InvalidOperationException("C:\\Dev\\secret\\library.db is locked");

        if (Empty.Contains((deck, slot)))
        {
            positionMs = 0;
            error = slot == 0 ? "the quick cue is not set" : $"slot {slot} is empty";
            return false;
        }

        Recalls.Add((deck, slot));
        positionMs = PositionMs;
        error = string.Empty;
        return true;
    }

    public bool Capture(PlaybackDeck deck, int slot, out long positionMs, out string error)
    {
        Captures.Add((deck, slot));
        positionMs = PositionMs;
        error = string.Empty;
        return true;
    }

    public void PlayPause(PlaybackDeck deck) => PlayPauses.Add(deck);

    public bool SetABMode(bool enabled, out string error)
    {
        ABMode.Add(enabled);
        error = string.Empty;
        return true;
    }

    public bool SetSync(string name, bool enabled, out string error)
    {
        if (!SyncAvailable)
        {
            error = "A-B mode is off; enable /transport/ab first";
            return false;
        }

        Syncs.Add((name, enabled));
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Stands in for <c>RegisterHotKey</c>: CI cannot press a global hotkey, but it
/// can check that a chord another process owns is reported instead of dropped.
/// </summary>
internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    /// <summary>Chords "another process already owns"; registration must fail for these.</summary>
    public HashSet<string> Taken { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Live { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Events { get; } = new();

    public event Action<string>? Triggered;

    /// <summary>Simulate a registered chord firing.</summary>
    public void Raise(string actionId) => Triggered?.Invoke(actionId);

    public bool TryBind(string actionId, string? chord, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(chord)) return true;

        if (Taken.Contains(chord))
        {
            error = $"'{chord}' is already registered by another application";
            Events.Add($"fail:{actionId}:{chord}");
            return false;
        }

        Live[actionId] = chord;
        Events.Add($"bind:{actionId}:{chord}");
        return true;
    }

    public void Unbind(string actionId)
    {
        if (Live.Remove(actionId)) Events.Add($"unbind:{actionId}");
    }
}
