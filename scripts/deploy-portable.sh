#!/usr/bin/env bash
# deploy-portable.sh - put dist/portable onto the live Windows install, then verify it.
#
# Why this exists: two hand-copies in a row shipped an incomplete payload
# (a36: stale bpm_scan.py, no key_scan.py; 2026-11-10: the whole libvlc/ folder).
# The copy step was the defect, not the operator. This script deploys *everything*
# dist/portable holds except known build scratch, then md5s every deployed file
# source-vs-target and fails the run if anything differs.
#
# md5 source == target proves a copy was faithful; it cannot prove the *source was
# complete*. dist/portable is gitignored and never cleaned, and `dotnet publish -o`
# never deletes stale files, so what sits there is a property of that directory at
# deploy time, not of the commit. scripts/portable-payload.manifest pins the expected
# payload; the preflight below refuses to copy when a listed file is missing or when
# the directory holds a file the manifest does not list. A short payload is a hard
# failure, never a warning.
#
# Additive only: never deletes in the target, no rsync --delete. The install keeps
# its *.bak-* exe history, JockeyBox-win-Setup.exe, logs and data/ untouched.
#
# Usage:
#   scripts/deploy-portable.sh --dry-run     plan only, writes nothing
#   scripts/deploy-portable.sh               deploy + verify
#   scripts/deploy-portable.sh --target DIR  deploy somewhere else
#   scripts/deploy-portable.sh --write-manifest
#                                            rewrite the payload manifest from
#                                            dist/portable, deploy nothing
#
# Exit: 0 ok, 1 bad argument / app running / manifest mismatch / verification failure.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/dist/portable"
TARGET="/mnt/c/Dev/JockeyBox"
MANIFEST="$ROOT/scripts/portable-payload.manifest"
DRY=0
WRITE_MANIFEST=0

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run|-n) DRY=1; shift ;;
    --write-manifest) WRITE_MANIFEST=1; shift ;;
    --target)     TARGET="${2:?--target needs a directory}"; shift 2 ;;
    -h|--help)    sed -n '2,29p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)            echo "deploy-portable: unknown argument: $1" >&2; echo "try --help" >&2; exit 1 ;;
  esac
done

[ -d "$SRC" ] || { echo "deploy-portable: no build output at $SRC - run build/build.sh first" >&2; exit 1; }
[ -f "$SRC/JockeyBox.exe" ] || { echo "deploy-portable: $SRC/JockeyBox.exe missing - run build/build.sh first" >&2; exit 1; }

# Ship the exe that was proven, not whatever is lying in dist/. dist/ is never
# cleaned and the manifest below pins names, not hashes, so a stale exe passes
# preflight; and a single-file exe built on an unstamped host starts and exits at
# once with no build error anywhere. build/build.sh records the md5 of the exe
# whose entry point it verified, so compare before copying anything.
VERIFIED="$ROOT/dist/.exe-verified"
if [ -f "$VERIFIED" ]; then
  WANT_MD5="$(tr -d '[:space:]' < "$VERIFIED")"
  SRC_MD5="$(md5sum "$SRC/JockeyBox.exe" | cut -d' ' -f1)"
  if [ "$WANT_MD5" != "$SRC_MD5" ]; then
    echo "deploy-portable: $SRC/JockeyBox.exe is not the exe build/build.sh verified" >&2
    echo "deploy-portable:   verified $WANT_MD5" >&2
    echo "deploy-portable:   on disk  $SRC_MD5" >&2
    echo "deploy-portable: re-run build/build.sh so the exe being shipped is the one it checked" >&2
    exit 1
  fi
  echo "deploy-portable: exe md5 ${SRC_MD5:0:12} matches the verified build"
else
  echo "deploy-portable: WARNING - no $VERIFIED; cannot prove this exe is the verified build" >&2
fi

# Temp files are cleaned on every exit path, including a refusal.
TMP_LIST=""; TMP_WANT=""; TMP_HAVE=""; TMP_NEW=""
tmp_cleanup() {
  local f
  for f in "$TMP_LIST" "$TMP_WANT" "$TMP_HAVE" "$TMP_NEW"; do
    [ -n "$f" ] && rm -f "$f"
  done
  return 0
}
trap tmp_cleanup EXIT

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

# The payload as a manifest records it: every regular file under SRC except build
# scratch, relative, POSIX slashes, sorted. One source of truth for both writing
# the manifest and checking it.
payload_list() {
  ( cd "$SRC" && find . -type f -printf '%P\n' | LC_ALL=C sort ) | while IFS= read -r rel; do
    is_scratch "$rel" || printf '%s\n' "$rel"
  done
}

# Record the current dist/portable as the expected payload, deploy nothing. Updating
# the manifest is a deliberate act with a reviewable diff, never a side effect of a
# deploy that already happened.
write_manifest() {
  local added=() removed=()
  TMP_NEW="$(mktemp)"; TMP_WANT="$(mktemp)"
  payload_list > "$TMP_NEW"
  if [ -f "$MANIFEST" ]; then LC_ALL=C sort "$MANIFEST" > "$TMP_WANT"; else : > "$TMP_WANT"; fi
  mapfile -t added   < <(comm -13 "$TMP_WANT" <(LC_ALL=C sort "$TMP_NEW"))
  mapfile -t removed < <(comm -23 "$TMP_WANT" <(LC_ALL=C sort "$TMP_NEW"))

  echo "==> write-manifest"
  echo "    source    $SRC"
  echo "    manifest  $MANIFEST"
  echo "    files     $(wc -l < "$TMP_NEW")"
  if [ "${#added[@]}" -eq 0 ] && [ "${#removed[@]}" -eq 0 ]; then
    echo "    dist/portable already matches - no lines added or removed."
  fi
  if [ "${#added[@]}" -gt 0 ]; then
    echo "    added     ${#added[@]} line(s)"
    printf '      + %s\n' "${added[@]:0:40}"
    [ "${#added[@]}" -gt 40 ] && echo "      ... and $(( ${#added[@]} - 40 )) more"
  fi
  if [ "${#removed[@]}" -gt 0 ]; then
    echo "    removed   ${#removed[@]} line(s)"
    printf '      - %s\n' "${removed[@]:0:40}"
    [ "${#removed[@]}" -gt 40 ] && echo "      ... and $(( ${#removed[@]} - 40 )) more"
  fi
  cp -f "$TMP_NEW" "$MANIFEST"
  echo "    wrote it; nothing was deployed. Commit the diff deliberately."
}

# Refuse to deploy a payload that is not the one the commit promises. md5 below can
# only prove the copy was faithful; this is what proves the source was complete.
# Runs in --dry-run too, so a plan can catch a short payload before it ships.
preflight_payload() {
  local missing=() unexpected=()
  if [ ! -f "$MANIFEST" ]; then
    echo "deploy-portable: no payload manifest at $MANIFEST" >&2
    echo "                without it a short payload cannot be told from a correct one." >&2
    echo "                run: scripts/deploy-portable.sh --write-manifest" >&2
    exit 1
  fi
  TMP_WANT="$(mktemp)"; TMP_HAVE="$(mktemp)"
  LC_ALL=C sort "$MANIFEST" > "$TMP_WANT"
  payload_list > "$TMP_HAVE"
  mapfile -t missing    < <(comm -23 "$TMP_WANT" "$TMP_HAVE")   # listed, not on disk
  mapfile -t unexpected < <(comm -13 "$TMP_WANT" "$TMP_HAVE")   # on disk, not listed
  PAYLOAD_COUNT="$(wc -l < "$TMP_HAVE")"
  MANIFEST_COUNT="$(wc -l < "$TMP_WANT")"

  if [ "${#missing[@]}" -eq 0 ] && [ "${#unexpected[@]}" -eq 0 ]; then
    echo "    payload  $PAYLOAD_COUNT file(s) match scripts/portable-payload.manifest"
    return 0
  fi

  echo "deploy-portable: dist/portable does not match scripts/portable-payload.manifest" >&2
  echo "                nothing was copied." >&2
  if [ "${#missing[@]}" -gt 0 ]; then
    echo "    missing     ${#missing[@]} file(s) the manifest lists but dist/portable does not:" >&2
    printf '      %s\n' "${missing[@]:0:40}" >&2
    [ "${#missing[@]}" -gt 40 ] && echo "      ... and $(( ${#missing[@]} - 40 )) more" >&2
  fi
  if [ "${#unexpected[@]}" -gt 0 ]; then
    echo "    unexpected  ${#unexpected[@]} file(s) in dist/portable the manifest does not list:" >&2
    printf '      %s\n' "${unexpected[@]:0:40}" >&2
    [ "${#unexpected[@]}" -gt 40 ] && echo "      ... and $(( ${#unexpected[@]} - 40 )) more" >&2
  fi
  echo >&2
  echo "  dist/portable is a directory, not a property of the commit: dotnet publish -o" >&2
  echo "  never deletes stale files. A payload file that went missing ships a dead feature" >&2
  echo "  and md5 still passes, because the copy was faithful." >&2
  echo "  Inspect the difference, then record the payload deliberately:" >&2
  echo "      scripts/deploy-portable.sh --write-manifest" >&2
  exit 1
}

if [ "$WRITE_MANIFEST" -eq 1 ]; then
  write_manifest
  exit 0
fi

preflight_payload

[ -d "$TARGET" ] || { echo "deploy-portable: target $TARGET is not a directory" >&2; exit 1; }

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

TMP_LIST="$(mktemp)"

# Every regular file in the payload, relative to SRC, sorted for a stable plan.
( cd "$SRC" && find . -type f -printf '%P\n' | LC_ALL=C sort ) > "$TMP_LIST"

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
done < "$TMP_LIST"

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

# ---------------------------------------------------------------------------
# Pin the single-file payload root, so %TEMP% cannot take the app's assemblies.
#
# JockeyBox.exe is a self-contained single-file bundle. Before any managed code
# runs, the host unpacks its ~250 files into %TEMP%\.net\JockeyBox\<bundle id>\
# - WPF's mixed-mode DirectWriteForwarder.dll among them - and WPF resolves more
# of them lazily, hours into a session. Anything that cleans %TEMP% while the app
# is up therefore pulls assemblies out from under it: the next popup dies with an
# empty-message FileNotFoundException for UIAutomationTypes, and the process then
# dies at exit with DllNotFoundException inside the mixed-mode CRT uninitializer
# (_app_exit_callback -> __scrt_uninitialize_type_info), which is not catchable.
#
# DOTNET_BUNDLE_EXTRACT_BASE_DIR moves that root. The host reads it before it
# extracts, so managed code cannot set it for its own process - it has to exist in
# the environment of whatever launches the exe. This app is installed by copying
# files and launched by double-clicking JockeyBox.exe: there is no installer step
# and no shortcut to carry the setting, so this script is the only place that can
# establish it. User scope, not machine scope: one user's app should not rewrite
# another user's extraction layout.
#
# Scope caveat, deliberate: the variable is machine-wide *for this user*, so other
# single-file .NET apps extract under this root too - still per app and per bundle
# (<root>\<app>\<bundle id>\), just not under %TEMP%.
EXTRACT_ROOT_VAR='DOTNET_BUNDLE_EXTRACT_BASE_DIR'

ensure_extract_root() {
  local want have
  if ! command -v powershell.exe >/dev/null 2>&1; then
    echo "    root    cannot pin: powershell.exe not in PATH" >&2
    return 1
  fi

  want="$(timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
    '$p = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "JockeyBox\runtime"; $p' \
    2>/dev/null | tr -d '\r\n')"
  if [ -z "$want" ]; then
    echo "    root    cannot compute %LOCALAPPDATA% (powershell.exe returned nothing)" >&2
    return 1
  fi

  have="$(timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
    '[Environment]::GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR","User")' \
    2>/dev/null | tr -d '\r\n')"

  if [ "$have" = "$want" ]; then
    echo "    root    $EXTRACT_ROOT_VAR = $want (user scope, already set)"
    return 0
  fi

  if [ "$DRY" -eq 1 ]; then
    echo "    root    would set $EXTRACT_ROOT_VAR -> $want (was '${have:-unset}')"
    return 0
  fi

  timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
    "[Environment]::SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR','$want','User')" \
    >/dev/null 2>&1 || true

  # Read it back: a silent failure here leaves the app exposed, and the next
  # session would look exactly like the two that vanished.
  have="$(timeout 60 powershell.exe -NoProfile -NonInteractive -Command \
    '[Environment]::GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR","User")' \
    2>/dev/null | tr -d '\r\n')"
  if [ "$have" != "$want" ]; then
    echo "    root    FAILED to set (read back '${have:-unset}')" >&2
    return 1
  fi
  echo "    root    $EXTRACT_ROOT_VAR -> $want (set for user ${USER:-$(id -un)})"
  echo "    root    takes effect for processes started from now on; a window opened"
  echo "    root    before it was set keeps the old value until it is reopened."
  return 0
}

echo "==> extraction root"
if ! ensure_extract_root; then
  echo "FAILED: the single-file payload root is not pinned to a persistent directory." >&2
  echo "        Copying the exe is not enough: it would unpack into %TEMP%\\.net\\JockeyBox\\" >&2
  echo "        where a temp cleaner can delete the files WPF loads later, mid-session." >&2
  exit 1
fi

echo "==> $( [ "$DRY" -eq 1 ] && echo "plan (nothing written)" || echo "summary" )"
printf '    payload   %s file(s) == %s manifest line(s)\n' "$PAYLOAD_COUNT" "$MANIFEST_COUNT"
printf '    copied    %s file(s), %s bytes\n' "$copied" "$total_bytes"
printf '    identical %s file(s)\n' "$identical"
printf '    skipped   %s build-scratch file(s) (*.pdb, __pycache__)\n' "$skipped"
printf '    verified  %s file(s) md5 source == target\n' "$verified"
[ "$DRY" -eq 1 ] && echo "    re-run without --dry-run to deploy."
exit 0
