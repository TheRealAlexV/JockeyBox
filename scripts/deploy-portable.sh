#!/usr/bin/env bash
# deploy-portable.sh - put dist/portable onto the live Windows install, then verify it.
#
# Why this exists: two hand-copies in a row shipped an incomplete payload
# (a36: stale bpm_scan.py, no key_scan.py; 2026-11-10: the whole libvlc/ folder).
# The copy step was the defect, not the operator. This script deploys *everything*
# dist/portable holds except known build scratch, then md5s every deployed file
# source-vs-target and fails the run if anything differs.
#
# Additive only: never deletes in the target, no rsync --delete. The install keeps
# its *.bak-* exe history, JockeyBox-win-Setup.exe, logs and data/ untouched.
#
# Usage:
#   scripts/deploy-portable.sh --dry-run     plan only, writes nothing
#   scripts/deploy-portable.sh               deploy + verify
#   scripts/deploy-portable.sh --target DIR  deploy somewhere else
#
# Exit: 0 ok, 1 bad argument / app running / verification failure.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/dist/portable"
TARGET="/mnt/c/Dev/JockeyBox"
DRY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run|-n) DRY=1; shift ;;
    --target)     TARGET="${2:?--target needs a directory}"; shift 2 ;;
    -h|--help)    sed -n '2,18p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)            echo "deploy-portable: unknown argument: $1" >&2; echo "try --help" >&2; exit 1 ;;
  esac
done

[ -d "$SRC" ] || { echo "deploy-portable: no build output at $SRC - run build/build.sh first" >&2; exit 1; }
[ -f "$SRC/JockeyBox.exe" ] || { echo "deploy-portable: $SRC/JockeyBox.exe missing - run build/build.sh first" >&2; exit 1; }
[ -d "$TARGET" ] || { echo "deploy-portable: target $TARGET is not a directory" >&2; exit 1; }

# Build scratch that must not be deployed: debug symbols and Python bytecode cache.
# Everything else in dist/portable is payload the exe needs at runtime, and is
# deployed by default so a new payload folder can never be forgotten:
#   JockeyBox.exe          the app (self-contained, bundles LibVLCSharp*.dll)
#   libvlc/                core + plugins, kept loose and resolved on disk
#                          (JockeyBox.csproj KeepLibVlcPayloadLoose;
#                           Core.Initialize() in Playback/PreviewStreamPlayer.cs)
#   AnalysisScripts/       bpm_scan.py + key_scan.py, read from AppContext.BaseDirectory
#                          (Audio/Beat/BpmAnalysisService.cs; key_scan imported by bpm_scan.py)
is_scratch() {
  case "$1" in
    *.pdb|*.pyc) return 0 ;;
    */__pycache__/*) return 0 ;;
    __pycache__/*) return 0 ;;
  esac
  return 1
}

# Refuse to touch the install while Windows has the app running. Never kills it.
app_running() {
  if command -v tasklist.exe >/dev/null 2>&1; then
    timeout 30 tasklist.exe /FI "IMAGENAME eq JockeyBox.exe" /NH /FO CSV 2>/dev/null \
      | grep -qi '^"JockeyBox.exe"'
    return $?
  fi
  if command -v powershell.exe >/dev/null 2>&1; then
    if timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
        'if (Get-Process -Name JockeyBox -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }' \
        >/dev/null 2>&1; then
      return 0
    fi
    return 1
  fi
  echo "deploy-portable: cannot probe for a running JockeyBox.exe (no tasklist.exe or powershell.exe in PATH)." >&2
  echo "deploy-portable: close the app and re-run where Windows interop works." >&2
  return 0
}

if app_running; then
  if [ "$DRY" -eq 1 ]; then
    echo "! JockeyBox.exe appears to be running - a real run would refuse now."
  else
    echo "deploy-portable: JockeyBox.exe is running. Close it and re-run; this script never kills it." >&2
    exit 1
  fi
fi

datefmt() { date +%m%d-%H%M; }

TMPLIST="$(mktemp)"
trap 'rm -f "$TMPLIST"' EXIT

# Every regular file in the payload, relative to SRC, sorted for a stable plan.
( cd "$SRC" && find . -type f -printf '%P\n' | LC_ALL=C sort ) > "$TMPLIST"

copied=0 identical=0 skipped=0 verified=0 bytes=0
copied_list=() identical_list=() skip_list=() mismatched=()

md5_of() { md5sum "$1" | cut -d' ' -f1; }

echo "==> deploy-portable"
echo "    source  $SRC"
echo "    target  $TARGET"
[ "$DRY" -eq 1 ] && echo "    mode    DRY RUN (no writes)" || echo "    mode    deploy + verify"
echo

# Source exe hash decides whether the exe backup is needed at all.
src_exe_md5="$(md5_of "$SRC/JockeyBox.exe")"
tgt_exe="$TARGET/JockeyBox.exe"
if [ "$DRY" -eq 1 ]; then
  if [ -f "$tgt_exe" ] && [ "$(md5_of "$tgt_exe")" = "$src_exe_md5" ]; then
    echo "    backup  not needed: target exe already identical"
  else
    echo "    backup  would save $tgt_exe -> JockeyBox.exe.bak-$(datefmt)"
  fi
elif [ ! -f "$tgt_exe" ]; then
  echo "    backup  none: no existing exe to replace"
elif [ "$(md5_of "$tgt_exe")" = "$src_exe_md5" ]; then
  echo "    backup  skipped: target exe already identical"
else
  stamp="$(datefmt)"
  bak="$tgt_exe.bak-$stamp"
  n=1
  while [ -e "$bak" ]; do bak="$tgt_exe.bak-$stamp-$n"; n=$((n + 1)); done
  cp -f "$tgt_exe" "$bak"
  echo "    backup  $(basename "$bak")  ($(stat -c %s "$bak") bytes)"
fi
echo

while IFS= read -r rel; do
  if is_scratch "$rel"; then
    skip_list+=("$rel"); skipped=$((skipped + 1))
    [ "$DRY" -eq 1 ] && printf '  SKIP      %s\n' "$rel"
    continue
  fi
  s="$SRC/$rel"; t="$TARGET/$rel"
  s_md5="$(md5_of "$s")"
  if [ -f "$t" ] && [ "$(md5_of "$t")" = "$s_md5" ]; then
    identical_list+=("$rel"); identical=$((identical + 1))
    [ "$DRY" -eq 1 ] && printf '  IDENTICAL %s\n' "$rel"
    verified=$((verified + 1))
    continue
  fi
  size="$(stat -c %s "$s")"
  if [ "$DRY" -eq 1 ]; then
    printf '  COPY      %s  (%s bytes)\n' "$rel" "$size"
    copied_list+=("$rel"); copied=$((copied + 1)); bytes=$((bytes + size))
    verified=$((verified + 1))
    continue
  fi
  mkdir -p "$(dirname "$t")"
  cp -f "$s" "$t"
  got="$(md5_of "$t")"
  if [ "$got" != "$s_md5" ]; then
    echo "  VERIFY FAIL $rel" >&2
    echo "              source $s_md5" >&2
    echo "              target $got" >&2
    mismatched+=("$rel")
    continue
  fi
  copied_list+=("$rel"); copied=$((copied + 1)); bytes=$((bytes + size))
  verified=$((verified + 1))
done < "$TMPLIST"

echo
if [ "${#mismatched[@]}" -gt 0 ]; then
  echo "FAILED: ${#mismatched[@]} file(s) did not match after copy:" >&2
  printf '  %s\n' "${mismatched[@]}" >&2
  exit 1
fi

total_bytes=0
if [ "$DRY" -eq 0 ]; then
  # Re-hash the whole deployed set from disk: prove the target, do not trust cp.
  mismatched=()
  for rel in "${copied_list[@]}" "${identical_list[@]}"; do
    [ "$(md5_of "$TARGET/$rel")" = "$(md5_of "$SRC/$rel")" ] || mismatched+=("$rel")
  done
  if [ "${#mismatched[@]}" -gt 0 ]; then
    echo "FAILED: ${#mismatched[@]} file(s) differ between deploy and install:" >&2
    printf '  %s\n' "${mismatched[@]}" >&2
    exit 1
  fi
  verified=$((copied + identical))
fi

for rel in "${copied_list[@]}"; do total_bytes=$((total_bytes + $(stat -c %s "$SRC/$rel"))); done

echo "==> $( [ "$DRY" -eq 1 ] && echo "plan (nothing written)" || echo "summary" )"
printf '    copied    %s file(s), %s bytes\n' "$copied" "$total_bytes"
printf '    identical %s file(s)\n' "$identical"
printf '    skipped   %s build-scratch file(s) (*.pdb, __pycache__)\n' "$skipped"
printf '    verified  %s file(s) md5 source == target\n' "$verified"
[ "$DRY" -eq 1 ] && echo "    re-run without --dry-run to deploy."
exit 0
