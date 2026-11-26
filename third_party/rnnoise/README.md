# RNNoise (vendored, Windows x64)

`win-x64/rnnoise.dll` is the RNNoise denoiser built from `xiph/rnnoise` at
commit `70f1d256` (2025-02-22), with runtime SIMD dispatch (AVX2 / SSE4.1) and
the default model compiled in.

| Fact | Value |
|---|---|
| SHA-256 | `2b99c735ec594b0dd389731c752da8a16b523263a57e378cc6f9c5803ece988b` |
| Size | 14,851,584 bytes |
| Machine | 8664 (x86-64), PE32+ |
| Exports | 10, `rnnoise_create` through `rnnoise_process_frame` |
| Licence | BSD-3-Clause, [`COPYING`](COPYING), attribution in [`NOTICE.md`](NOTICE.md) |
| Model | default weights compiled in, `model_version` `0a8755f8...` |

This is the exact binary the measurements in
[`docs/research/rnnoise-facts.md`](../../docs/research/rnnoise-facts.md) were
taken on. The scalar comparison build beside it in the original build tree is
**not** vendored: it hashes differently (`d7db717b60081ea2` vs
`8dfe4c101d0d01e2` for the same input) because SIMD sums in a different order.
Ship one binary.

## Layout

```
third_party/rnnoise/
  COPYING                  upstream licence, verbatim
  NOTICE.md                attribution for code and compiled model
  README.md                this file
  win-x64/rnnoise.dll      the vendored binary
  build/build-rnnoise.sh   reproducible rebuild, run on the rig
```

`win-x64/` follows the repo's only existing native-payload convention, the
`libvlc/win-x64/` layout that `scripts/portable-payload.manifest` enumerates.
There was no `third_party/`, `vendor/`, `native/` or `lib/` directory before
this.

## Rebuilding

On the rig (Linux host, Windows host reachable through the `win` shim):

```
third_party/rnnoise/build/build-rnnoise.sh
```

Overrides, all optional:

```
WORKDIR=/mnt/c/Dev/rnnoise-repro \
VS_VCVARS='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat' \
third_party/rnnoise/build/build-rnnoise.sh
```

The script:

1. Clones `xiph/rnnoise` and checks out `70f1d256`.
2. Downloads `rnnoise_data-<model_version>.tar.gz` from
   `media.xiph.org/rnnoise/models/`, checks its sha256 against the checked-out
   `model_version` file, and unpacks `rnnoise_data.c` and `rnnoise_data.h` into
   `src/`.
3. Writes the two-line local `config.h` (`RNN_ENABLE_X86_RTCD=1`,
   `CPU_INFO_BY_ASM=1`).
4. Patches the `write_weights.c` blob open from `"w"` to `"wb"`. Text mode
   corrupts `weights_blob.bin` on Windows.
5. Compiles with VS2022 `cl.exe` x64: ten portable units with no SIMD defines,
   then `x86_dnn_map.c`, `x86cpu.c`, `nnet_sse4_1.c` with
   SSE/SSE2/SSE4.1, then `nnet_avx2.c` with `/arch:AVX2`. Links
   `/DLL /MACHINE:X64 /OPT:REF /OPT:ICF /INCREMENTAL:NO /RELEASE`.
6. Compares the rebuild with the vendored DLL: a timestamp-normalised sha256
   that must match, plus hard checks on size, machine type and the full export
   list.

Gotchas the recipe encodes, each of which cost real time:

- `rnnoise_data.c` is **not** in the repo on master. Without step 2 the build
  fails on a missing file.
- No `.def` file. `include/rnnoise.h` already maps `RNNOISE_EXPORT` to
  `__declspec(dllexport)` for `WIN32 && RNNOISE_BUILD && DLL_EXPORT`.
- `OPUS_X86_MAY_HAVE_SSE4_1` must not be defined for the portable units, or
  `celt_lpc.h` includes a nonexistent `x86/celt_lpc_sse.h`. It is scoped to the
  x86 units only.

### What a green run means

A green run means the rebuild matches the vendored binary after normalising
exactly the fields that are not reproducible content:

- the COFF header `TimeDateStamp` (link time),
- the optional header `CheckSum` (derived from the file),
- the same timestamp value mirrored once inside `.rdata`.

Their offsets are found by walking the PE headers and the section table, never
hardcoded, and the script refuses to normalise if the layout moved or if the
timestamp mirror is not found exactly once. Everything outside those 12 cleared
bytes must match byte for byte. On a match it prints the count and each field:

```
norm sha256 vendored 259bb19d5a84e5666182ece0762b18f8c59d8065c7e07aa096c4448ed2082d1f
norm sha256 rebuilt  259bb19d5a84e5666182ece0762b18f8c59d8065c7e07aa096c4448ed2082d1f
OK: rebuilt and vendored DLLs are byte-identical after removing 7 non-content bytes across 3 fields (12 bytes cleared):
  COFF TimeDateStamp @0x100: 2/4 bytes differed
  PE CheckSum @0x150: 3/4 bytes differed
  .rdata timestamp mirror @0xe26f24: 2/4 bytes differed
```

A genuine difference exits non-zero, prints every differing byte range with its
offset and section, and states plainly that it is not the link timestamp. Size,
machine type and the full export list are hard failures on their own.

The generated weight compilation is noisy by nature: `C4305` truncation warnings
from `rnnoise_data.c` and one `C4244` from `nnet_arch.h` are expected and harmless.

## Why it is copied by csproj rather than `runtimes/win-x64/native/`

`src/JockeyBox.Dsp/JockeyBox.Dsp.csproj` lists the DLL as a `None` item with
`CopyToOutputDirectory="PreserveNewest"`. A `runtimes/` asset would need a
NuGet pack and a `DllImportResolver`, which buys nothing here: the library is
not published as a package, and the process is Windows-only for the denoiser.
The `None` copy lands the file next to `JockeyBox.Dsp.dll` for the app and for
`tests/JockeyBox.Dsp.Tests` through the normal project reference flow.

The same copy also puts a Windows DLL into the Linux build and test output. It
is inert there: nothing in the test project loads it, and the tests do not P/Invoke
into it. This is stated rather than filtered out so the two outputs stay
identical.

## Rejected: stripping the compiled-in model

This was considered and is now **rejected**. The idea was to drop
`rnnoise_data.c`, ship a `.rnn` and load it with
`rnnoise_model_from_filename()`. Measured on the rig, it buys nothing:

| Quantity | Bytes |
|---|---|
| DLL with the model compiled in (today) | 14,851,584 |
| everything except `rnnoise_data.obj`, i.e. a stripped DLL plus PE headers | 87,749 |
| `rnnoise_data.obj` inside the current DLL | 14,763,835 |
| `rnnoise_default.rnn`, the file a stripped build would have to ship | 14,751,424 |
| stripped DLL + shipped `.rnn` | 14,839,173 |

The DLL would drop to about 88 KB, but the payload moves rather than shrinks:
14,751,424 + 87,749 = 14,839,173 bytes, which is 12,411 bytes (0.08%) **smaller**
than today, not larger, because the COFF object wrapper happens to be 12,411
bytes bigger than the model file it wraps. There is no meaningful size win either
way.

**The earlier figure of ~85 kB for the model file was wrong.** The default model
file on the rig is 14,751,424 bytes. ~85 kB describes a small third-party
(werman-style) model, not the default one that is compiled in here.

Rejected because it changes nothing in payload terms and adds an external file to
locate at runtime, a new failure mode when that file is missing, and a different
artefact from the one every number in `docs/research/rnnoise-facts.md` was
measured on.
