using JockeyBox.Data;

namespace JockeyBox.ViewModels;

/// <summary>
/// The input-surface view of a cue bank: recall and capture addressed by slot
/// number instead of by pad control. Both routes are the same code the pads use
/// (<c>Jump</c> and <c>Store</c>), so a hotkey or an HTTP call cannot drift from
/// a mouse press.
///
/// <para>Clear mode is deliberately ignored here: a Stream Deck key labelled
/// "cue 3" must never silently delete that cue because a toggle was left armed.</para>
/// </summary>
public partial class CueBankViewModel
{
    /// <summary>Slot numbers a surface may address: 1..8 and 0 for the quick cue.</summary>
    public static bool IsKnownSlot(int slot) => slot is >= 0 and <= HotCueRepository.SlotCount;

    /// <summary>Jump to a stored cue. Fails when the slot is empty.</summary>
    public bool TryRecall(int slot, out string error)
    {
        return TryPad(slot, out var pad, out error) && Recall(pad, out error);
    }

    /// <summary>Store the deck's current media position in a slot.</summary>
    public bool TryCapture(int slot, out string error)
    {
        if (!TryPad(slot, out var pad, out error)) return false;
        if (!pad.IsQuick && _trackId == 0)
        {
            error = "no library track on this deck";
            return false;
        }

        Store(pad);
        error = string.Empty;
        return true;
    }

    /// <summary>Position held by a slot, or null when it is empty.</summary>
    public long? PositionOf(int slot) =>
        Pads.FirstOrDefault(p => p.Slot == slot) is { HasPosition: true } pad ? pad.PositionMs : null;

    private bool Recall(CueSlotViewModel pad, out string error)
    {
        if (!pad.HasPosition)
        {
            error = pad.IsQuick ? "the quick cue is not set" : $"slot {pad.Slot} is empty";
            return false;
        }

        Jump(pad);
        error = string.Empty;
        return true;
    }

    private bool TryPad(int slot, out CueSlotViewModel pad, out string error)
    {
        pad = null!;
        error = string.Empty;

        if (!IsKnownSlot(slot))
        {
            error = $"unknown slot {slot}; expected 1-{HotCueRepository.SlotCount} or 0 for the quick cue";
            return false;
        }

        var found = Pads.FirstOrDefault(p => p.Slot == slot);
        if (found == null)
        {
            error = $"slot {slot} is not in this bank";
            return false;
        }

        pad = found;
        return true;
    }
}
