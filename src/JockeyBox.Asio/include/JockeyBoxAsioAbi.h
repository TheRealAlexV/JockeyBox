// JockeyBoxAsioAbi.h - minimal, independently written declaration of the
// driver-side ASIO COM ABI.
//
// LICENCE: this file contains no Steinberg SDK code and no SDK header text.
// The ABI (vtable order, signatures, struct layouts, enum values) is a
// published wire format and was reconstructed from three independent sources,
// reconciled by hand:
//
//   1. NAudio (C#) src/NAudio.Asio/AsioDriver.cs - the `AsioDriverVTable`
//      struct lists every slot with an explicit index comment and a
//      function-pointer signature. Slots 3..23 are spelled out there.
//   2. PortAudio src/hostapi/asio/pa_asio.cpp - independent host-side
//      consumer; confirms the ordering used to reach getChannelInfo and
//      createBuffers, and confirms that `asioMessage` is a HOST callback
//      (ASIOCallbacks::asioMessage) rather than a driver-side slot.
//   3. asio.h / asiodrvr.h (Steinberg, the ABI's own definition) - used as
//      ground truth for the last word on ordering and struct layout.
//
// VTABLE (x64, one virtual per line below, in declaration order):
//    0 QueryInterface    1 AddRef            2 Release
//    3 init              4 getDriverName     5 getDriverVersion
//    6 getErrorMessage   7 start             8 stop
//    9 getChannels      10 getLatencies     11 getBufferSize
//   12 canSampleRate    13 getSampleRate    14 setSampleRate
//   15 getClockSources  16 setClockSource   17 getSamplePosition
//   18 getChannelInfo   19 createBuffers    20 disposeBuffers
//   21 controlPanel     22 future           23 outputReady
//   24 asioMessage  <-- JockeyBox extension, NOT part of the standard ABI.
//
// Note the three slots that a naive reading of the interface misses:
// getChannelInfo / createBuffers / disposeBuffers sit between
// getSamplePosition and controlPanel, and there is no getTimeCode or
// getTimeCodeSampleRate slot - those are ASIO 1.0 API functions that no
// longer exist on the interface.

#pragma once

#include <windows.h>

#include <cstdint>

namespace jb::asio {

// ASIOBool is a 4-byte int; ASIOError is a 4-byte int (negative codes, with
// one magic positive success value used only by future()).
using AsioBool = int32_t;
using AsioError = int32_t;
using AsioSampleRate = double;

// ASIO answers its IUnknown methods with __stdcall on x86 and every other
// method with __thiscall. On x64 there is only one calling convention, so
// both macros vanish; they are kept so an x86 build would still be correct.
#if defined(_M_IX86)
#define JB_COM_CALL __stdcall
#define JB_ASIO_CALL __thiscall
#else
#define JB_COM_CALL
#define JB_ASIO_CALL
#endif

constexpr AsioBool kAsioTrue = 1;
constexpr AsioBool kAsioFalse = 0;

// ASIOError values.
constexpr AsioError kAseOk = 0;
constexpr AsioError kAseSuccess = 0x3f4847a0;
constexpr AsioError kAseNotPresent = -1000;
constexpr AsioError kAseHwMalfunction = -999;
constexpr AsioError kAseInvalidParameter = -998;
constexpr AsioError kAseInvalidMode = -997;
constexpr AsioError kAseSpNotAdvancing = -996;
constexpr AsioError kAseNoClock = -995;
constexpr AsioError kAseNoMemory = -994;

// ASIOSampleType, little-endian subset. Only the one we advertise is needed
// by v1, but the values are cheap to keep and document the numbering.
constexpr int32_t kAsioStInt16Lsb = 16;
constexpr int32_t kAsioStInt24Lsb = 17;
constexpr int32_t kAsioStInt32Lsb = 18;
constexpr int32_t kAsioStFloat32Lsb = 19;
constexpr int32_t kAsioStFloat64Lsb = 20;

// asioMessage selectors (host -> driver query numbers).
enum : long {
    kAsioSelectorSupported = 1,
    kAsioEngineVersion = 2,
    kAsioResetRequest = 3,
    kAsioBufferSizeChange = 4,
    kAsioResyncRequest = 5,
    kAsioLatenciesChanged = 6,
    kAsioSupportsTimeInfo = 7,
    kAsioSupportsTimeCode = 8,
    kAsioMMCCommand = 9,
    kAsioSupportsInputMonitor = 10,
    kAsioSupportsInputGain = 11,
    kAsioSupportsInputMeter = 12,
    kAsioSupportsOutputGain = 13,
    kAsioSupportsOutputMeter = 14,
    kAsioOverload = 15,
};

// 64-bit sample position / timestamp, split hi/lo as the Windows ABI does.
struct AsioSamples {
    uint32_t hi;
    uint32_t lo;
};
struct AsioTimeStamp {
    uint32_t hi;
    uint32_t lo;
};

struct AsioTimeInfo {
    double speed;
    AsioTimeStamp systemTime;
    AsioSamples samplePosition;
    AsioSampleRate sampleRate;
    uint32_t flags;
    char reserved[12];
};

struct AsioTimeCode {
    double speed;
    AsioSamples timeCodeSamples;
    uint32_t flags;
    char future[64];
};

struct AsioTime {
    long reserved[4];
    AsioTimeInfo timeInfo;
    AsioTimeCode timeCode;
};

struct AsioBufferInfo {
    AsioBool isInput;    // in: kAsioTrue for an input channel
    long channelNum;     // in: zero-based channel index
    void* buffers[2];    // out: the two halves of the double buffer
};

struct AsioChannelInfo {
    long channel;        // in: zero-based channel index
    AsioBool isInput;    // in
    AsioBool isActive;   // out
    long channelGroup;   // out
    int32_t type;        // out: ASIOSampleType
    char name[32];       // out
};

struct AsioClockSource {
    long index;
    long associatedChannel;
    long associatedGroup;
    AsioBool isCurrentSource;
    char name[32];
};

struct AsioCallbacks {
    void (*bufferSwitch)(long doubleBufferIndex, AsioBool directProcess);
    void (*sampleRateDidChange)(AsioSampleRate sRate);
    long (*asioMessage)(long selector, long value, void* message, double* opt);
    AsioTime* (*bufferSwitchTimeInfo)(AsioTime* params, long doubleBufferIndex,
                                      AsioBool directProcess);
};

// Layout gates. These are the numbers a host compiled against the real SDK
// will use; if any of these ever fails to compile, the ABI reimplementation
// has drifted and the driver would corrupt host memory.
static_assert(sizeof(AsioBool) == 4, "ASIOBool is 4 bytes");
static_assert(sizeof(AsioSamples) == 8, "ASIOSamples is 8 bytes");
static_assert(sizeof(AsioTimeStamp) == 8, "ASIOTimeStamp is 8 bytes");
static_assert(sizeof(AsioTimeInfo) == 48, "AsioTimeInfo is 48 bytes");
static_assert(sizeof(AsioTimeCode) == 88, "ASIOTimeCode is 88 bytes");
static_assert(sizeof(AsioTime) == 152, "ASIOTime is 152 bytes");
static_assert(sizeof(AsioBufferInfo) == 24, "ASIOBufferInfo is 24 bytes");
static_assert(sizeof(AsioChannelInfo) == 52, "ASIOChannelInfo is 52 bytes");
static_assert(sizeof(AsioClockSource) == 48, "ASIOClockSource is 48 bytes");
static_assert(sizeof(AsioCallbacks) == 32, "ASIOCallbacks is 4 pointers");

// The driver interface. Declaration order IS the vtable order - do not
// reorder, do not insert, do not group "logically".
struct IASIO {
    virtual HRESULT JB_COM_CALL QueryInterface(REFIID riid, void** ppv) = 0;
    virtual ULONG JB_COM_CALL AddRef() = 0;
    virtual ULONG JB_COM_CALL Release() = 0;

    virtual AsioBool JB_ASIO_CALL init(void* sysHandle) = 0;
    virtual void JB_ASIO_CALL getDriverName(char* name) = 0;
    virtual long JB_ASIO_CALL getDriverVersion() = 0;
    virtual void JB_ASIO_CALL getErrorMessage(char* text) = 0;

    virtual AsioError JB_ASIO_CALL start() = 0;
    virtual AsioError JB_ASIO_CALL stop() = 0;

    virtual AsioError JB_ASIO_CALL getChannels(long* numInputChannels,
                                               long* numOutputChannels) = 0;
    virtual AsioError JB_ASIO_CALL getLatencies(long* inputLatency,
                                                long* outputLatency) = 0;
    virtual AsioError JB_ASIO_CALL getBufferSize(long* minSize, long* maxSize,
                                                 long* preferredSize,
                                                 long* granularity) = 0;

    virtual AsioError JB_ASIO_CALL canSampleRate(AsioSampleRate sampleRate) = 0;
    virtual AsioError JB_ASIO_CALL getSampleRate(AsioSampleRate* sampleRate) = 0;
    virtual AsioError JB_ASIO_CALL setSampleRate(AsioSampleRate sampleRate) = 0;

    virtual AsioError JB_ASIO_CALL getClockSources(AsioClockSource* clocks,
                                                   long* numSources) = 0;
    virtual AsioError JB_ASIO_CALL setClockSource(long reference) = 0;

    virtual AsioError JB_ASIO_CALL getSamplePosition(AsioSamples* sPos,
                                                     AsioTimeStamp* tStamp) = 0;
    virtual AsioError JB_ASIO_CALL getChannelInfo(AsioChannelInfo* info) = 0;

    virtual AsioError JB_ASIO_CALL createBuffers(AsioBufferInfo* bufferInfos,
                                                 long numChannels, long bufferSize,
                                                 AsioCallbacks* callbacks) = 0;
    virtual AsioError JB_ASIO_CALL disposeBuffers() = 0;

    virtual AsioError JB_ASIO_CALL controlPanel() = 0;
    virtual AsioError JB_ASIO_CALL future(long selector, void* opt) = 0;
    virtual AsioError JB_ASIO_CALL outputReady() = 0;

    // ---- slot 24: JockeyBox extension, NOT part of the standard ABI ----
    // Standard hosts never call past outputReady, so this is inert for them.
    // It exists so the selector query logic is testable and so a future
    // driver-side query path has somewhere to live. Semantics are those of
    // the host's ASIOCallbacks::asioMessage.
    virtual long JB_ASIO_CALL asioMessage(long selector, long value,
                                          void* message, double* opt) = 0;
};

// {7E1B5C2A-9F4D-4B3A-8E6C-5D2A1F0B9C31}
constexpr GUID kDriverClsid = {
    0x7e1b5c2a, 0x9f4d, 0x4b3a,
    {0x8e, 0x6c, 0x5d, 0x2a, 0x1f, 0x0b, 0x9c, 0x31}};

constexpr wchar_t kDriverClsidText[] =
    L"{7E1B5C2A-9F4D-4B3A-8E6C-5D2A1F0B9C31}";
constexpr char kDriverNameText[] = "JockeyBox Virtual ASIO";

}  // namespace jb::asio
