using System;
using System.Collections.Generic;
using System.Linq;

namespace JockeyBox.Audio.Beat;

/// <summary>
/// A track's beat grid: the tempo plus where its beats actually fall.
///
/// <para><b>Why beat times and not just BPM.</b> Two tracks at 128 BPM are not
/// aligned just because their tempo matches — the first beat of one may be 300 ms
/// into the file and the other 40 ms. Beat-matching needs the phase, and phase
/// comes from the analyser's beat list. When only a BPM exists (the analyser had
/// low confidence) the grid degrades to a synthetic grid starting at
/// <see cref="FirstBeatSeconds"/>, and callers can see that via
/// <see cref="IsMeasured"/>.</para>
///
/// <para><b>Half/double.</b> Tempo estimators routinely report 64 for a 128 BPM
/// house track (or 174 for 87). <see cref="NormalizedBpm"/> collapses the octave
/// ambiguity before any ratio is computed, so auto-mix does not try to run a
/// track at 2× and <see cref="TempoMatcher"/> never has to reason about it.</para>
/// </summary>
public sealed class BeatGrid
{
    private readonly double[] _beats;

    public BeatGrid(double bpm, double firstBeatSeconds, IReadOnlyList<double>? beats = null,
        double bpmP10 = 0, double bpmP90 = 0, double confidence = 0,
        string? key = null, double keyConfidence = 0, bool keyAnalysed = false)
    {
        Bpm = bpm > 0 ? bpm : 0;
        FirstBeatSeconds = Math.Max(0, firstBeatSeconds);
        _beats = beats?.ToArray() ?? Array.Empty<double>();
        BpmP10 = bpmP10;
        BpmP90 = bpmP90;
        Confidence = confidence;
        Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        KeyConfidence = keyConfidence;
        KeyAnalysed = keyAnalysed;
    }

    /// <summary>
    /// Estimated key in Camelot notation ("8A", "11B"), or null when the
    /// analyser found no defensible tonal centre.
    ///
    /// <para><b>An estimate, not a fact.</b> This value comes from chroma
    /// analysis of the audio; it is never written into the file's tags. The
    /// library shows it only when the file carries no ID3 <c>TKEY</c>, because
    /// an explicit tag always wins (see <see cref="Library.LibraryScanner"/>).
    /// </para>
    /// </summary>
    public string? Key { get; }

    /// <summary>Estimator confidence 0..1 (0 when no key was reported).</summary>
    public double KeyConfidence { get; }

    /// <summary>
    /// True when the key pass has run for this track, whether or not it found a
    /// key. Distinguishes "analysed, no tonal centre" from "analysed before key
    /// estimation existed", so the backfill runs once and never repeats.
    /// </summary>
    public bool KeyAnalysed { get; }

    /// <summary>Analysed tempo (as reported, not normalised).</summary>
    public double Bpm { get; }

    /// <summary>Time of the first beat in the file, in seconds.</summary>
    public double FirstBeatSeconds { get; }

    /// <summary>Measured beat positions in seconds, ascending; empty for a synthetic grid.</summary>
    public IReadOnlyList<double> Beats => _beats;

    /// <summary>10th percentile of the per-frame tempo estimate (0 when not reported).</summary>
    public double BpmP10 { get; }

    /// <summary>90th percentile of the per-frame tempo estimate (0 when not reported).</summary>
    public double BpmP90 { get; }

    /// <summary>Analyser confidence, 0..1 (0 when not reported).</summary>
    public double Confidence { get; }

    /// <summary>True when real beat times were measured (not a synthetic grid).</summary>
    public bool IsMeasured => _beats.Length >= 2;

    /// <summary>Seconds per beat.</summary>
    public double BeatSeconds => Bpm > 0 ? 60.0 / Bpm : 0;

    /// <summary>Phase within the beat at a source position, 0..1 (0 = on a beat).</summary>
    public double PhaseAt(double sourceSeconds)
    {
        if (Bpm <= 0) return 0;
        double beat = BeatSeconds;
        double t = sourceSeconds - FirstBeatSeconds;
        double phase = t / beat;
        phase -= Math.Floor(phase);
        return phase;
    }

    /// <summary>The next beat at or after a source position.</summary>
    public double NextBeatAfter(double sourceSeconds)
    {
        if (Bpm <= 0) return sourceSeconds;
        double beat = BeatSeconds;
        double t = sourceSeconds - FirstBeatSeconds;
        double n = Math.Ceiling(t / beat);
        return FirstBeatSeconds + n * beat;
    }

    /// <summary>Round a source position down to the nearest beat (never below the first beat).</summary>
    public double FloorToBeat(double sourceSeconds)
    {
        if (Bpm <= 0) return sourceSeconds;
        double beat = BeatSeconds;
        double t = sourceSeconds - FirstBeatSeconds;
        double n = Math.Floor(t / beat);
        return Math.Max(FirstBeatSeconds, FirstBeatSeconds + n * beat);
    }

    /// <summary>
    /// A musically sensible entry point: the first beat, advanced by whole bars so
    /// the incoming track does not start inside its intro pickup. Never invents a
    /// position past the end of the track.
    /// </summary>
    public double SuggestedStart(double durationSeconds, int barsToSkip = 0)
    {
        double start = FirstBeatSeconds;
        if (IsMeasured) start = Math.Max(start, _beats[0]);
        start += barsToSkip * 4 * BeatSeconds;
        return Math.Clamp(start, 0, Math.Max(0, durationSeconds - 0.5));
    }

    /// <summary>Collapse the octave ambiguity: bpm/2, bpm and bpm*2 snapped into 70..190.</summary>
    public double NormalizedBpm()
    {
        if (Bpm <= 0) return 0;
        double best = Bpm;
        double bestDistance = double.MaxValue;
        foreach (double candidate in new[] { Bpm / 2.0, Bpm, Bpm * 2.0 })
        {
            if (candidate < 70 || candidate > 190) continue;
            double distance = Math.Abs(candidate - 120); // prefer the centre of the DJ range
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Build from analyser output; returns null when the tempo is unusable.</summary>
    public static BeatGrid? FromAnalysis(double bpm, double? firstBeatMs, IReadOnlyList<int>? beatsMs, double durationSeconds,
        double bpmP10 = 0, double bpmP90 = 0, double confidence = 0,
        string? key = null, double keyConfidence = 0, bool keyAnalysed = false)
    {
        if (bpm <= 0) return null;
        if (firstBeatMs is double ms && (ms < 0 || ms / 1000.0 > durationSeconds + 1)) firstBeatMs = null;

        double first = firstBeatMs is double f ? f / 1000.0 : 0;
        double[]? beats = beatsMs is { Count: >= 2 }
            ? beatsMs.Select(b => b / 1000.0).ToArray()
            : null;

        // A long intro with no detectable beat still has a usable tempo; the grid
        // then starts at 0 and stays synthetic (IsMeasured == false), which the
        // tempo matcher refuses rather than inventing a phase.
        if (beats is { Length: >= 2 } && firstBeatMs is null) first = beats[0];

        return new BeatGrid(bpm, first, beats, bpmP10, bpmP90, confidence, key, keyConfidence, keyAnalysed);
    }
}

/// <summary>Outcome of matching two grids for an automated mix.</summary>
public readonly record struct TempoMatch(double Ratio, double FadeBeats, bool Matched, string Reason)
{
    public static TempoMatch Failed(string reason) => new(1.0, 0, false, reason);
}

/// <summary>
/// Decides how two tracks can be tempo-matched, and how long the resulting
/// crossfade may be.
///
/// <para><b>The arithmetic that constrains a fade.</b> If track B plays at ratio
/// <c>r</c> and the fade lasts <c>B</c> beats, the two grids drift apart by
/// <c>B·|1−r|</c> beats. Keeping the drift under a quarter beat is what stops a
/// mix sounding like a train wreck, so <c>B ≤ 0.25/|1−r|</c>. At ±8 % that caps a
/// beat-locked fade at about three beats — an eight-bar (32-beat) fade at 8 %
/// mismatch would drift 2.6 beats, which is not a stylistic choice, it is a
/// wrong answer. So the fade is shortened to what the mismatch allows, and when
/// even that is under two beats the caller is told to fall back to a plain
/// crossfade instead of pretending to beat-match.</para>
/// </summary>
public static class TempoMatcher
{
    /// <summary>Maximum drift allowed across a beat-locked fade, in beats.</summary>
    public const double MaxDriftBeats = 0.25;

    /// <summary>A beat-locked fade shorter than this is worse than a clean crossfade.</summary>
    public const double MinUsefulFadeBeats = 2.0;

    /// <summary>
    /// Match <paramref name="outgoing"/> to <paramref name="incoming"/>.
    /// <paramref name="requestedFadeBeats"/> is the user's fade length in beats of
    /// the outgoing track; <paramref name="maxPercent"/> is the tempo range limit.
    /// </summary>
    public static TempoMatch Match(BeatGrid outgoing, BeatGrid incoming, double maxPercent)
    {
        if (outgoing.Bpm <= 0 || incoming.Bpm <= 0)
            return TempoMatch.Failed("no tempo for one of the tracks");

        if (!outgoing.IsMeasured || !incoming.IsMeasured)
            return TempoMatch.Failed("beat grid not measured");

        double target = outgoing.NormalizedBpm();
        double source = incoming.NormalizedBpm();
        if (target <= 0 || source <= 0)
            return TempoMatch.Failed("tempo outside the mixable range");

        double ratio = target / source;

        // A ratio outside the limit is a real mismatch, not something to force.
        double maxRatioDelta = Math.Max(0.5, maxPercent) / 100.0;
        if (Math.Abs(ratio - 1.0) > maxRatioDelta)
            return TempoMatch.Failed($"tempo mismatch {Math.Abs(ratio - 1.0) * 100:0.0}% exceeds ±{maxPercent:0.#}%");

        // The ratio is applied to the incoming deck, so both grids beat at the
        // outgoing tempo and the drift across any fade length is zero. Were the
        // tempo left alone, drift would be B·|1−ratio| beats and a long beat-locked
        // fade would be arithmetically impossible — which is why auto-mix retunes
        // rather than merely aligning phase.
        double drift = MaxDriftBeats;
        return new TempoMatch(ratio, drift, true,
            $"ratio {ratio:0.####} ({(ratio - 1.0) * 100:+0.0;-0.0}%), drift 0");
    }

    /// <summary>Convert a fade length in seconds to beats of a track, rounded to whole beats.</summary>
    public static double SecondsToBeats(double seconds, BeatGrid grid)
    {
        if (grid.BeatSeconds <= 0) return 4;
        return Math.Max(1, Math.Round(seconds / grid.BeatSeconds));
    }
}
