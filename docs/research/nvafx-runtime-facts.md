# NVIDIA Audio Effects SDK — runtime facts

Measured on the Windows rig on 2026-11-25 against the **installed** SDK, not from memory.
Every measured number below cites the command that produced it. Anything I could not
establish is marked **NOT ESTABLISHED**.

Host shim: `win` (cmd) and `winps` (PowerShell) from `/home/alex/.local/bin/`.

---

## 1. Install layout

Root: `C:\Program Files\NVIDIA Corporation\NVIDIA Audio Effects SDK`

`NVAFX_SDK_DIR` is set at **Machine** scope and equals that path.
Verified:
```
winps '[Environment]::GetEnvironmentVariable("NVAFX_SDK_DIR","Machine")'
  -> C:\Program Files\NVIDIA Corporation\NVIDIA Audio Effects SDK
```
(It is also inherited into a normal process environment; the harness prints it as
`# NVAFX_SDK_DIR(process)=C:\Program Files\NVIDIA Corporation\NVIDIA Audio Effects SDK`.)

```
win 'dir /s /b "C:\Program Files\NVIDIA Corporation\NVIDIA Audio Effects SDK"'
```

| Path | Notes |
|---|---|
| `NVAudioEffects.dll` | FileVersion **1.6.1.2**, ProductVersion 0.8.1.0 |
| `cublas64_12.dll`, `cublasLt64_12.dll`, `cufft64_11.dll` | CUDA math |
| `nvinfer_10.dll`, `nvrtc64_120_0.dll` | TensorRT / runtime compile |
| `libcrypto-3-x64.dll` | OpenSSL |
| `version.h` | the only file resembling a header — version macros only |
| `ThirdPartyLicenses.txt`, 2 × PDF | licences |
| `models\` | 10 `.trtpkg` + `readme.txt` |

Models (all present), from `dir /s /b`:

```
aec_16k.trtpkg              aec_48k.trtpkg
denoiser_16k.trtpkg         denoiser_48k.trtpkg
dereverb_16k.trtpkg         dereverb_48k.trtpkg
dereverb_denoiser_16k.trtpkg dereverb_denoiser_48k.trtpkg
superres_8kto16k.trtpkg     superres_16kto48k.trtpkg
```

`models\readme.txt` (`win 'type ...\models\readme.txt'`) states every model is
**"10 milliseconds framesize"**. That single sentence explains the whole frame contract
in §5 — the frame length is the sample rate divided by 100.

**Correction to the "already known" list:** `dereverb_denoiser_16k.trtpkg` and
`dereverb_denoiser_48k.trtpkg` exist as stated, and there is no `superres_16k` or
`superres_48k`; only the two `XtoY` variants. Everything already known about the DLL set
and the model set was confirmed as stated.

### There is no import library

There is no `.lib`, no `.h`, no `.def` in the install root or anywhere under it
(`dir /s /b` output above is exhaustive: 12 files at root + 11 in `models`). So:
**it is not possible to link against this SDK normally; P/Invoke (or `LoadLibrary` +
`GetProcAddress`) is the only route,** and *no signature can ever be confirmed from a
declaration shipped with the SDK.*

Runtime dependencies (`dumpbin /dependents`, see §2 for the invocation pattern):

```
nvcuda.dll  cublas64_12.dll  cufft64_11.dll  nvinfer_10.dll  libcrypto-3-x64.dll
KERNEL32.dll SHELL32.dll ADVAPI32.dll MSVCP140.dll VCRUNTIME140.dll  + api-ms-win-crt-*
```

Consequence for the binding: **all of the co-shipped DLLs must be resolvable when
`NVAudioEffects.dll` is loaded.** Loading it from an arbitrary working directory without
setting the DLL search path first gives a load failure, not a graceful error. The harness
calls `SetDllDirectory(SdkDir)` before the first P/Invoke.

---

## 2. Export table

No `dumpbin` or `objdump` on `PATH`. Found MSVC's:

```
winps '$db="C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe"; & $db /nologo /exports "C:\Program Files\NVIDIA Corporation\NVIDIA Audio Effects SDK\NVAudioEffects.dll"'
```

Header of the table: **1 ordinal base, 26 functions, 26 names, no ordinal-only exports,
no name forwarding.** Full list, in export order:

| # | Name | Conventional set? |
|---|---|---|
| 1 | `NvAFX_CreateChainedEffect` | extra |
| 2 | `NvAFX_CreateEffect` | yes |
| 3 | `NvAFX_DestroyEffect` | yes |
| 4 | `NvAFX_GetCudaDeviceCount` | extra |
| 5 | `NvAFX_GetCudaDeviceProp` | extra |
| 6 | `NvAFX_GetEffectList` | extra |
| 7 | `NvAFX_GetFloat` | yes |
| 8 | `NvAFX_GetFloatList` | extra |
| 9 | `NvAFX_GetString` | yes |
| 10 | `NvAFX_GetStringList` | extra |
| 11 | `NvAFX_GetSupportedDevices` | extra |
| 12 | `NvAFX_GetU32` | yes |
| 13 | `NvAFX_GetU32List` | extra |
| 14 | `NvAFX_InitializeLogger` | extra |
| 15 | `NvAFX_Load` | yes |
| 16 | `NvAFX_Reset` | extra |
| 17 | `NvAFX_Run` | yes |
| 18 | `NvAFX_SetFloat` | yes |
| 19 | `NvAFX_SetFloatList` | extra |
| 20 | `NvAFX_SetLoggingCallback` | extra |
| 21 | `NvAFX_SetString` | yes |
| 22 | `NvAFX_SetStringList` | extra |
| 23 | `NvAFX_SetU32` | yes |
| 24 | `NvAFX_SetU32List` | extra |
| 25 | `NvAFX_UninitializeLogger` | extra |
| 26 | `NvOptimusEnablementCuda` | data export, not a function |

Names that differ from the conventional set: **none missing**, but fifteen extras exist.
Notable for a binding author:

- **`NvAFX_CreateChainedEffect`** — chained-effect creation is a real exported entry point.
- **`NvAFX_Reset`** — exists; usable to clear filter state (e.g. on seek/stream restart).
- **`NvAFX_GetEffectList`** — returns the effect name strings; the only self-describing API.
- **`NvAFX_GetSupportedDevices` / `GetCudaDeviceCount` / `GetCudaDeviceProp`** — GPU selection.
- **`NvAFX_InitializeLogger` / `UninitializeLogger` / `SetLoggingCallback`** — this is how you
  get diagnostics out. Relevant because the stated failure mode is a bare `1` with no text.
- `*List` variants for float/string/u32.

**No `NvAFX_GetVersion` or equivalent version getter is exported.** The already-known claim
that there is no version-getter export is **confirmed**; version comes from `version.h`
(`NVIDIA_AUDIOFX_SDK_VERSION_STRING "1.6.1.2"`, read via `win 'type ...\version.h'`) and from
the DLL's FileVersion resource.

### Decorations and calling convention

Every export is a **plain, undecorated C name** — no `_name@N`, no `?name@@`, no leading
underscore. On x64 Windows there is exactly one calling convention, so the
`cdecl` vs `stdcall` question is moot at the ABI level: `[DllImport(..., CallingConvention =
CallingConvention.Cdecl)]` and `StdCall` compile to identical call sequences. **The only real
risk is argument count and argument type**, which is exactly where the measured traps in §7 sit.

### `NvAFX_GetString`: not a missing export, and not a calling-convention problem

The already-known claim was that `NvAFX_GetString` returns `FAILED(1)` with a zeroed buffer
while `GetU32` works on the same handle. **That claim is wrong, and the correction matters.**
Measured with `nvafx-verify getstring`:

```
--- status code calibration (bad param names) ---
  SetU32(not_a_real_param, 1) -> 3
  GetU32(not_a_real_param)    -> 1
  SetString(not_a_real_param) -> 3
--- field "name"        --- variant A -> 3   variant B -> 3 (sizeAfter=512)  zeroed buffer
--- field "model_path"  --- variant A -> 0   variant B -> 0   raw = "C:\Program Files\NVIDIA "
--- field "log_level"   --- variant A -> 3   variant B -> 3
--- field "version"     --- variant A -> 3   variant B -> 3
```

So:

- `NvAFX_GetString` **is exported** (ordinal 9) and **works correctly**. It returned the real
  `model_path` string.
- Both plausible size-argument marshallings — `(byte[] buf, uint capacity)` and
  `(byte[] buf, ref uint size)` — return the same status and the same bytes, so the size
  argument cannot be distinguished from behaviour alone. **NOT ESTABLISHED:** which of the two
  is the declared form. Both work for a read.
- The earlier `FAILED` was almost certainly **an invalid parameter name**, not a broken import.
  The status codes are distinguishable and neither is 1:

| Status | Meaning, as measured |
|---|---|
| `0` | success |
| `1` | generic failure — also what `GetU32` returns for an **unknown name**, and what a 4-arg `Run` returns |
| `3` | **unknown / unsupported parameter name** — what every `Set*` and `GetString` returns for a bogus name |

A binding that only checks `!= 0` will happily report "parameter rejected" as "effect failed".
Recommendation: treat `3` as "wrong name in the binding" and `1` as "call failed".

---

## 3. Header availability — definitively none

Two passes, both negative.

Filename pass:
```
winps 'for each root in @("C:\Program Files\NVIDIA Corporation","C:\ProgramData","C:\NVIDIA",
"C:\SDK","C:\Program Files\NVIDIA GPU Computing Toolkit","C:\Program Files (x86)\NVIDIA Corporation",
"C:\OBS","C:\Users\Alex\obs-studio","C:\Users\Alex\source") { Get-ChildItem -Recurse -Include *.h,*.hpp,*.def,*.lib | ? { $_.Name -match "afx|audiofx|nvafx" } }'
-> no files
```
(`C:\NVIDIA`, `C:\SDK`, `C:\Users\Alex\obs-studio` do not exist on this box.)

Content pass — searching inside file contents for the symbol, not just filenames, over
`.h .hpp .c .cpp .cs .def .txt .md`:
```
winps '... | Select-String -Pattern "NvAFX_Run|NvAFX_CreateEffect|NVAFX_PARAM" -List'
-> only:
   C:\ProgramData\NVIDIA Corporation\NVIDIA Broadcast\logs\NvBroadcastClientCore_log_2026-11-01_16-51-00_info.txt
   C:\ProgramData\NVIDIA Corporation\NVIDIA Broadcast\logs\NvBroadcastClientCore_log_2026-11-04_21-07-00_info.txt
```

Those are NVIDIA Broadcast **log files** — they contain the text `NvAFX` in log lines, not
declarations. They are not a header and cannot confirm a signature.

**Verdict: no header, no `.lib`, and no `.def` declaring these functions exists anywhere on
this box, including the CUDA toolkit include directory.** The `version.h` in the install root
declares only version macros (`NVIDIA_AUDIOFX_SDK_VERSION*`) and no function.

Consequently **every argument type in the eventual binding is inferred from behaviour, never
confirmed from a declaration.** The only mitigations available on this box are the measured
status codes (§2) and the measured contract (§5).

---

## 4. OBS's own copies — not usable as a reference

OBS is installed at `C:\Program Files\obs-studio`. It contains **zero** files matching
`nvafx` or `audiofx`:

```
winps 'Get-ChildItem "C:\Program Files\obs-studio" -Recurse | Where-Object { $_.Name -match "nvafx|audiofx" }'
-> (nothing)
```

Specifically, **`nvafxdenoiser.dll`, `nvafxdereverb.dll`, `nvafxdereverbdenoiser.dll` and
`NVAudioEffects.dll` do not exist in the OBS tree, and a whole-`C:`-drive search for
`nvafx*.dll` and `NVAudioEffects.dll` found none outside the SDK install and our own probe
build output:**

```
winps 'Get-ChildItem -Path "C:\" -Recurse -Include nvafx*.dll,NVAudioEffects.dll -Force | % { $_.FullName + "  v" + $_.VersionInfo.FileVersion }'
-> C:\Dev\nvafx-probe\bin\Release\net8.0\nvafx-probe.dll  v1.0.0.0   (our own earlier probe)
   C:\Dev\nvafx-probe\obj\...                              (build intermediates)
```

The per-effect DLLs named in the brief are **not shipped by SDK 1.6.x at all** — this SDK is a
single `NVAudioEffects.dll` plus the TensorRT/CUDA stack. The OBS plugin that used them
(`obs-nvfilters`) is not installed here either.

OBS's current noise-suppression plugin imports no NVAFX symbol; it ships its own inference
runtime instead:
```
dumpbin /imports "C:\Program Files\obs-studio\obs-plugins\64bit\obs-noise.dll"
  filtered for nvafx|NVAudioEffects|nvinfer|onnxruntime -> (no match printed)
```
and the plugin directory contains `onnxruntime.dll` and `onnxruntime_providers_shared.dll`.
Modern `obs-noise` performs suppression through ONNX Runtime, not NVAFX.

I did **not** run the OBS application (out of scope by instruction), so I cannot report what
OBS logs at runtime. But there is nothing on disk to reverse-engineer a signature from:
**no header, no `.def`, no NVAFX-importing binary.** The claim that OBS can serve as a
reference is **false on this box.**

---

## 5. The frame contract, as measured

Harness: `C:\Dev\nvafx-verify` (throwaway .NET 9 console, source
`Program.cs`, built with `dotnet build "C:\Dev\nvafx-verify\nvafx-verify.csproj" -c Release`).
Command:
```
win "C:\Dev\nvafx-verify\bin\Release\net9.0\nvafx-verify.exe" contract
```
Sequence per effect: `CreateEffect` → `SetU32(sample_rate)` → `SetString(model_path, <abs path
into the SDK models dir>)` → `Load` → read back five fields with `GetU32`.

Every effect loaded and every `GetU32` in this table returned `0`:

| effect | `sample_rate` set | `input_sample_rate` | `output_sample_rate` | `num_input_channels` | `num_output_channels` | `num_samples_per_frame` |
|---|---|---|---|---|---|---|
| `denoiser` | 48000 | 48000 | 48000 | 1 | 1 | **480** |
| `dereverb` | 48000 | 48000 | 48000 | 1 | 1 | **480** |
| `dereverb_denoiser` | 48000 | 48000 | 48000 | 1 | 1 | **480** |
| `aec` | 48000 | 48000 | 48000 | **2** | **1** | **480** |
| `superres` | 16000 | **16000** | **48000** | 1 | 1 | **480** |

Corrections and clarifications against the already-known list:

1. **`aec` is 2 channels in but 1 channel out.** The known note said "1 channel in/out (`aec`
   is 2 in)". The output is **one** channel — the AEC returns the echo-cancelled mono signal.
   A binding that allocates a 2-element output array for `aec` is passing a contract the SDK
   does not expect. This is a real correction, and it is the kind of thing that returns `1`
   silently.
2. **`num_channels` is not a writable parameter.** `SetU32(handle, "num_channels", 2)` on `aec`
   returned **`3`** (unknown parameter name). The channel count is nevertheless correct (2 in)
   because it is derived from the effect + model, not from a settable key. Do not try to set
   it; treat `3` here as expected, not as a failure.
3. **The frame length is derived, not configurable.** `480` is exactly `48000 / 100`, and the
   models' own readme says every model is a "10 milliseconds framesize". So the correct frame
   length is `input_sample_rate / 100`.
   - 480 @ 48 kHz = 10 ms ✅
   - 160 @ 16 kHz = 10 ms ✅
   - 960 @ 48 kHz = 20 ms ❌ — wrong duration
   - 441 @ 44.1 kHz ❌ — **44.1 kHz is not a supported rate at all**; the models only exist at
     16 kHz and 48 kHz, so there is no 44.1 kHz model to load. The failure of 441 is a
     consequence of the rate being unsupported, not of the frame arithmetic.
4. **`superres` is the only effect that changes rate** (16 kHz in → 48 kHz out), and its
   `num_samples_per_frame` reads **480**, i.e. the **output-rate** frame count. Its input frame
   is 160 samples. **NOT ESTABLISHED:** whether `NvAFX_Run`'s `num_samples` argument for
   `superres` is expected in input-rate or output-rate units — I did not run a frame through
   `superres`, and the two candidate readings (160 vs 480) cannot be distinguished from the
   `GetU32` values alone. Anyone binding `superres` must settle this empirically (try 160 with a
   160-sample input buffer and check the returned status, then try 480).
5. The whole contract is readable **after `Load`** and is stable. `GetU32` requires the handle
   to be loaded; the five names above are the exact strings, confirmed accepted (status `0`).

Effect list is self-describing and matches the report exactly:
```
win "...\nvafx-verify.exe" effects
GetEffectList -> 0 count=5
  effect[0] = denoiser
  effect[1] = dereverb
  effect[2] = dereverb_denoiser
  effect[3] = aec
  effect[4] = superres
```

---

## 6. Timings — real, 1500 calls, cold start

Method, identical for every run:
```
win "C:\Dev\nvafx-verify\bin\Release\net9.0\nvafx-verify.exe" timing <effect> 1500
```
- 5-argument `NvAFX_Run(handle, in[], out[], numSamples, numChannels)`.
- 480-sample frames, 1 channel, `sample_rate=48000`, model at 48 kHz, **mono**.
- Input per frame: a continuous 440 Hz sine at 0.2 amplitude, sample-accurate across frames
  (not the same 480 samples repeated).
- **No warm-up calls.** Call 0 is the first call after `Load`, so first-call initialisation is
  inside the measured window — which is what makes the first-100 / last-100 comparison
  meaningful.
- `Stopwatch` around each individual call, `ElapsedTicks`/`Frequency`.
- p99 by the nearest-rank definition on the sorted sample.
- `fails` is the count of non-zero returns; it was **0 in every run**.
- Frame duration is 10 ms of audio (480 @ 48 kHz), so real-time ratio = 10 / mean_ms.

### denoiser (`denoiser_48k.trtpkg`), three independent runs

| run | p50 ms | p90 ms | p99 ms | mean ms | fps | vs real time | median first 100 | median last 100 |
|---|---|---|---|---|---|---|---|---|
| 1 | 0.0008 | 0.6271 | **1.1668** | 0.1641 | 6094 | **60.9×** | 0.0011 | 0.0008 |
| 2 | 0.0007 | 0.5678 | **1.2546** | 0.1526 | 6553 | **65.5×** | 0.0009 | 0.0009 |
| 3 | 0.0008 | 0.6573 | **1.1570** | 0.1660 | 6024 | **60.2×** | 0.0010 | 0.0008 |

min 0.0002 ms, max 1.6352 ms (run 1). Total 246.2 ms of call time against 246.4 ms wall.

**p99 ≈ 1.2 ms, mean ≈ 0.16 ms, ≈ 61× real time.** Stable to ±8% across runs.

### dereverb (`dereverb_48k.trtpkg`)

| p50 ms | p90 ms | p99 ms | mean ms | fps | vs real time | median first 100 | median last 100 |
|---|---|---|---|---|---|---|---|
| 0.0006 | 0.4577 | **1.1525** | 0.1279 | 7818 | **78.2×** | 0.0007 | 0.0005 |

min 0.0002, max 2.0602 ms, total 191.9 ms vs 192.1 ms wall. Loads and runs: **yes**, 0 failures
in 1500 calls. The permissive alternative is not needed for dereverb at 48 kHz.

### dereverb_denoiser (`dereverb_denoiser_48k.trtpkg`)

| p50 ms | p90 ms | p99 ms | mean ms | fps | vs real time | median first 100 | median last 100 |
|---|---|---|---|---|---|---|---|
| 0.0006 | 0.5448 | **1.0876** | 0.1433 | 6978 | **69.8×** | 0.0009 | 0.0008 |

total 215.0 ms vs 215.3 ms wall, 0 failures. Also loads and runs cleanly.

**Answer to item 5: `dereverb` and `dereverb_denoiser` both load and run at 48 kHz with
exactly the same frame contract as `denoiser` (1 in / 1 out / 480 samples) and neither needs a
fallback.**

### Reading the distribution correctly — this is the important part

**These distributions are bimodal, and the p50 is not the number to plan with.**
p50 is ~0.8 **microseconds** while p99 is ~1.2 **milliseconds** — a factor of ~1500. The mean
(0.16 ms) implies roughly 15–18 % of calls carry essentially all the work while the rest
return almost instantly. This is consistent with asynchronous submission (enqueue returns
immediately, the queue occasionally blocks), but **NOT ESTABLISHED: the mechanism is not
identified.** What *is* established is that this is not a no-op fast path — see the output
check below.

Practical consequence for the binding: **budget against p99 ≈ 1.2 ms per 10 ms frame, not
p50.** A design that assumes p50 = 0.8 µs will produce a 1500× underestimating latency budget
and will glitch on exactly the calls that do the work.

### The fast calls are not no-ops — output-integrity check

Timing alone cannot distinguish "fast because nothing happened" from "fast because
asynchronous". So the output buffer was poisoned with NaN before every call and the result
inspected after:
```
win "...\nvafx-verify.exe" pass 200
```
- `untouched = 0` (the output buffer was **written on every single call**; the NaN poison never
  survived)
- `passthru = 0` (no call returned its input unchanged)
- `changed = 200 / 200`, from call 0 onward
- `nans = 0` in every output

So every `NvAFX_Run` call writes processed output, including every sub-microsecond call.
The `FAILED(1)` / silent-nothing failure mode is therefore **not** what these fast calls are.

What the output *contains* for this input: `rmsIn ≈ 0.142`, `rmsOut ≈ 0.0004–0.005`, i.e. the
denoiser is emitting near-silence for a pure 440 Hz tone. That is expected behaviour for a
speech denoiser fed a synthetic sine, and it is **not** evidence of a broken binding — but it
also means **this harness proves the pipeline, not the audio quality.** Whether that near-zero
output is suppression or a one-frame latency artifact is **NOT ESTABLISHED** (distinguishing
them needs a speech-like input, or cross-correlation of output against a time-shifted input;
neither was run). The earlier "~49× real time on a synthetic tone" figure should be replaced
by the numbers above: **denoiser ≈ 61×, dereverb ≈ 78×, dereverb_denoiser ≈ 70×**, and the
earlier figure's provenance is not reproducible.

---

## 7. Traps

### Trap A — `NvAFX_Run` takes five arguments; four returns 1 (now measured, not asserted)

Same loaded handle, alternating calls:
```
win "...\nvafx-verify.exe" run4v5
--- 5-arg NvAFX_Run(h, in[], out[], 480, 1) ---
  call[0..4] -> 0 0 0 0 0
--- 4-arg NvAFX_Run(h, in[], out[], 480) ---
  call[0] -> 1
  call[1] -> 3
  call[2] -> 3
  call[3] -> 3
  call[4] -> 3
--- 5-arg again on the SAME handle ---
  call[0..4] -> 0 0 0 0 0
```

Three facts worth keeping:
- The 4-arg form returns **`1` on the first call**, matching the reported symptom.
- On subsequent calls it returns **`3`** — so the error code *changes* with call order. A
  binding that logs only the first failure will see `1`; one that logs the steady state will
  see `3`. Both mean "the handle is in a bad state", but they are not the same code, and this
  is why "it always returns 1" and "it always returns 3" reports can both be true.
- The handle **recovers**: the 5-arg call works again on the same handle, `0` five times
  running. The 4-arg call does not wedge it, so this trap is fully reversible and cheap to
  bisect for.
- On x64 the missing 5th argument is whatever happens to sit in the caller's shadow space, so
  the failure is *deterministic per build but arbitrary in value* — do not try to reason about
  it, just never omit the argument. There is no such thing as "it links, so it is fine":
  P/Invoke has no signature check at all, which is why the wrong arity surfaced only at
  runtime.

**NOT ESTABLISHED:** the exact garbage value passed as `numChannels` in the 4-arg case.

### Trap B — the wrong arity produces a plausible success code, not an argument error

Nothing in the 4-arg result says "you passed one argument too few". It reports a hardware-ish
failure. Combined with no header (§3) and no diagnostics by default (§2:
`NvAFX_SetLoggingCallback` / `InitializeLogger` exist but are not used by default), the wrong
arity is indistinguishable from a real GPU/model problem. **Wire up
`NvAFX_InitializeLogger` early** — it is exported specifically so that this class of failure
has text.

### Trap C — `3` is not `1`, and conflating them hides binding bugs

Measured meanings (§2): `0` OK, `1` failure (also `GetU32` with an unknown name, and the 4-arg
`Run`), `3` unknown parameter name (every `Set*` and `GetString` with a bogus name).
`GetU32` and `GetString` **disagree** on the code for an unknown name (`1` vs `3`). Treating
"non-zero" as one bucket makes a typo'd parameter name look identical to a dead effect.

### Trap D — `NvAFX_GetString` was blamed unfairly

`GetString` works (§2). The earlier `FAILED(1)`-with-zeroed-buffer observation is not
reproducible as a `GetString` defect: with a **valid** name it returns `0` and the real string;
with an invalid name it returns `3`, not `1`. Do not add a workaround, and do not skip
`GetString` — it is the only way to confirm what the SDK actually loaded (e.g. read back
`model_path` to prove the right `.trtpkg` was picked, which is precisely the "silent wrong
model" failure the brief worries about).

### Trap E — `aec` is 2-in / 1-out, and `num_channels` is not writable

See §5. `SetU32("num_channels", 2)` returns `3` and should be ignored; the effect is 2-in
anyway, from the effect name. Allocating a 2-wide output array for `aec` is wrong. `aec` was
**not** timed (out of the brief's scope) — **NOT ESTABLISHED:** its per-frame cost and whether
the 2-channel input marshalling (`IntPtr[2]` in, `IntPtr[1]` out) is accepted by
`NvAFX_Run`'s `numChannels` argument. It must be tried before being depended on.

### Trap F — do not infer the frame length; read it

`GetU32("num_samples_per_frame")` costs one call after `Load` and is always right (§5). Deriving
it as `rate/100` happens to agree for the four same-rate effects and **disagrees with
`superres`**, where the reported 480 is the output-rate count and the input frame is 160.
Hard-coding 480 is correct for 48 kHz mono and wrong for 16 kHz and for `superres`.

### Trap G — DLL search path

`NVAudioEffects.dll` has eight non-system dependencies sitting beside it (§1). Load it with the
SDK directory on the search path (`SetDllDirectory`, or `LoadLibraryEx` with
`LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR`). A plain `[DllImport("NVAudioEffects.dll")]` resolves only
if the DLL is copied next to the host or found on `PATH`, and the failure is a
`DllNotFoundException` at first call, not at startup.

---

## 8. What was not established

Stated plainly so nothing here is mistaken for a measurement:

- **The mechanism behind the bimodal timing** (async enqueue vs internal batching). The output
  is written on every call, so it is not skipping work, but *why* most calls return in <2 µs is
  unknown.
- **Whether the denoiser's near-silent output for a pure tone is suppression or latency.** No
  speech-like input was run and no cross-correlation was done, so audio quality is unproven.
- **`superres` frame-unit semantics** (160 vs 480 for the `num_samples` argument). Not run.
- **`aec` timing, and whether its 2-in/1-out arrays are accepted by `numChannels`.**
- **16 kHz contracts** (`denoiser_16k`, `dereverb_16k`, ...). Only the 48 kHz set and
  `superres`@16k-in were loaded. `num_samples_per_frame` should read 160, but that is inference,
  not measurement.
- **The `GetString` size argument's declared form** (`uint` vs `uint*`) — both work for a read,
  so behaviour cannot separate them.
- **The exact garbage value passed as the 5th argument in the 4-arg call.**
- **Whether `CreateChainedEffect` behaves like sequential `Run`s** — exported, never called.
- **No 44.1 kHz path exists**; if the OBS pipeline runs at 44.1 kHz, it must resample to 48 kHz
  before the effect. This is a design constraint, not a measurement gap.
- I did not run the OBS application, modify the SDK directory, or install anything. The only
  files created are the throwaway harness at `C:\Dev\nvafx-verify` and this document.
