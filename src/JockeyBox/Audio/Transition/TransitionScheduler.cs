using System;
using JockeyBox.Audio.Beat;
using JockeyBox.Audio.Graph;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Transition;

/// <summary>Where BPM/beat data comes from (analysis database, cache, or null when unknown).</summary>
public interface IBeatGridProvider
{
    /// <summary>Beat grid for a track path, or null when it has not been analysed yet.</summary>
    BeatGrid? For(string? path);
}

/// <summary>An immutable description of a transition that has been scheduled.</summary>
public sealed record TransitionPlan(
    PlaybackDeck From,
    PlaybackDeck To,
    long StartFrame,
    long FadeFrames,
    double Ratio,
    double StartPositionSeconds,
    bool BeatLocked,
    string Reason)
{
    /// <summary>True when the two decks are tempo-matched (as opposed to a plain fade).</summary>
    public bool TempoMatched => BeatLocked;
}

/// <summary>
/// Owns the <i>decision</i> and the <i>timing</i> of every automatic transition
/// (plan items 4, 10 and 11).
///
/// <para><b>One transition at a time.</b> Two writers to one fader envelope is how
/// fades get stuck half-way, so this class serialises transitions into a single
/// immutable <see cref="TransitionPlan"/>: a request while one is live is
/// refused, a user command cancels and replaces, and a seek invalidates the beat
/// alignment but never the envelope.</para>
///
/// <para><b>Beat matching that actually holds.</b> Phase alignment alone would
/// drift, because two tracks at 126 and 128 BPM separate by roughly one beat every
/// minute. Auto-mix therefore <i>applies the tempo ratio</i> to the incoming deck
/// (key-locked, via <see cref="TempoStage"/>), which makes the beat periods
/// identical and the drift zero by construction — the classic "you cannot
/// beat-lock an eight-bar fade across an 8 % mismatch" objection only applies
/// when the tempo is left alone. The tempo range limit stays, so a genuinely
/// unmatched pair falls back to a plain equal-power crossfade instead of being
/// forced into a train wreck.</para>
///
/// <para><b>Ends where the track ends.</b> A queued track is brought in by
/// starting the fade so that it <i>completes</i> at the end of the outgoing
/// track (the user's requirement 4), with the start snapped to a beat of the
/// outgoing deck when beat-locking. An explicit "crossfade now" starts
/// immediately and keeps its full length.</para>
/// </summary>
public sealed class TransitionScheduler
{
    private readonly AudioEngine _engine;
    private readonly IBeatGridProvider _beats;
    private readonly object _gate = new();

    private TransitionPlan? _current;
    private long _completeAtFrame = -1;
    private bool _completing;

    public TransitionScheduler(AudioEngine engine, IBeatGridProvider beats)
    {
        _engine = engine;
        _beats = beats;
    }

    /// <summary>Transition style; mirrors <see cref="AppSettings.TransitionMode"/>.</summary>
    public TransitionMode Mode { get; set; } = TransitionMode.AutoCrossfade;

    /// <summary>Requested fade length in seconds.</summary>
    public double FadeSeconds { get; set; } = 2.0;

    /// <summary>Maximum tempo change auto-mix may apply, in percent.</summary>
    public double TempoRangePercent { get; set; } = 8.0;

    /// <summary>The live plan, or null when no transition is running.</summary>
    public TransitionPlan? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>True while a transition is scheduled or running.</summary>
    public bool IsActive => Current != null;

    /// <summary>Fires on the render thread when the fade has finished.</summary>
    public event Action<TransitionPlan>? Completed;

    /// <summary>
    /// Schedule a transition into <paramref name="incoming"/>.
    /// <paramref name="atTrackEnd"/> true = wait so the fade ends with the
    /// outgoing track (the queued-track path); false = start now.
    /// Returns the plan, or null when refused (one already live, or nothing to do).
    /// </summary>
    public TransitionPlan? Begin(AudioEngine.DeckSlot fromSlot, AudioEngine.DeckSlot toSlot,
        bool atTrackEnd, TransitionMode? modeOverride = null)
    {
        lock (_gate)
        {
            if (_current != null)
            {
                Log.Info($"transition refused: {_current.From}->{_current.To} still live");
                return null;
            }
        }

        DeckSource from = fromSlot.Deck;
        DeckSource to = toSlot.Deck;

        if (from.DurationSeconds <= 0 || to.DurationSeconds <= 0)
            return null;

        TransitionMode mode = modeOverride ?? Mode;

        // How much of the outgoing track is left, in *audible* terms.
        double remaining = from.DurationSeconds - from.PositionSeconds;
        if (remaining <= 0.05 && !atTrackEnd) return null;

        // A fade cannot be longer than what is left; an explicit crossfade keeps
        // its requested length, a queued one is trimmed to fit.
        double fade = Math.Max(0.05, Math.Min(FadeSeconds, Math.Max(0.05, remaining - 0.02)));
        long fadeFrames = AudioEngine.FramesFor(fade);

        // Where the fade begins.
        long now = _engine.FrameIndex + AudioEngine.BlockFrames;
        long startFrame = atTrackEnd && remaining > fade
            ? now + AudioEngine.FramesFor(remaining - fade)
            : now;

        double ratio = 1.0;
        double startPosition = 0;
        bool beatLocked = false;
        string reason = mode == TransitionMode.AutoMix ? "auto-mix" : "crossfade";

        if (mode == TransitionMode.AutoMix)
        {
            BeatGrid? gridFrom = _beats.For(from.TrackPath);
            BeatGrid? gridTo = _beats.For(to.TrackPath);

            if (gridFrom == null || gridTo == null)
            {
                reason = "auto-mix: no BPM data, plain crossfade";
            }
            else
            {
                TempoMatch match = TempoMatcher.Match(gridFrom, gridTo, TempoRangePercent);
                if (!match.Matched)
                {
                    reason = $"auto-mix: {match.Reason}, plain crossfade";
                }
                else
                {
                    ratio = match.Ratio;
                    startPosition = gridTo.SuggestedStart(to.DurationSeconds);
                    beatLocked = true;
                    reason = $"auto-mix: {match.Reason}";

                    // Snap the start to the outgoing deck's nearest beat so the
                    // incoming track enters on a beat rather than a fraction after it.
                    startFrame = SnapToBeat(startFrame, from, gridFrom);
                }
            }
        }

        // Re-derive the fade after the beat snap: a queued transition still has to
        // finish exactly where the outgoing track does, an explicit one keeps its
        // requested length.
        double offset = Math.Max(0, AudioEngine.SecondsFor(startFrame - now));
        fade = atTrackEnd ? Math.Max(0.05, remaining - offset) : Math.Max(0.05, FadeSeconds);
        fade = Math.Min(fade, Math.Max(0.05, FadeSeconds));
        fadeFrames = AudioEngine.FramesFor(fade);

        var plan = new TransitionPlan(
            fromSlot.Deck == _engine.DeckA ? PlaybackDeck.A : PlaybackDeck.B,
            toSlot.Deck == _engine.DeckA ? PlaybackDeck.A : PlaybackDeck.B,
            startFrame, fadeFrames, ratio, startPosition, beatLocked, reason);

        lock (_gate)
        {
            _current = plan;
            _completeAtFrame = startFrame + fadeFrames;
        }

        Log.Info($"transition {plan.From}->{plan.To}: start={startFrame} fade={fade:0.###}s " +
                 $"ratio={ratio:0.####} pos={startPosition:0.###} beatLocked={beatLocked} ({reason})");

        _engine.ScheduleAt(startFrame, () =>
        {
            to.Seek(startPosition);
            to.SetTempo(ratio);
            to.Play();
            _engine.RampFader(toSlot.Deck == _engine.DeckB ? 1.0 : 0.0, fade);
        });

        _engine.ScheduleAt(_completeAtFrame, Finish);

        return plan;
    }

    /// <summary>Cancel any live transition, restoring the current fader position.</summary>
    public void Cancel()
    {
        TransitionPlan? plan;
        lock (_gate)
        {
            plan = _current;
            _current = null;
            _completeAtFrame = -1;
            _completing = false;
        }

        if (plan == null) return;

        // Freeze the envelope where it is: the incoming deck keeps playing at the
        // level it has reached, which is what a DJ does when they stop a fade.
        _engine.Fader.FreezeAt(_engine.FrameIndex);
        _engine.ScheduledActions.Clear();
        Log.Info($"transition {plan.From}->{plan.To} cancelled at frame {_engine.FrameIndex}");
    }

    /// <summary>
    /// Complete the live transition on the next block, whatever the clock says.
    ///
    /// <para>Used by the end-of-track path: an armed queued hand-off is timed to
    /// <i>end</i> where the outgoing track ends, so the two events race by a block
    /// or two - and a metadata length longer than the decoded audio can put the
    /// deck's end seconds ahead of the scheduled completion. Cancelling there
    /// freezes the fader mid-ramp and leaves the incoming deck silent, so the
    /// hand-off is completed instead: the incoming deck is placed at its planned
    /// entry point (when its start action has not run yet), started, the fader is
    /// snapped across and <see cref="Completed"/> fires as usual.</para>
    ///
    /// <para>Returns false when no transition is live (nothing to complete).</para>
    /// </summary>
    public bool CompleteNow()
    {
        TransitionPlan? plan;
        lock (_gate)
        {
            plan = _current;
            if (plan == null || _completing) return false;

            // A second end-of-track tick before the scheduled completion runs must
            // not queue a duplicate pass (two ramps, two Completed events).
            _completing = true;
        }

        // The scheduled start/finish frames are moot now; the completion below
        // replaces them, so nothing is left to fire twice.
        _engine.ScheduledActions.Clear();

        _engine.ScheduleAt(_engine.FrameIndex + AudioEngine.BlockFrames, () =>
        {
            lock (_gate) _completing = false;

            DeckSource to = plan.To == PlaybackDeck.A ? _engine.DeckA : _engine.DeckB;

            // The start action may never have run (the deck ended before the fade
            // window opened): put the incoming deck where the plan wanted it.
            if (to.State != DeckSource.DeckState.Playing)
            {
                to.Seek(plan.StartPositionSeconds);
                to.SetTempo(plan.Ratio);
            }

            to.Play();
            _engine.RampFader(to == _engine.DeckB ? 1.0 : 0.0, 0.05);

            Log.Info($"transition {plan.From}->{plan.To}: outgoing deck ended first - " +
                     $"completed at frame {_engine.FrameIndex}");
            Finish();
        });

        return true;
    }

    /// <summary>Clear state without touching audio (deck unloaded / engine stopped).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _current = null;
            _completeAtFrame = -1;
            _completing = false;
        }
    }

    private void Finish()
    {
        TransitionPlan? plan;
        lock (_gate)
        {
            plan = _current;
            _current = null;
            _completeAtFrame = -1;
        }

        if (plan == null) return;
        try { Completed?.Invoke(plan); }
        catch (Exception ex) { Log.Error("transition completion handler failed", ex); }
    }

    /// <summary>
    /// Move an engine frame to the nearest beat of the outgoing deck. The incoming
    /// deck is started from one of its own beats, so aligning the start frame makes
    /// both grids land together.
    /// </summary>
    private long SnapToBeat(long frame, DeckSource from, BeatGrid grid)
    {
        if (grid.Bpm <= 0) return frame;

        double delta = AudioEngine.SecondsFor(frame - _engine.FrameIndex);
        double positionAtStart = from.PositionSeconds + delta;
        double nearest = grid.FloorToBeat(positionAtStart);
        double beat = grid.BeatSeconds;
        if (positionAtStart - nearest > beat * 0.5) nearest += beat;

        double snappedSeconds = nearest - from.PositionSeconds;
        long snapped = _engine.FrameIndex + AudioEngine.FramesFor(Math.Max(0, snappedSeconds));

        // Never move the start by more than half a beat; a large correction means
        // the grid and the deck position disagree, and the plain timing is safer.
        return Math.Abs(snapped - frame) > AudioEngine.FramesFor(beat * 0.5) ? frame : snapped;
    }
}
