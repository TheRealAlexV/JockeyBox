using System;
using System.Collections.Generic;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// Frame-scheduled actions for the render thread.
///
/// <para><b>Why not a timer.</b> A transition must begin on a beat. A
/// <c>DispatcherTimer</c> fires when the UI pump gets around to it (±15 ms on an
/// idle machine, far worse while a waveform is being drawn), so a "beat-aligned"
/// mix would land up to a third of a beat late and every mix would differ. Here
/// the action is attached to an exact engine frame and drained inside the render
/// loop, so it fires at the top of the block that contains that frame — bounded
/// by the block size (10 ms), not by the UI.</para>
///
/// <para>Actions are posted from the UI/scheduler thread and drained only by the
/// render thread; the list is guarded by a lock that is never held across a
/// callback (drained actions run outside it) so an action may itself schedule
/// more work.</para>
/// </summary>
public sealed class ScheduledActions
{
    private readonly object _gate = new();
    private readonly List<(long Frame, Action Action)> _frameItems = new();
    private readonly List<(DeckSource Deck, Action Action)> _mediaEndItems = new();

    /// <summary>Schedule at an absolute engine frame.</summary>
    public void Add(long frame, Action action)
    {
        if (action == null) return;
        lock (_gate) _frameItems.Add((frame, action));
    }

    /// <summary>Schedule when a deck's media is exhausted (its State becomes Ended).</summary>
    public void AddMediaEnd(DeckSource deck, Action action)
    {
        if (deck == null || action == null) return;
        lock (_gate) _mediaEndItems.Add((deck, action));
    }

    /// <summary>Number of pending items (diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _frameItems.Count + _mediaEndItems.Count;
        }
    }

    /// <summary>Drop everything (transition cancelled, deck unloaded).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _frameItems.Clear();
            _mediaEndItems.Clear();
        }
    }

    /// <summary>Remove every action targeting a deck (that deck was stopped or reloaded).</summary>
    public void ClearFor(DeckSource deck)
    {
        lock (_gate) _mediaEndItems.RemoveAll(i => ReferenceEquals(i.Deck, deck));
    }

    /// <summary>Run everything due at <paramref name="frame"/> (render thread only).</summary>
    public void Drain(long frame, DeckSource deckA, DeckSource deckB)
    {
        List<Action>? due = null;

        lock (_gate)
        {
            for (int i = _frameItems.Count - 1; i >= 0; i--)
            {
                if (_frameItems[i].Frame > frame) continue;
                (due ??= new List<Action>()).Add(_frameItems[i].Action);
                _frameItems.RemoveAt(i);
            }

            for (int i = _mediaEndItems.Count - 1; i >= 0; i--)
            {
                var deck = _mediaEndItems[i].Deck;
                bool ended = ReferenceEquals(deck, deckA) || ReferenceEquals(deck, deckB)
                    ? deck.State is DeckSource.DeckState.Ended or DeckSource.DeckState.Failed
                    : true; // a deck that is gone (unloaded) still needs its notification
                if (!ended) continue;
                (due ??= new List<Action>()).Add(_mediaEndItems[i].Action);
                _mediaEndItems.RemoveAt(i);
            }
        }

        if (due == null) return;
        due.Reverse();
        foreach (var action in due)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("scheduled action failed", ex); }
        }
    }
}
