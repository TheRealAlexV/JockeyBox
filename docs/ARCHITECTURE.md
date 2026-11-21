# JockeyBox architecture

Written against `fix/deck-variant-switcher` at `4f4dc7c`. Every claim below was read from the
source; file and line references point at the code that implements it.

## 1. What it is and how it runs

JockeyBox is a dual-deck karaoke and DJ player: one WPF process on .NET 8 (`net8.0-windows`, `UseWPF`), built from `src/JockeyBox/JockeyBox.csproj`. The entry point is `JockeyBox.App` (`src/JockeyBox/App.xaml.cs`). It runs the Velopack lifecycle hooks first (`App.xaml.cs:26`), refuses a capture that points at the live profile (`App.xaml.cs:33`, `Selftest/CaptureHomeGuard.cs`), boots the library database (`Data/LibraryBoot.Boot()`), then lets WPF open the main window through `StartupUri="Views/MainWindow.xaml"` (`src/JockeyBox/App.xaml:4`). That window hosts two deck cards, the crossfader strip, a tabbed browser, the soundboard, the activity console and the folders strip. Audio is rendered by one engine on one render thread, not one player per stem.

## 2. Process and window layout

View first, view model second, thin code-behind. `Views/MainWindow.xaml` is 3137 lines of layout;
`ViewModels/MainViewModel.cs` is 4532 lines of state. Both are partial types split by feature.

Main window regions, by name in `Views/MainWindow.xaml`: Deck A and Deck B cards, bound as
`ContentControl Content="{Binding DeckA}"` and `DeckB` (`:1058`, `:1066`), each a `DeckViewModel`
(`ViewModels/DeckViewModel.cs:24`); the crossfader strip with `CrossfaderSlider` (`:1173`); the
browser `TabControl` (`:1607`), styled `BrowserTabControl`; the soundboard panel (`:940`); the
activity console, headed `ACTIVITY` (`:2305`); and `ShellScroll`, a root `ScrollViewer` so a tall
state scrolls instead of clipping (`:994`).

`Views/SettingsWindow.xaml` is a separate 660x720 window (`:4`) with its own `MainOutputBox` and
device pickers (`:120`). Other windows: `SetupWindow`, `LyricsWindow`, `YouTubeSearchWindow`,
`SpotifySetupDialog`, `YouTubeAuthDialog`, `MoveTrackDialog`, `RenameTrackDialog`.

**Partial view model convention.** One feature slice per file, each declaring
`partial class MainViewModel` in `ViewModels/`. The base `MainViewModel.cs` holds shared state,
library and deck construction (`:125`). The twelve slices beside it:

`MainViewModel.Audio2.cs` master volume, output delay, transitions, beat analysis, soundboard.
`MainViewModel.Cues.cs` cue plumbing shared by both decks. `MainViewModel.Cues.Inputs.cs` cue recall
and capture by slot number. `MainViewModel.Inputs.cs` hotkeys and the loopback HTTP endpoints.
`MainViewModel.KaraokePair.cs` one song on both decks, instrumental A, original B.
`MainViewModel.LibraryPerf.cs` `--perf` measurement hooks. `MainViewModel.Mic.cs` single microphone
state. `MainViewModel.Mics.cs` multiple microphone state. `MainViewModel.PlaylistOrder.cs` playlist
ordering. `MainViewModel.RemoteLists.cs` remote playlist targets. `MainViewModel.Transport.cs`
transport commands. `MainViewModel.Ui.cs` UI state.

## 3. Audio graph

`AudioEngine` (`src/JockeyBox/Audio/Graph/AudioEngine.cs`) owns one clock and one render thread and
feeds six named buses (`AudioBus`, `Audio/Graph/AudioBus.cs`) from the same 480-frame block. The
routing model is stated in the class comment (`AudioEngine.cs:44`): Main (decks plus soundboard when
it has no device, never microphone), KaraokeAid (the original stems in Karaoke mode, an undelayed
operator cue), Recording (sum of unmuted recording devices), Soundboard (alone, when it has a
device), Monitor (Monitor-flagged recording devices, never delayed, never on Main), and Bridge
(Main's deck content plus soundboard plus unmuted microphones).

The two decks are `DeckA` and `DeckB` of `DeckSource` (`Audio/Graph/DeckSource.cs`), each holding a
primary, karaoke and vocals reader (`:90`). Each is wrapped by `DeckChannel`
(`Audio/DeckChannel.cs`) and addressed by `DualDeckTransport` (`Audio/DualDeckTransport.cs:46`).

**Volume and crossfade.** One `FaderEnvelope` (`AudioEngine.cs:159`); `SetFader` snaps it (`:518`),
`RampFader` ramps it in exact frames (`:522`), and deck gain goes through `CrossfaderGain` (`:144`),
so the fade is sample accurate rather than quantised to a UI timer.

**Karaoke stem path.** `DeckSource` sums its stems *before* the split renderers (`DeckSource.cs:42`)
and switches mode through `SetStemMode`, driven by `AudioEngine.KaraokeSplit` (`AudioEngine.cs:604`).
`StemKind` (`DeckSource.cs:20`) is Mix, Karaoke or vocals only. In Karaoke mode the instrumental goes
to Main and the original to KaraokeAid, which is why the cue bus is pre-delay.

**Output device selection.** `AudioRoute` (`AudioEngine.cs:14`) holds one device id per bus, null
meaning None; `SetRouteAsync` (`:410`) applies it. `DeviceService` (`Audio/DeviceService.cs`)
enumerates WASAPI endpoints, keeping render and capture as separate lists on purpose (`:17`) so a mic
is never offered as an output, and reporting a stored id that no longer resolves as missing rather
than silently substituting (`:20`). Per-bus delay offsets are settings (`Settings/AppSettings.cs:78`).

## 4. Playback and tempo

`DualDeckTransport` (`Audio/DualDeckTransport.cs`) is the transport abstraction. It holds per-deck
`PlaybackDeck` state, `DeckScope` (active deck or both, `:30`) and `SyncActive` (`:581`). Sync is a
master/slave link: a master that runs out of media takes the slave with it (`:976`). The transport
owns no audio; it translates view model commands into engine calls.

Tempo is `TempoStage` (`Audio/Graph/TempoStage.cs:33`), a SoundTouch key-locked stretcher. It bypasses
at ratio 1.0 (`:13`), measures group delay into `LatencyTable` and interpolates it (`:17`), and adds
`LatencyFrames` so a stretched deck aligns with a bypassed one. Reader exceptions propagate on the
render thread instead of being read as end-of-track (`:24`). Beat data lives in `Audio/Beat/`:
`BeatGrid`, `BeatGridStore`, `BpmAnalysisService`, `AnalysisBackfill`.

## 5. Library and persistence

Per-user state lives in `%APPDATA%\JockeyBox`, or in `JOCKEYBOX_HOME` when set (`Tools/AppPaths.cs:18`,
`:24`). Four JSON files plus a database:

- **`settings.json`**, owned by `AppSettings` (`Settings/AppSettings.cs:47`, path `:468`). Holds roots,
  layout, per-device delays, transition mode and the Spotify secret encrypted with DPAPI. **Declares a
  version**: `SettingsSchemaVersion`, default 3 (`:97`), migrated to 4 (`:648`).
- **`library.json`**, owned by `TagCache` (`Library/TagCache.cs:34`, path `:49`). A dictionary of cached
  tags keyed by path. **Declares a version**: `CacheVersion = 2` (`:39`), stored per entry (`:15`).
- **`playlist-cache.json`**, owned by `PlaylistMembershipCache` (`Sources/PlaylistMembershipCache.cs:32`,
  path `:50`). Resolved playlist membership with three stamps: library generation, store revision and
  file stamp (`:18`). **Declares a version**: `SchemaVersion = 1` (`:34`). Deleting it costs one
  resolution pass.
- **`track-metadata.json`**, owned by `TrackMetadataStore` (`Metadata/TrackMetadataStore.cs:150`, `:186`).
  Per-track offsets and metadata. **Declares a version**: `SchemaVersion = 1` (`:176`).

So **all four declare a version**, not only `track-metadata.json`. Separately, `library.db` is SQLite
(`Data/JockeyBoxDatabase.cs:36`) migrated with `PRAGMA user_version` (`:122`), and `Data/LibraryBoot.cs`
boots it and imports legacy JSON.

## 6. Local API

`LocalApiServer` (`src/JockeyBox/Api/LocalApiServer.cs`) is an `HttpListener` bound to loopback only.
Default port 17890 (`:35`). If it is busy the server walks upward over twelve candidates
`port + attempt` (`:95`), each with a fresh listener because http.sys leaves a refused instance
unusable (`:100`), adding the prefix `http://127.0.0.1:{candidate}/` (`:104`). The base port comes from
`--api-port`, else `JOCKEYBOX_API_PORT`, else the default (`Api/ApiPortOption.cs:9`, `:20`).

**Auth is opt-in.** The token is passed to `Configure` (`LocalApiServer.cs:42`); when empty there is no
token check, and the code logs that consequence (`:124`). When set, requests must carry
`X-JockeyBox-Token`.

**Origin/Referer rejection.** Any request carrying `Origin` or `Referer` is refused (`:334`, `:337`)
before routing, because a page the operator merely opened could otherwise drive the surface. Browsers
attach those headers; a native client does not.

Routes, dispatched from `HandleAsync` (`:171`):

| Method | Path | Does |
|---|---|---|
| GET | `/sounds` | List soundboard pads (`:191`) |
| GET | `/state` | Current `ApiState` (`:197`) |
| POST | `/sounds/add` | Add a sound (`:203`) |
| POST | `/sounds/{id}` | Play one pad (`:209`) |
| POST | `/stop-all` | Stop everything (`:221`) |
| GET | `/cues` | Both cue banks, A-B and sync state (`Api/CueApiRouter.cs:71`) |
| POST | `/cue/{a\|b}/{slot}/recall` | Jump to a stored cue (`:72`) |
| POST | `/cue/{a\|b}/{slot}/capture` | Store current position (`:73`) |
| POST | `/transport/{a\|b}/playpause` | Deck play/pause (`:74`) |
| POST | `/transport/ab` | A-B link on or off (`:75`) |
| POST | `/transport/sync/{deck\|volume\|tempo}` | Sync toggles (`:76`) |

`CueApiRouter.Handles` gates the `/cues`, `/cue/` and `/transport/` prefixes (`:94`).

**WebSocket `/ws`.** A client gets one immediate `{"type":"state","state":{...}}` frame
(`LocalApiServer.cs:406`) and then the server only reads and discards: `ReceiveAsync` in a loop, no
dispatch (`:410`). **Inbound WS frames are ignored; commands are HTTP only.** The socket exists to push
state, not to accept input.

**No health or version endpoint.** There is no `/health`, `/version` or `/ping` route (no match
anywhere in `src/JockeyBox/Api/`). Nothing can check liveness or build identity over HTTP.

## 7. Stream Deck plugin

Source is `streamdeck/com.alex.jockeybox.soundboard.sdPlugin` (manifest UUID
`com.alex.jockeybox.soundboard`, `manifest.json:3`). Two actions: `Soundboard Pad` (`:29`) and
`Panic - Stop All` (`:47`). The property inspector sets port and token once for the whole plugin
(`ui/pi.js:10` default 17890, `:24` token field, `:52` save); both can also arrive through Stream Deck's
settings event (`:222`). It reaches the app over loopback HTTP with `X-JockeyBox-Token` and drives pad
playback and stop-all, not transport or cues.

## 8. UI capture harness

`--uishot <file.png>` builds the real main window, lays it out and writes a PNG with software rendering
and no desktop involved (`Selftest/UiShot.cs:24`). It exists because verification must not drive the
operator's console session (mouse, focus, windows). Options include `--uishot-wait`, `--uishot-window`,
`--uishot-rows`, `--uishot-activity` and `--uishot-console` (`UiShot.cs:70`, `:73`, `:163`, `:173`, `:157`).
`CaptureHomeGuard` (`Selftest/CaptureHomeGuard.cs`) refuses a capture aimed at the live profile.

**Sandbox rule.** Verification runs in the headless sandbox at `C:\Dev\jb-sandbox\run.cmd` with
`JOCKEYBOX_HOME` set, never in the interactive desktop session. One test documents that home
(`tests/JockeyBox.Tests/YouTubeLiveShotTests.cs:24`). Do not run captures against the operator's
`%APPDATA%\JockeyBox`.

**Never run `taskkill /IM JockeyBox.exe /F`.** It has killed a live app on the operator's console
before. Confirm which process is the sandbox's, then kill by PID.

## 9. Build and deploy

Build: `build/build.sh`, which compiles with `--no-incremental` and fails unless MSBuild's summary line
is present and clean (`build/build.sh:5`). Output is `dist/portable/JockeyBox.exe`, single file and
self-contained for win-x64 (`build/build.sh:3`, `:21`).

Deploy: `scripts/deploy-portable.sh` copies everything `dist/portable` holds to the live install at `C:\Dev\JockeyBox`, additively (no delete), after checking the payload against `scripts/portable-payload.manifest`, then md5-compares every file and fails on any difference (`scripts/deploy-portable.sh:2`, `:18`, `:29`). It refuses to run while the app is running. `build/package.sh` produces the Velopack installer package; unverified, I did not read it.

## 10. Known traps

- **Shared worktrees.** 38 worktrees share this checkout. Do not switch branches or check out `dev`;
  other sessions are using them.
- **The port walk-up.** A sandbox that binds 17890 answers the operator's Stream Deck plugin in place of
  their app (`Api/ApiPortOption.cs:12`). Pin the sandbox with `--api-port` or `JOCKEYBOX_API_PORT`.
- **The token is optional.** Empty token means no auth; anything running as this user can drive the
  surface (`LocalApiServer.cs:124`).
- **`JOCKEYBOX_HOME` redirects everything.** Settings, logs, caches, database and tokens all follow it
  (`Tools/AppPaths.cs:13`); a missing variable means you are on live data.
- **Origin/Referer is refused.** A browser test or curl wrapper that adds those headers gets a 4xx and
  looks like an auth failure.
- **No health endpoint.** Liveness and build identity must come from the log, not HTTP.

## Observations

Found while reading, not fixed:

1. `Api/LocalApiServer.cs:95` walks up to 12 ports but nothing publishes the chosen port, so a client on
   the default port cannot discover that the app moved. `ApiPortOption.cs:12` notes the plugin does talk
   to the wrong process in that case.
2. `Api/LocalApiServer.cs:124` records the unauthenticated consequence in a log line only; the UI does
   not surface that the API is open.
3. `docs/` held one file (`research-2.0.md`) against 92 unreleased commits. Docs debt, no code defect.
