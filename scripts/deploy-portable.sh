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

# Best effort list an operator can act on: PID, start time, path. It decides
# nothing - app_running() has already concluded - it only says what to close.
print_running_processes() {
  command -v powershell.exe >/dev/null 2>&1 || return 0
  timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
    'Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | ForEach-Object { "      pid {0}  started {1}  {2}" -f $_.Id, $_.StartTime, $_.Path }' \
    2>/dev/null | tr -d '\r' || true
}

# Refuse to touch the install while Windows has the app running. Never kills it.
#
# Fails CLOSED (2026-11-11): only a positive, parsed answer of "no matching
# process" counts as not running. A missing tool, a non-zero exit, a timeout, or
# empty output all mean we do not know - and guessing "not running" is what puts
# this script into a copy of a locked exe, where the operator sees
# "cp: cannot remove ...: Input/output error" instead of a clean refusal.
app_running() {
  local out rc why=""

  if command -v tasklist.exe >/dev/null 2>&1; then
    out="$(timeout 30 tasklist.exe /FI "IMAGENAME eq JockeyBox.exe" /NH /FO CSV 2>/dev/null)" && rc=0 || rc=$?
    if [ "$rc" -eq 0 ] && printf '%s' "$out" | grep -qi '^"JockeyBox\.exe"'; then
      echo "    app     running: tasklist.exe reports a match (exit $rc)"
      printf '%s\n' "$out" | grep -i '^"JockeyBox\.exe"' | sed 's/^/      /'
      print_running_processes
      return 0
    fi
    if [ "$rc" -ne 0 ]; then
      why="tasklist.exe exited $rc"
    elif [ -z "$(printf '%s' "$out" | tr -d '[:space:]')" ]; then
      why="tasklist.exe returned no output"
    else
      # Non-empty and no JockeyBox.exe row: that is tasklist's own notice that
      # nothing matched. The one answer we trust.
      echo "    app     not running: tasklist.exe found no JockeyBox.exe"
      return 1
    fi
  else
    why="tasklist.exe not in PATH"
  fi

  # tasklist did not answer usably - ask Windows a second way before deciding.
  if command -v powershell.exe >/dev/null 2>&1; then
    out="$(timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
      'Get-Process -Name JockeyBox -ErrorAction SilentlyContinue | ForEach-Object { "      pid {0}  started {1}  {2}" -f $_.Id, $_.StartTime, $_.Path }' 2>/dev/null)" && rc=0 || rc=$?
    out="$(printf '%s' "$out" | tr -d '\r')"
    if [ "$rc" -eq 0 ] && [ -n "$(printf '%s' "$out" | tr -d '[:space:]')" ]; then
      echo "    app     running: powershell.exe reports a match (tasklist: $why)"
      printf '%s\n' "$out" | sed 's/^/    /'
      return 0
    fi
    if [ "$rc" -eq 0 ]; then
      echo "    app     not running: powershell.exe found no JockeyBox process (tasklist: $why)"
      return 1
    fi
    why="$why; powershell.exe exited $rc"
  else
    why="$why; powershell.exe not in PATH"
  fi

  echo "    app     cannot tell ($why) - assuming JockeyBox.exe is running." >&2
  echo "deploy-portable: refusing to touch the install: cannot prove the app is closed." >&2
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
