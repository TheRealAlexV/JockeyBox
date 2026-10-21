#!/usr/bin/env bash
# Package JockeyBox as a Windows installer (Setup.exe) using Velopack.
# Runs entirely on WSL/Linux. Output: dist/installer/
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/src/JockeyBox/JockeyBox.csproj"
PUBLISH="$ROOT/dist/publish"
OUT="$ROOT/dist/installer"
VERSION="${1:-1.5.0}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Velopack CLI (vpk) is a dotnet global tool; install once.
if ! command -v vpk >/dev/null 2>&1; then
  echo "==> Installing Velopack CLI (vpk)..."
  dotnet tool install -g vpk
fi

echo "==> Publishing app folder (win-x64, framework-independent)..."
rm -rf "$PUBLISH"
dotnet publish "$PROJ" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:EnableWindowsTargeting=true \
  /p:Version="$VERSION" \
  -o "$PUBLISH"

echo "==> Building Windows installer with Velopack..."
rm -rf "$OUT"
vpk [win] pack \
  --packId JockeyBox \
  --packVersion "$VERSION" \
  --packDir "$PUBLISH" \
  --mainExe JockeyBox.exe \
  --packTitle "JockeyBox" \
  --packAuthors "Alex" \
  --outputDir "$OUT"

echo
echo "==> Done. Installer artifacts in $OUT:"
ls -la "$OUT"
