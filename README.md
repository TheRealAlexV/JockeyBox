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
- **Spotify and YouTube** - browse Spotify playlists and Liked Songs, search and
  import YouTube videos, playlists and channels, download into the Music folder
  with a layout preset, or play a remote track straight from the stream cache.

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

## Spotify and YouTube

Two remote sources sit next to the local library. Spotify supplies playlist
metadata only; the audio for a Spotify track is found on YouTube. Downloads
land in the Music folder and follow the layout rules below, so they pair with
their karaoke version by relative path like any other file.

### Spotify playlists

Settings → **Login with Spotify** opens the browser and completes a PKCE
authorisation against a loopback redirect (`http://127.0.0.1:8888/callback`).
Tokens are stored encrypted (DPAPI) in `settings.json` and refresh on their
own.

- The built-in client ID is Spotify's public web-player client and logs you in
  with no setup. It does not grant the playlist scopes, so the import screen
  needs your own app: create one at developer.spotify.com, add the redirect URI
  above, and paste its client ID into Settings → **Spotify Client ID
  (optional)**.
- Scopes requested: `user-read-private`, plus `playlist-read-private`,
  `playlist-read-collaborative` and `user-library-read` for the import screens.
  A login from before those were granted shows a "Grant playlist access"
  prompt.
- **Liked Songs** appears as a synthetic playlist with a track count.
- **Download all** queues the tracks of the selected playlist that are not in
  the library yet and leaves the rest alone; it says so when there is nothing
  to do. The **Download** button on a row queues a single track.
- A track is matched by artist and title against the download ledger, so a file
  you already own, or one downloaded earlier, is marked in-library and is not
  fetched again. The **Skip tracks already in the library** setting applies the
  same check to the queue in general.
- Downloaded playlists are remembered. **Sync playlists** re-reads them and
  queues only the tracks added since the last run.

### YouTube

The YouTube tab searches through yt-dlp and lists title, channel and duration.
Play, Download and + Add to playlist act on the selected row. Playlists are
kept by JockeyBox, so adding a video to one changes nothing on YouTube.

A URL pasted into the search box and sent through **Import** is treated as an
import:

- a video URL adds that one track to the selected playlist
- a playlist URL imports every entry into a new playlist named after it
- a channel URL reads the channel's uploads and stops at 500 entries

Audio is resolved by yt-dlp as the best available stream and converted to MP3
by ffmpeg, or kept as the original m4a when the bitrate is set to copy. Neither
binary has to be installed by hand: on first use JockeyBox downloads yt-dlp and
the ffmpeg essentials build into `%APPDATA%\JockeyBox\tools`. Settings → Tools
takes explicit paths when you would rather use your own copies, and the
Downloads window has **Install tools** and **Update yt-dlp** buttons.

If YouTube answers with **"Sign in to confirm you're not a bot"**, set
Settings → **Browser cookies** to the browser you are signed into YouTube with.
yt-dlp then reads that browser's session.

### Playing before downloading

Clicking a Spotify or YouTube track that is not in the library does not require
a download first. The resolver looks for the track in this order: a file
already attached to it, the library index, the download ledger, a warm stream
cache entry, and finally a fresh best-audio fetch. The fetch runs through the
same download queue as a stream item and lands in the stream cache instead of
the library.

The cached file is opened by Deck B like any local file, so device routing,
per-deck delay and lyrics behave as they do for a library track. Nothing is
tagged or written into the Music folder until you press Download.

The cache is capped at the **Stream cache size** setting in megabytes (2048 by
default) and evicts the least recently used files when it fills. **Clear stream
cache** in the Downloads window empties it.

### Download layout

A preset, or a custom template that overrides it, decides where a downloaded
file goes:

- **Artist / Album / Track** - `Artist\Album\NN Title.mp3`
- **Artist / Track** - `Artist\Title.mp3`
- **Playlist / Artist - Track** - `Playlist\Artist - Title.mp3`

A custom template such as `{artist}/{album}/{track} {title}` takes the same
tokens: `{artist}`, `{title}`, `{album}`, `{track}`, `{playlist}` and
`{source}`. Slashes become folders, `.mp3` is appended when the last segment
has no extension, and a name that is already taken gets a ` (2)` suffix instead
of overwriting the file. Missing values fall back to `Unknown Artist`,
`Unknown Album` and `Downloads`.

The Karaoke folder mirrors the relative path the download produced, so the
pairing rules above apply to downloaded tracks too.

### Settings

- **MP3 bitrate** - 128, 192, 256 or 320 kbps, or copy the original m4a stream
- **Concurrent downloads** - how many files are fetched at once
- **Browser cookies** - Chrome, Edge or Firefox, for the YouTube bot check
- **Stream cache size** - space reserved for streamed tracks, in MB
- **Skip tracks already in the library** - leaves existing files alone
- **Also generate karaoke versions after a batch** - runs the vocal removal
  engine on new downloads once the queue drains
- **yt-dlp path / ffmpeg path** - optional, overrides the managed copies

### Legal and etiquette

JockeyBox downloads audio from YouTube only, through yt-dlp. Spotify is
contacted strictly for playlist metadata through the official Web API and its
audio streams are never touched. Whether you have the right to keep a given
track is on you, and the terms of service of both platforms apply.

## Building (on WSL)

Requires the .NET 8 SDK on WSL (`https://dot.net/v1/dotnet-install.sh --channel 8.0`).

```bash
build/build.sh        # -> dist/portable/JockeyBox.exe  (single-file portable)
build/package.sh 1.5.0  # -> dist/installer/JockeyBox-win-Setup.exe (Velopack)
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
