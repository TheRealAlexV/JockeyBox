using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Data;
using JockeyBox.Library;
using JockeyBox.Playback;

namespace JockeyBox.ViewModels;

/// <summary>
/// Cue plumbing shared by both decks. The pads live on the deck card, so the bank
/// itself hangs off <see cref="DeckViewModel"/>; this half owns the one thing a
/// deck must not know: where saved cues live.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Saved hot cues, or null when the library runs JSON-only.</summary>
    internal static HotCueRepository? HotCueStore =>
        LibraryDatabase.Current is { } db ? new HotCueRepository(db) : null;

    /// <summary>Deck index the cue table uses: 0 is Deck A, 1 is Deck B.</summary>
    internal static int CueDeckOf(PlaybackDeck deck) => deck == PlaybackDeck.A ? 0 : 1;
}

/// <summary>
/// One deck's cue bank: eight saved pads plus the quick cue.
///
/// <para><b>Media time, not audible time</b>: a pad stores
/// <c>DeckChannel.MediaPositionSeconds</c>. The audible clock drops the bus delay
/// and the stretcher's group delay, so a cue keyed to it would land late and
/// drift with the tempo.</para>
///
/// <para><b>Press</b>: an empty pad stores the current position, an occupied pad
/// jumps to it. Clear mode turns the same press into a clear, which is one
/// discoverable way to empty a pad without hiding it behind a right-click the
/// operator cannot see on a touch screen.</para>
///
/// <para>The quick cue is set-then-jump like the saved pads, but it is one
/// position in memory and is never written to the database.</para>
/// </summary>
public partial class CueBankViewModel : ObservableObject
{
    private readonly int _deck;
    private readonly Func<double> _mediaSeconds;
    private readonly Action<double> _seekToSeconds;
    private readonly Func<HotCueRepository?> _store;

    private long _trackId;
    private CueSlotViewModel? _lastPressed;

    public CueBankViewModel(int deck, Func<double> mediaSeconds, Action<double> seekToSeconds,
                            Func<HotCueRepository?> store)
    {
        _deck = deck;
        _mediaSeconds = mediaSeconds;
        _seekToSeconds = seekToSeconds;
        _store = store;

        for (int slot = 1; slot <= HotCueRepository.SlotCount; slot++)
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

    /// <summary>Which deck this bank drives, for the tests and the pad tooltips.</summary>
    public int Deck => _deck;

    /// <summary>The track whose cues are loaded; 0 when nothing is loaded or the row is unknown.</summary>
    public long TrackId => _trackId;

    [RelayCommand]
    private void ToggleClearMode() => IsClearing = !IsClearing;

    /// <summary>Clear-mode control's own label: press CLEAR to arm, DONE to leave.</summary>
    public string ClearModeText => IsClearing ? "DONE" : "CLEAR";

    partial void OnIsClearingChanged(bool value) => OnPropertyChanged(nameof(ClearModeText));

    /// <summary>
    /// Point the bank at a track and load its saved cues. Called from the deck's
    /// load funnel, after the deck has been told what to play.
    /// </summary>
    public void SetTrack(string? primaryPath)
    {
        _lastPressed = null;

        // The cue row points at tracks.id, not at a path, so a file the library
        // does not know simply gets an in-memory bank for this session.
        long trackId = string.IsNullOrWhiteSpace(primaryPath)
            ? 0
            : LibraryDatabase.Tracks?.GetByPath(primaryPath)?.Id ?? 0;

        SetTrackId(trackId);
    }

    /// <summary>Point the bank at a known track id and load its rows.</summary>
    internal void SetTrackId(long trackId)
    {
        _lastPressed = null;
        _trackId = trackId;

        foreach (var slot in Slots) slot.Reset();
        QuickCue.Reset();

        if (trackId == 0 || _store() is not { } repo) return;

        foreach (var row in repo.Get(trackId, _deck))
        {
            if (row.Slot is < 1 or > HotCueRepository.SlotCount) continue;
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

        if (slot.IsQuick || _trackId == 0 || _store() is not { } repo) return;
        repo.Clear(_trackId, _deck, slot.Slot);
    }

    private void Store(CueSlotViewModel slot)
    {
        long positionMs = (long)Math.Round(Math.Max(0, _mediaSeconds()) * 1000.0);
        slot.Set(positionMs);
        _lastPressed = slot;

        if (slot.IsQuick || _trackId == 0 || _store() is not { } repo) return;
        repo.Set(_trackId, _deck, slot.Slot, positionMs);
    }

    private void Jump(CueSlotViewModel slot)
    {
        _lastPressed = slot;
        _seekToSeconds(slot.PositionMs / 1000.0);
    }

    /// <summary>The pad pressed last, for the tests; null before any press.</summary>
    internal CueSlotViewModel? LastPressed => _lastPressed;
}
