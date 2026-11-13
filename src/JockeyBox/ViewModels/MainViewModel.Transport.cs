using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Playback;

namespace JockeyBox.ViewModels;

/// <summary>
/// The transport row's link and sync model: the A-B toggle, the three sync
/// switches that only mean something while both decks take commands, and the
/// two-way mirroring behind VOL SYNC and TEMPO SYNC.
///
/// <para>This lives beside <c>MainViewModel.cs</c> rather than inside it because
/// that file is owned elsewhere. <see cref="InitializeDeckUi"/> is the wiring the
/// constructor would normally have done; the window calls it from its Loaded
/// handler, and so do the tests.</para>
///
/// <para><b>Echo guards</b>: a mirrored write comes straight back through the same
/// <see cref="DeckViewModel.PropertyChanged"/> handler that started it. The guard
/// lives in <see cref="DeckMirror"/>, never in <c>DeckChannel</c>: the channel has
/// no way to tell an operator's move from the mirror's.</para>
/// </summary>
public partial class MainViewModel
{
    private DeckMirror? _volumeMirror;
    private DeckMirror? _tempoMirror;
    private bool _abMode;

    /// <summary>The transport the row drives, for the tests.</summary>
    internal DualDeckTransport Transport => _transport;

    /// <summary>
    /// True while the transport addresses both decks. It follows the transport
    /// rather than leading it, because a karaoke load selects the pair by itself
    /// (<c>DualDeckTransport.LoadOnDeck</c>).
    /// </summary>
    public bool IsABMode
    {
        get => _abMode;
        set
        {
            if (value == _abMode) return;
            _abMode = value;
            _transport.SetTarget(value ? TransportTarget.Both : TransportTarget.Active);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSyncAvailable));
            if (value) return;

            // A toggle that silently stays on while its mode is gone is a lie:
            // dropping the link drops the three switches with it.
            IsDeckSync = false;
            IsVolumeSync = false;
            IsTempoSync = false;
        }
    }

    /// <summary>The sync switches are only honest while both decks take commands.</summary>
    public bool IsSyncAvailable => _abMode;

    /// <summary>Master/slave playhead sync: the active deck drives the other.</summary>
    public bool IsDeckSync
    {
        get => _transport.SyncActive;
        set
        {
            if (value == _transport.SyncActive) return;
            _transport.SetSyncActive(value);
            OnPropertyChanged();
        }
    }

    /// <summary>One fader move, both decks: mirroring with an echo guard.</summary>
    [ObservableProperty] private bool _isVolumeSync;

    /// <summary>One tempo move, both decks: mirroring with an echo guard.</summary>
    [ObservableProperty] private bool _isTempoSync;

    /// <summary>
    /// Wire the row. Called from the window's Loaded handler because
    /// <c>MainViewModel</c>'s constructor belongs to another lane.
    /// </summary>
    public void InitializeDeckUi()
    {
        _volumeMirror = new DeckMirror(
            value => DeckA.Volume = (float)value,
            value => DeckB.Volume = (float)value);

        _tempoMirror = new DeckMirror(
            value => DeckA.TempoPercent = value,
            value => DeckB.TempoPercent = value);

        DeckA.PropertyChanged += OnDeckPropertyChanged;
        DeckB.PropertyChanged += OnDeckPropertyChanged;

        _transport.StateChanged += (_, _) => SyncLinkFromTransport();
        SyncLinkFromTransport();
    }

    /// <summary>Press A: just this deck, so the link drops.</summary>
    [RelayCommand]
    private void SelectDeckA() => HandOverTo(PlaybackDeck.A);

    /// <summary>Press B: just this deck, so the link drops.</summary>
    [RelayCommand]
    private void SelectDeckB() => HandOverTo(PlaybackDeck.B);

    private void HandOverTo(PlaybackDeck deck)
    {
        IsABMode = false;
        _transport.ActivateDeck(deck);
    }

    /// <summary>Take the link's state from the transport, which can set it itself.</summary>
    private void SyncLinkFromTransport()
    {
        bool both = _transport.Target == TransportTarget.Both;
        if (both != _abMode)
        {
            _abMode = both;
            OnPropertyChanged(nameof(IsABMode));
            OnPropertyChanged(nameof(IsSyncAvailable));
            if (!both)
            {
                IsDeckSync = false;
                IsVolumeSync = false;
                IsTempoSync = false;
            }
        }

        // SetSyncActive lands on the transport through its own path, so the switch
        // can read stale after a transport-side change.
        OnPropertyChanged(nameof(IsDeckSync));
    }

    private void OnDeckPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DeckViewModel deck) return;

        switch (e.PropertyName)
        {
            case nameof(DeckViewModel.Volume):
                if (!IsVolumeSync) return;
                _volumeMirror?.Push(deck == DeckA ? 0 : 1, deck.Volume);
                break;

            case nameof(DeckViewModel.TempoPercent):
                if (!IsTempoSync) return;
                _tempoMirror?.Push(deck == DeckA ? 0 : 1, deck.TempoPercent);
                break;
        }
    }
}

/// <summary>
/// Copies one deck's control onto the other. The mirrored write arrives back
/// through the handler that issued it; refusing that second pass, while the
/// first is still running, is the whole guard. <see cref="Passes"/> counts the
/// writes that reached the other deck, so a missing guard shows up as a number
/// in the tests instead of a stack trace.
/// </summary>
internal sealed class DeckMirror
{
    private readonly Action<double> _toDeckA;
    private readonly Action<double> _toDeckB;
    private bool _mirroring;

    public DeckMirror(Action<double> toDeckA, Action<double> toDeckB)
    {
        _toDeckA = toDeckA;
        _toDeckB = toDeckB;
    }

    public bool IsMirroring => _mirroring;

    /// <summary>Writes to the other deck; false when this call is the echo of one.</summary>
    public bool Push(int fromDeck, double value)
    {
        if (_mirroring) return false;
        _mirroring = true;
        try
        {
            Passes++;
            (fromDeck == 0 ? _toDeckB : _toDeckA)(value);
            return true;
        }
        finally
        {
            _mirroring = false;
        }
    }

    /// <summary>Writes that reached the other deck.</summary>
    internal int Passes { get; private set; }
}
