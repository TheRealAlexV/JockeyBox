# Mic Coach: the advisory auto-pipeline for the mic strip

Status: design spec, pre-implementation.
Repo: `/home/alex/OBS/jb-shell`, branch `feat/shell-panels`, commit `68802e1`.
Originating intent: `docs/research/mic-processing-2026.md` §5.

---

## 1. Purpose and non-goals

**Purpose.** Measure the mic signal that actually reaches the room, decide what is measurably wrong, and offer the operator a small set of specific parameter changes to fix it. One measurement, one proposal, one click, done.

**The advisory contract, stated as invariants.**

1. Nothing is written to a strip without an explicit operator action. The pipeline computes and shows. It never writes.
2. Acceptance is a one-shot write of absolute values through `MicMixer.Commands`. No parameter is ever marked "automated", locked, or hidden from the UI. Every knob the coach touched stays a knob, and moving it is allowed and expected.
3. Every accepted proposal carries an Undo that restores only the values it changed, and only for fields still holding the value the coach wrote. If the operator has since moved a field, that field is skipped and the undo says so.

**Non-goals.**

- Not `AutoMix`. That name already means DJ beat-matched deck transitions (`TransitionScheduler.cs`, `AppSettings.AutoMix`, `AutoMixTests`). This feature is called **Mic Coach** and never uses the word "auto" in user-facing text. Rejected alternatives: "Advisor" (says nothing), "Tune" (sounds automatic), "Assist" (implies it acts).
- Not a one-knob enhancer. NVIDIA Broadcast, Apple Voice Isolation, Waves Clarity Vx and Adobe Podcast Enhance hide the result behind one control and measure only the input. That is the opposite of what this spec builds.
- Not a replacement for the operator, and not a live servo. Nothing chases the signal continuously. If it did, it would fight the fader the human is holding.
- Not a new DSP algorithm. Every stage it proposes already exists in `JockeyBox.Dsp`.

The model is iZotope Neutron/Ozone Assistant: analyze, propose a chain with per-processor settings, expose every value, allow an overall amount or target, one-shot. Documented as "a logical starting point", explicitly not a final answer.

---

## 2. Where this comes from

`mic-processing-2026.md` §5 (lines 194-224) is the originating intent. Quoting the four bullets:

> Measured loudness, fully buildable, do this first. ITU-R BS.1770 / EBU R128 ... Auto-gain = measure momentary LUFS → adjust gain toward a target.
> Auto gate threshold = noise-floor estimate.
> Auto EQ = spectral-balance matching, keep the correction gentle.
> Auto compressor / de-esser = crest-factor and sibilance-band energy, lower value than the other three.

**Adopted unchanged.** Build the loudness meter first. Derive the gate threshold from the noise floor. Derive compression from crest factor and de-essing from sibilance-band energy. Keep EQ corrections small. Run analysis asynchronously so render-thread latency is unchanged. Give the compressor and de-esser lower priority than level and gate.

**Sharpened.** "Measure momentary LUFS → adjust gain" is the one line that cannot be implemented as written, and this spec replaces it.

A momentary window is 400 ms. A gain decision needs to know the level of the *speech*, not of the last 400 ms, which may be a pause, a breath, a laugh, or the operator saying "test one two". Two corrections:

- **Short-term loudness (3 s, ungated) is the level signal.** Momentary is reported to the operator and used only for peak-ish statistics (p95 over speech frames). Integrated loudness is implemented in the meter but never used by a proposal: below roughly one minute of programme it is not defined in any useful sense, and EBU Tech 3342 does not even recommend loudness range under 60 s. For a live mic that starts and stops, integrated is noise.
- **Speech-frame gating replaces programme gating.** Every loudness statistic is computed only over frames the VAD marks as speech, or, when the VAD is unavailable, over frames above the measured noise floor plus 15 dB. A pause cannot drag the average down, because pauses are not samples of speech.

**Contradicted, with reasons.**

| §5 says | This spec says | Why |
|---|---|---|
| "Auto EQ = spectral-balance matching vs a target tilt" | Per-defect rules on measured band ratios, no target curve | There is no validated "natural voice" curve, and a wrong target curve is worse than no EQ proposal. Defect rules are testable; a curve is a guess wearing a formula. |
| "Integrated loudness" among the deliverables for live use | Implemented, suppressed below 60 s, never proposed on | See above. |
| The four bullets imply the pipeline may enable stages | It never enables a gate or a limiter, only tunes stages already on | Enabling changes what the operator hears without them asking, and a gate on a music-fed input chops reverb tails and quiet sung phrases. This is a karaoke strip. |

Everything else in §5 stands.

---

## 3. What it observes

Analysis runs off the render thread against a copy of the signal. The tap point matters: the noise floor must be measured *before* the gate (the gate hides the floor by design), and spectral balance must be measured *after* the chain, because the point is what the room hears.

Two mono rings per device, 8 s each at 48 kHz (384 000 floats, 1.5 MB each). Written by the render thread, read by the analysis thread.

| Signal | How measured | Window | Where it lives | Exists today |
|---|---|---|---|---|
| Sample peak per strip | max abs of the block | 10 ms block | `MicStrip.Peak` (`MicMixer.cs:113-114`, set `:533`) | Yes |
| **Momentary loudness** | BS.1770-5 K-weighting (shelf + RLB high-pass), mean square, ungated | 400 ms sliding, updated every 100 ms | new `JockeyBox.Dsp/Analysis/LoudnessMeter.cs` | **No** |
| **Short-term loudness** | same filter chain, longer integration | 3000 ms sliding, updated every 100 ms | same | **No** |
| **True peak (dBTP)** | 4x oversample (48 to 192 kHz), polyphase, max of reconstructed waveform | 400 ms sliding max, plus 3 s max for ceiling decisions | same | **No** |
| Integrated loudness | gated: -70 LUFS absolute, -10 LU relative | programme | same, reported as "not yet meaningful" under 60 s | **No** |
| **Noise floor** | 10th percentile of 50 ms RMS frames, pre-strip tap (pre-gate) | trailing 8 s = 160 frames, minimum 100 frames | new `JockeyBox.Dsp/Analysis/StripAnalysis.cs` | **No** (RMS exists only privately: `Compressor.cs:118-121`, `DeEsser.cs:9`) |
| **Spectral balance** | 2048-point Hann FFT, 10 ISO band sums, post-strip tap, speech frames only | 500 ms cadence over a 10 s average | same | **No** |
| **Spectral tilt** | slope of the band levels, dB/octave, and band-over-band ratios | 10 s speech average | same | **No** |
| **Crest factor** | peak minus RMS, pre-strip, speech frames only | 3 s | same | **No** |
| **Sibilance energy** | 4-8 kHz band over 500 Hz-2 kHz band, per 50 ms frame; report p95 minus median | 3 s of speech frames | same | **No** |
| **Speech probability (VAD)** | RNNoise's own return value, currently discarded at `RnNoiseStage.cs:172` | per 480-sample frame | expose as `volatile float LastVoiceProbability` on `RnNoiseStage`, written on the render thread | **Computed and thrown away** |
| Compressor gain reduction | `Compressor.GainReductionDb` (`Compressor.cs:107`) | mean over 3 s, sampled at 100 ms | via `MixerStrip.Compressor` | Yes, unread |
| Gate GR and open state | `NoiseGate.GainReductionDb`, `IsOpen` (`:119`, `:122`) | speech fraction, open ratio over 3 s | via `MixerStrip.Gate` | Yes, unread |
| De-esser GR | `DeEsser.GainDb` (`:66`) | mean over 3 s | via `MixerStrip.DeEsser` | Yes, unread |
| Limiter GR | not exposed | - | - | **No** (out of scope, see §8) |
| Latency | `MixerStrip.LatencyFrames`, `InterleavedMixerStrip.LatencyFrames`, `LatencyLedger` | on demand | existing | Yes |
| Rate, block, channels | constants 48000, 480, 2 | - | `AudioEngine.cs:110-116` | Yes |

**Precision caveat, stated once and honoured everywhere downstream.** The GR getters are plain fields written on the render thread and read elsewhere without `volatile` or a lock. That is fine for a 3 s mean at 100 ms cadence, and it is not fine for anything sample-accurate. No proposal may depend on a single GR reading; every rule that uses GR uses the mean over at least 3 s, and the accepted cost of a torn read is one outlier sample that the mean discards. Nothing in this design gates audio on a GR value.

**VAD availability.** The VAD only exists while `denoise.on` is true, because it is a by-product of the denoise stage. The analysis must therefore work without it. Fallback speech discriminator: RMS frame above `noiseFloor + 15 dB`. The VAD, when present, tightens the noise-floor estimate (exclude frames with p < 0.2) and improves the speech fraction. It is never required.

**Band level reference.** All spectral ratios are taken against the 500 Hz band, set to 0 dB, on the post-strip tap, averaged over speech frames. Ten bands, ISO centres 31 Hz to 16 kHz, matching `TenBandEq.CentresHz`.

---

## 4. What it proposes

Six proposal kinds. Each has: a trigger, a derivation, the clamp taken from the real parameter table, the evidence floor below which it stays silent, and what the operator sees.

Common preconditions for every kind (checked once, before any rule runs):

- At least 6 s of audio in the ring and at least 3 s of speech frames in the last 8 s.
- Strip is not muted, output short-term loudness above -50 LUFS, and speech fraction at least 0.4.
- Measured noise floor is known (see evidence floor per kind).
- The proposed value differs from the current value by at least the parameter's step. Otherwise say nothing.

### 4.1 Level (new `in.gain`)

**The lever, and why not the fader.** `MicStrip.Volume` exists (`MicMixer.cs:93-97`, applied at `:527`) but is the wrong lever: it is linear 0..2, so its ceiling is +6 dB; it is app-domain state outside `StripParameters`, so it never appears in `SnapshotStrips()`; and it is the operator's fader, which automation must not move. Instead add `StripParameters.GainDb` and insert the already-written, already-tested `GainStage` (`JockeyBox.Dsp/GainStage.cs`, currently used only by `StripChain` and tests) as the first stage of `MixerStrip.Process`. One new API parameter, `in.gain`, range -24 to +24, step 0.5, default 0, unit dB. This is the only new control the feature needs.

**Trigger.** `|target - shortTermLufs| >= 1.5 dB`, where `shortTermLufs` is the median of the last 3 short-term readings taken over speech frames.

**Derivation.** `delta = target - shortTermLufs`, then:
- `1.5 <= |delta| <= 9`: propose `in.gain = clamp(current + delta, -24, +24)` rounded to 0.5.
- `9 < |delta| <= 12`: propose the full delta and attach the note "this is a large correction; check the interface preamp gain".
- `|delta| > 12`: propose nothing. Show the measurement only, with "check the interface preamp gain, the strip cannot fix this without adding noise". Amplifying a badly set preamp by 18 dB raises the noise floor by 18 dB and that is the operator's problem to solve at the source, not the coach's to hide.

**Compressor interaction.** If mean compressor GR over the last 3 s exceeds 8 dB, do not propose gain; propose a compressor threshold change instead (§4.5). More input into a pinned compressor buys nothing and raises the noise floor.

**Clamp.** `in.gain` -24..+24 dB, step 0.5, through the same `Ranges.Fix` path as every other field.

**Evidence floor to speak.** 4 s of speech frames, short-term stable within 1 dB across the last three readings. Below that, no proposal.

**Operator sees.** "Short-term is -31.4 LUFS, target -16.0 LUFS: raise input gain by 15.0 dB." Accept / Dismiss. (Example is over the 12 dB limit deliberately: that case shows the note instead.)

### 4.2 Gate threshold

**Trigger.** `gate.on` is already true, and `|gate.threshold - (noiseFloor + 8)| >= 3 dB`.

**Derivation.** `threshold = noiseFloor + 8` dB, hysteresis 4 dB, everything else untouched. 8 dB sits in the middle of the consensus 6-10 dB above the floor; 4 dB sits in the middle of the consensus 3-6 dB hysteresis band.

**Hard rule.** The coach never sets `gate.on`. Enabling a gate on an input that might carry music chops reverb tails and quiet sung phrases, and this is a karaoke strip. Gate tuning is offered only to an operator who has already decided they want a gate.

**Extra precondition.** The floor must be at least 20 dB below speech short-term loudness. If the "floor" is only 10 dB down, it is not a floor, it is a quiet passage, and a gate set above it will chew the ends of words.

**Clamp.** `gate.threshold` -80..0, step 1. `gate.hysteresis` 0..24, step 1. `gate.floor` is depth and is not proposed; it stays where the operator put it.

**Evidence floor.** Noise floor known and stable within 1.5 dB across two estimates 4 s apart.

**Operator sees.** "Noise floor is -62 dBFS. Gate threshold -40 dB is 22 dB too high: lower it to -54 dB." Accept / Dismiss.

### 4.3 High-pass corner

**Trigger.** `hp.on` is true (it is true by default at the transparent 20 Hz corner) and low-band energy is excessive: mean of the 31 Hz and 63 Hz bands is at least 6 dB above the 250 Hz band.

**Derivation.** Excess of 6-12 dB: propose 80 Hz. Excess above 12 dB, or the 125 Hz band also above 250 Hz by 6 dB: propose 100 Hz. Never propose above 120 Hz. 80 Hz is below every adult male fundamental (85-180 Hz) and every female one (165-255 Hz), so it is safe for all speech; 120 Hz rolls off the deepest male voices.

**Hard rule.** Never enable `hp.on` (it is already on by default, so this never comes up in practice) and never apply a high-pass to a source that is not voice: require speech fraction at least 0.6 over 10 s.

**Clamp.** `hp.freq` 20..24000 with a proposed-value ceiling of 120 Hz.

**Evidence floor.** 10 s of speech frames, low-band excess within 2 dB across two estimates 5 s apart.

**Operator sees.** "Rumble at 31-63 Hz is 9 dB above the 250 Hz band. High-pass is at 20 Hz: raise to 80 Hz."

Also offered when the corner is already above 80 Hz and the excess has gone: "The 31-63 Hz excess is gone; the high-pass at 100 Hz is now cutting into the voice. Lower to 80 Hz." This is the only downward proposal, and it exists because a high-pass left too high is a common and audible fault.

### 4.4 EQ tilt

**Trigger.** One or more defect rules fire. There is no target curve. The rules, all on the post-strip 10 s speech average, all relative to the 500 Hz band at 0 dB:

| Defect | Condition | Proposal |
|---|---|---|
| Boxiness | 250 Hz or 500 Hz at least 4 dB above the mean of 1 kHz and 2 kHz | cut that band by `min(3, excess * 0.5)` dB |
| Harshness | 2 kHz or 4 kHz at least 5 dB above the mean of 500 Hz and 1 kHz | cut that band by `min(3, excess * 0.5)` dB |
| Dullness | 8 kHz at least 18 dB below the 2 kHz band | raise 8k and 16k by `min(2, deficit * 0.25)` dB |

**Never boosted:** 31 Hz and 63 Hz. Rumble is a high-pass problem, and boosting it is how a mic strip gets a boom.

**Never touched by EQ:** 4-8 kHz when a de-esser proposal is live, because EQ and de-essing fight over the same band and the operator gets two contradictory cards.

**Accumulation rule.** A proposal needs total absolute correction of at least 1.5 dB across the bands it touches, and total absolute correction per proposal is capped at 6 dB. Values are rounded to the 0.5 dB step. Because the measurement is post-strip, the proposal is always an increment on top of what the operator already has: `newGain = clamp(current + correction, -15, 15)`.

**Evidence floor.** 10 s of speech frames, and each triggering band stable within 1.5 dB across two estimates 5 s apart. 31 Hz and 63 Hz are excluded from EQ entirely, so no evidence question arises there.

**Operator sees.** "250 Hz and 500 Hz are 6 dB above the 1 kHz-2 kHz region. Cut 250 Hz by 2.5 dB and 500 Hz by 3.0 dB."

Why not curve matching: a "natural voice" target curve is an assumption dressed as a measurement. These three defects are the ones an engineer can hear and name, and each is a ratio between bands of the same recording, so a wrong mic or a wrong distance cancels out.

### 4.5 Compressor

**Trigger.** Pre-strip crest factor over 3 s of speech frames is at least 14 dB, and either `comp.on` is false or mean compressor GR is below 1 dB.

**Derivation.** ratio 3.0, attack 10 ms, release 100 ms, knee 6 dB, makeup 0 dB, and `threshold = p95(momentary loudness over speech frames) - 6`.

The arithmetic behind the -6: to get 4 dB of GR at 3:1, the input must sit `4 / (1 - 1/3) = 6` dB over threshold. Setting threshold 6 dB below the loud passages therefore lands GR in the 3-6 dB target on those passages. Makeup stays at 0 because the level proposal re-measures loudness after the compressor is accepted; letting both add gain double-counts.

**Clamp.** `comp.threshold` -60..0, `comp.ratio` 1..20, `comp.attack` 0.01..1000, `comp.release` 0.01..5000, `comp.knee` 0..24, `comp.makeup` -12..+24. Proposed values are capped tighter than the clamp: ratio never above 4, release never below 50 ms.

**Hard rules.** Never raise ratio above 4 on spoken word; 2:1 to 4:1 is the practitioner consensus for speech. Never drop release below 50 ms on programme; short releases pump on sustained vowels.

**Evidence floor.** 6 s of speech frames, and the crest factor must hold above 14 dB on two consecutive estimates.

**Operator sees.** "Crest factor is 17 dB and the compressor is off. Set 3:1, 10 ms attack, 100 ms release, threshold -24 dB, knee 6 dB."

Marked as consensus, not a standard: no standards body specifies compressor settings for speech.

### 4.6 De-esser

**Trigger.** Sibilance excess at least 6 dB (p95 minus median of the 4-8 kHz to 500 Hz-2 kHz ratio over speech frames), and speech fraction at least 0.6. The speech-fraction rule is the music guard: cymbals look exactly like sibilance to an energy ratio.

**Derivation.** `deess.split = 6.5 kHz` (centre of the 4-8 kHz sibilance band, 6-7 kHz being the usual centre), threshold = `p95 sibilance frame level - 4` dB, `deess.amount` (= ratio) 4, `deess.maxred` 6, attack 0.5 ms, release 50 ms. Sidechain low/high stay at 4.5/9 kHz.

**Clamp.** threshold -60..0, amount 1..20, split 1..20 kHz, maxred 0..60, attack 0.01..1000, release 0.01..5000, sidechain low/high 20 Hz-20/24 kHz. Proposed values capped: maxred never above 6, amount never above 6, split never outside 4-8 kHz.

**Hard rule.** Never propose more than 6 dB of reduction. Beyond that, "s" turns to "th" and the operator hears a lisp.

**Evidence floor.** 6 s of speech frames, and the p95-median gap stable within 1.5 dB across two estimates.

**Operator sees.** "Sibilance at 6.5 kHz is 8 dB above the speech body. De-ess 4 dB at 6.5 kHz, max 6 dB."

### 4.7 What it never touches

`bypass`, `denoise.on`, `denoise.mix`, `lp.on`, `lp.freq`, `gate.on`, `gate.floor`, `gate.attack`, `gate.release`, `gate.hold`, `rev.*`, `pitch.*`, `deess.sidechain.*`, `limiter.on`, `limiter.release`, device selection, `MicStrip.Volume`, `MicStrip.Muted`, `MicStrip.Monitor`.

Two of those deserve the reason stated out loud:

- `limiter.on`: enabling it adds `Limiter.LatencyFrames` to the strip and re-pads every peer through `RecalculateLatency` (`MicMixer.cs:418-422`). Latency is compensated, but the change touches every other mic in the block, and that is a decision for the operator. The coach tunes an existing limiter (§4.8) and never creates one.
- `denoise.mix`: a denoise mix decision is a taste call between artefacts and noise. There is no measurement that settles it, so it is not a proposal.

### 4.8 Limiter ceiling (tune only, no rebuild)

`MixerStrip.Apply` retunes a live limiter in place (`MixerStrip.cs:180-186` assigns `CeilingDb` and `ReleaseMs`), and `Limiter.cs:61-75` implements `CeilingDb` as a setter that recomputes only `_ceilingLin`, so the lookahead buffers are untouched and latency does not change. Rebuilding would churn the heap and lose the lookahead buffer.

The stale XML at `MixerStrip.cs:127-130` still claims the opposite. **That doc comment is a bug** and should be fixed in the same change as the test below, or the next reader will re-derive the false constraint.

So the spec does not need a strip-rebuild path for the ceiling. It needs a test that pins the behaviour, because it is load-bearing: apply a new ceiling to a built strip, assert `LatencyFrames` is unchanged and the output clamps at the new ceiling within one block.

**Trigger.** `limiter.on` is true, the 3 s true peak exceeds the ceiling target, and the operator's monitor send for this device is off.

**Derivation.** The parameter is dBFS sample peak; the target is dBTP. Convert with the measurement: `ceiling = targetDbtp - (truePeakDbtp - samplePeakDbfs) - 0.5` dB, using the measured delta between reconstructed and sample peak. With a -1 dBTP target and a measured 0.8 dB overshoot, that lands near -1.3 dBFS rather than -1.0, which is the point.

**Clamp.** `limiter.ceiling` -12..0, step 0.1.

**Evidence floor.** A true-peak over at least two 3 s windows, and a limit of one proposal per session per device: a ceiling that keeps needing to move means the gain staging upstream is wrong.

**Operator sees.** "True peak is +0.4 dBTP, ceiling is 0 dBFS. Lower to -1.3 dBFS for -1 dBTP."

---

## 5. The proposal object

Home: **`src/JockeyBox.Dsp/Coach/`**. New namespace `JockeyBox.Dsp.Coach`, net8.0, no WinUI, testable on Linux. Files: `MixProposal.cs` (the types below), `MicCoach.cs` (the decision function), `CoachSettings.cs` (targets and floors).

```csharp
namespace JockeyBox.Dsp.Coach;

public enum ProposalKind { Level, GateThreshold, HighPass, EqTilt, Compressor, DeEsser, LimiterCeiling }

/// <summary>One parameter write, absolute, with the value it replaces.</summary>
public sealed record ProposedChange(string Param, double From, double To, string Reason);

public sealed record MixProposal(
    ProposalKind Kind,
    string DeviceId,
    long ObservedAtMs,
    string Headline,                     // one sentence of measurement, not a verdict
    IReadOnlyList<ProposedChange> Changes,
    IReadOnlyList<string> Evidence,      // the measured facts, in the order that led to the proposal
    double Confidence);                  // 0..1, secondary, never the whole story
```

**Fields, and what each is for.**

- `Changes` are absolute target values, never deltas. `From` exists so undo needs no separate history and so the UI can show "40 to 54" rather than "+14".
- `Param` is the API parameter id (`gate.threshold`, `eq.250`, `in.gain`). The same string is the write key and the clamp key, so there is exactly one naming system.
- `Headline` is the measured sentence. "Short-term is -31.4 LUFS, target -16.0" is judgeable. "Confidence 78%" is not: a percentage is unanchored, and the operator cannot tell whether 78 is good. This is synthesis rather than a published audio finding, and it is the reason `Confidence` is secondary.
- `Evidence` is one short string per measurement that the rule used, so the card can show the reasoning instead of the conclusion. Automation bias (Parasuraman and Riley 1997) is the failure mode where operators accept a suggestion without checking it; showing the inputs is the cheapest mitigation and it also lets an operator reject a correct-looking suggestion built on a wrong assumption, for example when they know the singer is hoarse today.

**What confidence means here.** Hard evidence conditions are binary: if they fail, no proposal exists. `Confidence` is a display and ordering value, computed as the product of three stability factors, each clamped 0..1: speech seconds divided by 6, band or level stability (1 minus the spread across the last two estimates divided by 3 dB), and VAD availability (1.0 with, 0.8 without). It exists so the UI can order several cards and so a weak card looks weak. **It never decides whether a proposal is shown**, and no rule is allowed to consult it.

**Clamp rule.** Every `To` is produced already inside the parameter's range and rounded to its step, so `To` is always idempotent under `Ranges.Fix`. The accept path still passes through `StripParameters.Clamp()` because it always does, but a proposal that changes meaning under `Clamp()` is a bug in the rule, not a save by the clamp. A test asserts `Clamp(apply(current, proposal)) == apply(current, proposal)` for every published proposal.

**One-shot semantics.** A proposal is created once, immutable, and never updated. It is queued to the UI as a card. Its lifecycle is `Open → Accepted | Dismissed`. There is no state in which a card changes its numbers underneath the operator.

**Dismissal.** Dismissal silences that `(device, kind)` pair for the rest of the session. The operator can re-open it with "Check again", which clears the mute for that kind and re-measures. There is no automatic re-arm, not even on a large change: one-shot, explicit, dismissible beats persistent nagging, and no source supports continuous silent re-proposing.

**Accept and undo.** Accept posts one `Commands` action that applies every change for that device in a single `ApplyAll` call, so the strip moves atomically rather than staggering field by field across blocks. The ledger records the accepted `Changes`. Undo posts `From` for every change whose current value still equals `To`; fields that moved since are skipped, and the undo result names them. Undo is available until a new proposal for the same kind is accepted, or the session ends.

**Guarantee.** Nothing is applied without acceptance, and there is no code path from `MicCoach.Propose` to a strip write. The policy returns values; the UI posts them. That is enforceable by inspection: `MicCoach` takes no `RenderCommands`, no `MicMixer`, no strip reference.

---

## 6. Architecture and threading

**The pure policy.** Mirrors `BpmQueuePolicy`: a static function of facts, with the clock injected so tests own time.

```csharp
public static class MicCoach
{
    public static IReadOnlyList<MixProposal> Propose(
        string deviceId,
        in AnalysisSnapshot observation,
        in StripParameters current,
        CoachSettings settings,
        long nowMs);
}
```

`AnalysisSnapshot` is a `readonly record struct` (`JockeyBox.Dsp/Analysis/AnalysisSnapshot.cs`) holding: `ShortTermLufs`, `MomentaryLufs`, `TruePeakDbtp`, `SamplePeakDbfs`, `NoiseFloorDbfs` plus a `bool NoiseFloorKnown`, `CrestFactorDb`, `SibilanceExcessDb`, `SpeechFraction`, `VoiceProbability` plus a `bool VadAvailable`, ten band levels as ten named doubles (the same no-array shape as `EqParameters`, so it copies without allocating), mean `CompressorGrDb`, mean `DeEsserGrDb`, gate open ratio, and `SpeechSeconds` observed. Tests construct it directly; no audio device, no engine.

**What it reads.** Only the snapshot and the parameter record. Never a strip, never a processor, never a command queue.

**Where analysis runs.** `src/JockeyBox/Audio/Coach/MicCoachService.cs`, a `System.Threading.Timer` at 100 ms (the same pattern as `TrackMetadataStore`). Each tick:

1. `Mics.SnapshotStrips()` for parameters (lock, copy, off render thread).
2. Drain new samples from the two per-device rings.
3. Update `LoudnessMeter` and `StripAnalysis`.
4. Every 5th tick, recompute the spectrum (500 ms cadence).
5. When the observation has settled, call `MicCoach.Propose` and publish any new `MixProposal` to the UI.

Budget: 4800 samples of two biquads plus a 4x true-peak pass per tick is tens of microseconds; a 2048-point FFT every 500 ms is microseconds. A 2 ms per tick budget is generous by an order of magnitude, and the timer callback is allowed to overrun into the next tick without consequence because the rings do not care.

**The tap and why latency is unchanged.** `MicMixer.Render` already has each device's block contiguous in `_scratch` before and after `strip.Process`. `AnalysisTap` writes mono copies into two preallocated rings at those two points (`MicMixer.cs:516-545`). Cost per block: two 480-float copies, about 2 microseconds, allocation-free. The ring write updates an index and never blocks; if the analysis thread has fallen behind, the oldest samples are overwritten and the analysis skips the gap, which is a missing sample, not a stall. The render thread never waits for the analysis thread, never calls it, and never reads its state. Zero added render-thread latency, exactly as §5 requires, with the one honest cost that the render thread does 3.8 KB of copying per block per device.

**Tap points and what they measure.**

- Pre-strip (before `Process`, after `Read`): noise floor, crest factor.
- Post-strip (after `Process`, before `MicStrip.Volume`): loudness, true peak, spectrum, sibilance.

The post-strip tap deliberately excludes `MicStrip.Volume`. The fader belongs to the human. If the coach compensated for it, lowering the mic in the mix would make the coach propose raising the input gain, which is exactly the fight this design exists to avoid. The UI therefore labels the coach's loudness readout "strip output, pre-fader".

**Two hard constraints, handled.**

*Constraint 1, the limiter ceiling.* Resolved by the code as of `68802e1`: `MixerStrip.cs:180-186` and `Limiter.cs:61-75` retune ceiling and release in place, latency flat. No rebuild path is needed and none is built. The stale XML at `MixerStrip.cs:127-130` is corrected, and a test pins the retune so nobody reintroduces the assumption. What remains true, and is respected: enabling the limiter for the first time adds lookahead latency and re-pads peers, so the coach tunes and never enables, and only when the device's monitor send is off.

*Constraint 2, the RNNoise frame size.* `RnNoiseStage` throws on a partial frame, so nothing may hand it arbitrary buffers. The design never does. The VAD is not run by the analysis at all: it is read from `RnNoiseStage` as a by-product of the samples the render thread already processed, exposed as one `volatile float`. The analysis tap works in whole 480-sample blocks and never constructs a frame or calls `ProcessFrame`. If a future RNNoise build changes `FrameSize`, the tap is unaffected because it copies blocks, not frames; the only thing that breaks is the assumption that one block is one frame, and the VAD value is simply read once per block either way.

**Where accepted proposals land.** `MicMixer.Commands.Post(() => Mics.ApplyAll(updates))`. `ApplyAll` clamps, applies the whole device batch, and calls `RecalculateLatency` once after the batch (`MicMixer.cs:418-422`). One block sees one consistent set of targets, which is the property the existing preset path already relies on.

**Persistence.** A proposal is not persisted. Accepted values persist through the existing `MixerSettingsStore` path, because they are strip parameters like any other. On restart, the coaching state is empty and the operator's accepted values are simply the current state, which is correct: the coach measures the current state, it does not remember an intention.

---

## 7. The numbers table

Every default the design adopts. `S` = standard or specification. `C` = practitioner consensus. `A` = assumption or local choice. `Y` = synthesis, derived here, not published.

| Number | Value | Source | Class |
|---|---|---|---|
| Loudness standard | ITU-R BS.1770-5 (Nov 2023) | ITU | S |
| Programme loudness | EBU R 128 v5.0 (Nov 2023), Tech 3341/3342 | EBU | S |
| Broadcast loudness, US | ATSC A/85:2026-07 | ATSC | S |
| K-weighting | high-shelf + RLB high-pass, channel weight 1.0 for a mono mic | BS.1770-5 | S |
| Momentary window | 400 ms, ungated | BS.1770-5 | S |
| Short-term window | 3 s, ungated | BS.1770-5 | S |
| Integrated gates | -70 LUFS absolute, -10 LU relative | BS.1770-5 | S |
| Integrated suppressed | under 60 s of programme | EBU Tech 3342 | S |
| True peak | 4x oversample, reconstructed waveform, not sample peak | BS.1770-5 Annex 2 | S |
| EBU R 128 target | -23 LUFS, +/-1 LU, -1 dBTP | EBU R 128 | S |
| ATSC A/85 target | -24 LKFS, +/-2 LU, -2 dBTP | ATSC A/85 | S |
| Spotify | -14 LUFS, lifts quiet tracks only to 1 dB true-peak headroom | Spotify published guidance | S |
| YouTube | -14 LUFS, downward only | YouTube published guidance | S |
| Twitch | no published normalisation; -14 is community convention | none | A |
| TikTok | nothing published | none | A |
| **Mic strip level target (default)** | **-16 LUFS short-term, -1 dBTP** | podcast convention, chosen as a gain-staging anchor | A |
| Level deadband | 1.5 dB, 3 readings stable within 1 dB | chosen | Y |
| Level large-correction note | over 9 dB; refuse over 12 dB | chosen | Y |
| Gate threshold above floor | 8 dB (band 6-10) | practitioner consensus | C |
| Gate hysteresis | 4 dB (band 3-6) | practitioner consensus | C |
| Gate floor precondition | floor at least 20 dB under speech | chosen margin | Y |
| High-pass, safe for all speech | 80 Hz | male 85-180 Hz, female 165-255 Hz fundamentals | S |
| High-pass, common | 100 Hz | practitioner consensus | C |
| High-pass, female-safe limit | 120 Hz; KMS 104 ships 120 Hz HPF | product spec plus fundamentals | C |
| Compressor ratio, speech | 2:1 to 4:1 | practitioner consensus, not a standard | C |
| Compressor attack / release | 5-20 ms / 50-150 ms | practitioner consensus | C |
| Compressor GR target | 3-6 dB | practitioner consensus | C |
| Compressor derivation | threshold = p95 momentary - 6 dB for ~4 dB GR at 3:1 | arithmetic, this spec | Y |
| De-esser band | 4-8 kHz, centre about 6.5 kHz | consensus | C |
| De-esser reduction cap | 6 dB (3-6 dB typical) | consensus; exceeding it causes s/th lisping | C |
| Limiter lookahead | a few ms, implementation-specific | consensus | C |
| Limiter ceiling | -1 dBTP delivery, -2 dBTP strict or low bitrate | consensus | C |
| Noise floor estimate | 10th percentile of 50 ms RMS, 8 s, min 100 frames | chosen; minimum-statistics alternative | Y |
| Speech fallback threshold | floor + 15 dB | chosen | Y |
| Spectral analysis | 2048-point Hann, 10 ISO bands, 10 s average, 500 ms cadence | chosen to match `TenBandEq` | Y |
| EQ correction factor / cap | 0.5 of measured excess, max 3 dB per band, 6 dB per proposal | chosen to stay gentle | Y |
| EQ dullness rule | 8 kHz at least 18 dB under 2 kHz | heuristic, unvalidated | A |
| Sibilance metric | p95 minus median of 4-8 kHz to 500 Hz-2 kHz ratio | chosen | Y |
| Analysis cadence | 100 ms timer, spectrum every 5th tick | matches diagnostics timer order | A |
| Evidence floors | 3-6 s of speech depending on kind; 10 s for EQ and high-pass | chosen | Y |
| Percentage is unanchored | hence measured sentence primary, score secondary | synthesis, not a published audio finding | Y |

No number in this table is presented as a standard unless the class column says S.

---

## 8. Refusals and limits

Hard rules. Each is a line of code in `MicCoach`, and each has a test that asserts silence.

| Refusal | Reason |
|---|---|
| Never set `gate.on` | A gate on music chops reverb tails and quiet sung phrases. This is a karaoke strip; the input is often music. Tuning an existing gate is allowed, enabling one is not. |
| Never gate when the floor is under 20 dB below speech | No usable separation; the gate will chew word ends. |
| Never enable `hp.on` or apply a high-pass to a non-voice source (speech fraction under 0.6) | A high-pass on music removes the bass line and the kick. |
| Never propose more than 6 dB of de-essing | "S" becomes "th". |
| Never de-ess with speech fraction under 0.6 | Cymbals and hi-hats have the same 4-8 kHz energy profile as sibilance. |
| Never raise compressor ratio above 4, never release below 50 ms on programme | Pumping on sustained vowels. |
| Never enable the limiter, and never propose a limiter change while the device's monitor send is on | Enabling adds lookahead latency and re-pads every peer in the block; the monitor path is the operator's cue. |
| Never boost 31 Hz or 63 Hz | Rumble is a high-pass problem. Boosting it is how a strip gets a boom. |
| Never touch `rev.*`, `pitch.*`, `denoise.mix`, `bypass`, device selection, `MicStrip.Volume`, `Muted`, `Monitor` | Taste calls, or the operator's own controls, or state that would make the coach fight the human. |
| Never propose from a single GR reading | The GR getters are non-volatile plain fields. A mean over 3 s is sound; one sample is not. |
| Never speak on silence, on music, or on fewer than 3 s of speech | Automation bias makes a confident card on bad evidence worse than no card. |
| Never write without acceptance; never lock a control; never re-propose after dismissal | The advisory contract in §1. |

Limits, stated plainly:

- The coach cannot fix a bad preamp setting. Above 12 dB of required correction it refuses and says so.
- It cannot see a limiter's gain reduction, because `Limiter` does not expose it. True peak over the ceiling is the proxy.
- It measures one channel per device, taken as mono. It has no opinion about stereo imaging, phase, or two-mic comb filtering.
- It has no model of the music bed. It cannot tell whether a mic sits well in the mix, only whether the strip alone is well behaved.
- Its EQ rules are heuristics with no validation set. They are capped at 3 dB per band precisely because they are not trustworthy above that.

---

## 9. Open decisions for the operator

Four genuine choices. Everything else is settled by measurement or by the refusals above.

1. **Default level target.** -16 LUFS (podcast), -14 LUFS (streaming) or -23 LUFS (broadcast). Recommendation: **-16 LUFS**, because it is a gain-staging anchor for the strip rather than a delivery spec, it leaves 2 dB of headroom over -14 for a live mic, and it is the least likely to make a close-mic'd singer sound thin. Per-device override in `CoachSettings`.

2. **Does the pipeline respect chain order as a preference?** EQ before or after compression, and de-esser before or after compression, are both used by competent engineers. The chain in this repo is fixed (`MixerStrip.Process`: denoise, high-pass, low-pass, EQ, gate, compressor, de-esser, limiter) and this spec does not propose reordering, because reordering is a strip rebuild and a different sound. Decision needed: does the operator want a setting that changes processing order, which means a real feature, or is the fixed order accepted as the house sound? Recommendation: **accept the fixed order**, document it as the house sound, and revisit only if someone can name a case where it is audibly wrong. Rewiring a working chain to satisfy a preference that both camps use is not worth the regression surface.

3. **Surface the VAD?** It costs one `volatile` field and one line in `RnNoiseStage`, and it is only available when denoise is on. Exposing it as a meter tells the operator "this is speech, this is not" and makes every proposal's evidence auditable. Recommendation: **yes, expose it as a small meter on the Coach panel**, labelled with its dependency on `denoise.on` so nobody wonders why it reads zero with denoise off.

4. **Is a limiter ceiling proposal worth it now?** It needs true peak measurement, a conversion from dBTP to dBFS, and a test pinning live retune. It is the least valuable of the six, since the limiter is off by default and the strip rarely clips a properly gained input. Recommendation: **last**, and only after the meter has been in the operator's hands for a while. If the ceiling never needs to move in practice, the feature is dead weight and should not be built.

---

## 10. Build order

Each step is independently shippable, each is Linux-testable except the UI card, and each has value even if the next one is never built.

**Step 0. Correct the stale limiter claim.** Rewrite the XML at `MixerStrip.cs:127-130`, add a test that applies a ceiling change to a built strip and asserts `LatencyFrames` is unchanged and the output clamps at the new ceiling within one block. Ten minutes, and it stops the next reader re-deriving a false constraint. (`tests/JockeyBox.Dsp.Tests`.)

**Step 1. The loudness meter.** `JockeyBox.Dsp/Analysis/LoudnessMeter.cs` plus tests: 1 kHz sine at -23 dBFS reads -23.0 LUFS +/-0.1; a 12 kHz sine at -3 dBFS sampled at 45 degrees reads sample peak -6.0 dBFS and true peak -3.0 dBTP +/-0.3; the Tech 3341 three-level gating case yields -23.0 integrated; momentary is not reported before 400 ms of input; short-term not before 3 s. No app wiring, no UI. This is the highest-value, lowest-risk piece and §5 already says so.

**Step 2. The taps and the analysis pass.** `AnalysisTap` (two rings per device, written from `MicMixer.Render`), `StripAnalysis` (noise floor, crest, spectrum, sibilance), `AnalysisSnapshot`, and `MicCoachService` on a 100 ms timer. Expose the numbers on the existing diagnostics surface so they can be read without a UI. Ship the LUFS and dBTP readout to the mixer panel now: a working meter is a win on its own. Tests: synthetic block sequences through the tap, percentile floor against a known noise signal, band sums against a sine sweep.

**Step 3. The gain lever.** `StripParameters.GainDb`, `GainStage` inserted as the first chain stage in `MixerStrip.Process`, `in.gain` in `BuildParams()` and in `ParamDef` read/write, `Clamp()` range -24..+24, tests for neutral transparency, clamping, and that `MixerStrip.Apply` moves the gain without changing latency. Without this step the level proposal has no lever; `comp.makeup` and `MicStrip.Volume` are both wrong for the reasons in §4.1.

**Step 4. The policy, level and gate only.** `MixProposal`, `CoachSettings`, `MicCoach.Propose` with two rules. Tests mirror `BpmQueuePolicyTests`: clock injected, snapshot constructed by hand, assert exact proposal values, assert silence below the evidence floor, assert dismissal state, assert `Clamp` idempotence. Pure, no audio, no device.

**Step 5. Wire proposals to a surface.** HTTP first: `GET /api/mic/coach/{device}/proposals`, `POST .../accept`, `POST .../dismiss`, `POST .../undo`, on the existing `MixerApiRouter` pattern, with the ledger in the app layer. This makes the feature testable end to end without any XAML, which is the cheapest way to find out whether the proposals are any good.

**Step 6. The Coach panel.** Cards with the headline, the evidence lines, Accept, Dismiss, Undo. No badge, no glow, no percentage as the primary figure.

**Step 7. High-pass, EQ, compressor, de-esser.** In that order. High-pass first because it is the most reliable rule and the easiest to hear. EQ last among the three because it is the most subjective.

**Step 8. Limiter ceiling.** Only if §9 decision 4 says yes after real use.

Steps 1 to 5 are testable on Linux with no Windows audio device, which is most of the work.

---

## 11. Out of scope

- Integrated-loudness delivery reports, and any loudness normalisation of the recording or the mix bus. The meter computes integrated loudness; nothing acts on it.
- Any processing of the DJ decks, the music bed, or the master bus. The coach sees mic strips only.
- Reverb, pitch, denoise mix, and any new DSP stage beyond inserting the existing `GainStage`.
- Automatic strip rebuilds, chain reordering, and enabling any stage the operator has not enabled.
- A learned model, per-operator preference learning, or anything that improves from accept/dismiss history. The ledger exists for undo, not for training.
- Windows endpoint volume (`IAudioEndpointVolume`). The strip is app-domain and stays that way.
- Multi-mic coherence, phase alignment between two mics, and anything requiring cross-device analysis.
- Persisting proposals across restarts, notifications outside the UI, and any silent background action.
