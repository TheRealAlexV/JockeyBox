# JockeyBox

Dual-deck audio player for live karaoke processing. Deck A plays the
instrumental (karaoke) version, Deck B the full original - perfectly synced,
each routed to its own audio device with independent millisecond delay
compensation, plus synchronized scrolling lyrics.

Built with **.NET 8 / WPF / NAudio** - compiled on WSL, runs on Windows 10/11.

---

## Features

- **Two decks, one transport** - Play / Pause / Stop / FF / RW / seek bar drive
  both decks simultaneously. Every transport action is a hard resync point
  (both WASAPI outputs are re-primed sample-accurately), so the decks cannot
  drift apart across pauses or seeks.
- **Independent device routing** - each deck has its own WASAPI output device
  dropdown (e.g. Deck A -> main speakers, Deck B -> headphones).
- **Per-deck delay (ms)** - positive delay plays that deck later, negative
  earlier (range +/-5000 ms, sample-accurate, survives seeking). Use it to
  line the audio up with external visual/vocal processing chains.
- **Unified track browser** - configure a **Music folder** (your full library,
  plays on Deck B) and a **Karaoke folder** (instrumentals, plays on Deck A)
  in Settings. Files pair by identical relative path (extension may differ).
  The browser shows one list (Title / Artist / BPM / Duration from ID3 tags
  via TagLib#) with a status icon per track: `✓` has a karaoke version,
  `⭕ needed` still needs one. A FileSystemWatcher keeps the list live; tag
  reads are cached so rescans are instant.
- **Built-in vocal removal (UVR)** - right-click any track marked `⭕ needed`
  and choose **Make Karaoke Track**. The app strips the vocals with an
  Ultimate Vocal Remover AI model (via the audio-separator engine) and writes
  the instrumental into the Karaoke folder at the same relative path.
  Generation runs in a background queue with live progress in the browser row;
  the first use walks you through a one-click engine install.
- **Lyrics** - resolved per track from four sources, in priority order:
  1. A `.lrc` sidecar file next to the audio file
  2. **Spotify Color Lyrics** — direct from Spotify's internal API using your
     OAuth token (no self-hosted server needed). Just click "Login with Spotify"
     in Settings and authorize your account — lyrics work immediately.
  3. Your self-hosted [spotify-lyrics-api](https://github.com/akashrchandran/spotify-lyrics-api)
     (optional — only if you have it running with an SP_DC cookie)
  4. [LRCLIB](https://lrclib.net) — free, zero configuration
  Line-synced lyrics scroll and highlight against the master playback clock.

## Layout

```
Music\   Artist\Song.mp3     -> Deck B  (source of truth)
Karaoke\ Artist\Song.flac    -> Deck A  (generated; same relative path,
                                         extension may differ)
```

Supported formats: mp3, m4a/aac, wav, flac, wma (Windows Media Foundation).

## Vocal removal engine

"Make Karaoke Track" uses [audio-separator](https://github.com/nomadkaraoke/python-audio-separator),
the headless engine for the [Ultimate Vocal Remover](https://github.com/anjok07/ultimatevocalremovergui)
model family. The app manages everything itself in `%APPDATA%\JockeyBox`:

- `uvr-venv\` - a private Python virtual environment with audio-separator
- `uvr-models\` - UVR model files, downloaded automatically on first use

Requirements: **none to preinstall** - the setup checks for Python 3.10-3.13
and, if missing, offers to install Python 3.12 automatically (winget when
available, otherwise the official python.org installer - silent, per-user, no
admin rights). Everything else is automatic too: the first right-click →
Make Karaoke Track offers to install the engine and streams the installer log.

Settings → Vocal Removal Engine lets you pick the model
(`UVR-MDX-NET-Inst_HQ_3.onnx` by default; `UVR_MDXNET_KARA_2.onnx` keeps
backing vocals; BS-Roformer for maximum quality), the output format
(FLAC/MP3/WAV), an explicit python.exe, and an optional NVIDIA CUDA build of
the engine (~3 GB, requires CUDA 12 + cuDNN 9).

## Building (on WSL)

Requires the .NET 8 SDK on WSL (`https://dot.net/v1/dotnet-install.sh --channel 8.0`).

```bash
build/build.sh        # -> dist/portable/JockeyBox.exe  (single-file portable)
build/package.sh 1.4.0  # -> dist/installer/JockeyBox-win-Setup.exe (Velopack)
```

Both cross-compile for win-x64 from Linux via `EnableWindowsTargeting`.

## Running on Windows

- **Portable:** copy `dist/portable/JockeyBox.exe` anywhere on the Windows
  side and double-click it.
- **Installer:** run `dist/installer/JockeyBox-win-Setup.exe`; it installs
  per-user and creates Start Menu / Desktop shortcuts.

First run: open **Settings**, point the **Music folder** and **Karaoke folder**
at your library, then pick each deck's output device. Settings persist in
`%APPDATA%\JockeyBox\`.

## Lyrics configuration (optional)

Out of the box lyrics come from LRCLIB (no setup). For the Spotify pipeline:

1. Deploy the API: `cd deploy && echo "SP_DC=your_sp_dc_cookie" > .env && docker compose up -d`
   (get `sp_dc` from open.spotify.com cookies while logged in).
2. Create a free app at https://developer.spotify.com/dashboard to get a
   Client ID/Secret (used only for track search).
3. In JockeyBox: Settings -> fill in Lyrics API base URL
   (e.g. `http://server:8091`), Client ID and Secret.

## Security notes

- The Spotify **client secret is encrypted at rest** with Windows DPAPI
  (current-user scope) inside `%APPDATA%\JockeyBox\settings.json`; legacy
  plaintext values are migrated to encrypted form on first load.
- The self-hosted lyrics API has **no authentication** - anyone on your LAN
  can query lyrics through it (it never exposes your SP_DC cookie). Keep it
  on a trusted network segment or bind it to localhost + a reverse proxy.
- Track IDs returned by Spotify search are validated (base62) before being
  placed into lyrics API URLs; all user/tag-derived query values are
  URL-encoded; HTTP responses are size-capped (5 MB) and time-limited (10 s).
- Symlinks/junctions inside the library are not followed (guards against
  cycles); `.lrc` sidecars larger than 2 MB are ignored.

## Architecture

```
src/JockeyBox/
  Audio/      AudioDeck, SyncTransport (master clock), DeviceService,
              SilencePrependSampleProvider (sample-accurate delay)
  Library/    LibraryScanner (pairing), TagCache, LibraryWatcher
  Lyrics/     LyricsService + providers (sidecar / spotify-lyrics-api / LRCLIB),
              LrcParser
  ViewModels/ MainViewModel, DeckViewModel (MVVM, CommunityToolkit)
  Views/      MainWindow, SettingsWindow
  Themes/     Dark.xaml (Rekordbox-style dark theme)
```

Delay model: `deck read position = master position - deck delay`; when the
result is negative the gap is filled with generated silence. Both outputs are
primed while stopped and started back-to-back, so device start jitter is
minimal (single-digit ms; trim any constant offset with the deck delay) and
every pause/seek re-aligns the decks exactly.
