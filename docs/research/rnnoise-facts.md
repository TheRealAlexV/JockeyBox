# RNNoise — measured runtime facts

Measured on the Windows rig on 2026-11-25 against the **vendored** binary
`third_party/rnnoise/win-x64/rnnoise.dll` (sha256
`2b99c735ec594b0dd389731c752da8a16b523263a57e378cc6f9c5803ece988b`), not from
memory. Every number below was produced by a probe that `LoadLibrary`ed that
file and called the exported functions. Anything I could not establish is
marked **NOT ESTABLISHED**.

Raw probe output is retained on the rig:

| File | What it is |
|---|---|
| `C:\Dev\rnnoise-build\probe\final_shipped.txt` | the shipped SIMD DLL, all numbers in §4-§9 |
| `C:\Dev\rnnoise-build\probe\final_scalar.txt` | scalar comparison build |
| `C:\Dev\rnnoise-build\probe\final_rtcd_*.txt` | earlier SIMD runs, identical to shipped within the last digit |
| `C:\Dev\rnnoise-build\probeout_scalar.txt` | scalar build, full dump |

Probe source: `C:\Dev\rnnoise-build\probe\probe.c`.

---

## 1. Binary identity

```
sha256sum rnnoise.dll
2b99c735ec594b0dd389731c752da8a16b523263a57e378cc6f9c5803ece988b  rnnoise.dll
```

| Fact | Value |
|---|---|
| Size | **14,851,584 bytes** |
| Machine | **8664** (`pei-x86-64`, architecture `i386:x86-64`) |
| Format | PE32+ DLL |
| Named exports | **10** |
| Source | `xiph/rnnoise` commit `70f1d256` (2025-02-22) |
| Model | default weights compiled in, `model_version` `0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37` |
| Licence | BSD-3-Clause, `third_party/rnnoise/COPYING` |

```
objdump -f rnnoise.dll
architecture: i386:x86-64, flags 0x0000012f
```

**Rebuild status:** the recipe in `third_party/rnnoise/build/` reproduces the
binary from a clean clone. The raw sha256 differs by 7 bytes of link metadata
(COFF `TimeDateStamp`, the `.rdata` mirror of that value, and the derived PE
`CheckSum`); after normalising exactly those three fields the hashes are equal,
`259bb19d5a84e5666182ece0762b18f8c59d8065c7e07aa096c4448ed2082d1f`. Details in
`third_party/rnnoise/README.md`.

The scalar comparison build beside it (`rnnoise_scalar_alt.dll`, 14,825,472
bytes, sha256 `9b7cda5a2c2073470b23918b55df47f91ec34979e72308eba13ff02b496c6b43`)
is **not** vendored. See §8 for why shipping more than one binary is unsafe.

## 2. Export table

```
objdump -p rnnoise.dll   # [Ordinal/Name Pointer] Table
```

| # | Name |
|---|---|
| 0 | `rnnoise_create` |
| 1 | `rnnoise_destroy` |
| 2 | `rnnoise_get_frame_size` |
| 3 | `rnnoise_get_size` |
| 4 | `rnnoise_init` |
| 5 | `rnnoise_model_free` |
| 6 | `rnnoise_model_from_buffer` |
| 7 | `rnnoise_model_from_file` |
| 8 | `rnnoise_model_from_filename` |
| 9 | `rnnoise_process_frame` |

Ten exports, no version getter. There is no `.def` file and none is needed:
`include/rnnoise.h` maps `RNNOISE_EXPORT` to `__declspec(dllexport)` under
`WIN32 && RNNOISE_BUILD && DLL_EXPORT`, which is what the build defines.

## 3. Frame contract

```
rnnoise_get_frame_size() = 480
rnnoise_get_size()       = 32688 bytes
_msize(state)            = 32688 bytes   delta 0
```

- Frame size is **480 samples** at 48 kHz and it is a **runtime query**
  (`rnnoise_get_frame_size()`). Never hardcode 480.
- State size is **32,688 bytes**, confirmed exactly by `_msize()` on a real
  `DenoiseState` returned by `rnnoise_create(NULL)`, not just by trusting the
  header.

## 4. Samples are int16 scale, not `[-1,1]`

1 kHz tone, 100 blocks, measured on the shipped DLL:

| Input amplitude | in_rms | out_rms (blk 9-100) | VAD mean (blk 9-100) | Behaviour |
|---|---|---|---|---|
| **16000.0** (int16 scale) | 11313.7085 | **0.0656** | **0.0055** | gated |
| 0.5 (`[-1,1]` scale) | 0.3536 | 0.3543 | **0.0000** | passed through, +0.02 dB |

At `[-1,1]` scale the VAD pins at **exactly 0.0000 for all 100 blocks** and the
signal passes through untouched (+0.02 dB). The network is inert because its
input is roughly **3.4e4x** too small.

**A caller that feeds normalised floats gets a silent no-op that still returns
"success".** Scale to int16 (`*32768`) before `rnnoise_process_frame`, or divide
by 32768 after it.

## 5. Aliasing, latency, reset and models

- **`out == in` in-place aliasing is bit-identical** to separate input and
  output buffers. Probe: `in-place output bit-identical to separate buffers`.
- **The engine is not zero-latency: output frame N carries input frame N−2, a delay of
  960 samples (two 480-sample frames).** Cross-correlating a jittered formant vowel
  (non-periodic, so the peak is unambiguous) against the engine output gives a unique
  peak at **lag 960 samples, correlation 0.9882** (959 → 0.9865, 961 → 0.9777).
  Burst onset confirms it in the time domain: a vowel burst that starts at input frame
  10 first shows output energy at frame 12. Measured on the rig, this build. The stage
  must report 960 through `LatencyFrames`; treating the frame callback as instantaneous
  under-reports by 20 ms.

  The per-block RMS table is the same delay seen from a fresh state: block N's output
  is block N−2's input, so blocks 1–2 are the leading edge of the delay, not a warm-up
  to be discarded.

  | Block | in_rms | out_rms | ratio |
  |---|---|---|---|
  | 1 | 3424.18 | **0.00** | 0.000 |
  | 2 | 3207.51 | **44.18** | 0.014 |
  | 3 | 3587.30 | 3367.02 | 0.939 |

  The 0.00 and 44.18 are not a dead state: they are the silence before any input has
  reached the output and the partial frame at the delay boundary. Dropping two frames
  was upstream's workaround for the same delay; a live chain instead reports the 960
  samples as latency and keeps the stream continuous. Do not drop frames on a live
  chain (that loses sync), and do not crossfade current dry against a wet that is two
  frames old.

- **Open measurement: is the 2029-frame worst case reachable?** The ceiling is sized
  for denoise 960 + limiter 240 + the pitch shifter's white-noise down-shift 829 = 2029,
  but that is an arithmetic cross-product. With denoise at full wet the pitch shifter's
  input is denoised voice, not white noise, so it may sit nearer its tonal down-shift
  (655) than 829. This is unresolved: measure the pitch shifter's latency with the
  denoiser active in front of it, feeding noise, and compare. Do not claim the worst
  case is unreachable before that measurement exists, and do not use it to justify a
  smaller ceiling.
- **No reset API.** Resetting means destroying the `DenoiseState` and creating a
  fresh one.
- **A model outlives its states.** Call `rnnoise_model_free` only after every
  state that references it has been destroyed.
- **`NULL` model selects the weights compiled into `rnnoise_data.c`**, i.e. the
  default model, with no file on disk.
- werman's `.rnn` files load through upstream's
  `rnnoise_model_from_filename`. **`GregorR/rnnoise-nu` is a fork with its own
  header and will not load upstream.**

## 5b. Signal classes: match the signal to the assertion

Three gate runs failed on probes whose input the network could not respond to as the assertion
demanded. The stage was right each time; the signal was wrong.

| signal | produced | why it was wrong for its assertion |
|---|---|---|
| `[-1,1]` normalised noise | VAD pinned `0.000000`; voiced +0.02 dB | amplitude ~3.4e4x too small; the network is inert at that scale |
| int16 lone impulse | output exactly `0.000000` | removing a lone transient is the denoiser's job |
| int16 periodic ramp (period 5001) | `0.00 dB`, passed through | periodic and harmonically rich, so it reads as voiced |

**Rule: an energy assertion may only use a signal class the denoiser is known to transform in
that direction.** In this codebase `RnNoiseSignals.Suppressive` is the noise-like class
(measured −68.94 dB at int16 scale) for “it removed energy”; `RnNoiseSignals.Voiced` is the
periodic/formant class (measured +0.02 dB normalised, −8.58 dB int16) for latency correlation and
bit-exact comparisons. A probe that asserts energy removal on `Voiced` is a defect even if it
happens to pass.

Before writing a denoiser assertion, ask which class of input the assertion needs, not whether
the code is correct.

## 6. Quality

Source: the Demucs vocal stem `01 Sweater Weather\vocals.wav`, converted to
48 kHz mono s16, 40 s = 1920000 samples = 4000 whole frames, `in_rms` 3818.

| Case | out_rms | Delta |
|---|---|---|
| clean | 3523.5 | **-0.70 dB** overall |
| speech + white noise, target 10 dB SNR | 3542.1 | -1.07 dB |
| noise only, no voice | 29.8 | **-32.15 dB** |

Speech + noise at 10 dB SNR, split into gaps and speech:

| Segment | in | out | Delta |
|---|---|---|---|
| gaps | 1265.13 | 457.64 | **-8.83 dB** |
| speech | 5186.9 | 4610.7 | -1.02 dB |
| **SNR** | **12.26 dB** | **20.06 dB** | **+7.81 dB** |

VAD on clean speech: mean **0.890**, **90.4%** of frames above 0.5
(p10 0.521, p50 0.999, p90 1.000).

**Limit, stated plainly:** the source was *sung* Demucs vocal, not close-mic
spoken word. Quality on real monitor speech is **NOT ESTABLISHED**.

## 7. Cost

Thread CPU per 480-sample (10 ms) block, one channel:

| Run | Mean | Share of a 10 ms block |
|---|---|---|
| shipped SIMD | **757.8 us** | **7.58%** |
| rerun | 689.1 us | 6.89% |
| fastest 200 blocks | 232.5 us | ~2.3% |

A stereo monitor therefore pays about **15%** of one core.

**The wall-clock p99 of 2473.7 us (24.7%) is contaminated**: another gate held
74-86% of all 24 logical cores during that run. It needs re-taking on a quiet
machine and should not be quoted as a latency figure until it is.

The scalar build costs **1117.2 us** per block, so the AVX2/SSE4.1 runtime
dispatch is worth **1.5-1.6x**.

## 8. Determinism: one binary per comparison

- Same input twice in one process: **bit-identical**, `FULLHASH=8dfe4c101d0d01e2`.
- Identical across processes and after relinking.
- The scalar build produces a **different** hash, `d7db717b60081ea2`, because
  SIMD sums in a different order.

**Comparisons are only valid within one DLL. Ship one binary.** Two builds of
"the same" library will not match bit for bit, and a test that compares across
them will produce a false alarm.

## 9. Memory

10,000 create/destroy cycles: working set 16112 KB before and after, delta
**+0 KB**; private bytes 2944 KB before and after. No growth observed.

## 10. What was not established

- Quality on real, close-mic **spoken** monitor speech (only sung Demucs vocal
  was measured).
- A clean **wall-clock p99** (§7 run was contaminated by another gate).
- Nothing about the model size: the earlier ~85 kB `.rnn` figure was simply
  wrong. The default model file measures 14,751,424 bytes on the rig (§11).
- Behaviour on CPUs without AVX2: the SSE4.1 path exists in the binary but was
  not timed separately.
- Multi-channel behaviour beyond stereo, and long-run (hours) drift, were not
  measured.
- Interaction with a specific Windows audio driver or WASAPI exclusive-mode
  buffer size was not measured.

## 11. Rejected: stripping the compiled-in model

This was considered as a size reduction and is **rejected**. The idea: do not
link `rnnoise_data.c`, ship a `.rnn` instead, and load it with
`rnnoise_model_from_filename()`.

| Quantity | Bytes |
|---|---|
| DLL with the model compiled in (shipped) | 14,851,584 |
| everything except `rnnoise_data.obj`, i.e. a stripped DLL plus PE headers | 87,749 |
| `rnnoise_data.obj` inside the current DLL | 14,763,835 (99.4% of the DLL) |
| `rnnoise_default.rnn`, the file a stripped build would ship | 14,751,424 |
| stripped DLL + shipped `.rnn` | 14,839,173 |

The DLL would drop to about 88 KB, but total payload barely moves: 14,751,424 +
87,749 = 14,839,173 bytes, which is 12,411 bytes (0.08%) **smaller** than today,
not larger, because the COFF object wrapper is 12,411 bytes bigger than the model
file it wraps. There is no meaningful size win in either direction.

**The earlier figure of ~85 kB for the model file was wrong.** The default model
file on the rig is 14,751,424 bytes; ~85 kB describes a small third-party
(werman-style) model, not the default one compiled in here.

Rejected because it buys no payload reduction and adds an external file to locate
at runtime, a failure mode when it is missing, and a different artefact from the
one every number in this document was measured on.
