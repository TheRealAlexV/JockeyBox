#!/usr/bin/env bash
# Rebuild third_party/rnnoise/win-x64/rnnoise.dll from upstream source.
#
# Run on the rig: the Linux side does fetch/patch/stage, the Windows side does
# cl.exe/link. Requires git, curl, sha256sum, tar, and the `win` shim
# (~/.local/bin/win) for the compile step.
#
# Every non-obvious step below was discovered the hard way; see README.md.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
VENDORED="$REPO_ROOT/third_party/rnnoise/win-x64/rnnoise.dll"
EXPECTED_SHA="2b99c735ec594b0dd389731c752da8a16b523263a57e378cc6f9c5803ece988b"

SRC_COMMIT="70f1d256"
# Upstream's model_version at SRC_COMMIT: sha256 of the weights tarball.
MODEL_SHA="0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37"
MODELS_BASE="https://media.xiph.org/rnnoise/models"

WORKDIR="${WORKDIR:-/mnt/c/Dev/rnnoise-repro}"
VS_VCVARS="${VS_VCVARS:-C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\VC\\Auxiliary\\Build\\vcvars64.bat}"

die() { printf '\nFAILED: %s\n' "$*" >&2; exit 1; }
step() { printf '\n=== %s ===\n' "$*"; }

[ -f "$VENDORED" ] || die "vendored binary missing: $VENDORED"
command -v git curl tar sha256sum >/dev/null || die "need git, curl, tar, sha256sum"
command -v win >/dev/null || die "need the 'win' shim on PATH (cmd.exe on the Windows host)"

mkdir -p "$WORKDIR"
WORKDIR="$(cd "$WORKDIR" && pwd)"
# /mnt/c/Dev/x -> C:\Dev\x
WIN_WORKDIR="$(printf '%s' "$WORKDIR" | sed -e 's|^/mnt/\([a-z]\)/|\U\1:\\|' -e 's|/|\\|g')"
[ "$WIN_WORKDIR" != "$WORKDIR" ] || die "WORKDIR must live under /mnt/<drive>/ (got $WORKDIR)"

step "source: xiph/rnnoise $SRC_COMMIT"
if [ ! -d "$WORKDIR/src/.git" ]; then
  rm -rf "$WORKDIR/src"
  git clone --quiet https://github.com/xiph/rnnoise.git "$WORKDIR/src"
fi
git -C "$WORKDIR/src" fetch --quiet origin
git -C "$WORKDIR/src" checkout --quiet "$SRC_COMMIT"
actual_commit="$(git -C "$WORKDIR/src" rev-parse --short=8 HEAD)"
printf 'checked out %s\n' "$actual_commit"

step "weights: rnnoise_data-$MODEL_SHA.tar.gz"
# rnnoise_data.c is NOT in the git tree. It comes from the models tarball.
upstream_version="$(tr -d '[:space:]' < "$WORKDIR/src/model_version")"
[ "$upstream_version" = "$MODEL_SHA" ] \
  || die "upstream model_version ($upstream_version) != recipe pin ($MODEL_SHA)"
TARBALL="$WORKDIR/rnnoise_data-$MODEL_SHA.tar.gz"
if [ ! -f "$TARBALL" ]; then
  curl -fsSL -o "$TARBALL.part" "$MODELS_BASE/$(basename "$TARBALL")"
  mv -f "$TARBALL.part" "$TARBALL"
fi
echo "$MODEL_SHA  $TARBALL" | sha256sum -c - || die "weights tarball sha256 mismatch"
tar xzf "$TARBALL" -C "$WORKDIR/src" src/rnnoise_data.c src/rnnoise_data.h
printf 'extracted rnnoise_data.c (%s bytes)\n' "$(stat -c %s "$WORKDIR/src/src/rnnoise_data.c")"

step "config.h: enable runtime SIMD dispatch"
cat > "$WORKDIR/src/config.h" <<'EOF'
#define RNN_ENABLE_X86_RTCD 1
#define CPU_INFO_BY_ASM 1
EOF
# Found via /DHAVE_CONFIG_H; must be in the /I search path.

step "patch write_weights.c: text mode corrupts the blob on Windows"
# Upstream opens weights_blob.bin with "w"; on Windows that writes CRLF and
# mangles binary data. One character: "w" -> "wb".
sed -i 's|fopen("weights_blob.bin", "w")|fopen("weights_blob.bin", "wb")|' \
  "$WORKDIR/src/src/write_weights.c"
grep -q 'weights_blob.bin", "wb"' "$WORKDIR/src/src/write_weights.c" \
  || die 'write_weights.c patch did not apply'

step "compile + link (VS2022 cl.exe x64)"
cat > "$WORKDIR/build.cmd" <<EOF
@echo off
setlocal
call "$VS_VCVARS" >nul 2>&1
if errorlevel 1 ( echo VCVARS FAILED & exit /b 1 )
cd /d "$WIN_WORKDIR\\src"
mkdir obj 2>nul
del /q obj\\*.obj rnnoise.dll rnnoise.lib 2>nul

REM No .def file and no /DEF: include/rnnoise.h maps RNNOISE_EXPORT to
REM __declspec(dllexport) when WIN32 + RNNOISE_BUILD + DLL_EXPORT are defined.
REM
REM OPUS_X86_MAY_HAVE_SSE4_1 must NOT be in CDEF: with it defined,
REM celt_lpc.h includes x86/celt_lpc_sse.h, which does not exist.
set CDEF=/nologo /c /O2 /Oi /Oy /GS- /DNDEBUG /DWIN32 /DRNNOISE_BUILD /DDLL_EXPORT /DHAVE_CONFIG_H /Iinclude /Isrc /I. /W3 /MD /bigobj
set XDEF=%CDEF% /DOPUS_X86_MAY_HAVE_SSE /DOPUS_X86_MAY_HAVE_SSE2 /DOPUS_X86_MAY_HAVE_SSE4_1
set CORE=src\\denoise.c src\\rnn.c src\\pitch.c src\\kiss_fft.c src\\celt_lpc.c src\\nnet.c src\\nnet_default.c src\\parse_lpcnet_weights.c src\\rnnoise_data.c src\\rnnoise_tables.c

echo === portable units ===
cl %CDEF% /Foobj\\ %CORE%
if errorlevel 1 ( echo COMPILE FAILED & exit /b 1 )

echo === x86 units (sse4.1) ===
cl %XDEF% /Foobj\\ src\\x86\\x86_dnn_map.c src\\x86\\x86cpu.c src\\x86\\nnet_sse4_1.c
if errorlevel 1 ( echo X86 COMPILE FAILED & exit /b 1 )

echo === avx2 unit ===
cl %XDEF% /DOPUS_X86_MAY_HAVE_AVX2 /arch:AVX2 /Foobj\\nnet_avx2.obj src\\x86\\nnet_avx2.c
if errorlevel 1 ( echo AVX2 COMPILE FAILED & exit /b 1 )

echo === link ===
link /nologo /DLL /MACHINE:X64 /OPT:REF /OPT:ICF /INCREMENTAL:NO /RELEASE /OUT:rnnoise.dll /IMPLIB:rnnoise.lib ^
  obj\\denoise.obj obj\\rnn.obj obj\\pitch.obj obj\\kiss_fft.obj obj\\celt_lpc.obj ^
  obj\\nnet.obj obj\\nnet_default.obj obj\\parse_lpcnet_weights.obj ^
  obj\\rnnoise_data.obj obj\\rnnoise_tables.obj ^
  obj\\x86_dnn_map.obj obj\\x86cpu.obj obj\\nnet_sse4_1.obj obj\\nnet_avx2.obj
if errorlevel 1 ( echo LINK FAILED & exit /b 1 )
echo === built ===
EOF

win "$WIN_WORKDIR\\build.cmd" || die "Windows build failed"

step "verify"
BUILT="$WORKDIR/src/rnnoise.dll"
[ -f "$BUILT" ] || die "no DLL produced at $BUILT"
command -v python3 >/dev/null || die "need python3 for the PE-aware verification"
command -v objdump >/dev/null || die "need objdump for the export-list check"

# Normalise the three fields that are not reproducible content:
#   * COFF header TimeDateStamp  (link time)
#   * optional header CheckSum   (derived from the file)
#   * the same timestamp value mirrored once inside .rdata
# Everything else must match byte for byte. Offsets are located by walking the
# PE headers, never hardcoded, so a layout shift fails instead of normalising
# the wrong bytes.
if ! python3 - "$VENDORED" "$BUILT" "$EXPECTED_SHA" <<'PY'
import hashlib, struct, sys

vendored_path, built_path, expected_sha = sys.argv[1], sys.argv[2], sys.argv[3]
EXPECTED_SIZE = 14851584
EXPECTED_MACHINE = 0x8664  # AMD64

def die(msg):
    print(msg)
    sys.exit(2)

def parse(path):
    b = open(path, 'rb').read()
    if b[:2] != b'MZ':
        die(f'FAIL: {path}: not a PE file')
    lf = struct.unpack_from('<I', b, 0x3c)[0]
    if b[lf:lf + 4] != b'PE\0\0':
        die(f'FAIL: {path}: no PE signature at 0x{lf:x}')
    machine = struct.unpack_from('<H', b, lf + 4)[0]
    nsect = struct.unpack_from('<H', b, lf + 6)[0]
    ts_off = lf + 8
    size_opt = struct.unpack_from('<H', b, lf + 20)[0]
    opt = lf + 24
    ck_off = opt + 64
    table = opt + size_opt
    sections = []
    for i in range(nsect):
        o = table + i * 40
        name = b[o:o + 8].rstrip(b'\0').decode('latin-1')
        vsize, va, rsize, roff = struct.unpack_from('<IIII', b, o + 8)
        sections.append((name, va, vsize, roff, rsize))
    return b, machine, ts_off, ck_off, sections

def where(sections, off):
    for name, va, vsize, roff, rsize in sections:
        if roff <= off < roff + rsize:
            return f'{name}+0x{off - roff:x}'
    return 'headers'

def mirror_hits(b, ts, sections):
    needle = struct.pack('<I', ts)
    hits = []
    for name, va, vsize, roff, rsize in sections:
        start, end = roff, roff + rsize
        while True:
            i = b.find(needle, start, end)
            if i < 0:
                break
            hits.append((name, i))
            start = i + 1
    return hits

vb, vmach, vts_off, vck_off, vsects = parse(vendored_path)
bb, bmach, bts_off, bck_off, bsects = parse(built_path)

hard_fail = False
for label, data, mach in (('vendored', vb, vmach), ('rebuilt', bb, bmach)):
    if len(data) != EXPECTED_SIZE:
        print(f'FAIL: {label} size {len(data)} bytes, expected {EXPECTED_SIZE}')
        hard_fail = True
    if mach != EXPECTED_MACHINE:
        print(f'FAIL: {label} machine 0x{mach:x}, expected 0x{EXPECTED_MACHINE:x}')
        hard_fail = True
if hashlib.sha256(vb).hexdigest() != expected_sha:
    print(f'FAIL: vendored binary is not the recorded artefact '
          f'(sha256 {hashlib.sha256(vb).hexdigest()}, expected {expected_sha})')
    hard_fail = True
if hard_fail:
    sys.exit(2)

vts = struct.unpack_from('<I', vb, vts_off)[0]
bts = struct.unpack_from('<I', bb, bts_off)[0]

vh = mirror_hits(vb, vts, vsects)
bh = mirror_hits(bb, bts, bsects)
if len(vh) != 1:
    die(f'FAIL: expected exactly one timestamp mirror in .rdata of the vendored '
        f'binary, found {len(vh)} at {[hex(o) for _, o in vh]}')
if len(bh) != 1:
    die(f'FAIL: expected exactly one timestamp mirror in .rdata of the rebuilt '
        f'binary, found {len(bh)} at {[hex(o) for _, o in bh]}')
if (vh[0][0], vh[0][1]) != (bh[0][0], bh[0][1]):
    die(f'FAIL: timestamp mirror moved: vendored {vh[0][0]}+0x{vh[0][1]:x}, '
        f'rebuilt {bh[0][0]}+0x{bh[0][1]:x}')
if (vts_off, vck_off) != (bts_off, bck_off):
    die('FAIL: header layout differs between the two binaries; refusing to normalise')

fields = [
    ('COFF TimeDateStamp', vts_off, 4),
    ('PE CheckSum', vck_off, 4),
    (f'{vh[0][0]} timestamp mirror', vh[0][1], 4),
]
normalised_bytes = set()
for _, off, ln in fields:
    normalised_bytes.update(range(off, off + ln))

diffs = [i for i in range(len(vb)) if vb[i] != bb[i]]
outside = [i for i in diffs if i not in normalised_bytes]

print(f'raw sha256  rebuilt  {hashlib.sha256(bb).hexdigest()}')
print(f'raw sha256  vendored {hashlib.sha256(vb).hexdigest()}')

def normalise(b):
    x = bytearray(b)
    for _, off, ln in fields:
        x[off:off + ln] = b'\0' * ln
    return hashlib.sha256(bytes(x)).hexdigest()

nv, nb = normalise(vb), normalise(bb)
print(f'norm sha256 vendored {nv}')
print(f'norm sha256 rebuilt  {nb}')

if outside or nv != nb:
    print('')
    print('######################################################################')
    print('## REAL CONTENT DIFFERENCE - this is not the link timestamp')
    print('######################################################################')
    if outside:
        ranges = []
        for i in outside:
            if ranges and i == ranges[-1][1] + 1:
                ranges[-1][1] = i
            else:
                ranges.append([i, i])
        for start, end in ranges[:20]:
            print(f'## 0x{start:x}-0x{end:x}  {end - start + 1} byte(s)  '
                  f'{where(vsects, start)}')
        if len(ranges) > 20:
            print(f'## ... {len(ranges) - 20} more range(s)')
    print(f'## normalised hashes differ: {nv} vs {nb}')
    print('## The rebuild is not the measured artefact. Check the model_version')
    print('## pin and the toolchain before trusting it, and re-run the probe.')
    print('######################################################################')
    sys.exit(1)

per_field = []
for name, off, ln in fields:
    n = sum(1 for i in range(off, off + ln) if vb[i] != bb[i])
    per_field.append((name, off, ln, n))
cleared = sum(ln for _, _, ln in fields)
if not diffs:
    print('')
    print('OK: rebuilt and vendored DLLs are byte-identical with no normalisation needed.')
else:
    print('')
    print(f'OK: rebuilt and vendored DLLs are byte-identical after removing '
          f'{len(diffs)} non-content bytes across {len(fields)} fields '
          f'({cleared} bytes cleared):')
    for name, off, ln, n in per_field:
        print(f'  {name} @0x{off:x}: {n}/{ln} bytes differed')
PY
then
  die "verification failed: see above"
fi

step "exports and machine"
exports_of() {
  objdump -p "$1" \
    | sed -n '/\[Ordinal\/Name Pointer\] Table/,/^$/p' \
    | sed -nE 's/^[[:space:]]*\[[[:space:]]*[0-9]+\][[:space:]]+([A-Za-z0-9_]+).*/\1/p' \
    | sort
}
v_exports="$(exports_of "$VENDORED")"
b_exports="$(exports_of "$BUILT")"
v_count="$(printf '%s\n' "$v_exports" | sed '/^$/d' | wc -l)"
b_count="$(printf '%s\n' "$b_exports" | sed '/^$/d' | wc -l)"
printf 'exports: vendored %s, rebuilt %s\n' "$v_count" "$b_count"
if [ "$v_count" != "10" ] || [ "$b_count" != "10" ]; then
  die "expected 10 exports on both binaries"
fi
if [ "$v_exports" != "$b_exports" ]; then
  printf '!! export lists differ:\n'
  diff <(printf '%s\n' "$v_exports") <(printf '%s\n' "$b_exports") || true
  die "export list mismatch"
fi

printf '\nOK\n'

