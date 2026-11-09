# JockeyBox Soundboard — Stream Deck plugin

`com.alex.jockeybox.soundboard` puts JockeyBox's soundboard pads on a Stream Deck.
Each key fires one pad and lights up while that pad is playing. A second action
stops everything.

This is an **SDK 3 (Node.js) plugin**: `bin/plugin.js` is a bundled Node program
launched by the Stream Deck app, not the deprecated CEF/HTML plugin format.

| | |
|---|---|
| UUID | `com.alex.jockeybox.soundboard` |
| Actions | `…soundboard.pad` (pad key), `…soundboard.panic` (stop all) |
| Stream Deck | 7.1 or newer, Windows |
| Node runtime | the one Stream Deck bundles (manifest asks for 24; 20.5+ also works) |
| Talks to | JockeyBox local API on `127.0.0.1:17890` (configurable) |

## Install

Two ways; both end with the same folder in place:

```
C:\Users\<you>\AppData\Roaming\Elgato\StreamDeck\Plugins\com.alex.jockeybox.soundboard.sdPlugin
```

The folder name **must** equal the manifest UUID — Stream Deck ignores it otherwise.

### From WSL/Linux, one shot

```bash
streamdeck/install.sh            # npm install + build + copy + restart
streamdeck/install.sh --no-restart
```

The script builds, validates, copies the folder (without `node_modules`, `src/`
or the check harness) to the Plugins directory above, and restarts the plugin.

### With the Elgato CLI (does not copy anything)

```bash
cd streamdeck/com.alex.jockeybox.soundboard.sdPlugin
npm install
npm run build
npx streamdeck link .            # symlinks this folder into the Plugins directory
```

`streamdeck link` is convenient while developing — rebuilding `bin/plugin.js` is
enough, you only need to restart the plugin.

### Manual copy

```bash
npm install && npm run build
cp -r streamdeck/com.alex.jockeybox.soundboard.sdPlugin \
      /mnt/c/Users/<you>/AppData/Roaming/Elgato/StreamDeck/Plugins/
```

(the `node_modules`, `src` and `checks` folders are not needed at runtime — the
bundle is self-contained)

## Restart the plugin

After any change to `manifest.json`, or to reload `bin/plugin.js`:

```
streamdeck://plugins/restart/com.alex.jockeybox.soundboard
```

> **First install:** Stream Deck scans the Plugins directory only at startup, so a
> freshly copied folder is invisible until the app is restarted once (the deep
> link above is ignored for a plugin the app has never loaded). Restart Stream
> Deck once after the first `install.sh`; from then on the deep link is enough.

Paste that into a browser or Run dialog on the Windows host, or:

```bash
winps "Start-Process 'streamdeck://plugins/restart/com.alex.jockeybox.soundboard'"
cd streamdeck/com.alex.jockeybox.soundboard.sdPlugin && npx streamdeck restart com.alex.jockeybox.soundboard
```

Editing only property-inspector files (`ui/`) needs no restart: close and reopen
the inspector.

## Configure port and token

Both are **plugin-wide** (global settings), so one trip through the property
inspector covers every key.

1. Select a pad key (or a panic key) in the Stream Deck app.
2. In the inspector's **Connection** panel set:
   - **API port** — the port JockeyBox's local API listens on. Default `17890`;
     JockeyBox steps upward (17891, 17892, …) when the port is taken, so read the
     real one from the app if the default fails.
   - **API token** — the `ApiToken` value from JockeyBox settings. Sent as the
     `X-JockeyBox-Token` header, and as `?token=` on the WebSocket.
3. **Test connection** should say `connected to 127.0.0.1:<port>`.

Values are stored by Stream Deck, not hardcoded. Leaving the token empty only
works if JockeyBox itself has no API token configured.

### Bind a key to a pad

Pick a pad from the **Sound** dropdown. The list is a live datasource: the
inspector asks the plugin, the plugin asks `GET /sounds`, and the reply fills the
dropdown. Press **Refresh** after adding pads in JockeyBox. If a pad disappears
from the app, the key shows the id as `(missing)` rather than silently unbinding.

The inspector cannot call JockeyBox itself — it runs on a `file://` origin, so
the app's loopback API would be a cross-origin request. It talks to the plugin
over Stream Deck's own socket instead.

## What the keys look like

Pad key (`…soundboard.pad`):

| State | Artwork | Title |
|---|---|---|
| pad playing | blue equaliser bars, blue dot | pad name |
| pad idle | grey equaliser bars | pad name |
| nothing selected | dashed empty box with a `+` | `PICK PAD` |
| pad id removed from JockeyBox | dashed empty box | `NOT FOUND` |
| JockeyBox unreachable / token rejected | red `!` | pad name (or `APP DOWN`) |

Panic key (`…soundboard.panic`): red-ish artwork from the manifest, title
`STOP ALL`, fires `POST /stop-all`.

Behaviour:

- Keys paint from `GET /sounds` + `GET /state` when they appear, and repaint on
  every state frame from `WS /ws` — so a pad started from the JockeyBox panel
  lights up on the deck too.
- Pressing a key shows "playing" immediately (optimistic), then trusts the
  server: a state frame at least 400 ms old reverts it, and a hard 2 s timeout
  reverts it if no frame ever arrives.
- `setImage`/`setTitle` are throttled to 8 Hz per plugin run and skipped when the
  value has not changed, because hammering them grows the host's memory.
- On system wake (and on any dropped socket) the plugin re-reads state and
  repaints every visible key; the socket reconnects with backoff (0.5 s → 15 s).
- A failed press shows the red alert triangle; a successful one shows the green
  check.

## API used

All against `http://127.0.0.1:<port>`, all JSON:

| Call | Used for |
|---|---|
| `GET /sounds` | pad names, ids, hotkeys (key titles + inspector dropdown) |
| `GET /state` | which pads are playing, `anyDeckPlaying` |
| `POST /sounds/{id}/play` | pressing a pad key |
| `POST /stop-all` | panic key |
| `WS /ws` | live state frames, full state on connect |

## Development

```bash
npm install
npm run build      # rollup → bin/plugin.js (ESM, self-contained)
npm run watch
npm run checks     # offline assertions: SVG art, API paths, state machine
npm run pi         # headless smoke test of the property inspector
npm run e2e        # the real bundle against a fake Stream Deck host + fake API
npm run validate   # manifest against Elgato's schema
npm run check      # node --check bin/plugin.js
```

Layout:

```
manifest.json              Stream Deck manifest (SDKVersion 3, Nodejs 24)
src/plugin.ts              entry point: registers actions, PI bridge, wake handler
src/actions/pad.ts         pad key (appear / paint / press)
src/actions/panic.ts       stop-all key
src/bridge.ts              single source of truth: state, throttled repaint, WS client
src/api.ts                 loopback HTTP client + API errors
src/config.ts              global-settings normalisation (port, token)
src/render.ts              SVG key artwork as data URIs
ui/pad.html, pi.js, pi.css property inspector
imgs/                      static PNGs for the manifest and default key art
checks/                    offline test harness (not shipped)
bin/plugin.js              build output (single ESM bundle, no node_modules needed)
bin/package.json           `{"type":"module"}` so that bundle runs as ESM
```

Plugin logs go to **stderr**, prefixed `[jockeybox-soundboard]`; Stream Deck
collects them in its own log files. Running `node bin/plugin.js` by hand outside
Stream Deck will fail to register (there is no host socket) — that is expected.

## Troubleshooting

- **Everything shows a red `!`** — JockeyBox is not running, or the port is
  wrong. Press **Test connection** in the inspector.
- **Red `!` plus a rejected token** — copy `ApiToken` from JockeyBox settings into
  the inspector again (it is trimmed; whitespace does not matter).
- **`PICK PAD`** — the key has no `soundId`; choose a pad in the inspector.
- **`NOT FOUND`** — the pad was deleted in JockeyBox; choose another one.
- **Key does nothing and flashes red** — the HTTP call failed; check the log for
  `cannot reach JockeyBox on 127.0.0.1:<port>`.
- **Changes do not show up** — the plugin caches nothing across restarts, but the
  Stream Deck app caches the bundle: restart the plugin (link above).
