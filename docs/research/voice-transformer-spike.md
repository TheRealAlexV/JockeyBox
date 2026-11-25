# Real-time voice transformer in one 480-frame block — feasibility spike

Status: research only. No production code, no integration decision taken.
Scope: the live mic monitor path first, the record/stream path second.

---

## 1. The constraint, stated precisely

| Quantity | Value |
|---|---|
| Sample rate | 48 000 Hz |
| Block | 480 frames = **10.0 ms** |
| Monitor budget, added latency | **20 ms** (2 blocks) |
| Already spent: limiter lookahead | 5 ms (0.5 block) |
| Already spent: NVIDIA AFX `denoiser` / `dereverb` | 10 ms (1 block), if enabled |
| **Free headroom for a new stage, AFX on** | **≈ 5 ms (0.5 block)** |
| Free headroom, AFX off | ≈ 15 ms (1.5 blocks) |
| Record/stream budget | 50 ms, capture buffer allowed |

Two things must therefore be answered separately, and they are *not* the same question:

1. **Compute** — can the DSP finish a block inside 10 ms of wall clock, on one core, without allocating?
2. **Lookahead** — how much *future* audio does the algorithm need before it can emit the current sample?

A stage that needs 20 ms of lookahead cannot live on the monitor path even if it costs 0.1 ms of CPU,
because lookahead is added latency by definition. Point 2 is where every real vocoder dies.

A useful rule that follows directly, and that decides most of this document:
**any stage that resolves a spectral envelope needs a window of at least 2–3 pitch periods.**
At a 100 Hz male F0 that is 20–30 ms; at the 71 Hz floor of WORLD it is 28–42 ms.
Formant-aware processing therefore cannot be a 5 ms stage, no matter how fast the CPU is.

---

## 2. Candidate findings

### 2.1 WORLD (Morise) — licence fine, port exists, latency is the problem

**Licence: confirmed, and it is the best licence in this document.**
The official repository states: "This source code is released under the modified-BSD license.
There is no patent in all algorithms in WORLD." — <https://github.com/mmorise/World>.
Modified BSD is closed-source-compatible with attribution; the explicit no-patent statement removes the
usual vocoder patent anxiety (the classic PSOLA/phase-vocoder patent minefield). This is not a blocker.

**C# / .NET ports — four exist, and one of them is genuinely good.**

| Port | Repository | Last activity | Licence | Port or wrapper | Per-frame cost published? |
|---|---|---|---|---|---|
| **WorldNet** | <https://github.com/routersys/WorldNet> (page was 404 at time of writing; package page reachable) · NuGet `WorldNet` 1.2.1 | package published for .NET 8/10, 2026 | **MIT** (per package page) | **Straight managed C# port**, no native library, zero-allocation, Native AOT annotated | **Yes — per-stage benchmark table, published by the author** |
| World.NET | <https://github.com/aqtq314/World.NET> | last push **2026-09-18** (GitHub API) | MIT | **P/Invoke wrapper** over the native C++ (`World.dll`) | No |
| DotnetWorld | <https://github.com/yamachu/DotnetWorld> | last push **2021-11-08** (GitHub API) | **none detected** by GitHub (NOASSERTION) | wrapper | No |
| TorchScriptWORLD | <https://github.com/aqtq314/TorchScriptWORLD> | last push **2026-09-07** | not declared (NOASSERTION) | GPU reimplementation, TorchScript export | No |

DotnetWorld is the one to avoid: five years stale and **no declared licence**, which is a legal
non-starter for a closed-source app. World.NET is fine but reintroduces a native DLL, which the shell
currently does not need for DSP.

WorldNet is the interesting one. Its published claims and benchmark
(<https://www.nuget.org/packages/WorldNet/1.2.1>), measured on a Xeon at 22 050 Hz, 17 500 samples
(0.794 s of audio, 159 frames at 5 ms):

| Stage | Time for 0.794 s of audio | Cost per second of audio |
|---|---|---|
| Dio (F0) | 2.91 ms | 3.7 ms/s |
| StoneMask (F0 refinement, optional) | 2.92 ms | 3.7 ms/s |
| CheapTrick (spectral envelope) | 5.52 ms | 7.0 ms/s |
| D4C (aperiodicity) | **22.93 ms** | 29.1 ms/s |
| Synthesis | 6.74 ms | 8.5 ms/s |
| **Total** | **55.65 ms** | **70 ms/s ≈ 7 % of one core** |

That is the *author's* number on a 2.1 GHz Xeon-class VM, not an independent measurement, and no
third party appears to have reproduced it. Treat it as an upper bound with a fudge factor, not a promise.

**My estimate for our case (48 kHz, one 480-frame block).** Two scalings apply: 2.18× more samples per
second, and the CheapTrick/D4C FFT grows from 1024 to 2048 points (N·log N ratio ≈ 2.2). Net ≈ **2.2×**:

| Chain | Per second of audio | **Per 480-frame block** |
|---|---|---|
| DIO + CheapTrick + D4C + Synthesis | ≈ 154 ms/s | **≈ 1.5 ms** |
| DIO + CheapTrick + Synthesis (D4C skipped) | ≈ 42 ms/s | **≈ 0.42 ms** |

**So compute is not the problem — it fits with 3× to 12× margin.** D4C is skippable entirely for our
purpose: aperiodicity only controls the noise/harmonic mixture in synthesis, and a voice *character*
effect can run on unity aperiodicity. Dropping D4C is both the cheapest and the largest saving.

**The crux is the analysis window. WORLD is a whole-utterance batch algorithm.**

From the original sources (48 kHz, default 5 ms frame period, 71 Hz `f0_floor`):

| Stage | Window | **Lookahead** | Blocks consumed |
|---|---|---|---|
| Dio (<https://github.com/mmorise/World/blob/master/src/dio.cpp>) | Designed *before* per-frame work: one low-cut FIR applied as a **single FFT sized to the whole signal length**; F0 contour then refined over a ±N-frame neighbourhood (`voice_range_minimum` scales with `f0_floor`) | **not streaming at all**; ~35 ms of context if chunked (my estimate: 7 frames) | **7+** |
| CheapTrick (<https://github.com/mmorise/World/blob/master/src/cheaptrick.cpp>) | **centred** Hann window, `half_window = 1.5·fs/f0` → 3/f0 total | `1.5/f0` = **21 ms @71 Hz, 15 ms @100 Hz, 6.8 ms @220 Hz** | **3 / 2 / 1** |
| D4C (<https://github.com/mmorise/World/blob/master/src/d4c.cpp>) | `window_length_ratio = 4.0` → 4/f0, centred, sampled at ±0.25/f0 | `2/f0` = **28 ms @71 Hz, 20 ms @100 Hz** | 3 / 2 (skippable) |
| Synthesis | `WorldSynthesizer` is the **sequential/real-time** variant (buffer size in frames + overlap-add) | ~1–2 frames of parameter lookahead | **1–2** |
| **End to end, realistic** | | **≈ 25–35 ms** | **≈ 4–6 blocks** |

Verdict for WORLD: **the 480-frame block does not contain it.** It needs 4–6 blocks of lookahead even
in the friendliest configuration (no D4C, female-range F0 where CheapTrick drops to one block). It is a
**record/stream-lane** stage under the 50 ms budget, where 30 ms of analysis delay is comfortable.

A final caveat on quality, not latency: WORLD resynthesis of *unmodified* parameters is already audibly
"vocoder-ish" on sustained vowels, and a karaoke mic is exactly a sustained-vowel source. Pitch/formant
shifting through WORLD will sound processed. That is acceptable for a character voice, and not acceptable
as an "invisible" transparent stage — nobody should propose routing the clean monitor path through it.

---

### 2.2 RVC and so-vits-svc — 90–170 ms class, and one is licence-dead

**RVC.** Licence **MIT** on the reference implementation
(<https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI>, GitHub API licence field `MIT`;
last push 2026-08-04). That licence is why the project is everywhere — and it covers the *code*, not the
pretrained voice models, which each carry their own terms.

Latency, from the project's own README
(<https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI/blob/main/docs/en/README.en.md>):
*"We have achieved an end-to-end latency of 170 ms. With the use of ASIO input and output devices, we
have managed to achieve an end-to-end latency of 90 ms, but it is highly dependent on hardware driver
support."* I found **no independent third-party measurement** of RVC real-time latency during this spike;
every number in circulation traces back to those two figures or to user reports consistent with them.

**So my "60–80 ms class" framing was wrong — correct it to 90–170 ms.** Even the ASIO best case is
4.5× the entire monitor budget before the limiter's 5 ms is counted.

Runtime: **both**. PyTorch for training and the default inference, ONNX Runtime export for the real-time
GUI (`w-okada/voice-changer`, <https://github.com/w-okada/voice-changer>, 21k stars, last push
2026-11-17, no licence detected by GitHub in its root — another licence flag). Model size, approximate:
tens of MB for the per-voice checkpoint, but the pipeline also loads a HuBERT content encoder and an
RMVPE pitch model, so the resident footprint is several hundred MB plus a CUDA context. Real-time mode
with a documented buffer: **yes**, the `realtime_gui` / w-okada client exposes chunk and buffer settings;
typical usable chunks are 100–300 ms, which is where the 170 ms figure comes from.

**so-vits-svc. Disqualified on licence.** <https://github.com/svc-develop-team/so-vits-svc> is
**AGPL-3.0** and was **archived on 2023-11-11** (GitHub API). AGPL in a closed-source desktop app is the
worst case of the GPL family, and the project is dead. No further analysis is warranted.

Both are also the wrong *kind* of tool: they transfer identity from a training recording. The question
asked for a cheap voice change, not an impersonation.

---

### 2.3 DeepFilterNet3 — licence fine, and it is the wrong category

**Licence: confirmed dual.** The repository root carries `LICENSE`, `LICENSE-APACHE` and `LICENSE-MIT`
(<https://github.com/Rikorose/DeepFilterNet>, verified via the GitHub contents API), so the
MIT-or-Apache-2.0 choice you believed in is correct. Actively maintained: last push 2026-11-06, 5.1k stars.
Code and pretrained weights are published under that open licence per the paper
(<https://arxiv.org/abs/2305.08227>).

Latency: 48 kHz, 960-sample FFT (20 ms) with a 480-sample hop (10 ms), plus deep-filter lookahead —
the project's own issue tracker puts the total at `conv_lookahead + df_lookahead ≈ 4 frames ≈ 40 ms`
(<https://github.com/Rikorose/DeepFilterNet/issues/471>). Cost is low: real-time factor 0.19
single-threaded on a notebook CPU for the demo (<https://arxiv.org/abs/2305.08227>), 0.04 on a
Core-i5 for DFN2 (<https://arxiv.org/abs/2205.05474>).

**But it is a denoiser.** It does not change voice character in any way. Its only claim on this
document is as a *substitute* for NVIDIA AFX `denoiser`, and there it loses on both axes:

- AFX already gives denoise **and** dereverb inside the existing one-block (10 ms) slot, for free in
  latency and with no extra CPU on the audio thread, and it is already in the product.
- DFN3 costs 40 ms and 4 blocks to do one of those two jobs.

Verdict: **no.** Not a voice transformer, and redundant with what is already shipped. The only scenario
where it re-enters is "operator has an AMD/Intel GPU or no GPU and wants better denoise than RNNoise on
the record path" — a denoise question, not this one.

---

### 2.4 The cheap options that actually fit — pitch shift and formant shift, done simply

Here the field splits cleanly, and physics (not CPU) does the splitting.

**(a) Pitch-only shift.** No spectral envelope is estimated, so no long window is needed.

| Method | Added latency | Licence | Notes |
|---|---|---|---|
| **Delay-line resampling shifter** (two read pointers at rate *r*, crossfaded) — ~100 lines of C# | **= crossfade window**, 5 ms at 480 samples, 10 ms at 960 | your own code | Zero lookahead: both taps read *past* samples. Artifact rate = 1/crossfade, so 5 ms gives a 200 Hz warble. Formants travel with pitch (chipmunk / monster). This is what a cheap hardware voice pedal does. |
| **TD-PSOLA** (grain-based, needs F0) | 1–2 pitch periods ≈ **10–20 ms** (my derivation: a grain must be centred on a pitch mark, so you need the mark before you emit) | your own code, published algorithm | Better quality than (a), but the F0 estimate drags the latency back up. |
| **Phase vocoder**, 2048/512 at 48 kHz | **one full analysis window = 21–43 ms** | your own code / any FFT | Formant-preserving with the usual "phasiness"; needs the window filled before a frame can be transformed, so latency = window. |
| **SoundTouch** | author states i/o latency **"around 100 ms"** for time-stretch, "much shorter" for rate-only (<https://www.surina.net/soundtouch/README.html>) | **LGPL-2.1** (dynamic linking only in a closed app; a commercial licence is offered) | Source moved to <https://codeberg.org/soundtouch/soundtouch>. WSOLA `SEQUENCE_MS`/`SEEKWINDOW_MS`/`OVERLAP_MS` are tunable, so ~15–25 ms is achievable, at quality cost. |
| **Rubber Band** | configurable, R3 engine is the low-latency one | **GPL-2.0**, commercial licence required for a proprietary app (<https://breakfastquay.com/rubberband/license.html>) | Best quality of the three; **licence is out** unless bought. |
| **Signalsmith Stretch** | `configure(channels, blockSamples, intervalSamples)`; latency is whatever you configure, `inputLatency()`/`outputLatency()` report it | **MIT** (<https://github.com/Signalsmith-Audio/signalsmith-stretch>, confirmed via API), header-only C++11 | The best permissive option if we ever want polyphonic-grade quality. Header-only C++ means a native shim, and its *default* presets favour quality over latency (tens of ms), so it needs manual `configure()` to approach 10 ms. |

**(b) Formant-only or formant-independent shift.** This is where the 5 ms headroom is lost for good.
Resolving formants needs frequency resolution of roughly 50 Hz at 1 kHz, i.e. a 20 ms window; WORLD's
CheapTrick uses 3/f0, which is 15–21 ms of *half* window. There is no short-window trick that gets around
this. A crude "formant-ish" character can be faked with a small fixed filterbank (a 4-band shelf warp with
a 2–5 ms smoothing window), which sounds nasal/throaty but is not a formant shift and does not track the
speaker.

**Is pitch-only good enough to ship as v1? Yes — with one honest caveat.** Pitch-only shifting is
recognisably a voice change (every cheap hardware voice changer on the market is pitch-only, plus a
"formant" knob that is really a fixed EQ warp). It fails only for the specific ask "make me sound like a
different *person* at the same pitch", where the formants are the whole effect. Ship pitch-only, and let
the record lane carry the formant work.

---

## 3. Latency comparison

Measured = someone else published it. Estimated = my derivation, stated as such.

| Candidate | Licence | Compute per 480-block | **Lookahead** (blocks) | Source of the number | Fits 5 ms headroom? | Fits 50 ms record lane? |
|---|---|---|---|---|---|---|
| **WORLD full** (DIO+CheapTrick+D4C+Synth), WorldNet | MIT (port) / BSD (original) | ≈ 1.5 ms *(est.)* | **4–6** ≈ 25–35 ms | windows from cheaptrick.cpp / d4c.cpp; cost table from NuGet page *(measured by author)* | **No** | Yes |
| **WORLD lite** (DIO+CheapTrick+Synth, unity aperiodicity) | same | ≈ 0.42 ms *(est.)* | **3–4** ≈ 20–30 ms | as above, minus D4C | **No** | Yes |
| **WORLD on high F0 only** (≥220 Hz voices) | same | ≈ 0.4 ms *(est.)* | 2 ≈ 10–14 ms | CheapTrick 1.5/f0 *(est.)* | No (2 blocks > 0.5) | Yes |
| **RVC realtime** | MIT (code) | n/a (GPU) | **9–17** ≈ 90–170 ms | project README *(measured by author, no third party found)* | **No** | No (over 50 ms) |
| **so-vits-svc** | **AGPL-3.0**, archived 2023 | n/a | ≥ 100 ms | GitHub API licence + archive date | No | **No — licence** |
| **DeepFilterNet3** | MIT / Apache-2.0 | low (RTF 0.19) | **4** ≈ 40 ms | arxiv 2305.08227 + issue #471 | No | Yes, but redundant with AFX |
| **NVIDIA AFX denoise+dereverb** (incumbent) | NVIDIA SDK | GPU, offloaded | **1** = 10 ms | already shipped | Yes (already spent) | n/a |
| **Delay-line pitch shifter** (crossfade 480) | own code | ≈ 0.05 ms *(est.)* | **1** = 10 ms | derivation: latency = crossfade window | Marginal (needs AFX off) | Yes |
| **Delay-line pitch shifter** (crossfade 240) | own code | ≈ 0.05 ms *(est.)* | **0.5** = 5 ms | as above | **Yes, exactly at the limit** | Yes |
| **TD-PSOLA** | own code | ≈ 0.1 ms *(est.)* | 1–2 ≈ 10–20 ms | derivation: 1–2 pitch periods | No | Yes |
| **Phase vocoder 2048/512** | own code | ≈ 0.3 ms *(est.)* | 2 ≈ 21 ms | latency = analysis window | No | Yes |
| **SoundTouch (tuned)** | LGPL-2.1 | low | 1.5–2.5 ≈ 15–25 ms | README: ~100 ms default, `SEQUENCE_MS` tunable *(author)* | No | Yes |
| **Signalsmith Stretch** | MIT | low | configurable, ≥ 2 at practical settings *(est.)* | configure() API | No at defaults | Yes |

---

## 4. Recommendation

**NO-GO on a voice transformer in the live monitor path at 5 ms of headroom. Confidence: high (~85 %).**

The reason is not CPU and not licensing — both are fine. It is that the cheapest *formant-aware* stage
needs a 3-pitch-period window, which is 15–21 ms of half-window at a male F0 and 21 ms at WORLD's 71 Hz
floor. 15 ms > 5 ms by 3×. There is no configuration of WORLD, and no other published transformer, that
inverts that inequality. Anyone who says otherwise is quoting compute cost and ignoring the window.

**GO on two separate things instead.**

1. **Monitor lane (v1, ships first): pitch-only delay-line shifter, our own code.**
   240–480-sample crossfade window, **0.5–1 block of added latency (5–10 ms)**, ~0.05 ms of CPU per
   block, no dependency, no licence question, no allocation, trivially resettable and testable.
   Because 5 ms is *exactly* the free headroom, ship it either (a) with a 240-sample crossfade, or
   (b) as a monitor insert that is mutually exclusive with AFX `denoiser` — pick (b) if the warble at
   240 samples is unacceptable, and state the trade in the UI instead of hiding it.
   Formants travel with pitch; that is the accepted sound of every cheap voice changer and it is enough
   for a karaoke character voice.

2. **Record/stream lane (v2): WORLD-lite on WorldNet — DIO → CheapTrick → Synthesis, D4C skipped,**
   pitch and formant shift as independent controls, **~0.4 ms of CPU per block, 3–4 blocks of lookahead
   (20–30 ms)**, comfortably inside the 50 ms record budget behind the capture buffer. MIT-licensed pure
   C#, no native DLL, zero-alloc, AOT-friendly — it fits this codebase's existing constraints better than
   any native alternative. Budget one caveat: verify the author's benchmark numbers on our target machine
   before committing to a UI that promises instant response, because nobody else has.

**Explicitly not doing:** RVC (90–170 ms, GPU, hundreds of MB), so-vits-svc (AGPL, archived), Rubber Band
(GPL), DeepFilterNet3 (denoiser, redundant with AFX), DotnetWorld (no licence, stale).

---

## 5. What would change this answer

- A formant shifter that works on a 5 ms window. None exists in the literature; the time-frequency bound
  is the reason. If someone claims one, demand the measured latency, not the FFT size.
- Monitor budget rising to 30 ms (i.e. dropping the limiter lookahead requirement, or making AFX and the
  shifter mutually exclusive *and* accepting 25 ms). At 30 ms, WORLD-lite at 20–30 ms becomes marginal
  rather than impossible, and TD-PSOLA at 10–20 ms becomes comfortable.
- A licence purchase for Rubber Band, if quality on the record lane proves to matter more than the
  licence fee.

## 6. What I could not verify

- **No independent measurement of WorldNet's per-stage cost.** The table in §2.1 is the package author's
  own benchmark, from a page whose GitHub repository returned 404 during this spike. Re-measure before
  quoting it in a spec.
- **No third-party RVC latency measurement found.** The 90/170 ms figures are the RVC project's own.
- Model sizes for RVC (§2.2) are approximate and unverified; they do not affect the conclusion.
- All per-block cost figures marked *(est.)* in §3 are my scaling of the author's 22.05 kHz benchmark to
  48 kHz (2.2×), not measurements.
