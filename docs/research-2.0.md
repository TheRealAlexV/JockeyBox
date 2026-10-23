# JockeyBox 2.0 — verified research notes

Collected 2026-10-23 for the 2.0 audio/mixer/playlist work. Every claim below was checked
against the packages, the docs, or the live Windows host. Unverified items are marked.

## Environment facts (verified on this host)
- App data: `C:\Users\Alex\AppData\Roaming\JockeyBox\` (WSL: `/mnt/c/Users/Alex/AppData/Roaming/JockeyBox`).
  Files: `settings.json` (2.5 KB), `library.json` (181 KB, 771 entries), `playlists.json`
  (2.42 MB, 5 272 remote track rows over 4 playlists — this is the playlist-switch latency),
  `playlist-cache.json` (65 B), `track-metadata.json`, `downloads/{queue,ledger}.json`,
  `logs/jockeybox.log` (2 MB rotate).
- Music root `C:\Users\Alex\Music` (771 files in the tag cache).
- .NET on Windows: SDK 9.0.319, runtimes 8.0.32 + 9.0.21 incl. `Microsoft.WindowsDesktop.App 8.0.32`
  → a `net8.0-windows` xunit project can be built AND run on the host.
- `dotnet build` works from WSL with `DOTNET_ROOT=$HOME/.dotnet` (8.0.423 SDK), ~7.6 s clean.
- Deploy/verify harness already on the host: `C:\Dev\jb-deploy.sh` (kill → copy → run scheduled task
  `JockeyBox-Launch` → print pid), `C:\Dev\jb-verify.ps1` (UIAutomation finds window "JockeyBox",
  dumps control tree + `C:\Dev\shots\jockeybox.png`), plus ffmpeg at
  `C:\ProgramData\chocolatey\bin\ffmpeg.exe` and `av-offset-record.ps1` (uses
  `-f dshow -i audio="virtual-audio-capturer"`) / `av-offset-analyze.ps1` for measured A/V offset.

## BPM / beat grid
- librosa **0.11.0 + numpy 2.4.6 are already installed** in the app's own venv
  `%APPDATA%\JockeyBox\uvr-venv\Scripts\python.exe` (it is a dependency of `audio-separator`).
  No new Python dependency is needed.
- Measured on 4 real library tracks (`C:\Dev\bpm_probe.py`, 22050 Hz mono):
  first file 11.5 s decode + 19.4 s analysis (cold JIT); after that 0.44-0.97 s decode +
  0.77-1.42 s `beat_track`. → budget ~2 s/track warm, one long batch process.
- `librosa.beat.beat_track(onset_envelope=..., sr=..., start_bpm=128, tightness=100, trim=True,
  units="time")` → `(tempo_ndarray, beats_seconds)`. `librosa.feature.tempo(aggregate=None)`
  gives per-frame tempo for the 70-vs-140 (octave error) decision (`p10`/`p90`).
- Octave errors are THE failure mode; store `bpm`, `p10`, `p90`, `first_beat`, `confidence`, and
  expose ×½ / ×2 in the UI. `start_bpm` is a policy knob (use the library median).
- No maintained OSS package gives downbeats. Approach: assume 4/4, take `beat_track` beats,
  pick the bar phase with the strongest low-band (30-120 Hz) onset energy per 4-beat window.

## Tempo / time-stretch
- `SoundTouch.Net.NAudioSupport.Core` **2.3.2** restores cleanly against NAudio 2.2.1 (verified by
  `dotnet add package` in a scratch project). It depends on `SoundTouch.Net 2.3.2` + `NAudio.Core 2.0.0`.
  Use `SoundTouchWaveProvider` (needs 32-bit float; `ISampleProvider.ToWaveProvider()` produces it).
  `Tempo` = time-stretch with pitch preserved (key lock). `Rate` = vinyl (pitch changes).
  Runtime `Tempo` changes are supported without re-init; ramp in ~20 ms steps to avoid a glitch.
- Frozen since 2021, LGPL-2.1 → keep it behind our own `ITempoStage` seam and ship the licence text.
  Escape hatches: `SoundTouchSharp` 2.3.2 (MIT wrapper, LGPL native, 2024) or Rubber Band 4.0
  (GPL/commercial — best quality, would need our own P/Invoke).
- Vendor latency ≈100 ms max at 44.1 kHz; combine with a 40-60 ms `WasapiOut` buffer.
- `WdlResamplingSampleProvider` (in NAudio) is the resampler to use. `SmbPitchShiftingSampleProvider`
  is pitch-only and metallic — not for tempo.

## NAudio 3.x / multi-device facts
- NAudio is 2.2.1 here; NAudio 3.x targets **net9.0 only** — not usable until the .NET upgrade.
- Each `WasapiOut` owns a private `AudioClient` and its own clock; WASAPI has no cross-endpoint clock
  and Windows has no aggregate device. Author (NAudio #736): use low latency, start together, and
  correct with a provider that knows the other devices' positions.
- `WasapiLoopbackCapture` only fires while audio is actually playing; `WasapiCapture` is for inputs.

## OBS routing verdict (drove the mic-scope change)
- OBS "Application Audio Capture (BETA)" uses `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK` with
  `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` on the pseudo-device
  `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`. **No endpoint is selected; it captures every render
  stream of the process tree on every device** (obs-studio issue #7922). Endpoint mute is not honoured
  (#11761). There is no per-session/per-device exclusion in OBS.
- Consequence: two render sessions (program + monitor) from the same process = doubled music in OBS.
  If the app ever needs a different local mix than the broadcast mix, OBS must capture a *device*
  (virtual cable → OBS "Audio Input Capture"), not the process.
- With the mic moved to OBS (decided 2026-10-23), the app renders one program mix; per-deck extra
  sends are optional and off by default. Keep the app to a single program session unless the user
  explicitly enables another output, and document the OBS consequence in the settings UI.

## SQLite
- `Microsoft.Data.Sqlite` **8.0.32** + explicit `SQLitePCLRaw.bundle_e_sqlite3` **2.1.14** (SQLite
  3.54.0, FTS5 enabled). Verified restorable from WSL. MDS auto-calls `Batteries_V2.Init()`.
- Single-file publish: `IncludeNativeLibrariesForSelfExtract=true` (already in `build.sh`) is the
  supported path for the native `e_sqlite3.dll` (dotnet/efcore#23385).
- Put the DB in `%APPDATA%` (WAL needs `-wal`/`-shm` siblings; not next to a Program Files exe).
- Open-time pragmas (idempotent): `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`,
  `cache_size=-65536`, `mmap_size=268435456`, `temp_store=MEMORY`. `DefaultTimeout=30`,
  `Cache=Private` (never shared cache), pooling on.
- One long-lived writer connection behind `SemaphoreSlim(1)`; readers per operation (pooled).
  MDS async is fake — do work on `Task.Run`.
- Bulk: one reused command in `BEGIN IMMEDIATE` … `COMMIT`, 1-5k rows per commit; `MAX_VARIABLE_NUMBER=32766`.
  Incremental: `INSERT … ON CONFLICT(path_key) DO UPDATE SET …` with `excluded.*`; scan generation in
  `last_seen_utc`, then `is_missing=1` for the rest.
- FTS5 external-content table + AI/AD/AU triggers for search; `unicode61 remove_diacritics 2`.
  `COLLATE NOCASE` is ASCII-only → store normalised sort columns for non-ASCII artists.
- Sub-16 ms view switching is NOT a DB problem: keep an immutable in-memory snapshot of rows,
  swap it in one volatile field, filter over an array. SQL only for cold loads and search.

## Current code map (for the rewrite)
- `Audio/AudioDeck.cs` — one stem = one `MediaFoundationReader` + one `WasapiOut` + `Stopwatch` clock;
  `ArmAt/ BeginRender / Pause / Stop / SetPosition`; `DelayMs` prepends silence
  (`SilencePrependSampleProvider`).
- `Audio/DeckChannel.cs` — owns primary + karaoke `AudioDeck`, `DeviceId`, `DelayMs`, `Volume`,
  `FaderGain` (→ `ApplyVolumes`), `HasKaraoke`, `ReachedEnd` (`Elapsed >= EndPosition`), `StartAt`, `Seek`.
- `Audio/DualDeckTransport.cs` — 16 ms `DispatcherTimer` crossfade, `SetCrossfader`,
  `CrossfadeToDeck(deck, 2 s)`, equal-power `cos/sin`, `ActiveDeck` drives the shared transport.
- `Audio/DeviceService.cs` — `GetOutputDevices()` / `ResolveDevice(id)` (render endpoints only,
  falls back to the default endpoint). No input/capture code exists anywhere in the app.
- `ViewModels/MainViewModel.cs` — `_uiTimer` 33 ms drives `OnUiTick` (readouts, lyrics, crossfader
  mirror, end detection → `AdvanceQueueAfterEndAsync`); `StartQueueItemAsync` → `StartDeckAfterLoad`
  → `PlayDeck` + `CrossfadeToDeck` (starts playback IMMEDIATELY on queue); `LoadTrack` never plays.
- `ViewModels/DeckViewModel.cs` — `Devices`, `SelectedDevice` → `_channel.DeviceId`, `DelayMs`,
  `Volume`, `KaraokeGain`, `Waveform`/`KaraokeWaveform` (`List<float>`, 1000 points),
  `WaveformProgress`, 300 ms resync debounce.
- `Views/MainWindow.xaml` — `DeckCardTemplate` lines 22-173; decks side-by-side 361-381
  (col 0 / 12 px / col 2); crossfader card 383-397 (`Value="{Binding Crossfader, Mode=TwoWay}"`);
  transport card 399-499; root rows 329-336; queue tab 946-1138.
- `Views/MainWindow.xaml.cs` — `DrawDeckWaveform` 433-466, `RenderWaveform` 468-502 (Polyline +
  Polygon), playheads 420-428, `_deckWaveforms` 385.
- `Sources/PlaylistStore.cs` — `ObservableCollection<UserPlaylist> Playlists`, synchronous `Load()` in
  ctor + `Save()` on every mutation (2.4 MB rewrite), `AddTracks` (imported only),
  `AddLibraryTracks` (internal only, needs `TrackEntry`).
- `Sources/SourceTrack.cs` — `State`, `LibraryPath` (`[JsonIgnore]`), `LocalKey` (internal identity),
  `StateGlyph`. Remote-tab "in library" test = `LibraryLedger.FindPathByLibraryKey` (linear scan under
  a global lock, per row) + `File.Exists`.
- `Library/KaraokeNaming.cs` (`Marker=".karaoke"`, `KaraokePathFor`), `TrackEntry.KaraokePath`,
  `HasBoth`, `NeedsKaraoke`, `KaraokeStatus` (✓/⛔/⏳/⚙/✖).
- `Library/LibraryIndex.cs` — immutable snapshot with artist+title / title / filename dictionaries +
  `Resolve(SourceTrack)`, `Contains`. Not currently reachable from the Spotify/YouTube view models.
- `Library/TagCache.cs` (`library.json`, key = absolute path + size + mtime), `Metadata/TrackMetadataStore.cs`
  (`track-metadata.json`, key = normalised relative key, 1 s debounce).
