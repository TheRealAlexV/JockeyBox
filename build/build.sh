#!/usr/bin/env bash
# Build JockeyBox for Windows (win-x64) from WSL/Linux.
# Output: dist/portable/JockeyBox.exe (single-file, self-contained)
#
# This script is the gate, so it must never report success for a build that did
# not compile:
#   1. the app project is always recompiled (dotnet build --no-incremental), so
#      MSBuild cannot skip CoreCompile and return in a second;
#   2. MSBuild's summary line pair ("N Warning(s)" / "N Error(s)") must be present
#      in the build log - a compile that did not run prints no summary - else we
#      fail with a distinct message;
#   3. the verdict comes from those summary numbers, not from grepping the output;
#   4. the published exe must carry the managed entry point that CreateAppHost
#      stamped into the single-file host. Without it the exe is a 150 MB file that
#      exits at once with "This executable is not bound to a managed DLL to
#      execute" while build and publish both report success.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/src/JockeyBox/JockeyBox.csproj"
OUT="$ROOT/dist/portable"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

TMP="$(mktemp -d -t jockeybox-build.XXXXXX)" || {
  echo "build: FAILED - cannot create a temp directory" >&2
  exit 1
}
trap 'rm -rf "$TMP"' EXIT
BUILD_LOG="$TMP/build.log"
PUBLISH_LOG="$TMP/publish.log"

# counts LOG -> "N M" (warnings, errors) taken from MSBuild's summary.
# Returns non-zero when the summary is absent, i.e. when no build actually ran.
counts() {
  local log="$1" w e
  w="$(sed -nE 's/^[[:space:]]*([0-9]+) Warning\(s\)[[:space:]]*$/\1/p' "$log" | tail -n 1)"
  e="$(sed -nE 's/^[[:space:]]*([0-9]+) Error\(s\)[[:space:]]*$/\1/p' "$log" | tail -n 1)"
  [ -n "$w" ] && [ -n "$e" ] || return 1
  echo "$w $e"
}

set +e
echo "==> Compiling (Release, win-x64, no-incremental)..."
dotnet build "$PROJ" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  --no-incremental \
  /p:EnableWindowsTargeting=true \
  -v:m 2>&1 | tee "$BUILD_LOG"
BUILD_RC=${PIPESTATUS[0]}
set -e

if ! read -r WARNINGS ERRORS < <(counts "$BUILD_LOG"); then
  echo "build: FAILED - no MSBuild summary (no 'N Warning(s)'/'N Error(s)' line) in the build log." >&2
  echo "build: the compiler did not run on this tree, so this build proves nothing." >&2
  exit 3
fi

echo
echo "build: compiled, $WARNINGS warnings, $ERRORS errors"

# The compile gate runs before publish, so a broken tree never reaches dist/.
if [ "$BUILD_RC" -ne 0 ] || [ "$ERRORS" -gt 0 ]; then
  echo "build: FAILED - the build reported errors (build exited $BUILD_RC)" >&2
  [ "$BUILD_RC" -ne 0 ] && exit "$BUILD_RC"
  exit 1
fi

if [ "$WARNINGS" -gt 0 ]; then
  echo "build: FAILED - this gate does not accept warnings" >&2
  exit 2
fi

#
# No --no-build here. Publish must run its own build pass, because that pass is
# what runs CreateAppHost and stamps the managed entry point into
# obj/<config>/<tfm>/<rid>/singlefilehost.exe. The compile gate above builds
# without PublishSingleFile, so it leaves no singlefilehost.exe behind, and
# --no-build made publish demand a file it never created:
#   error MSB4018: The "GenerateBundle" task failed unexpectedly ...
#   Could not find file '.../obj/Release/net8.0-windows/win-x64/singlefilehost.exe'
# Seeding that file by hand from ~/.nuget/packages is worse than the error: the
# copy is the pristine host template with an empty entry-point field, so the
# bundle is appended to a host that cannot find its managed DLL and the 150 MB
# result dies at startup. Let the SDK resolve the host package at the same
# version as the runtime pack it restores for this build; do not pin, do not seed.
set +e
echo "==> Publishing single-file portable exe (win-x64)..."
dotnet publish "$PROJ" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:EnableWindowsTargeting=true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:IncludeAllContentForSelfExtract=true \
  -o "$OUT" \
  -v:m 2>&1 | tee "$PUBLISH_LOG"
PUBLISH_RC=${PIPESTATUS[0]}
set -e

if [ "$PUBLISH_RC" -ne 0 ]; then
  echo "build: FAILED - publish exited $PUBLISH_RC" >&2
  exit "$PUBLISH_RC"
fi

[ -f "$OUT/JockeyBox.exe" ] || {
  echo "build: FAILED - $OUT/JockeyBox.exe missing after publish" >&2
  exit 1
}

# Dead-exe guard. A single-file exe only runs when its host carries the managed
# entry point name CreateAppHost stamped into it; a singlefilehost.exe copied out
# of ~/.nuget/packages is the pristine template, whose entry-point field is empty.
# GenerateBundle still appends the bundle to that template, so publish succeeds
# and leaves a 150 MB exe that exits immediately with
#   "This executable is not bound to a managed DLL to execute".
# The stamp lives inside the host region, i.e. before the end of the host file
# the bundle was built on; the embedded bundle data that follows also contains the
# assembly name, so the offset of the first occurrence is what discriminates.
HOST_OBJ="$ROOT/src/JockeyBox/obj/Release/net8.0-windows/win-x64/singlefilehost.exe"
[ -f "$HOST_OBJ" ] || {
  echo "build: FAILED - publish left no $HOST_OBJ to verify against" >&2
  exit 1
}
HOST_SIZE="$(stat -c %s "$HOST_OBJ")"
STAMP_OFF="$(grep -abo -m 1 'JockeyBox\.dll' "$OUT/JockeyBox.exe" | cut -d: -f1 || true)"
if [ -z "$STAMP_OFF" ] || [ "$STAMP_OFF" -ge "$HOST_SIZE" ]; then
  echo "build: FAILED - $OUT/JockeyBox.exe has no stamped managed entry point inside its host region." >&2
  echo "build: host template $HOST_SIZE bytes; first 'JockeyBox.dll' at offset ${STAMP_OFF:-<absent>}." >&2
  echo "build: that exe would start and exit with 'This executable is not bound to a managed DLL to execute'," >&2
  echo "build: so it is being removed from $OUT rather than left there to be deployed." >&2
  rm -f "$OUT/JockeyBox.exe" "$ROOT/dist/.exe-verified"
  exit 4
fi
echo "build: exe host ok - entry point stamped at $STAMP_OFF of $HOST_SIZE host bytes"

# Provenance for scripts/deploy-portable.sh: dist/ is never cleaned and the
# manifest lists names, not hashes, so an older or hand-published exe sitting in
# dist/portable would pass preflight. Record which exe this run verified.
md5sum "$OUT/JockeyBox.exe" | cut -d' ' -f1 > "$ROOT/dist/.exe-verified"

# The publish step prints no MSBuild summary today; fold one in only if a future
# SDK adds it, so its warnings cannot slip past the gate.
if pub="$(counts "$PUBLISH_LOG")"; then
  read -r PW PE <<<"$pub"
  if [ "$PW" -gt 0 ] || [ "$PE" -gt 0 ]; then
    echo "build: compiled, $((WARNINGS + PW)) warnings, $((ERRORS + PE)) errors"
    echo "build: FAILED - the publish step reported $PW warning(s), $PE error(s)" >&2
    exit 2
  fi
fi

echo "==> Done: $OUT/JockeyBox.exe"
