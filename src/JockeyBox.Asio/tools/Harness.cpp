// AsioHostHarness - stands in for a real ASIO host against the virtual driver.
//
// It registers the CLSID under HKCU (in-proc COM honours HKCU\Software\Classes,
// so no elevation) and then does exactly what a host does: CoCreateInstance ->
// init -> query caps -> createBuffers -> start -> count callbacks for a second
// -> stop -> release.
//
// Two passes are made over the same object:
//   * an index pass that reaches slots by their documented ASIO numbers
//     (3, 5, 9, 11, 7, 8) through raw vtable pointers, compiled from the
//     numbering NAudio and PortAudio use. This is what actually proves the
//     vtable order: it never consults the local C++ interface.
//   * a normal pass through the declared interface, which is what a host
//     built against the driver header would do.

#include "JockeyBoxAsioAbi.h"
#include "../src/RingReader.h"

#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <memory>
#include <thread>

namespace {

std::atomic<long> g_callbacks{0};
long g_lastIndex = -1;

// ---- audio-path capture ----------------------------------------------------
// Set by a bus test; null during the ABI passes. The callback copies the two
// input halves it is about to hand the host, so the comparison is against
// exactly what reached the host, not against the ring.
constexpr int kBusFrames = 128;
constexpr int kBusChannels = 2;
constexpr int kBusCapacity = 16;
constexpr int kBusSampleRate = 48000;
constexpr int kMaxCaptures = 4096;

struct Captures {
    std::atomic<long> count{0};
    float block[kMaxCaptures][kBusChannels][kBusFrames];
};

Captures* g_capture = nullptr;
const float* g_inBuf[kBusChannels][2] = {};

// Read-only attach mode: what the driver handed the host during a live run.
constexpr int kLiveMaxFrames = 512;      // the app's block is 480; leave room
constexpr long kLiveMaxCaptures = 600;   // ~6 s at 100 blocks/s
constexpr int kLiveMaxRefBlocks = 8;     // the ring's capacity: any block it can still hold

struct LiveCaptures {
    std::atomic<long> count{0};
    int64_t writeIndex[kLiveMaxCaptures];
    float left[kLiveMaxCaptures][kLiveMaxFrames];
    float right[kLiveMaxCaptures][kLiveMaxFrames];
    // The blocks the ring still held at this callback. Snapshotting here is what
    // makes the comparison honest: the ring is a moving target, so reading the
    // payload after the run would compare against blocks already overwritten.
    // Every block the driver can still serve this callback is in this set.
    int refCount[kLiveMaxCaptures];
    float ref[kLiveMaxCaptures][kLiveMaxRefBlocks][kLiveMaxFrames * 2];
};

LiveCaptures* g_live = nullptr;
std::atomic<int64_t>* g_liveWrite = nullptr;
const uint8_t* g_livePayload = nullptr;
int64_t g_liveBlockBytes = 0;
int g_liveCapacity = 0;
int g_liveFrames = 0;

// Every published block carries its own index in every sample, so a wrong
// block, a stale block or a byte-level layout error is visible in the numbers.
float sampleFor(int64_t block, int frame, int channel) {
    return static_cast<float>(block * 1000 + frame * 2 + channel);
}

void onBufferSwitch(long doubleBufferIndex, jb::asio::AsioBool directProcess) {
    (void)directProcess;
    g_lastIndex = doubleBufferIndex;
    g_callbacks.fetch_add(1, std::memory_order_relaxed);

    LiveCaptures* live = g_live;
    if (live) {
        const long seq = live->count.load(std::memory_order_relaxed);
        if (seq < kLiveMaxCaptures) {
            const int frames = g_liveFrames;
            for (int ch = 0; ch < 2; ++ch) {
                const float* src = g_inBuf[ch][doubleBufferIndex & 1];
                float* dst = ch == 0 ? live->left[seq] : live->right[seq];
                if (src)
                    memcpy(dst, src, sizeof(float) * frames);
                else
                    memset(dst, 0, sizeof(float) * frames);
            }
            const int64_t w = g_liveWrite->load(std::memory_order_acquire);
            live->writeIndex[seq] = w;
            const int span = g_liveCapacity < kLiveMaxRefBlocks ? g_liveCapacity
                                                               : kLiveMaxRefBlocks;
            int kept = 0;
            for (int k = 0; k < span; ++k) {
                const int64_t idx = w - 1 - k;
                if (idx < 0) break;
                memcpy(live->ref[seq][k],
                       g_livePayload + (idx % g_liveCapacity) * g_liveBlockBytes,
                       size_t(g_liveBlockBytes));
                kept++;
            }
            live->refCount[seq] = kept;
            live->count.store(seq + 1, std::memory_order_release);
        }
        return;
    }

    Captures* cap = g_capture;
    if (!cap) return;
    const long seq = cap->count.load(std::memory_order_relaxed);
    if (seq >= kMaxCaptures) return;
    for (int ch = 0; ch < kBusChannels; ++ch) {
        const float* src = g_inBuf[ch][doubleBufferIndex & 1];
        if (src)
            memcpy(cap->block[seq][ch], src, sizeof(float) * kBusFrames);
        else
            memset(cap->block[seq][ch], 0, sizeof(float) * kBusFrames);
    }
    cap->count.store(seq + 1, std::memory_order_release);
}

void onSampleRateChanged(jb::asio::AsioSampleRate) {}

long onAsioMessage(long, long, void*, double*) { return 0; }

jb::asio::AsioTime* onBufferSwitchTimeInfo(jb::asio::AsioTime*, long, jb::asio::AsioBool) {
    return nullptr;
}

bool registerInHkcu(const wchar_t* dllPath) {
    wchar_t key[512];
    swprintf_s(key, L"Software\\Classes\\CLSID\\%s\\InprocServer32",
               jb::asio::kDriverClsidText);

    HKEY hkey = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, key, 0, nullptr, 0, KEY_WRITE, nullptr,
                        &hkey, nullptr) != ERROR_SUCCESS)
        return false;

    const DWORD bytes = static_cast<DWORD>((wcslen(dllPath) + 1) * sizeof(wchar_t));
    bool ok = RegSetValueExW(hkey, nullptr, 0, REG_SZ,
                             reinterpret_cast<const BYTE*>(dllPath), bytes) ==
              ERROR_SUCCESS;
    const wchar_t* both = L"Both";
    ok = ok && RegSetValueExW(
                   hkey, L"ThreadingModel", 0, REG_SZ,
                   reinterpret_cast<const BYTE*>(both),
                   static_cast<DWORD>((wcslen(both) + 1) * sizeof(wchar_t))) ==
                   ERROR_SUCCESS;
    RegCloseKey(hkey);
    return ok;
}

void unregisterInHkcu() {
    wchar_t key[512];
    swprintf_s(key, L"Software\\Classes\\CLSID\\%s", jb::asio::kDriverClsidText);
    RegDeleteTreeW(HKEY_CURRENT_USER, key);
}

// ---- audio-path tests -----------------------------------------------------
//
// The app writes into the ring, the driver reads it into its ASIO inputs. These
// tests play the app's part with the documented layout and then assert on the
// buffers that actually reached the host callback.

struct TestBus {
    HANDLE map = nullptr;
    uint8_t* base = nullptr;
    uint8_t* payload = nullptr;
    std::atomic<int64_t>* write = nullptr;
    int64_t blockBytes = 0;
    wchar_t mapName[160] = {};

    bool create(const wchar_t* busName) {
        swprintf_s(mapName, L"Local\\JockeyBox.Bus.%s", busName);
        const int64_t size = jb::asio::bus::kPayloadOffset +
                             int64_t(kBusCapacity) * kBusFrames * kBusChannels * 4;
        map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                 static_cast<DWORD>(size >> 32),
                                 static_cast<DWORD>(size & 0xffffffffu), mapName);
        if (!map) return false;
        base = static_cast<uint8_t*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        if (!base) return false;

        auto put32 = [&](int offset, int32_t value) {
            memcpy(base + offset, &value, sizeof(value));
        };
        const uint32_t magic = jb::asio::bus::kMagic;
        memcpy(base + jb::asio::bus::kMagicOffset, &magic, sizeof(magic));
        put32(jb::asio::bus::kVersionOffset, jb::asio::bus::kVersion);
        put32(jb::asio::bus::kHeaderSizeOffset, jb::asio::bus::kHeaderSize);
        put32(jb::asio::bus::kSampleRateOffset, kBusSampleRate);
        put32(jb::asio::bus::kChannelsOffset, kBusChannels);
        put32(jb::asio::bus::kBlockFramesOffset, kBusFrames);
        put32(jb::asio::bus::kCapacityBlocksOffset, kBusCapacity);
        put32(jb::asio::bus::kReaderSlotCountFieldOffset, jb::asio::bus::kReaderSlotCount);
        put32(jb::asio::bus::kPayloadOffsetFieldOffset, jb::asio::bus::kPayloadOffset);

        write = reinterpret_cast<std::atomic<int64_t>*>(
            base + jb::asio::bus::kWriteBlockIndexOffset);
        write->store(0, std::memory_order_release);
        payload = base + jb::asio::bus::kPayloadOffset;
        blockBytes = int64_t(kBusFrames) * kBusChannels * 4;
        return true;
    }

    void publish(int64_t block) {
        float* dst = reinterpret_cast<float*>(
            payload + (block % kBusCapacity) * blockBytes);
        for (int f = 0; f < kBusFrames; ++f)
            for (int c = 0; c < kBusChannels; ++c)
                dst[f * kBusChannels + c] = sampleFor(block, f, c);
        write->store(block + 1, std::memory_order_release);
    }

    uint8_t* slot(int index) const {
        return base + jb::asio::bus::kReaderSlotBase + index * jb::asio::bus::kReaderSlotSize;
    }

    int activeSlot() const {
        for (int i = 0; i < jb::asio::bus::kReaderSlotCount; ++i) {
            const int32_t active =
                *reinterpret_cast<const volatile int32_t*>(slot(i) + jb::asio::bus::kSlotActive);
            if (active == 1) return i;
        }
        return -1;
    }

    int64_t slotValue(int index, int field) const {
        return *reinterpret_cast<const volatile int64_t*>(slot(index) + field);
    }

    void close() {
        if (base) UnmapViewOfFile(base);
        if (map) CloseHandle(map);
        base = nullptr;
        map = nullptr;
    }
};

bool blockEquals(const float (*captured)[kBusFrames], int64_t block) {
    for (int f = 0; f < kBusFrames; ++f)
        for (int c = 0; c < kBusChannels; ++c) {
            const float expected = sampleFor(block, f, c);
            float got = captured[c][f];
            if (memcmp(&got, &expected, sizeof(float)) != 0) return false;
        }
    return true;
}

bool blockSilent(const float (*captured)[kBusFrames]) {
    for (int c = 0; c < kBusChannels; ++c)
        for (int f = 0; f < kBusFrames; ++f)
            if (captured[c][f] != 0.0f) return false;
    return true;
}

void reportMismatch(const float (*captured)[kBusFrames], int64_t block) {
    for (int f = 0; f < kBusFrames; ++f)
        for (int c = 0; c < kBusChannels; ++c) {
            const float expected = sampleFor(block, f, c);
            const float got = captured[c][f];
            if (memcmp(&got, &expected, sizeof(float)) != 0) {
                wprintf(L"  first mismatch at block %lld frame %d ch %d: got %.1f expected %.1f\n",
                        static_cast<long long>(block), f, c, got, expected);
                return;
            }
        }
}

long long qpcTicks() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

double qpcSeconds(long long ticks) {
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    return double(ticks) / double(f.QuadPart);
}

// ---- read-only attach: watch a live app feed the ring ----------------------
//
// The audio-path tests above create the section themselves and publish their own
// index-coded blocks, which proves the ring contract but not that the *app* is
// the producer. This mode never creates or writes anything: it opens the section
// the running app publishes, sizes the ASIO buffers to the ring's block, and
// checks that every buffer the driver handed the host is bit-identical to a block
// the app published.

struct LiveBus {
    HANDLE map = nullptr;
    uint8_t* base = nullptr;
    uint8_t* payload = nullptr;
    std::atomic<int64_t>* write = nullptr;
    int sampleRate = 0;
    int channels = 0;
    int blockFrames = 0;
    int capacity = 0;

    bool attach(const wchar_t* busName) {
        wchar_t mapName[192];
        if (swprintf_s(mapName, L"Local\\JockeyBox.Bus.%s", busName) <= 0) return false;
        map = OpenFileMappingW(FILE_MAP_READ, FALSE, mapName);
        if (!map) return false;
        base = static_cast<uint8_t*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0));
        if (!base) return false;

        uint32_t magic = 0;
        int32_t version = 0, headerSize = 0, payloadOffset = 0;
        memcpy(&magic, base + jb::asio::bus::kMagicOffset, 4);
        memcpy(&version, base + jb::asio::bus::kVersionOffset, 4);
        memcpy(&headerSize, base + jb::asio::bus::kHeaderSizeOffset, 4);
        memcpy(&payloadOffset, base + jb::asio::bus::kPayloadOffsetFieldOffset, 4);
        if (magic != jb::asio::bus::kMagic || version != jb::asio::bus::kVersion ||
            headerSize != jb::asio::bus::kHeaderSize || payloadOffset < headerSize) {
            wprintf(L"FATAL: section exists but its header is not a JockeyBox ring\n");
            return false;
        }

        memcpy(&sampleRate, base + jb::asio::bus::kSampleRateOffset, 4);
        memcpy(&channels, base + jb::asio::bus::kChannelsOffset, 4);
        memcpy(&blockFrames, base + jb::asio::bus::kBlockFramesOffset, 4);
        memcpy(&capacity, base + jb::asio::bus::kCapacityBlocksOffset, 4);
        if (sampleRate <= 0 || channels <= 0 || blockFrames <= 0 || capacity < 2) {
            wprintf(L"FATAL: section geometry is unusable\n");
            return false;
        }
        payload = base + payloadOffset;
        write = reinterpret_cast<std::atomic<int64_t>*>(
            base + jb::asio::bus::kWriteBlockIndexOffset);
        return true;
    }

    int64_t writeIndex() const {
        return write->load(std::memory_order_acquire);
    }

    const float* block(int64_t index) const {
        const int64_t bytes = int64_t(blockFrames) * channels * 4;
        return reinterpret_cast<const float*>(payload + (index % capacity) * bytes);
    }

    void close() {
        if (base) UnmapViewOfFile(base);
        if (map) CloseHandle(map);
        base = nullptr;
        map = nullptr;
        payload = nullptr;
    }
};

bool liveBlockEquals(const float* payloadBlock, const float* left, const float* right,
                     int frames, int channels) {
    for (int c = 0; c < 2; ++c) {
        const float* captured = c == 0 ? left : right;
        for (int f = 0; f < frames; ++f) {
            const float expected = payloadBlock[f * channels + c];
            if (memcmp(&captured[f], &expected, sizeof(float)) != 0) return false;
        }
    }
    return true;
}

bool liveBlockSilent(const float* left, const float* right, int frames) {
    for (int f = 0; f < frames; ++f)
        if (left[f] != 0.0f || right[f] != 0.0f) return false;
    return true;
}

/// Read-only: drive the driver against the section the app is publishing and
/// report bit-exactness and cadence. Never publishes a block of its own.
int runAttachMode(const wchar_t* busNameW) {
    char busName[128] = {};
    if (WideCharToMultiByte(CP_UTF8, 0, busNameW, -1, busName, sizeof(busName),
                            nullptr, nullptr) <= 0)
        return 1;

    LiveBus live;
    if (!live.attach(busNameW)) {
        wprintf(L"FATAL: no live section Local\\JockeyBox.Bus.%s; start the app with the "
                L"virtual ASIO flag on first\n", busNameW);
        live.close();
        return 1;
    }

    wprintf(L"\n== live ring watch (read-only) ==\n");
    wprintf(L"section           : Local\\JockeyBox.Bus.%s\n", busNameW);
    wprintf(L"geometry          : %d Hz, %d ch, block %d frames, %d blocks (%.0f ms)\n",
            live.sampleRate, live.channels, live.blockFrames, live.capacity,
            1000.0 * live.capacity * live.blockFrames / live.sampleRate);
    if (live.channels != 2 || live.blockFrames > kLiveMaxFrames) {
        wprintf(L"FATAL: the driver cannot serve this geometry (%d ch, block %d)\n",
                live.channels, live.blockFrames);
        live.close();
        return 1;
    }

    auto captures = std::make_unique<LiveCaptures>();
    g_live = captures.get();
    g_liveWrite = live.write;
    g_livePayload = live.payload;
    g_liveBlockBytes = int64_t(live.blockFrames) * live.channels * 4;
    g_liveCapacity = live.capacity;

    SetEnvironmentVariableA("JOCKEYBOX_ASIO_BUS", busName);
    jb::asio::IASIO* driver = nullptr;
    {
        void* raw = nullptr;
        const HRESULT hr = CoCreateInstance(jb::asio::kDriverClsid, nullptr,
                                            CLSCTX_INPROC_SERVER,
                                            jb::asio::kDriverClsid, &raw);
        if (FAILED(hr) || !raw) {
            wprintf(L"FATAL: CoCreateInstance 0x%08lX\n", static_cast<unsigned long>(hr));
            g_live = nullptr;
            live.close();
            return 1;
        }
        driver = static_cast<jb::asio::IASIO*>(raw);
        if (!driver->init(nullptr)) {
            wprintf(L"FATAL: driver init failed\n");
            driver->Release();
            g_live = nullptr;
            live.close();
            return 1;
        }
    }

    // ASIO only accepts power-of-two buffer sizes, so the host buffer is the next
    // power of two at or above the ring block (512 for the app's 480). The driver
    // copies only what fits: the first ring.blockFrames samples are exactly one
    // block, the tail is untouched. Compare that prefix, never the padding.
    long bufferSize = 1;
    while (bufferSize < live.blockFrames) bufferSize <<= 1;
    g_liveFrames = live.blockFrames;

    jb::asio::AsioCallbacks callbacks = {onBufferSwitch, onSampleRateChanged,
                                         onAsioMessage, onBufferSwitchTimeInfo};
    jb::asio::AsioBufferInfo infos[4] = {};
    for (long i = 0; i < 2; ++i) {
        infos[i].isInput = jb::asio::kAsioTrue;
        infos[i].channelNum = i;
        infos[2 + i].isInput = jb::asio::kAsioFalse;
        infos[2 + i].channelNum = i;
    }

    if (driver->createBuffers(infos, 4, bufferSize, &callbacks) != jb::asio::kAseOk) {
        wprintf(L"FATAL: createBuffers(%ld) failed\n", bufferSize);
        driver->Release();
        g_live = nullptr;
        live.close();
        return 1;
    }
    for (int ch = 0; ch < 2; ++ch) {
        g_inBuf[ch][0] = static_cast<const float*>(infos[ch].buffers[0]);
        g_inBuf[ch][1] = static_cast<const float*>(infos[ch].buffers[1]);
    }

    const int64_t writeBefore = live.writeIndex();
    const auto t0 = std::chrono::steady_clock::now();
    if (driver->start() != jb::asio::kAseOk) {
        wprintf(L"FATAL: start failed\n");
        driver->Release();
        g_live = nullptr;
        live.close();
        return 1;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(3000));
    const double seconds =
        std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    const int64_t writeAfter = live.writeIndex();
    driver->stop();
    const long captured = captures->count.load(std::memory_order_acquire);
    driver->Release();
    g_live = nullptr;

    const int frames = live.blockFrames;
    long matched = 0, mismatched = 0, silent = 0;
    int lag[9] = {};
    for (long i = 0; i < captured; ++i) {
        const float* left = captures->left[i];
        const float* right = captures->right[i];

        if (liveBlockSilent(left, right, frames)) {
            // A ring lap is served as silence by the driver, and is counted as
            // such rather than as a differing block.
            silent++;
            continue;
        }
        bool found = false;
        for (int k = 0; k < captures->refCount[i]; ++k) {
            if (liveBlockEquals(captures->ref[i][k], left, right, frames, live.channels)) {
                lag[k + 1]++;
                matched++;
                found = true;
                break;
            }
        }
        if (!found) mismatched++;
    }

    const int64_t published = writeAfter - writeBefore;
    const double blockRate = live.sampleRate / double(live.blockFrames);
    const double callbacksPerSecond = captured / seconds;
    const double publishedPerSecond = published / seconds;

    wprintf(L"\n-- %.2f s watched while the app rendered --\n", seconds);
    wprintf(L"buffer size       : %ld frames (first %d = one ring block; the rest is padding)\n",
            bufferSize, frames);
    wprintf(L"blocks published  : %lld by the app (%.1f/s; block rate is %.0f/s)\n",
            static_cast<long long>(published), publishedPerSecond, blockRate);
    wprintf(L"bufferSwitch      : %ld calls (%.1f/s)\n", captured, callbacksPerSecond);
    wprintf(L"bit-exact blocks  : %ld/%ld matched, %ld mismatched\n", matched, captured,
            mismatched);
    wprintf(L"lag of matched    : 1=%d 2=%d 3=%d 4=%d 5=%d\n", lag[1], lag[2], lag[3],
            lag[4], lag[5]);
    wprintf(L"non-silent blocks : %ld/%ld\n", captured - silent, captured);

    const bool cadenceOk = callbacksPerSecond > blockRate * 0.9 &&
                           callbacksPerSecond < blockRate * 1.1;
    const bool ringFed = publishedPerSecond > blockRate * 0.9;
    // Silence is not a pass: an all-zero run would compare equal to an empty ring.
    const bool signal = silent < captured;
    // Every block the driver actually served must match; a lap it could not serve
    // (the host buffer's cadence differs from the ring's) is counted as skipped.
    const long served = captured - silent;
    const bool exact = mismatched == 0 && served > 0 && matched == served;
    const bool passed = exact && cadenceOk && ringFed && signal;

    wprintf(L"\nlive feed         : %s\n",
            passed ? L"PASS (every served block bit-exact, paced, non-silent)"
                   : (exact ? L"FAIL (bit-exact but the run is not a live feed)"
                            : L"FAIL (served blocks differ)"));
    if (silent > 0)
        wprintf(L"note              : %ld callback(s) were ring laps (host buffer %.0f/s vs "
                L"ring %.0f/s)\n", silent, callbacksPerSecond, blockRate);
    live.close();
    return passed ? 0 : 1;
}


// Creates the CLSID registration's driver object for one bus test.
jb::asio::IASIO* openDriver(const char* busName) {
    SetEnvironmentVariableA("JOCKEYBOX_ASIO_BUS", busName);
    void* raw = nullptr;
    if (FAILED(CoCreateInstance(jb::asio::kDriverClsid, nullptr, CLSCTX_INPROC_SERVER,
                                jb::asio::kDriverClsid, &raw)) ||
        !raw)
        return nullptr;
    auto* driver = static_cast<jb::asio::IASIO*>(raw);
    driver->init(nullptr);
    return driver;
}

struct AudioPathResult {
    bool bitExactRan = false;
    bool bitExactPassed = false;
    long matched = 0;
    long mismatched = 0;
    long expectedBlocks = 0;
    bool gapPassed = false;
    long long underrunsBeforeGap = 0;
    long long underrunsAfterGap = 0;
    double callbacksPerSecond = 0.0;
    bool crossLanguageRan = false;
    bool crossLanguagePassed = false;
    long crossLanguageMatched = 0;
    long crossLanguageMismatched = 0;
    long crossLanguageSilent = 0;
};

// Spawns the real C# VirtualBusWriter (BusWriterCli) so the contract is proven
// across the language boundary, not just against a C++ re-implementation.
bool spawnBusWriter(const wchar_t* exe, const wchar_t* busName, PROCESS_INFORMATION* pi) {
    wchar_t cmd[512];
    swprintf_s(cmd, L"\"%s\" 750 %s", exe, busName);
    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    return CreateProcessW(nullptr, cmd, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
                          nullptr, &si, pi) != FALSE;
}

AudioPathResult runAudioPathTests(const wchar_t* harnessDir) {
    AudioPathResult r;
    wprintf(L"\n== audio path: ring -> ASIO inputs ==\n");

    auto captures = std::unique_ptr<Captures>(new Captures());
    wchar_t busName[64];
    char busNameA[64];
    swprintf_s(busName, L"harness-%lu", GetCurrentProcessId());
    sprintf_s(busNameA, "harness-%lu", GetCurrentProcessId());

    TestBus bus;
    if (!bus.create(busName)) {
        wprintf(L"FATAL: could not create section %s\n", bus.mapName);
        return r;
    }

    jb::asio::IASIO* driver = openDriver(busNameA);
    if (!driver) {
        wprintf(L"FATAL: could not create the driver for the bus test\n");
        bus.close();
        return r;
    }

    jb::asio::AsioCallbacks callbacks = {onBufferSwitch, onSampleRateChanged,
                                         onAsioMessage, onBufferSwitchTimeInfo};
    jb::asio::AsioBufferInfo infos[4] = {};
    for (long i = 0; i < 2; ++i) {
        infos[i].isInput = jb::asio::kAsioTrue;
        infos[i].channelNum = i;
        infos[2 + i].isInput = jb::asio::kAsioFalse;
        infos[2 + i].channelNum = i;
    }
    if (driver->createBuffers(infos, 4, kBusFrames, &callbacks) != jb::asio::kAseOk) {
        wprintf(L"FATAL: createBuffers failed in the bus test\n");
        driver->Release();
        bus.close();
        return r;
    }
    for (int ch = 0; ch < kBusChannels; ++ch) {
        g_inBuf[ch][0] = static_cast<const float*>(infos[ch].buffers[0]);
        g_inBuf[ch][1] = static_cast<const float*>(infos[ch].buffers[1]);
    }

    char error[124] = {};
    driver->getErrorMessage(error);
    wprintf(L"bus section       : %s\n", bus.mapName);
    wprintf(L"getErrorMessage   : %hs\n", error);

    g_capture = captures.get();
    if (driver->start() != jb::asio::kAseOk) {
        wprintf(L"FATAL: start failed in the bus test\n");
        g_capture = nullptr;
        driver->Release();
        bus.close();
        return r;
    }

    // --- 1. bit-exactness, in lockstep with the reader --------------------
    // One block is published only once the driver has consumed the previous
    // one, so the sequence is deterministic: no skips and no repeats unless
    // the driver is wrong.
    const int64_t wanted = 64;
    int lockstepTimeouts = 0;
    int claimedSlot = -1;
    LARGE_INTEGER freq;
    QueryPerformanceFrequency(&freq);
    for (int64_t block = 0; block < wanted; ++block) {
        bus.publish(block);
        const int slot = bus.activeSlot();
        if (slot < 0) {
            lockstepTimeouts++;
            break;
        }
        claimedSlot = slot;
        const long long limit = qpcTicks() + freq.QuadPart / 5;  // 200 ms
        while (bus.slotValue(slot, jb::asio::bus::kSlotReadBlockIndex) < block + 1 &&
               qpcTicks() < limit) {
            Sleep(0);
        }
        if (bus.slotValue(slot, jb::asio::bus::kSlotReadBlockIndex) < block + 1)
            lockstepTimeouts++;
    }

    // --- 2. gap: stop writing mid-run ----------------------------------------
    // The driver's clock keeps running with nothing to read: every later buffer
    // must be exactly zero, and the reader slot must tick once per callback.
    const long long underrunsBefore =
        claimedSlot >= 0 ? bus.slotValue(claimedSlot, jb::asio::bus::kSlotUnderruns) : -1;
    Sleep(150);  // ~56 callbacks at 128 frames
    driver->stop();
    const long long underrunsAfter =
        claimedSlot >= 0 ? bus.slotValue(claimedSlot, jb::asio::bus::kSlotUnderruns) : -1;
    g_capture = nullptr;

    const long captured = captures->count.load(std::memory_order_acquire);
    long matched = 0, mismatched = 0, silent = 0;
    int64_t expect = 0;
    for (long i = 0; i < captured && expect < wanted; ++i) {
        if (blockEquals(captures->block[i], expect)) {
            matched++;
            expect++;
        } else if (blockSilent(captures->block[i])) {
            silent++;
        } else {
            if (mismatched == 0) reportMismatch(captures->block[i], expect);
            mismatched++;
        }
    }

    r.bitExactRan = true;
    r.expectedBlocks = wanted;
    r.matched = matched;
    r.mismatched = mismatched;
    r.bitExactPassed = (matched == wanted && mismatched == 0 && lockstepTimeouts == 0);

    const long gapWindow = captured < 15 ? captured : 15;
    bool gapSilent = gapWindow > 0;
    for (long i = captured - gapWindow; i < captured; ++i)
        if (!blockSilent(captures->block[i])) gapSilent = false;
    r.underrunsBeforeGap = underrunsBefore;
    r.underrunsAfterGap = underrunsAfter;
    r.gapPassed = gapSilent && underrunsAfter - underrunsBefore >= 10;

    // --- 3. cadence while streaming -------------------------------------------
    // A free-running writer at exactly the driver's block rate. The cadence is
    // the clock thread's job: a blocking read path would show up here.
    g_callbacks.store(0);
    driver->start();
    const long long cadenceStart = qpcTicks();
    std::thread writer([&bus] {
        const long long t0 = qpcTicks();
        const double period = double(kBusFrames) / double(kBusSampleRate);
        int64_t block = bus.write->load(std::memory_order_acquire);
        for (int i = 0; i < 750; ++i) {  // 2 seconds at 375 blocks/s
            const double due = (i + 1) * period;
            while (qpcSeconds(qpcTicks() - t0) < due) {
                const double left = due - qpcSeconds(qpcTicks() - t0);
                if (left > 0.001)
                    Sleep(0);
                else
                    YieldProcessor();
            }
            bus.publish(block++);
        }
    });
    writer.join();
    const double cadenceSeconds = qpcSeconds(qpcTicks() - cadenceStart);
    const long cadenceCallbacks = g_callbacks.load();
    driver->stop();
    r.callbacksPerSecond = cadenceSeconds > 0
        ? double(cadenceCallbacks) / cadenceSeconds
        : 0.0;

    driver->disposeBuffers();
    driver->Release();
    bus.close();

    // --- 4. cross-language: the real C# writer --------------------------------
    wchar_t cli[512];
    swprintf_s(cli, L"%sbuswriter\\BusWriterCli.exe", harnessDir);
    if (GetFileAttributesW(cli) == INVALID_FILE_ATTRIBUTES) {
        wprintf(L"cross-language    : not run (%s not found)\n", cli);
        return r;
    }

    auto xcaptures = std::unique_ptr<Captures>(new Captures());
    wchar_t xname[64];
    char xnameA[64];
    swprintf_s(xname, L"harness-cli-%lu", GetCurrentProcessId());
    sprintf_s(xnameA, "harness-cli-%lu", GetCurrentProcessId());

    // The C# writer creates and owns the section; the driver joins it.
    PROCESS_INFORMATION pi = {};
    if (!spawnBusWriter(cli, xname, &pi)) {
        wprintf(L"cross-language    : not run (could not spawn %s)\n", cli);
        return r;
    }
    Sleep(150);  // let it create the section and publish its first blocks

    // The C# writer creates and owns the section; the driver joins it. Poll for
    // it, because a .NET host cold start can take longer than one block period.
    TestBus xbus;
    wchar_t xmap[160];
    swprintf_s(xmap, L"Local\\JockeyBox.Bus.%s", xname);
    for (int attempt = 0; attempt < 100 && !xbus.map; ++attempt) {
        xbus.map = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, xmap);
        if (!xbus.map) Sleep(50);
    }
    jb::asio::IASIO* xdriver = xbus.map ? openDriver(xnameA) : nullptr;
    if (xdriver) {
        const jb::asio::AsioError xcb = xdriver->createBuffers(infos, 4, kBusFrames, &callbacks);
        if (xcb == jb::asio::kAseOk) {
            for (int ch = 0; ch < kBusChannels; ++ch) {
                g_inBuf[ch][0] = static_cast<const float*>(infos[ch].buffers[0]);
                g_inBuf[ch][1] = static_cast<const float*>(infos[ch].buffers[1]);
            }
            g_capture = xcaptures.get();
            xdriver->start();
            WaitForSingleObject(pi.hProcess, 5000);
            xdriver->stop();
            g_capture = nullptr;
            r.crossLanguageRan = true;
        }
        xdriver->disposeBuffers();
        xdriver->Release();
    } else {
        wprintf(L"cross-language    : section open=%p driver=%p\n", (void*)xbus.map,
                (void*)xdriver);
    }
    if (xbus.map) CloseHandle(xbus.map);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    if (r.crossLanguageRan) {
        const long n = xcaptures->count.load(std::memory_order_acquire);
        int64_t last = -1;
        long matchedX = 0, mismatchedX = 0, silentX = 0;
        for (long i = 0; i < n; ++i) {
            if (blockSilent(xcaptures->block[i])) {
                silentX++;
                continue;
            }
            bool found = false;
            for (int64_t k = last + 1; k < 750 && !found; ++k) {
                if (blockEquals(xcaptures->block[i], k)) {
                    last = k;
                    matchedX++;
                    found = true;
                }
            }
            if (!found) mismatchedX++;
        }
        r.crossLanguageMatched = matchedX;
        r.crossLanguageMismatched = mismatchedX;
        r.crossLanguageSilent = silentX;
        r.crossLanguagePassed = (matchedX >= 300 && mismatchedX == 0);
    }

    return r;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    wchar_t dllPath[MAX_PATH] = {};
    if (argc > 1) {
        wcscpy_s(dllPath, argv[1]);
    } else {
        GetModuleFileNameW(nullptr, dllPath, MAX_PATH);
        wchar_t* slash = wcsrchr(dllPath, L'\\');
        if (slash) *slash = L'\0';
        wcscat_s(dllPath, L"\\JockeyBox.Asio.dll");
    }

    wprintf(L"DLL              : %s\n", dllPath);
    if (!registerInHkcu(dllPath)) {
        wprintf(L"FATAL: could not write HKCU InprocServer32\n");
        return 1;
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) {
        wprintf(L"FATAL: CoInitializeEx 0x%08lX\n", static_cast<unsigned long>(hr));
        return 1;
    }

    // --attach [busName]: read-only watch of the section a running app publishes.
    // Never creates the section and never writes a block, so the numbers are the
    // app's own output and not the harness's.
    if (argc > 2 && wcscmp(argv[2], L"--attach") == 0) {
        const wchar_t* busName = argc > 3 ? argv[3] : L"main";
        const int rc = runAttachMode(busName);
        CoUninitialize();
        unregisterInHkcu();
        return rc;
    }

    void* raw = nullptr;
    hr = CoCreateInstance(jb::asio::kDriverClsid, nullptr, CLSCTX_INPROC_SERVER,
                          jb::asio::kDriverClsid, &raw);
    if (FAILED(hr) || !raw) {
        wprintf(L"FATAL: CoCreateInstance 0x%08lX\n", static_cast<unsigned long>(hr));
        CoUninitialize();
        return 1;
    }
    auto* driver = static_cast<jb::asio::IASIO*>(raw);
    wprintf(L"CoCreateInstance : OK\n\n");

    // ---- index pass: prove the slot numbering, never using the interface ----
    void** vtbl = *reinterpret_cast<void***>(raw);
    auto slotInit = reinterpret_cast<jb::asio::AsioBool (*)(void*, void*)>(vtbl[3]);
    auto slotVersion = reinterpret_cast<long (*)(void*)>(vtbl[5]);
    auto slotChannels = reinterpret_cast<jb::asio::AsioError (*)(void*, long*, long*)>(vtbl[9]);
    auto slotBufferSize = reinterpret_cast<jb::asio::AsioError (*)(void*, long*, long*, long*, long*)>(vtbl[11]);

    const jb::asio::AsioBool inited = slotInit(raw, nullptr);
    const long version = slotVersion(raw);
    long inCh = 0, outCh = 0;
    const jb::asio::AsioError chErr = slotChannels(raw, &inCh, &outCh);
    long bMin = 0, bMax = 0, bPref = 0, bGran = 0;
    const jb::asio::AsioError bsErr = slotBufferSize(raw, &bMin, &bMax, &bPref, &bGran);

    wprintf(L"-- index pass (raw vtable slots) --\n");
    wprintf(L"slot  3 init              -> %d\n", inited);
    wprintf(L"slot  5 getDriverVersion  -> %ld\n", version);
    wprintf(L"slot  9 getChannels       -> %d (in=%ld out=%ld)\n", chErr, inCh, outCh);
    wprintf(L"slot 11 getBufferSize     -> %d (min=%ld max=%ld pref=%ld gran=%ld)\n",
            bsErr, bMin, bMax, bPref, bGran);

    // ---- normal pass: what a host compiled against the header sees ----
    char name[32] = {};
    char error[124] = {};
    driver->getDriverName(name);
    driver->getErrorMessage(error);
    jb::asio::AsioSampleRate rate = 0.0;
    driver->getSampleRate(&rate);

    wprintf(L"\n-- declared interface --\n");
    wprintf(L"getDriverName     : %hs\n", name);
    wprintf(L"getDriverVersion  : %ld\n", driver->getDriverVersion());
    wprintf(L"getErrorMessage   : %hs\n", error);
    wprintf(L"getSampleRate     : %.0f\n", rate);
    wprintf(L"canSampleRate 48k : %d\n", driver->canSampleRate(48000.0));
    wprintf(L"canSampleRate 44k : %d\n", driver->canSampleRate(44100.0));

    jb::asio::AsioChannelInfo ci = {};
    ci.channel = 0;
    ci.isInput = jb::asio::kAsioTrue;
    const jb::asio::AsioError ciErr = driver->getChannelInfo(&ci);
    wprintf(L"getChannelInfo in0: %d (%hs, type=%d, active=%d)\n", ciErr, ci.name,
            ci.type, ci.isActive);

    jb::asio::AsioCallbacks callbacks = {onBufferSwitch, onSampleRateChanged,
                                         onAsioMessage, onBufferSwitchTimeInfo};

    jb::asio::AsioBufferInfo infos[4] = {};
    for (long i = 0; i < 2; ++i) {
        infos[i].isInput = jb::asio::kAsioTrue;
        infos[i].channelNum = i;
        infos[2 + i].isInput = jb::asio::kAsioFalse;
        infos[2 + i].channelNum = i;
    }

    const long bufferSize = bPref ? bPref : 128;
    const jb::asio::AsioError cbErr =
        driver->createBuffers(infos, 4, bufferSize, &callbacks);
    wprintf(L"createBuffers     : %d (4 channels @ %ld frames)\n", cbErr, bufferSize);
    if (cbErr != jb::asio::kAseOk) {
        driver->Release();
        CoUninitialize();
        return 1;
    }

    const float* out0 = static_cast<const float*>(infos[2].buffers[0]);
    const float* out0b = static_cast<const float*>(infos[2].buffers[1]);
    wprintf(L"out buffers       : %p / %p, first sample %.1f / %.1f\n", (void*)out0,
            (void*)out0b, out0[0], out0b[0]);

    const jb::asio::AsioError startErr = driver->start();
    wprintf(L"start             : %d\n", startErr);

    jb::asio::AsioSamples pos0 = {};
    jb::asio::AsioTimeStamp ts0 = {};
    driver->getSamplePosition(&pos0, &ts0);

    const auto t0 = std::chrono::steady_clock::now();
    std::this_thread::sleep_for(std::chrono::milliseconds(1000));
    const auto elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();

    jb::asio::AsioSamples pos1 = {};
    driver->getSamplePosition(&pos1, &ts0);
    const long calls = g_callbacks.load();

    const jb::asio::AsioError stopErr = driver->stop();
    const double seconds = elapsed;
    const int64_t frames0 = (int64_t(pos0.hi) << 32) | pos0.lo;
    const int64_t frames1 = (int64_t(pos1.hi) << 32) | pos1.lo;

    wprintf(L"stop              : %d\n", stopErr);
    wprintf(L"\n-- 1 second run --\n");
    wprintf(L"elapsed           : %.3f s\n", seconds);
    wprintf(L"bufferSwitch      : %ld calls\n", calls);
    wprintf(L"cadence           : %.1f callbacks/s (expected %.1f)\n",
            double(calls) / seconds, 48000.0 / double(bufferSize));
    wprintf(L"frames counted    : %lld (delta %lld)\n", frames1, frames1 - frames0);
    wprintf(L"last buffer index : %ld (must be 0 or 1)\n", g_lastIndex);

    driver->disposeBuffers();
    driver->Release();

    // ---- audio path: ring -> ASIO inputs ----------------------------------
    wchar_t harnessDir[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, harnessDir, MAX_PATH);
    if (wchar_t* slash = wcsrchr(harnessDir, L'\\')) *slash = L'\0';
    wcscat_s(harnessDir, L"\\");

    const AudioPathResult audio = runAudioPathTests(harnessDir);

    wprintf(L"\n== audio path summary ==\n");
    wprintf(L"bit-exact blocks  : %ld/%ld matched, %ld mismatched\n", audio.matched,
            audio.expectedBlocks, audio.mismatched);
    wprintf(L"gap -> silence    : %s (underruns %lld -> %lld)\n",
            audio.gapPassed ? L"yes" : L"NO",
            audio.underrunsBeforeGap, audio.underrunsAfterGap);
    wprintf(L"cadence streaming : %.1f callbacks/s\n", audio.callbacksPerSecond);
    if (audio.crossLanguageRan) {
        wprintf(L"cross-language    : ran (%ld matched, %ld mismatched, %ld silence)\n",
                audio.crossLanguageMatched, audio.crossLanguageMismatched,
                audio.crossLanguageSilent);
    } else {
        wprintf(L"cross-language    : not run\n");
    }

    const bool audioPassed = audio.bitExactPassed && audio.gapPassed &&
                             audio.callbacksPerSecond > 350.0 &&
                             audio.callbacksPerSecond < 400.0 &&
                             (!audio.crossLanguageRan || audio.crossLanguagePassed);

    CoUninitialize();

    if (argc > 2 && wcscmp(argv[2], L"--keep") == 0) {
        wprintf(L"\nHKCU registration kept.\n");
    } else {
        unregisterInHkcu();
        wprintf(L"\nHKCU registration removed.\n");
    }
    return (calls > 0) ? 0 : 2;
}
