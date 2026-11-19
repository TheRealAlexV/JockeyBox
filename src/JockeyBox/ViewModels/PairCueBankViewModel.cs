using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Data;

namespace JockeyBox.ViewModels;

/// <summary>
/// The karaoke pair's cue bank: eight saved pads plus the quick cue, shared by
/// both decks.
///
/// <para><b>One position, two decks</b>: a pair is already aligned by media time,
/// so a cue is captured from the master deck's media clock and recalled on
/// <i>both</i> channels with the same position. There is no per-deck offset.</para>
///
/// <para><b>Recall seeks the channels directly</b>, through the two-channel
/// lambda injected at construction, exactly as the per-deck bank seeks its own
/// channel. It deliberately does not go through <c>DualDeckTransport.Seek</c> /
/// <c>SeekDeck</c>: the mirror half is gated on sync being active and on the
/// active deck, so with sync off the slave is left behind, and with sync on the
/// slave is seeked twice.</para>
///
/// <para>The quick cue is set-then-jump like a saved pad, but it is one position
/// in memory and is never written to the database.</para>
/// </summary>
public partial class PairCueBankViewModel : ObservableObject
{
    private readonly Func<double> _mediaSeconds;
    private readonly Action<double> _seekBothSeconds;
    private readonly Func<PairHotCueRepository?> _store;

    private long _trackIdA;
    private long _trackIdB;
    private CueSlotViewModel? _lastPressed;

    public PairCueBankViewModel(Func<double> mediaSeconds, Action<double> seekBothSeconds,
                                Func<PairHotCueRepository?> store)
    {
        _mediaSeconds = mediaSeconds;
        _seekBothSeconds = seekBothSeconds;
        _store = store;

        for (int slot = 1; slot <= PairHotCueRepository.SlotCount; slot++)
            Slots.Add(new CueSlotViewModel(slot));

        QuickCue = new CueSlotViewModel(CueSlotViewModel.QuickSlot);

        foreach (var pad in Slots) Pads.Add(pad);
        Pads.Add(QuickCue);
    }

    /// <summary>Saved pads, slot 1 first. Eight of them, always: the bank does not resize.</summary>
    public ObservableCollection<CueSlotViewModel> Slots { get; } = new();

    /// <summary>The ephemeral cue: set-then-jump, never saved.</summary>
    public CueSlotViewModel QuickCue { get; }

    /// <summary>Every pad in one loop, saved slots then the quick cue.</summary>
    public ObservableCollection<CueSlotViewModel> Pads { get; } = new();

    /// <summary>Clear mode: while on, pressing a pad removes its position instead of recalling it.</summary>
    [ObservableProperty] private bool _isClearing;

    /// <summary>The pair's deck-A track id (the render); 0 when nothing is loaded.</summary>
    public long TrackIdA => _trackIdA;

    /// <summary>The pair's deck-B track id (the original); 0 when nothing is loaded.</summary>
    public long TrackIdB => _trackIdB;

    [RelayCommand]
    private void ToggleClearMode() => IsClearing = !IsClearing;

    /// <summary>Clear-mode control's own label: press CLEAR to arm, DONE to leave.</summary>
    public string ClearModeText => IsClearing ? "DONE" : "CLEAR";

    partial void OnIsClearingChanged(bool value) => OnPropertyChanged(nameof(ClearModeText));

    /// <summary>Point the bank at a pair and load its saved cues.</summary>
    public void SetPair(long trackIdA, long trackIdB)
    {
        _lastPressed = null;
        _trackIdA = trackIdA;
        _trackIdB = trackIdB;

        foreach (var slot in Slots) slot.Reset();
        QuickCue.Reset();

        if (trackIdA == 0 || trackIdB == 0 || _store() is not { } repo) return;

        foreach (var row in repo.Get(trackIdA, trackIdB))
        {
            if (row.Slot is < 1 or > PairHotCueRepository.SlotCount) continue;
            Slots[row.Slot - 1].Set(row.PositionMs);
        }
    }

    /// <summary>
    /// One press on a pad: recall, store, or clear, depending on the pad and the
    /// mode. <paramref name="slot"/> is the pad that was pressed.
    /// </summary>
    [RelayCommand]
    private void Press(CueSlotViewModel? slot)
    {
        if (slot == null) return;

        if (IsClearing)
        {
            Clear(slot);
            return;
        }

        if (slot.HasPosition)
        {
            Jump(slot);
            return;
        }

        Store(slot);
    }

    /// <summary>Empty one pad, in the database too when it is a saved one.</summary>
    [RelayCommand]
    private void Clear(CueSlotViewModel? slot)
    {
        if (slot == null) return;
        slot.Reset();

        if (slot.IsQuick || _trackIdA == 0 || _trackIdB == 0 || _store() is not { } repo) return;
        repo.Clear(_trackIdA, _trackIdB, slot.Slot);
    }

    /// <summary>Slot numbers a surface may address: 1..8 and 0 for the quick cue.</summary>
    public static bool IsKnownSlot(int slot) => slot is >= 0 and <= PairHotCueRepository.SlotCount;

    /// <summary>Jump to a stored cue on both decks. Fails when the slot is empty.</summary>
    public bool TryRecall(int slot, out string error)
    {
        if (!TryPad(slot, out var pad, out error)) return false;
        if (!pad.HasPosition)
        {
            error = pad.IsQuick ? "the pair quick cue is not set" : $"pair slot {pad.Slot} is empty";
            return false;
        }

        Jump(pad);
        error = string.Empty;
        return true;
    }

    /// <summary>Store the master deck's current media position in a slot.</summary>
    public bool TryCapture(int slot, out string error)
    {
        if (!TryPad(slot, out var pad, out error)) return false;
        if (!pad.IsQuick && (_trackIdA == 0 || _trackIdB == 0))
        {
            error = "no karaoke pair loaded";
            return false;
        }

        Store(pad);
        error = string.Empty;
        return true;
    }

    /// <summary>Position held by a slot, or null when it is empty.</summary>
    public long? PositionOf(int slot) =>
        Pads.FirstOrDefault(p => p.Slot == slot) is { HasPosition: true } pad ? pad.PositionMs : null;

    private bool TryPad(int slot, out CueSlotViewModel pad, out string error)
    {
        pad = Pads.FirstOrDefault(p => p.Slot == slot)!;
        if (pad != null && IsKnownSlot(slot))
        {
            error = string.Empty;
            return true;
        }

        error = $"unknown slot '{slot}'";
        return false;
    }

    private void Store(CueSlotViewModel slot)
    {
        long positionMs = (long)Math.Round(Math.Max(0, _mediaSeconds()) * 1000.0);
        slot.Set(positionMs);
        _lastPressed = slot;

        if (slot.IsQuick || _trackIdA == 0 || _trackIdB == 0 || _store() is not { } repo) return;
        repo.Set(_trackIdA, _trackIdB, slot.Slot, positionMs);
    }

    private void Jump(CueSlotViewModel slot)
    {
        _lastPressed = slot;

        // Both channels, same position, no transport and no sync gate.
        _seekBothSeconds(slot.PositionMs / 1000.0);
    }

    /// <summary>The pad pressed last, for the tests; null before any press.</summary>
    internal CueSlotViewModel? LastPressed => _lastPressed;
}
