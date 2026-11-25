# Microphone Processing for the Karaoke Chain — 2026 Research

> Applies to: JockeyBox, .NET 8 WPF, C# x64, NAudio 2.2.1, 48 kHz, fixed 480-frame
> blocks, real time. Existing managed chain per capture device (stereo = two strips):
> HPF → LPF → 10-band ISO EQ → NoiseGate → Compressor → Limiter (5 ms lookahead).
>
> New-chain latency budget: **~10 ms total, lower is better**. Host has an NVIDIA RTX GPU.
>
> Research date: 25 Nov 2026. Every latency figure is the algorithmic delay of the
> processor itself; device/ASIO round-trip is on top.

---

## 0. The constraint that decides everything

The 10 ms budget is binding, and it splits the candidates into three groups:

| Group | Candidate | Algorithmic latency |
|---|---|---|
| Fits the budget | De-esser, reverb (parallel), analysis-only auto-pipeline, AEC3/SpeexDSP (causal), RNNoise | ~0–10 ms |
| Blows the budget | DeepFilterNet3 (~40 ms), any neural voice changer (RVC 60–80 ms, OpenVoice ~100 ms+) | 40–100 ms+ |
| GPU-licensed, low-ish latency | NVIDIA Maxine AFX (denoise/dereverb/AEC) | not published, "reduced by Tensor Cores" |

**"Best in class" noise suppression (DeepFilterNet3) and "real-time voice morphing" (RVC)
cannot both live inside a 10 ms budget.** The operator must pick: (a) hold 10 ms and accept
RNNoise-grade suppression + a classic pitch/formant shifter, or (b) spend the budget on a
neural stage (Maxine on the RTX GPU, or DFN3) and accept tens of ms. The stack below assumes
(a) is the default and treats (b) as an optional GPU lane.

---

## 1. Noise suppression

### Recommendation
Two viable open-source options; the pick is a quality-vs-latency trade.

**RNNoise** — the low-latency default.
- License: BSD-3-Clause (Xiph.Org / Jean-Marc Valin / Mozilla). Commercial redistribution fine.
- CPU only, one core; 48 kHz native, 480-sample (10 ms) frames, ~7.5 ms internal lookahead;
  effective added latency ~10 ms. Steady/stationary noise (fan, hum) is what it kills well;
  it is coarser on transient noise and babble.
- Integration: C API, single header + static lib (`rnnoise.h`). P/Invoke is trivial; there
  are already C# bindings and a NuGet path. No ONNX dependency.
- Maturity: maintained (v0.2 retrain April 2024, AVX2), but it is a small model; its ceiling
  is well known. Best choice when 10 ms is hard.

**DeepFilterNet3 (DFN3)** — best open-source quality, outside the 10 ms budget.
- License: dual MIT / Apache-2.0 (confirmed from the repo LICENSE). Commercial fine.
- 48 kHz full-band, real time on CPU (RTF ~0.19 on one laptop core, no GPU needed);
  **~40 ms algorithmic delay** (20 ms window, 10 ms hop, ~2-frame lookahead). A `--compensate-delay`
  flag removes STFT+model lookahead from the *offline* output, but the streaming/live path
  still carries the model lookahead.
- Integration: reference implementation is Rust; there is a first-party ONNX export
  (`df3_onnx`) and a Python/Rust crates ecosystem, but **no maintained C# binding**. Route to
  .NET is ONNX Runtime (`Microsoft.ML.OnnxRuntime`) or P/Invoke to a small C wrapper.
- Maturity: last release 0.5.6 was **August 2023** — you own its upkeep. The Rust crate sees
  community maintenance.

**NVIDIA Maxine / Audio Effects SDK (AFX)** — the "best in class" the operator asked about,
but it is a licensed SDK, not a library.
- License: **proprietary, commercial license via NVIDIA**; there is a public API repo
  (`NVIDIA-Maxine/Maxine-AFX-SDK`) but the actual DLLs, models and dependency libs ship in an
  installer gated behind NVIDIA's developer/broadcast page, with mandatory **branding
  guidelines**. Redistribution in a commercial desktop app requires an NVIDIA agreement.
- GPU: **requires an NVIDIA RTX GPU with Tensor Cores** (Windows 10/11, driver ≥ 520.46). The
  host qualifies.
- Effects: Background Noise Suppression (denoise), Room Echo Cancellation (dereverb), combined
  dereverb+denoise, **Acoustic Echo Cancellation (AEC)**, Audio Super Resolution.
- Latency: NVIDIA says Tensor Cores "reduce latency"; exact per-effect ms is not in the public
  README — treat as "low, GPU-accelerated, confirm with NVIDIA".
- Integration: C/C++ API (`nvafx`), P/Invoke to the shipped DLLs.
- Distinct from **NVIDIA Broadcast / RTX Voice**: those are free *consumer apps*, not
  redistributable SDKs. Do not confuse "free Broadcast" with "licensed Maxine SDK".

**ONNX-hosted alternatives (DTLN / NSNet2).**
- DTLN (Microsoft DNS-Challenge winner lineage) is the canonical real-time ONNX denoiser:
  small, real time, ~32 ms algorithmic delay, ~3–4 ms inference/chunk on modest hardware.
  Code MIT, models CC-BY/MIT per repo. NSNet2 is the DNS-Challenge baseline (also ONNX,
  similar class).
- Integration: `Microsoft.ML.OnnxRuntime` NuGet; CPU by default, and on this host you can
  switch to a CUDA/TensorRT/DirectML execution provider. Cleaner .NET story than DFN3's Rust.
- These sit between RNNoise and DFN3 on both quality and latency.

### Verdict
Default to **RNNoise** if 10 ms is sacred; if the operator accepts ~30–40 ms, **DFN3 via ONNX
Runtime** is the open-source quality ceiling. If the app can carry an NVIDIA EULA and the RTX
GPU, **Maxine AFX** is the "best in class" route the operator named — but its licensing and
latency numbers must be confirmed with NVIDIA before committing.

---

## 2. Echo suppression / acoustic echo cancellation (AEC)

### The honest answer first
**True AEC is not viable without a reference (render/far-end) signal.** An echo *canceller*
is an adaptive filter that subtracts a delayed copy of the reference from the mic; with no
reference it has nothing to model. Without a reference you can only do echo *suppression*
(detect + attenuate when the mic hears what the speakers are playing), which is what "room
echo removal"/dereverb does — it tames the room, it does not cancel a specific playback loop.

**What the app must feed it:** the post-master playback bus (the signal actually sent to the
speakers, i.e. music + any cue/monitor content) as the reference, time-aligned with the mic
capture. In JockeyBox this reference already exists at the mixer output, so AEC is *feasible*
— route the master-out (pre-speaker) as the far-end signal into the mic strip's AEC.

### Comparison
- **WebRTC AEC3** (via `webrtc-audio-processing`, the standalone library): **BSD-3-Clause**.
  Industry standard. Causal adaptive filter — **no lookahead delay**; it processes 10 ms
  band-split frames in 64-sample blocks. Handles clock drift, double-talk, and stereo/48 kHz.
  Integration: C++ lib; P/Invoke to a compiled Windows x64 DLL, or the Rust bindings
  (`tonarino/webrtc-audio-processing`) as a bridge. This is the strongest open option and it
  fits the 10 ms budget (adds ~0 algorithmic latency, plus a few ms of capture/render
  alignment).
- **SpeexDSP AEC**: BSD, patent-free, but old and simple. Tail length is configurable (block
  length affects delay); quality and double-talk handling are clearly below AEC3. Use only as
  a fallback or if you want a tiny C-only dependency.
- **ML alternatives**: NVIDIA Maxine AFX ships a dedicated AEC (proprietary, GPU). "Hybrid"
  AEC+AI products exist (the Fora Soft write-up is a good secondary read) but no clean
  open-source ML AEC that beats AEC3 in a redistributable license.

### Verdict
Use **WebRTC AEC3** with the master-playback bus as reference. It is free, causal, and the
correct tool. Ship SpeexDSP only if the C++ build becomes a problem. Skip "ML AEC" unless
you already license Maxine.

---

## 3. De-esser

### What is standard practice
A de-esser is a frequency-selective compressor that ducks a high band (typically 4.5–9 kHz,
center ~6–7 kHz) only when sibilance ("s", "sh", "ch") exceeds a threshold. The three
architectures, in practice:
- **Split-band de-esser** — the traditional default for lead vocals: split into HF band and
  everything else, compress the HF band, recombine. Cheap, musical, forgiving. This is what
  most hardware/classic de-essers are.
- **Dynamic EQ** — a parametric bell/shelf whose gain is keyed by the signal *in that band*
  (modern, more transparent/surgical). Better when you want to tame a specific resonance
  without ducking the whole top end.
- **Wideband (full-range) de-esser** — ducks everything when sibilance triggers; used rarely,
  mostly for hard consonant taming.

### Placement (before or after compression)
Both conventions are defensible; the widely cited reasoning:
- **Before compression**: stops the compressor from grabbing the esses (compressor reacts to
  the loud high-frequency transients).
- **After compression / last in the corrective chain**: compression raises the average level
  and therefore the sibilance, so de-essing last catches what compression brought up.

Modern practice leans **after EQ/compression, just before the limiter**, because compression
is what makes sibilance audible, and a de-esser there reacts to the actual problem. In this
specific chain (which already has a limiter), put the de-esser **after the compressor, before
the limiter**, so the limiter never has to absorb sibilance peaks.

### Verdict
**Build it in-process** — no external library. A split-band de-esser is ~50 lines of managed
C# (a high-pass/bell filter into an envelope follower into a gain-smoothing element). Latency
is sub-millisecond (IIR + smoothing), comfortably inside the budget. Dynamic-EQ flavor is a
slightly larger build (a keyed parametric band); start with split-band.

---

## 4. Reverb

### Algorithm choices
- **Freeverb (Schroeder: 8 comb + 4 allpass)** — public domain (Jezar at Dreampoint). Trivial
  CPU (a few percent of a core), no algorithmic latency beyond a chosen pre-delay. Slightly
  metallic/colored on long tails, which is actually fine for a karaoke ambience. Easiest to
  hand-port to C#.
- **FDN (Feedback Delay Network)** — the modern standard (Zita-rev1 is the canonical
  reference, **GPL-3.0**, so read but don't lift its code into a proprietary app; implement
  your own FDN). Smoother/denser tails than Freeverb, modestly higher CPU, still no inherent
  latency. This is the better "plate/hall" for singing.
- **Convolution reverb** — real impulse responses (best realism), but CPU is the highest and,
  critically, **low-latency partitioned convolution adds latency equal to the first partition
  size** (e.g. 64–256 samples = 1.3–5.3 ms at 48 kHz). Fine on this budget, but the IR and
  partition tuning matter.

### Placement
**Never inline in the channel strip.** Reverb is a *parallel/send* effect: dry vocal → EQ →
compressor → de-esser → limiter on the dry path; a *wet* tap is split off (post-compressor),
sent through the reverb, and summed back before the final limiter (or onto its own bus with
its own ceiling). This keeps the limiter seeing the dry transient and prevents the tail from
muddying the karaoke track. A high-pass on the wet return (~200–300 Hz) keeps the low end
clean — a standard live-vocal move.

### Verdict
**Hand-port a Freeverb or a small FDN** in managed C#; run it as a parallel send, high-passed,
wet mixed low (~10–20%). ~0 added latency. Skip convolution for v1 unless real IRs are a
hard requirement.

---

## 5. AI auto-pipeline (auto gain / EQ / gate / compressor / de-esser)

### What actually exists and is usable
- **Measured loudness — fully buildable, do this first.** ITU-R BS.1770 / EBU R128: K-weighting
  filter + gating → LUFS (integrated/momentary) and true peak (dBTP). Reference targets: −23 LUFS
  broadcast (EBU R128), −16 LUFS podcast, −14 LUFS Spotify/YouTube, all with a ~−1 dBTP true-peak
  ceiling. Auto-gain = measure momentary LUFS → adjust gain toward a target. This is ~100–150
  lines of C# and is the single highest-value, lowest-risk piece.
- **Auto gate threshold** = noise-floor estimate (minimum-statistics or a low percentile of the
  envelope during silence). Buildable; you already have a NoiseGate stage to drive.
- **Auto EQ** = spectral-balance matching: long-term-average magnitude spectrum vs a target tilt
  (pink/noise or a "natural voice" curve), convert to your 10-band ISO EQ gains. Buildable as a
  heuristic; keep the correction gentle (a few dB per band, slow time constants).
- **Auto compressor / de-esser** = crest-factor (peak/RMS) → ratio/threshold; sibilance-band
  energy → de-esser threshold. Buildable heuristics; lower value than the loudness + gate + EQ
  trio because the settings are more source-dependent.
- **Learned "engineer" models** — the honest picture: **DeepAFx** (Adobe Research) is the
  notable open effort (differentiable FX + a controller network), but it is a *research
  framework*, not a redistributable SDK (Adobe Research license; verify before commercial use).
  The commercial equivalents (iZotope Neutron, Sonible smart:EQ, Gullfoss) are **VST3 plugins,
  not embeddable SDKs** — you would host them via a VST3/CLAP host (Duality etc.), not link
  them. There is no clean, commercially-licensed "auto-mix" SDK in the open today.

### Build vs buy — realistic answer
**Build** the measured-loudness + noise-floor + spectral-tilt + crest-factor heuristics; they
cover 80% of "make the mic sound set up" and cost an afternoon each. **Buy/host** a VST3 only
if a true learned "set everything for me" is a hard requirement; otherwise treat the ML
auto-mix as a later, separate lane. Analysis can run **asynchronously** (buffer a second of
signal, compute, apply parameters smoothly) so it adds **zero** render-thread latency.

---

## 6. Voice transformer / morpher / cloner (real time)

### Blunt real-time ranking (for live monitoring, 10 ms budget)
| Tool | Real-time? | Latency | GPU | License | .NET route |
|---|---|---|---|---|---|
| **WORLD vocoder** (pitch + formant-preserving shift) | **Yes** | tens of ms (CPU) | no | modified BSD, no patents | C# ports exist (World.NET, DotnetWorld, WorldNet) |
| **Rubber Band** (formant-preserved pitch/time) | **Yes** | configurable, low | no | GPL-2+ **or commercial** | C ABI, P/Invoke |
| Simple formant-preserving phase-vocoder pitch shift | Yes | low | no | your code | in-process |
| **RVC** (retrieval voice conversion) | **Yes, on GPU** | **~60–80 ms** (w-okada chunk) | **RTX required** | MIT (WebUI); check pretrained-model terms | Python/ONNX; heavy |
| **so-vits-svc** | offline (a fork adds "realtime") | high | GPU | varies by fork; verify | Python |
| **OpenVoice** | offline-ish | ~85 ms/s on A10G → ~100 ms+ live | GPU (PyTorch) | MIT (V1/V2) | ONNX export exists, still heavy |

### The key distinction
- **Timbre "morphing" (pitch + formant shift) can be truly real-time and low-latency.** WORLD
  does exactly this: estimate F0 (DIO/Harvest) + spectral envelope (CheapTrick) + aperiodicity,
  shift F0 while holding the envelope (formant-preserving), resynthesize. It has a documented
  real-time synthesis path, is **BSD with no patents**, runs on CPU, and already has C# wrappers
  (World.NET / DotnetWorld / WorldNet NuGet). This is the correct tool for a karaoke
  "sing-along key / character voice" effect. Latency is tens of ms — the one voice stage that
  can plausibly sit near the budget.
- **Voice *cloning* (identity transfer) is not a 10 ms live feature.** RVC, so-vits-svc and
  OpenVoice are GPU inference pipelines (content encoder HuBERT + pitch RMVPE/CREPE + vocoder
  HiFi-GAN/VITS). RVC's real-time forks bottom out around **60–80 ms** on an RTX GPU; OpenVoice
  and so-vits-svc are effectively offline (or GPU-heavy) with 100 ms+. None of these fit live
  monitoring at low latency, and all carry model-license scrutiny for commercial use.

### Verdict
Ship **WORLD (or Rubber Band if you'd rather pay than port)** for a real-time pitch/formant
morpher. Treat **RVC as a separate, explicitly-flagged "voice clone" lane** with its own
60–80 ms latency disclosure, not part of the monitoring chain. Skip so-vits-svc and OpenVoice
for live; they are offline tooling.

---

## 7. Recommended stack (build order, quality per unit of risk)

1. **De-esser (split-band)** — in-process C#, sub-ms, huge perceived-quality win on sibilance.
   Lowest risk, first build.
2. **Auto-pipeline core: EBU R128 / BS.1770 loudness + true peak → auto-gain**, plus
   noise-floor → gate threshold and spectral-tilt → EQ. Asynchronous analysis, zero
   render-latency. Highest leverage per line of code.
3. **AEC3 (WebRTC)** fed by the master-playback reference — kills the speaker→mic loop that
   otherwise makes everything else pointless in a loud room. Causal, fits the budget.
4. **Reverb (Freeverb/FDN, parallel send, high-passed wet)** — in-process, ~0 latency, fills
   out the singing without mud.
5. **RNNoise (P/Invoke)** — the ~10 ms noise suppressor that fits the budget. Accept its
   ceiling; this is the stage to *swap for DeepFilterNet3 (ONNX, ~40 ms) or Maxine AFX (GPU,
   licensed)* if the operator later accepts more latency or an NVIDIA EULA.
6. **WORLD-based pitch/formant morpher** — real-time, CPU, BSD, C# ports available.
7. **(Later, opt-in) RVC voice-clone lane** — 60–80 ms GPU, disclosed as such, not in the
   monitoring path.

### Avoid (and why)
- **NVIDIA Broadcast / RTX Voice as "the" solution** — free consumer app, not a redistributable
  SDK; it cannot be embedded. Maxine AFX is the SDK, and it is proprietary + branded + needs an
  NVIDIA agreement + exact latency unpublished.
- **DeepFilterNet3 *and* a neural voice changer both inside 10 ms** — arithmetic impossibility
  (40 ms + 60–80 ms). Pick at most one neural stage and blow the budget deliberately.
- **Convolution reverb for v1** — realism not worth the partition-latency and IR-management
  burden when Freeverb/FDN sound fine for karaoke.
- **so-vits-svc / OpenVoice for live** — offline or GPU-heavy with 100 ms+ latency and murky
  model-license terms.
- **Zita-rev1 code in a proprietary app** — GPL-3.0; implement your own FDN instead.
- **AEC without a reference signal** — you'd be building echo *suppression*, not cancellation;
  wire the master-playback bus first or skip AEC entirely.

---

## Sources

- RNNoise (README, license COPYING): https://github.com/xiph/rnnoise · https://gitlab.xiph.org/xiph/rnnoise
- DeepFilterNet (README, LICENSE, DFN2/DFN3 papers): https://github.com/Rikorose/DeepFilterNet · arXiv:2205.05474 · arXiv:2305.08227
- NVIDIA Maxine AFX SDK: https://github.com/NVIDIA-Maxine/Maxine-AFX-SDK · https://developer.nvidia.com/maxine · https://www.nvidia.com/broadcast-sdk-resources
- Fora Soft — RNNoise vs DeepFilterNet vs Krisp (2026): https://www.forasoft.com/learn/ai-for-video-engineering/articles-ai/real-time-noise-suppression-krisp-rnnoise-deepfilternet
- Fora Soft — WebRTC AEC3 five-stage write-up: https://www.forasoft.com/learn/audio-for-video/articles-audio/acoustic-echo-cancellation-aec3
- Microsoft DNS-Challenge (DTLN/NSNet2 baseline, latency requirements): https://github.com/microsoft/DNS-Challenge · https://aka.ms/dns-challenge
- webrtc-audio-processing (Rust bindings, BSD): https://github.com/tonarino/webrtc-audio-processing
- SpeexDSP: https://github.com/xiph/speexdsp (mirror; development at gitlab.xiph.org/xiph/speexdsp)
- WORLD vocoder (modified BSD, no patents, C# wrappers): https://github.com/mmorise/World · World.NET https://github.com/aqtq314/World.NET · DotnetWorld https://github.com/yamachu/DotnetWorld
- Rubber Band Library v4 (GPL/commercial): https://breakfastquay.com/rubberband/ · https://github.com/breakfastquay/rubberband
- RVC-Project WebUI: https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI
- so-vits-svc-fork (realtime): https://github.com/voicepaw/so-vits-svc-fork
- OpenVoice (MIT): https://github.com/myshell-ai/OpenVoice
- w-okada realtime latency (~60–80 ms): https://docs.aihub.gg/realtime-voice-changer/local/deiteris-w-okada-fork/
- Zita-rev1 (FDN reference, GPL): https://kokkinizita.linuxaudio.org/linuxaudio/zita-rev1-doc/quickguide.html
- Freeverb internals: https://reillyspitzfaden.com/posts/2025/06/reverb-part-1/
- EBU R128 (v4 PDF) / ITU-R BS.1770 LUFS: https://tech.ebu.ch/docs/r/r128v4_0.pdf · https://audiolinter.com/tools/loudness-check
- DeepAFx (Adobe Research): https://github.com/adobe-research/DeepAFx
- Local ground truth: `/home/alex/OBS/jb-shell/src/JockeyBox.Dsp/MixerStrip.cs` (chain), `/home/alex/OBS/jb-shell/src/JockeyBox/JockeyBox.csproj:38` (NAudio 2.2.1), `/home/alex/OBS/jb-shell/src/JockeyBox.Dsp/JockeyBox.Dsp.csproj` (Dsp is dependency-free, net8.0)
