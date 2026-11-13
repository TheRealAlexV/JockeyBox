using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.ViewModels;

/// <summary>
/// One pad in a deck's cue bank: a slot number, the media position stored in it,
/// and the text the pad shows. The bank owns what pressing means; a pad is only
/// the state a button can bind to.
///
/// <para>Occupancy is never colour alone: an empty pad reads "—" and an occupied
/// one reads its position, so the two differ by shape and text as well as tint.</para>
/// </summary>
public partial class CueSlotViewModel : ObservableObject
{
    public CueSlotViewModel(int slot) => Slot = slot;

    /// <summary>1..8 for a saved pad; <see cref="QuickSlot"/> for the quick cue.</summary>
    public int Slot { get; }

    /// <summary>The slot number of the ephemeral quick cue.</summary>
    public const int QuickSlot = 0;

    public bool IsQuick => Slot == QuickSlot;

    /// <summary>Position in raw media milliseconds (see <c>DeckChannel.MediaPositionSeconds</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(PadText))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private long _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(PadText))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _hasPosition;

    /// <summary>The label on the pad: the slot number, or "Q" for the quick cue.</summary>
    public string SlotText => IsQuick ? "Q" : Slot.ToString();

    /// <summary>The position line on the pad; an em-free dash when the slot is empty.</summary>
    public string PositionText => HasPosition ? Format(PositionMs) : "\u2013";

    /// <summary>Both lines of the pad, for a screen reader and for the tests.</summary>
    public string PadText => $"{SlotText} {PositionText}";

    public string ToolTip => HasPosition
        ? (IsQuick ? "Quick cue" : $"Cue {Slot}") + $" at {PositionText}: press to jump. Clear mode removes it."
        : (IsQuick
            ? "Quick cue (not saved): press to store the current position"
            : $"Cue {Slot} is empty: press to store the current position");

    internal void Set(long positionMs)
    {
        PositionMs = Math.Max(0, positionMs);
        HasPosition = true;
    }

    internal void Reset()
    {
        PositionMs = 0;
        HasPosition = false;
    }

    /// <summary>m:ss.d, the same shape the deck's time readout uses.</summary>
    private static string Format(long positionMs)
    {
        var t = TimeSpan.FromMilliseconds(positionMs);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}
