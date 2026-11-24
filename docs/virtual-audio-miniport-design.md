# JockeyBox Virtual Audio Miniport — Design Document

Kernel-mode, test-signed, zero-cost. A fixed set of named cable pairs, each a Windows playback endpoint plus a recording endpoint sharing one internal stream, exchanging audio with the JockeyBox app over a shared-memory ring.

**Status:** research complete, pre-build design. No code, no builds, no installs.
**Date:** 2025-11-23
**Applies to:** Windows 11 (x64). All citations are to current Microsoft Learn pages.

---

## TL;DR recommendation

Build **one WDM function driver** as a **WaveRT miniport** (`IMiniportWaveRT` / `IPortWaveRT`), root-enumerated like SysVAD, that registers **N wave filter factories + N topology filter factories** via `PcRegisterSubdevice`. That yields N pairs (one speaker endpoint + one mic endpoint each) from a single device object, no `IoInvalidateDeviceRelations` child PDOs, no per-pair device objects. Share audio with the app through a **`Global\` section created by the driver with an explicit DACL**. Test-sign it with a **self-signed certificate installed in both Trusted Root and Trusted Publishers**, enable `TESTSIGNING`, sign the `.sys` embedded with `/fd sha256` (and the `.cat` catalog). Build with the **EWDK (no Visual Studio required)**.

- **Framework:** WaveRT. WaveCyclic is explicitly deprecated by Microsoft.
- **Device tree:** one function driver, N filter factories. Not N child devices.
- **Runtime CRUD:** individual endpoints cannot be created/deleted at runtime in PortCls. The set of pairs is fixed at install. Add/remove = reinstall, or adopt the child-PDO model for hot-add/remove of whole devices.
- **Signing:** self-signed cert, two stores, `TESTSIGNING`, SHA-256 embedded signature. No attestation, no vendor, no money.
- **Build:** EWDK, `msbuild /p:configuration=Release /p:platform=x64`.

---

## 1. Framework: WaveRT vs WaveCyclic

### Recommendation: WaveRT

Microsoft deprecates WaveCyclic outright. The WaveCyclic Port Driver page states: "The use of WaveCyclic is no longer recommended, instead use WaveRT." ([WaveCyclic Port Driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/wavecyclic-port-driver), opened 2025-11-23.)

### WaveRT

- **Concrete interfaces** (all in `portcls.h` / `Ksmedia.h`): the adapter binds an `IMiniportWaveRT` object to an `IPortWaveRT` object, which PortCls registers as a **wave filter** ([IMiniportWaveRT reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/portcls/nn-portcls-iminiportwavert)). The stream objects implement `IMiniportWaveRTStream`; for event notification add `IMiniportWaveRTStreamNotification`. The port side exposes `IPortWaveRTStream`.
- **Latency model:** the miniport allocates a **cyclic buffer** that the audio engine maps directly and renders into. No copy loop in the driver. This is the whole point of WaveRT; a virtual device has no hardware latency to add, so end-to-end latency is governed by the buffer period, not by DMA.
- **Notification model** (three mechanisms, all optional to the app):
  - **Events:** `KSPROPERTY_RTAUDIO_BUFFER_WITH_NOTIFICATION` + the miniport's `IMiniportWaveRTStreamNotification`, with the app registering a notification event via `KSPROPERTY_RTAUDIO_REGISTER_NOTIFICATION_EVENT`. The event fires every `NotificationCount` bytes ([KSPROPERTY_RTAUDIO_BUFFER_WITH_NOTIFICATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-buffer-with-notification)).
  - **Polling:** `KSPROPERTY_RTAUDIO_POSITIONREGISTER` maps the position register straight into the client's address space so it can poll it with no syscall ([KSPROPERTY_RTAUDIO_POSITIONREGISTER](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-positionregister)).
  - **Packet mode:** `KSPROPERTY_RTAUDIO_PACKETCOUNT` / `SET_WRITE_PACKET` / `PRESENTATION_POSITION`, used by the low-latency/pro-audio path ([KSPROPERTY_RTAUDIO_PACKETCOUNT](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-packetcount), [KSPROPERTY_RTAUDIO_PRESENTATION_POSITION](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-presentation-position)).
- **Minimum buffer:** the miniport allocates the buffer to the engine's request; it is cyclic (wraps). The practical floor is one 10 ms period = **480 frames at 48 kHz**; smaller periods (a few ms) work in raw mode but raise glitch risk and APO overhead. WaveRT imposes no hardware-derived minimum on a virtual device — the miniport can honor whatever the engine asks.
- **INF requirements:** the INF must carry the device-class registration from the system `wdmaudio.inf` — the recommended structure is `Needs=KS.Registration, WDMAUDIO.Registration, ...` with the miniport's `AddReg` — so the wave/topology filters are registered with the audio device class ([Installing a Port Class Audio Adapter](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/installing-a-port-class-audio-adapter)). Filter factories carry `KSCATEGORY_REALTIME` (WaveRT) plus `KSCATEGORY_AUDIO` for legacy app compatibility.

### WaveCyclic (for contrast)

`IMiniportWaveCyclic` / `IMiniportWaveCyclicStream` / `IPortWaveCyclic`, DMA-based; the port driver copies audio between the engine's buffer and a miniport DMA buffer through `IDmaChannel`. No direct user-mode mapping of the buffer → higher latency and CPU per stream, and it is the deprecated model ([WaveCyclic Port Driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/wavecyclic-port-driver)).

### Cost of each

| | WaveRT | WaveCyclic |
|---|---|---|
| Interfaces | `IMiniportWaveRT`, `IMiniportWaveRTStream`, (`IMiniportWaveRTStreamNotification`), `IPortWaveRT` | `IMiniportWaveCyclic`, `IMiniportWaveCyclicStream`, `IPortWaveCyclic` |
| Buffer | miniport-owned cyclic buffer, mapped by engine directly | port-driver-managed DMA cyclic buffer, copy path |
| Notification | event / position-register poll / packet | cyclic-buffer position notifications via copy path |
| Latency | period-bound, no copy | copy-bound, higher |
| Status | recommended | deprecated |
| Driver complexity | moderate (no HW, no DMA controller) | higher (IDmaChannel bookkeeping) for no benefit |

---

## 2. Device tree: one driver, N filter factories (not N child devices)

### What SysVAD does (the reference)

The SysVAD sample is "a WDM audio driver that exposes support for multiple audio devices" using **WaveRT**, from **one driver** ([SysVAD README](https://github.com/microsoft/Windows-driver-samples/blob/main/audio/sysvad/README.md)). It is root-enumerated with a `Root\Sysvad_TabletAudioSample` hardware ID and installed with `devcon install TabletAudioSample.inf Root\Sysvad_TabletAudioSample`.

### The PortCls model

One function driver has one device object. In `StartDevice`, the adapter registers **subdevices** via `PcRegisterSubdevice` ([Subdevice Creation](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/subdevice-creation)). Each subdevice is a **filter factory** (a wave filter backed by an `IMiniportWaveRT`, or a topology filter backed by an `IMiniportTopology`). The Audio Endpoint Builder turns each wave filter plus its topology into the endpoints the user sees in Settings and in app device pickers.

**For 5–8 pairs:** register, per pair, one wave filter factory and one topology filter factory. The endpoint builder then produces one render endpoint (speaker) and one capture endpoint (mic/line) per pair, each with its own name. This is the simplest thing that enumerates correctly, and it is exactly the SysVAD pattern.

### The alternative: N separate devices

Two sub-variants, both more work and both unnecessary here:

1. **N child PDOs via `IoInvalidateDeviceRelations` (bus relations).** The function driver becomes a bus enumerator, creating child physical device objects and reporting them through `BusRelations`. Each child is its own adapter with its own device ID, INF match, and driver-stack instance. This is the mechanism behind **dynamic** subdevices (jack detection / hot-plug): see [Dynamic Audio Subdevices](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/dynamic-audio-subdevices), whose keywords explicitly cover "removing subdevices, deleting subdevices, unregistering subdevices."
2. **N root-enumerated devices.** N separate "devices" in Device Manager, each with its own INF section and `Root\...` ID, each loaded as its own function driver instance.

### Which is simpler

**One function driver + N filter factories** (option A). No PnP relations code, no bus-enumeration state machine, no per-device INF sections. Child PDOs only pay off if you need to hot-add/remove whole pairs at runtime (see §3).

---

## 3. Runtime CRUD — what "create/delete" can and cannot mean

### The hard constraint

PortCls subdevices are registered at device start, and there is **no `PcUnregisterSubdevice`**. A filter factory, once registered, lives for the life of the device object. The endpoint builder derives endpoints from those filters at enumeration time. Therefore, in the standard model, **you cannot create or delete an individual endpoint while the driver is loaded.**

### What you can do

- **Read:** enumerate endpoints via `IMMDeviceEnumerator` at any time; the app sees the full set.
- **Mute / disable / set default** per endpoint at runtime (audio policy and endpoint-control APIs). This is state change, not creation.
- **Update names / topology:** requires editing the INF and reinstalling the device (names and endpoint identity come from the INF's `AddInterface` / topology). Not a runtime change.
- **Hot add/remove whole pairs:** adopt the child-PDO model. The driver implements bus enumeration, creates/removes a child PDO, and calls `IoInvalidateDeviceRelations(BusRelations)` so PnP re-enumerates; the audio engine then adds or removes the corresponding endpoints ([Dynamic Audio Subdevices](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/dynamic-audio-subdevices)). This is what "create/delete a pair" maps to on Windows, and it is a different, larger driver shape.

### What happens to open handles on removal

This is standard PnP device-removal semantics, not audio-specific: when the device (or a child device) is removed, the endpoint is torn down, and any app holding an open `IAudioClient`/stream on it gets a device-invalidated path — its open handles are stale and subsequent operations fail, the audio engine fires the device-state-change notification, and the app must release handles and re-enumerate. An app does not keep a valid handle across removal. A pair removal mid-stream is therefore a **disconnect**, not a seamless transfer.

### Recommendation

**Fixed set of pairs at install time.** Model CRUD in the app as "which pairs are configured/visible," not as runtime endpoint creation. Ship a fixed number of pairs (e.g., 8) from the INF; the app picks a subset by name. Revisit child PDOs only if runtime hot-plug becomes a real requirement, because that is a multi-week shape change.

---

## 4. Buffer and notification model

### WaveRT

- **Cyclic buffer layout:** the miniport allocates one contiguous buffer per stream; positions wrap modulo the buffer size. The app obtains a mapping of the buffer via `KSPROPERTY_RTAUDIO_BUFFER` and reads/writes interleaved PCM at the current position ([KSPROPERTY_RTAUDIO_BUFFER](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-buffer)). For JockeyBox, interleaved 32-bit float at 48 kHz is the natural buffer format; the engine handles conversion to/from the app's requested mix format.
- **Notification event:** with `KSPROPERTY_RTAUDIO_BUFFER_WITH_NOTIFICATION`, the miniport declares a `NotificationCount`, and the app registers a kernel event that is signaled each time that many bytes have been transferred ([KSPROPERTY_RTAUDIO_BUFFER_WITH_NOTIFICATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-buffer-with-notification)).
- **Polling vs events:** the app may instead poll the position directly through `KSPROPERTY_RTAUDIO_POSITIONREGISTER` (no syscall) ([KSPROPERTY_RTAUDIO_POSITIONREGISTER](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-positionregister)), or drive packet-based low-latency I/O via `KSPROPERTY_RTAUDIO_PACKETCOUNT` / `SET_WRITE_PACKET` / `PRESENTATION_POSITION`.
- **Lowest practical buffer:** no hardware floor on a virtual device. The engine's default is 10 ms (**480 frames @ 48 kHz**); treat that as the floor for a first cut. A few ms (e.g., 128–256 frames) is achievable in raw mode but multiplies callback/APO load and glitch risk. Recommend starting at 480 frames and tuning down.

### WaveCyclic (equivalent, for reference)

The cyclic buffer is managed by the port driver's DMA channel; there is no direct user-mode buffer mapping, and notification is via position updates through the copy path. This is strictly worse for this use case and deprecated (see §1).

---

## 5. Sharing audio with the app

### Why `Global\`

Kernel objects live in a namespace rooted at `\`. On a terminal-services system, each logon session gets a **private (local) namespace**, and session 0 (where kernel services and drivers run) has its own local namespace that a user session cannot see. The **`Global\` namespace** is shared across all sessions ([Kernel object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces), [Object Names](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/object-names)). A driver running in session 0 therefore cannot hand the app a `Local\` section the app can open — it must use `Global\`. This matches the stated requirement exactly.

### Driver side

The driver calls `ZwCreateSection` with an object name such as `\BaseNamedObjects\Global\JockeyBox\Pair3` and, critically, an **explicit security descriptor (DACL)** that grants the interactive user (or `Everyone`, or a well-known SID) the rights the app needs: `GENERIC_READ | GENERIC_WRITE | SECTION_MAP_WRITE | SECTION_MAP_READ`. Creating the section with a default/NULL DACL yields `STATUS_ACCESS_DENIED` on the app's open. The driver is in the system context and is not subject to `SeCreateGlobalPrivilege` — that privilege gates *user-mode services* creating `Global\` objects, not kernel code.

### App side

The app opens the existing section by name — `OpenFileMappingW` + `MapViewOfFile` (or `CreateFileMappingW` with the same name, which opens the existing object) — then maps it. **Opening an existing object requires no special privilege**; access is governed solely by the DACL the driver placed on it. The same applies to a kernel-created `Global\` notification event, opened with `OpenEventW`.

### Notification contract

Either (a) the driver creates a `Global\` named event (`\BaseNamedObjects\Global\JockeyBox\Pair3Event`) that it signals when data advances, and the app opens it with `OpenEventW` and waits; or (b) the app spins on a publish counter in the shared ring (the same contract the ASIO side already uses). The ring itself — one publish counter plus per-reader slots, 48 kHz interleaved float, fixed block size — is the shared memory discipline; the WaveRT engine pushes/pulls samples through the miniport's cyclic buffer, and the miniport copies between that cyclic buffer and the ring (see §8, phase 2, for where that copy lives).

**Note:** the WaveRT cyclic buffer (mapped to the engine) and the app-facing `Global\` ring are **two different buffers**. The miniport is the bridge: render side, samples the engine wrote to the WaveRT buffer are copied into the ring's write side; capture side, samples the app wrote into the ring are copied into the WaveRT buffer the engine reads. One pair = one internal ring = one render endpoint + one capture endpoint sharing it.

---

## 6. Signing under test mode, at no cost

### The chain, in order

1. **Create a self-signed test certificate.** Use `makecert` from the WDK (or PowerShell `New-SelfSignedCertificate`), with the code-signing EKU (`1.3.6.1.5.5.7.3.3`). Microsoft's page is [Creating Test Certificates](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/creating-test-certificates). Name it something like `JockeyBox(Test)`.
2. **Install it into two machine stores:** `Trusted Root Certification Authorities` **and** `Trusted Publishers`. The root store makes the chain trusted; the publisher store is what the kernel/loader checks for a kernel-mode signature. Microsoft's pages: [Test Signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/test-signing) ("To enable trust, install this certificate in the Trusted Root Certification Authorities store") and [Trusted Publishers Certificate Store](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/trusted-publishers-certificate-store) ("your company should install the Authenticode certificates that are used to sign driver packages in the Trusted Publishers certificate store"). Both must be in the **local machine** store (PnP install-time verification reads the machine store).
3. **Enable test mode:** `bcdedit /set TESTSIGNING ON`, then reboot. The loader then accepts a signature rooted in a test cert. See [Loading Test Signed Code](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option). This requires **Secure Boot off** (a Secure Boot-enabled machine rejects the TESTSIGNING change; the boot-configuration doc calls this out).
4. **Embed-sign the `.sys`:** `signtool sign /v /fd sha256 /s PrivateCertStore /n "JockeyBox(Test)" /t http://timestamp.digicert.com jockeybox.sys`. `/fd sha256` is mandatory — signtool defaults to SHA-1, and SHA-1 is not accepted for kernel-mode signatures on modern Windows. See [Test-Signing a Driver Binary through an Embedded Signature](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/test-signing-a-driver-through-an-embedded-signature) (the doc's own example is `Signtool sign /v /fd sha256 /s PrivateCertStore /n Contoso.com(Test) /t http://timestamp...`).
5. **Sign the `.cat` catalog** (for the INF/PNP install path) with the same command. Generate the catalog from the INF with `Inf2Cat /driver:<dir> /os:10_X64`; the catalog name is referenced from the INF's `[Version] CatalogFile=`. See [Signing a Driver During Development and Testing](https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/signing-a-driver-during-development-and-testing).

### Is a `.cat` needed?

For a **non-boot-start PnP driver** installed via INF, the driver **package** signature is carried by the catalog; the installer validates the `.cat`. The `.sys` embedded signature is *sufficient* for the kernel to load the image under test mode, but the **clean, reliable path is to sign both**: embed-sign the `.sys` (so the image always loads) and sign the `.cat` (so `devcon`/`pnputil` accepts the package). Do both. Microsoft's own framing: for development, embed-sign the binary with a test cert and enable test mode ([Loading Test Signed Code](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option)).

### `ERROR_INVALID_IMAGE_HASH` / `STATUS_INVALID_IMAGE_HASH` — exactly what fails

This is the loader's "I could not validate this image's signature" error (surfaces as Code 52 / `CM_PROB_UNSIGNED_DRIVER` at install time, and as `STATUS_INVALID_IMAGE_HASH` `0xC0000428` at load). It means **the signature does not validate against the file and the trusted roots** — the causes, in order of likelihood for a test-signed driver:

1. **Signed with SHA-1** (signtool's default when `/fd` is omitted). SHA-1 is not accepted for kernel mode on Windows 10/11. Fix: `/fd sha256`. This is the single most common cause and the reason to always pass `/fd`.
2. **Certificate not trusted** — the test cert is missing from `Trusted Root Certification Authorities` or `Trusted Publishers` (machine store), or `TESTSIGNING` is off so there is no chain to a trusted root. Fix: install into both stores, enable test mode, reboot.
3. **File modified after signing** — any re-link/re-copy after `signtool` invalidates the hash. Fix: sign as the *last* step before install.
4. **Catalog mismatch** — the installed catalog does not cover the exact `.sys`/INF pair (stale or un-signed `.cat`).

Diagnose via the Code Integrity operational event log (Event Viewer → Applications and Services Logs → Microsoft → Windows → CodeIntegrity), per [Detecting Driver Load Errors](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/detecting-driver-load-errors).

### Why no attestation/vendor signing is needed

Test mode is explicitly designed to load test-signed (self-signed) drivers on a development machine. No WHQL/HLK submission, no attestation signature, no third-party cert, no money. The tradeoff is purely operational: `TESTSIGNING ON` and Secure Boot off on this one rig. If the rig must keep Secure Boot on for other reasons, then — and only then — this zero-cost path is blocked, and that becomes a decision for the operator.

---

## 7. Build tooling

- **Project type:** a WDM driver project (the SysVAD sample is a plain WDM project, not KMDF — audio miniports plug into PortCls, the WDM class framework). In Visual Studio this is the "Empty WDM Driver" / "Kernel Mode Driver (KMDF)" project with `DriverType` set to WDM. The WDK supplies the MSBuild targets (`WindowsDriver.Common.targets`).
- **Visual Studio is not mandatory.** The **Enterprise WDK (EWDK)** is "a standalone, self-contained command-line environment for building drivers," with headers, libs, and build tools, no Visual Studio install required ([Download the WDK](https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk), [Using the Enterprise WDK](https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/using-the-enterprise-wdk)). Alternatively install the regular WDK alongside VS Build Tools.
- **Build command** (EWDK): run `LaunchBuildEnv.cmd` in the EWDK root, then:
  ```
  msbuild /t:build /p:configuration=Release /p:platform=x64 jockeybox.vcxproj
  ```
  The WDK build flow is documented in [Building a Driver with the WDK](https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/building-a-driver).

---

## 8. Phased build plan

1. **Skeleton that loads and enumerates.** One WDM WaveRT miniport, root-enumerated, one wave filter factory + one topology filter factory → one speaker endpoint + one mic endpoint, both named. Milestone: Device Manager shows the device without a bang; Settings shows the pair. This proves the signing chain, the INF, and `PcRegisterSubdevice` all at once.
2. **Silent pair that passes audio.** Wire the render→capture loop through a single in-driver ring: render side copies the WaveRT cyclic buffer into the ring, capture side copies the ring into the WaveRT capture buffer. Milestone: OBS or a loopback test sees silence-on-the-way-in come out the mic side. Proves the cyclic-buffer/position/notification plumbing.
3. **N pairs.** Generalize to 8 filter-factory pairs, each with its own ring and `Global\` section + notification event. Milestone: 8 speaker + 8 mic endpoints, independently selectable in Discord/Zoom/OBS.
4. **App side.** JockeyBox opens each `Global\` section by name, maps it, and runs the existing ASIO-style ring contract (publish counter + reader slots) against it. Milestone: audio written to a pair's playback side appears on its recording side, with the app in the loop.

---

## 9. Risks that would cost days

1. **Secure Boot vs TESTSIGNING.** The other lane owns this check, but it is the single gate that can kill the zero-cost path. If Secure Boot must stay on, test signing is off the table and the whole approach changes (that is an operator decision, not an engineering one).
2. **SHA-1 default in signtool.** Forgetting `/fd sha256` yields `STATUS_INVALID_IMAGE_HASH` and a wasted debugging session. Burn this into the build script.
3. **Cert in the wrong store.** Root-only installs cause a load failure; the cert must be in **both** Trusted Root and Trusted Publishers, in the **machine** store, and `TESTSIGNING` must be on before install.
4. **INF / endpoint-builder naming.** Endpoints appearing with the wrong name, wrong direction, or not appearing at all is an INF `AddInterface` + topology problem, not a driver-logic problem. Expect to iterate on the INF to get "speaker" vs "mic" and the friendly names right; the audio endpoint builder is finicky.
5. **Buffer/section ACL mistakes.** A `Global\` section created with a default DACL is `ACCESS_DENIED` to the app; a wrong object-name prefix (`Local\` instead of `Global\`) is invisible to the user session. Both are silent failures.
6. **Test-mode rig isolation.** `TESTSIGNING ON` and Secure Boot off persist on the host; treat the test rig as a non-production machine.

---

## 10. Host checklist — what must be true before the first line of driver code

1. **Windows 11 x64** rig available as a dedicated test machine.
2. **Secure Boot off**, or confirmed toggleable (this is the zero-cost gate).
3. **BitLocker suspended** before any `bcdedit` change (the SysVAD README warns BCDEdit changes require suspending BitLocker first; the other lane owns confirming this).
4. **WDK or EWDK installed** on the build host (EWDK is sufficient; no Visual Studio needed).
5. **Admin / dev mode** on the target for `bcdedit`, `devcon`/`pnputil`, and certificate-store changes.
6. **The self-signed cert created and installed** into Trusted Root + Trusted Publishers (machine store) before first load.

---

## Sources

- WaveRT Port Driver — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/wavert-port-driver
- WaveRT Miniport Driver — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/wavert-miniport-driver
- IMiniportWaveRT — https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/portcls/nn-portcls-iminiportwavert
- WaveCyclic Port Driver (deprecation) — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/wavecyclic-port-driver
- Introducing / Understanding / Developing a WaveRT Miniport — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/introducing-the-wavert-port-driver · /understanding-the-wavert-port-driver · /developing-a-wavert-miniport-driver
- KSPROPERTY_RTAUDIO_BUFFER — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-buffer
- KSPROPERTY_RTAUDIO_BUFFER_WITH_NOTIFICATION — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-buffer-with-notification
- KSPROPERTY_RTAUDIO_POSITIONREGISTER — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-positionregister
- KSPROPERTY_RTAUDIO_PACKETCOUNT — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-packetcount
- KSPROPERTY_RTAUDIO_PRESENTATION_POSITION — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-rtaudio-presentation-position
- Installing a Port Class Audio Adapter (INF) — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/installing-a-port-class-audio-adapter
- Subdevice Creation — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/subdevice-creation
- Dynamic Audio Subdevices — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/dynamic-audio-subdevices
- SysVAD README — https://github.com/microsoft/Windows-driver-samples/blob/main/audio/sysvad/README.md
- Sample Audio Drivers — https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/sample-audio-drivers
- Kernel object namespaces — https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
- Object Names — https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/object-names
- Test Signing — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/test-signing
- Creating Test Certificates — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/creating-test-certificates
- Test-Signing a Driver Binary through an Embedded Signature — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/test-signing-a-driver-through-an-embedded-signature
- Loading Test Signed Code (TESTSIGNING) — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/the-testsigning-boot-configuration-option
- Trusted Publishers Certificate Store — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/trusted-publishers-certificate-store
- Signing a Driver During Development and Testing — https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/signing-a-driver-during-development-and-testing
- Installing Test-Signed Driver Packages — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/installing-test-signed-driver-packages
- Detecting Driver Load Errors — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/detecting-driver-load-errors
- Building a Driver with the WDK — https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/building-a-driver
- Using the Enterprise WDK — https://learn.microsoft.com/en-us/windows-hardware/drivers/develop/using-the-enterprise-wdk
- Download the WDK — https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk
- SignTool — https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool
