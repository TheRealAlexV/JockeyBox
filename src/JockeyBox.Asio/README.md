# JockeyBox Virtual ASIO

A user-mode, in-process COM **ASIO driver**: a DLL a host enumerates, opens and
pulls buffers from. No kernel component, no WDK, no test signing. This is the
same shape ASIO4ALL and FlexASIO have.

v1 is a tracer bullet: 2 inputs / 2 outputs at 48 kHz, 128/256/512 frame
buffers, zeros in every direction, and a real clock thread. It exists to prove
the ABI and the callback cadence, not to carry audio.

## No SDK code

`include/JockeyBoxAsioAbi.h` is an independent declaration of the driver ABI.
The Steinberg SDK is not vendored, linked, included or copied anywhere in this
directory, and the built DLL contains none of it. The ABI (vtable order,
signatures, struct layout, enum values) was reconciled against:

| Source | What it pinned down |
| --- | --- |
| NAudio, `src/NAudio.Asio/AsioDriver.cs` (`AsioDriverVTable` struct) | explicit slot numbers 3..23 with function-pointer signatures |
| PortAudio, `src/hostapi/asio/pa_asio.cpp` | host-side call order to getChannelInfo/createBuffers; that `asioMessage` is a **host callback**, not a driver slot |
| `asio.h` / `asiodrvr.h` (Steinberg) | ground truth for ordering and struct layout |

`static_assert`s in the header gate the struct sizes a host compiled against
the real SDK will assume, so a layout drift fails the build instead of
corrupting host memory.

### Vtable, as implemented

```
 0 QueryInterface   1 AddRef            2 Release
 3 init             4 getDriverName     5 getDriverVersion
 6 getErrorMessage  7 start             8 stop
 9 getChannels     10 getLatencies     11 getBufferSize
12 canSampleRate   13 getSampleRate    14 setSampleRate
15 getClockSources 16 setClockSource   17 getSamplePosition
18 getChannelInfo  19 createBuffers    20 disposeBuffers
21 controlPanel    22 future           23 outputReady
24 asioMessage  <-- extension, not part of the standard ABI
```

Two corrections against a commonly repeated (wrong) ordering: there is **no**
`getTimeCode` or `getTimeCodeSampleRate` slot, and `controlPanel` comes
**before** `future` and `outputReady`. Slot 24 is a JockeyBox addition; standard
hosts stop at 23 and never touch it.

## Build

Builds on the Windows host with MSVC 14.x (WSL can only drive it; MSVC cannot
read a `\\wsl.localhost` path, so copy this directory to a local Windows path
first).

```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Output: `out\JockeyBox.Asio.dll`, `out\AsioHostHarness.exe`.

## Verify

```
out\AsioHostHarness.exe            # registers HKCU, runs 1 s, unregisters
out\AsioHostHarness.exe <dll> --keep
```

The harness registers the CLSID under `HKCU\Software\Classes\CLSID\{...}` (in-proc
COM honours HKCU, so no elevation), calls `CoCreateInstance`, then runs two
passes: one that reaches slots by their documented numbers through raw vtable
pointers (this is what actually proves the ordering, since it never consults the
local C++ interface), and one through the declared interface.

Measured on this box, 128 frames @ 48 kHz:

```
bufferSwitch      : 377 calls in 1.004 s
cadence           : 375.5 callbacks/s (expected 375.0)
```

## Register for real hosts

obs-asio, BASS and NAudio enumerate `HKLM\SOFTWARE\ASIO`, which needs an
elevated write. **Not run by the build.**

```
powershell -ExecutionPolicy Bypass -File .\register.ps1 -DllPath .\out\JockeyBox.Asio.dll
powershell -ExecutionPolicy Bypass -File .\unregister.ps1
```

## Deliberate omissions in v1

- `future`, `controlPanel`, `outputReady`, `setClockSource` return `ASE_NotPresent`.
- `getClockSources` reports 0 sources (always the internal clock).
- The clock only ever fires `bufferSwitch`; `asioMessage` reports
  `kAsioSupportsTimeInfo = 0` because there is no time info to hand over.
- Buffers are allocated once in `createBuffers`; nothing allocates per callback.
