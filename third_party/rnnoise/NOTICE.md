# RNNoise attribution and licence

Vendored artefact: `third_party/rnnoise/win-x64/rnnoise.dll`
Size: 14,851,584 bytes
SHA-256: `2b99c735ec594b0dd389731c752da8a16b523263a57e378cc6f9c5803ece988b`
Machine: 8664 (x86-64), PE32+ DLL, 10 named exports.

## Licence: BSD-3-Clause

The full licence text is [`COPYING`](COPYING) in this directory. It was copied
byte for byte from the upstream source tree at commit `70f1d256`
(2025-02-22):

```
sha256sum COPYING
45d37ca1cdb278c088e1aa85e0e65ca3a534ed86a28dcc96ca16810248a61d35  COPYING
```

Upstream copyright holders, verbatim from `COPYING`:

```
Copyright (c) 2007-2017, 2024 Jean-Marc Valin
Copyright (c) 2023 Amazon
Copyright (c) 2017, Mozilla
Copyright (c) 2005-2017, Xiph.Org Foundation
Copyright (c) 2003-2004, Mark Borgerding
```

These five lines are the complete copyright block shipped upstream. There is no
separate Xiph-only line to quote.

## The compiled-in model

`rnnoise_data.c` is not in the upstream git tree. It is generated from the
weights tarball published at
<https://media.xiph.org/rnnoise/models/>, and the default model ends up inside
the DLL as static C tables. Those tables are Xiph.Org's trained model data, not
JockeyBox code.

Pinned model version, the sha256 of the tarball and the contents of upstream's
`model_version` file at commit `70f1d256`:

```
0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37
rnnoise_data-0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37.tar.gz
```

No separate model licence file is published with that tarball. The model is part
of the RNNoise distribution and is redistributed here under the same
BSD-3-Clause terms as the code; the binary-redistribution clause of `COPYING`
therefore covers both the compiled code and the compiled model tables, and this
NOTICE plus `COPYING` must travel with any redistribution of the DLL.

## Source and build provenance

- Source: `xiph/rnnoise`, commit `70f1d256` (2025-02-22).
- Toolchain: Visual Studio 2022 `cl.exe` x64, `/DLL /MACHINE:X64`, SIMD via
  runtime CPU dispatch (`RNN_ENABLE_X86_RTCD=1`), including the `src/x86` units.
- Rebuild recipe: [`build/build-rnnoise.sh`](build/build-rnnoise.sh) and
  [`README.md`](README.md).

Byte identity is not promised across toolchain or linker versions. The rebuild
script compares its output against the SHA-256 above and reports a mismatch
loudly.
