#!/usr/bin/env bash
#
# Build the JockeyBox Soundboard Stream Deck plugin and install it into the
# Stream Deck app on the Windows host.
#
#   ./install.sh                 # install + restart the plugin
#   ./install.sh --no-restart    # install only
#
# Everything runs where it can: npm in this WSL checkout, the copy through the
# /mnt/c mount (falling back to the winps helper if that mount is missing).
set -euo pipefail

UUID="com.alex.jockeybox.soundboard"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$SCRIPT_DIR/$UUID.sdPlugin"
PLUGINS_DIR="/mnt/c/Users/Alex/AppData/Roaming/Elgato/StreamDeck/Plugins"
DEST_DIR="$PLUGINS_DIR/$UUID.sdPlugin"
RESTART=1

for arg in "$@"; do
  case "$arg" in
    --no-restart) RESTART=0 ;;
    -h|--help)
      sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "unknown option: $arg (try --help)" >&2
      exit 2
      ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }
fail() { printf 'install.sh: %s\n' "$1" >&2; exit 1; }

[ -d "$PLUGIN_DIR" ] || fail "plugin folder not found: $PLUGIN_DIR"

step "Build"
cd "$PLUGIN_DIR"
if [ -f package-lock.json ]; then
  npm ci --no-audit --no-fund || npm install --no-audit --no-fund
else
  npm install --no-audit --no-fund
fi
npm run build
npm run check
npm run validate || echo "warning: manifest validation complained; continuing"

[ -f bin/plugin.js ] || fail "build did not produce bin/plugin.js"

step "Install to $DEST_DIR"
[ -d "$PLUGINS_DIR" ] || fail "Stream Deck plugin directory not found: $PLUGINS_DIR (is the C: drive mounted?)"
[ -w "$PLUGINS_DIR" ] || fail "cannot write to $PLUGINS_DIR"
# A folder that was not there before means Stream Deck has to (re)scan: the app
# only looks at this directory when it starts.
FIRST_INSTALL=0
[ -d "$DEST_DIR" ] || FIRST_INSTALL=1
# The runtime only needs the manifest, the bundle, the inspector and the images.
rm -rf "$DEST_DIR"
mkdir -p "$DEST_DIR"
tar -C "$PLUGIN_DIR" -cf - manifest.json README.md bin ui imgs | tar -C "$DEST_DIR" -xf -
echo "copied $(find "$DEST_DIR" -type f | wc -l) files"

[ -f "$DEST_DIR/manifest.json" ] || fail "install incomplete: no manifest.json in $DEST_DIR"
grep -q "\"UUID\": \"$UUID\"" "$DEST_DIR/manifest.json" || fail "installed manifest UUID does not match $UUID"
grep -q "\"CodePath\": \"bin/plugin.js\"" "$DEST_DIR/manifest.json" || fail "installed manifest has no CodePath"
[ -f "$DEST_DIR/bin/plugin.js" ] || fail "install incomplete: no bin/plugin.js in $DEST_DIR"

step "Restart"
DEEP_LINK="streamdeck://plugins/restart/$UUID"
if [ "$RESTART" -eq 0 ]; then
  echo "skipped (--no-restart)"
elif [ "$FIRST_INSTALL" -eq 1 ]; then
  # Stream Deck only discovers plugin folders at startup, and ignores the restart
  # deep link for a plugin it has never seen, so say so instead of pretending.
  echo "new plugin folder: the Stream Deck app has to be restarted once before it"
  echo "shows up. Afterwards, reload the plugin with:"
  echo "  $DEEP_LINK"
else
  if command -v winps >/dev/null 2>&1; then
    winps "Start-Process '$DEEP_LINK'" >/dev/null 2>&1 \
      && echo "restart requested via $DEEP_LINK" \
      || echo "could not open the deep link; run it yourself: $DEEP_LINK"
  else
    echo "restart it yourself: $DEEP_LINK"
  fi
fi

cat <<EOF

Installed. Select a "Soundboard Pad" action, pick a pad in the inspector, and
set the API port/token under Connection.
EOF
