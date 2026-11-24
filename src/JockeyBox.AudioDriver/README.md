# JockeyBox Virtual Line 1 — phase 1 driver skeleton

A WDM PortCls function driver that installs **one** virtual audio cable pair,
named `JockeyBox Virtual Line 1`, as a single WaveRT capture endpoint. It exists
to prove the plumbing: the driver compiles, packages and signs into a `.sys` and
a `.cat`, and its stream object satisfies the WaveRT contract.

## What phase 1 does

- `DriverEntry` calls `PcInitializeAdapterDriver`; the adapter is a plain WDM
  driver, not KMDF.
- `StartDevice` creates one `IPortWaveRT` + `CMiniportWaveRT` factory and one
  `IPortTopology` + `CMiniportTopology` factory, registers each with
  `PcRegisterSubdevice`, and pairs wave pin 0 with topology pin 0 through
  `PcRegisterPhysicalConnection`.
- The wave filter is a **capture** pin (`KSPIN_DATAFLOW_OUT`, bridge) with a
  single data range: PCM, 48 kHz, 16-bit, stereo. The filter is registered under
  `KSCATEGORY_AUDIO`, `KSCATEGORY_REALTIME` (WaveRT) and `KSCATEGORY_CAPTURE`.
- The stream implements `IMiniportWaveRTStream`: `SetFormat`, `SetState`,
  `GetPosition`, `AllocateAudioBuffer`, `FreeAudioBuffer`, `GetHWLatency`,
  `GetPositionRegister`, `GetClockRegister`.
- **It produces silence.** The buffer handed to PortCls is non-paged memory,
  zeroed when allocated and zeroed again on every transition into `KSSTATE_RUN`.
  There is no DMA engine, no copy loop and no data source, so the audio engine
  reads zeros. Position advances from `KeQueryPerformanceCounter`, so the stream
  looks like a live device to the engine.
- The INF is root-enumerated (`Root\JockeyBoxVirtualLine`) and takes the audio
  class registration from `wdmaudio.inf` via
  `Needs=KS.Registration,WDMAUDIO.Registration` and
  `Needs=WDMAUDIO.Services`.
- `build.ps1` runs MSBuild (WDK 10.0.26100.0, `WindowsKernelModeDriver10.0`),
  `Inf2Cat /os:10_X64`, and `signtool` for the `.sys` and the `.cat`, using a
  self-signed code-signing certificate exported to `out\JockeyBoxTest.pfx`.
  The certificate is removed from the user store after export, so this lane
  leaves no certificate installed.
  MSBuild runs with `/p:SignMode=Off`; signing is done by the script because a
  file-only certificate is not in a store for the WDK's TestSign target to use.

## What phase 1 does not do

- No audio. No shared-memory ring, no section, no IOCTL to the app — that is
  phase 2 and 3.
- No render (playback) endpoint. The pair here is the capture half; the
  playback endpoint and the ring come next.
- No notification support (`IMiniportWaveRTStreamNotification`), no
  `KSPROPERTY_RTAUDIO_BUFFER_WITH_NOTIFICATION`, no packet/offload interface.
- No volume, mute or other topology nodes. The topology is two bridge pins and
  one connection.
- Nothing is installed, loaded, or registered: no `devcon install`, no
  `TESTSIGNING`, no certificate in Trusted Root or Trusted Publishers.
- No runtime endpoint add/remove. The set of pairs is fixed at install.

## Build

On the Windows host, from this directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Output in `out\`: `JockeyBox.Line.sys`, `JockeyBox.Line.cat`,
`JockeyBox.Line.inf`, `JockeyBoxTest.pfx`, `JockeyBoxTest.cer`.

The `/p:SpectreMitigation=false` switch is required on this host because the
Spectre-mitigated MSVC libraries are not installed (otherwise MSB8040 fails the
build).
