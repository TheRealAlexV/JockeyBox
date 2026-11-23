# JockeyBox Virtual ASIO

A user-mode, in-process COM **ASIO driver**: a DLL a host enumerates, opens and
pulls buffers from. No kernel component, no WDK, no test signing. This is the
same shape ASIO4ALL and FlexASIO have.

v1 carries audio one way: the JockeyBox app writes into the transport ring and
this driver reads it into its ASIO **inputs**. obs-asio records those inputs;
that is the delivery path. The ASIO **outputs** stay silent — this is not a
playback path. 2 inputs / 2 outputs at 48 kHz, 128/256/512/1024 frame host
buffers, a real clock thread paced by the host buffer size.

## Audio path

| Piece | Value |
| --- | --- |
| Section name | `Local\JockeyBox.Bus.<name>` (same session as the app) |
| Bus name | env `JOCKEYBOX_ASIO_BUS`, default `main` |
| Layout | `src/JockeyBox.Ipc/BusLayout.cs` — the single source of truth |

`src/RingReader.h` transcribes the documented header (magic `0x5355424A`,
version 1, header 1024 bytes, payload at 1024, interleaved float32) and pins it
with `static_assert`s on the shared atomics. Header geometry that does not
validate is rejected, and the driver falls back to silence.

Per buffer switch the reader streams exactly `bufferSize` frames, as one
contiguous float stream, crossing ring-block boundaries wherever they fall:

- The cursor is (block index, intra-block frame offset). It only moves forward,
  it is anchored to the release-published `writeBlockIndex`, and it never waits
  for a block boundary. A 512-frame host buffer on a 480-frame producer streams
  indefinitely with no laps; the two sizes have no arithmetic relationship and
  none is required. The callback never blocks, never allocates and never throws.
- Silence is served only when the writer has not published the next block yet,
  and the reader slot's `underruns` counter ticks **once for that callback**.
- Falling further behind than the ring can hold (the cursor's block has been
  overwritten) means a jump to the newest published block and one `corrections`
  tick. That is the only correction path; block-boundary arithmetic never causes
  one.
- One reader slot is claimed (`active = 1`, `readBlockIndex`, `corrections`,
  `underruns`), so the app can see a live reader.

`readBlockIndex` is the block **currently being consumed**. The intra-block frame
offset is not published, so across callbacks that fall inside one block the index
stands still and then advances by one per whole block consumed. Reading it as
"blocks consumed" still holds; it lags by the intra-block position, never by
more than one block.

On attach the reader joins the stream behind the writer instead of exactly at the
newest block: `3 * bufferSize` frames, raised to at least three ring blocks and
capped two blocks below the ring's capacity (so the cursor's slot can never be
the slot the writer is about to `memcpy`). The cushion is required, not cosmetic
— the writer publishes in whole blocks, so a reader sitting one block behind is
exactly at the writer's own block-quantisation amplitude and starves at every
wrap. The chosen cushion is logged once at attach (debug output).

`getLatencies` reports that cushion honestly: input latency is
`bufferSize + cushionFrames`, where `cushionFrames` is the effective value
`RingReader::attach` used, so the number follows the cushion if it changes. The
output path is a virtual sink the driver never writes, so it reports one buffer.

The reader also runs a one-frame rate trim. When a bus shares a route with a
physical device the writer is paced by that device's WASAPI clock while the
reader runs on QPC, so the two clocks differ by a fixed ppm. The trim measures
that difference from the mean lag over a span and slips the cursor by a single
input sample (never more) at a persistent rate, so the lag stays bounded instead
of creeping to empty or to a dropped block. At an exact rate match the measured
drift stays inside half a block and the trim performs no slips at all. A separate
`trims` counter in the reader slot reports the count; it is not a correction and
does not touch the corrections counter.

If the section is absent at `start()`, the driver serves silence and retries
attaching at most **once per second** from the clock thread, never per callback.
`getErrorMessage` reports which bus it is waiting for. A `JOCKEYBOX_ASIO_BUS`
longer than the 63 characters the ring namespace accepts is reported distinctly
(it never silently falls back to `main`), so the host can see why the bus will
not attach.

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

Output: `out\JockeyBox.Asio.dll`, `out\AsioHostHarness.exe`,
`out\buswriter\BusWriterCli.exe`.

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

It then proves the audio path against a section it creates with the documented
layout:

1. **Bit-exactness** — a block is published only once the driver has consumed
   the previous one, and every buffer that reached the host is compared sample
   by sample, bit for bit, against the block that should be there.
2. **Gap** — the writer stops mid-run; later buffers must be exactly zero and
   the reader slot's `underruns` must have risen.
3. **Cadence under load** — a writer streams at 375 blocks/s for two seconds
   while the callback rate is measured.
4. **Cross-language** — `BusWriterCli.exe` (the real
   `JockeyBox.Ipc.VirtualBusWriter`) writes the same ramp and the same
   assertions run against it.

Measured on this box, 128 frames @ 48 kHz:

```
bufferSwitch      : 377 calls in 1.004 s
cadence           : 375.5 callbacks/s (expected 375.0)
bit-exact blocks  : 64/64 matched, 0 mismatched
gap -> silence    : yes (underruns 0 -> 45)
cadence streaming : 375.2 callbacks/s
cross-language    : ran (n matched, 0 mismatched, n silence)
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
