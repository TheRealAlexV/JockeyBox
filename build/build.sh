#!/usr/bin/env bash
# Build JockeyBox for Windows (win-x64) from WSL/Linux.
# Output: dist/portable/JockeyBox.exe (single-file, self-contained)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/src/JockeyBox/JockeyBox.csproj"
OUT="$ROOT/dist/portable"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "==> Publishing single-file portable exe (win-x64)..."
dotnet publish "$PROJ" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:EnableWindowsTargeting=true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

echo
echo "==> Done: $OUT/JockeyBox.exe"
