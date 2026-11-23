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
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>
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

// --- soak marker reference --------------------------------------------------
// The writer stamps every input frame with its absolute index (mod 65536), so
// the delivered buffer names the exact input frame it carries. Pairing that with
// the writer's own frame counter gives the ring delay in frames, independently
// of the driver's lag arithmetic. Accumulated per driver span (4 s of frames) so
// the comparison is the same window and the same statistic as getLatencies.
std::atomic<bool> g_markerActive{false};
std::atomic<int64_t>* g_markerWrite = nullptr;
std::atomic<int> g_markerBlockFrames{0};
std::atomic<int> g_markerFrames{0};
std::atomic<int64_t> g_markerOut{0};
std::atomic<int64_t> g_markerPeriod{0};
std::atomic<int64_t> g_markerPeriodEnd{0};
std::atomic<double> g_markerSum{0.0};
std::atomic<long long> g_markerCount{0};
std::atomic<double> g_markerSpanMean{-1.0};
std::atomic<int64_t> g_markerLag{0};
std::atomic<int64_t> g_markerMin{-1};
std::atomic<int64_t> g_markerMax{0};
std::atomic<int64_t> g_markerFinal{0};

// Read-only attach mode: what the driver handed the host during a live run.
constexpr int kLiveMaxFrames = 1024;     // largest host buffer the driver accepts
constexpr long kLiveMaxCaptures = 1400;  // 3 s at 128 frames (375 callbacks/s)
constexpr int kLiveMaxRefBlocks = 8;     // the ring's capacity: any block it can still hold

// The served stream is checked at an absolute frame position, so this records
// every frame the host was handed. A 512- or 1024-frame callback no longer lines
// up with the 480-frame ring block, so comparing whole blocks per callback
// cannot see anything but a false mismatch. Per callback it also snapshots the
// blocks the ring still held: the ring is a moving target, so reading the payload
// after the run would compare against blocks already overwritten.
struct LiveCaptures {
    std::atomic<long> count{0};
    int frames = 0;        // host buffer size
    int blockFrames = 0;   // ring block size
    int channels = 0;
    int capacity = 0;
    std::vector<int64_t> writeIndex;
    std::vector<float> left;    // [kLiveMaxCaptures][frames]
    std::vector<float> right;   // [kLiveMaxCaptures][frames]
    std::vector<int> refCount;  // blocks kept per callback
    std::vector<float> ref;     // [kLiveMaxCaptures][kLiveMaxRefBlocks][blockFrames*channels]
};

LiveCaptures* g_live = nullptr;
std::atomic<int64_t>* g_liveWrite = nullptr;
const uint8_t* g_livePayload = nullptr;
int64_t g_liveBlockBytes = 0;
int g_liveCapacity = 0;
long g_liveRequestedFrames = 0;  // 0 = next power of two at or above the ring block

// Every published block carries its own index in every sample, so a wrong
// block, a stale block or a byte-level layout error is visible in the numbers.
float sampleFor(int64_t block, int frame, int channel) {
    return static_cast<float>(block * 1000 + frame * 2 + channel);
}

void onBufferSwitch(long doubleBufferIndex, jb::asio::AsioBool directProcess) {
    (void)directProcess;
    g_lastIndex = doubleBufferIndex;
    g_callbacks.fetch_add(1, std::memory_order_relaxed);

    if (g_markerActive.load(std::memory_order_relaxed) && g_markerWrite) {
        const float* b = g_inBuf[0][doubleBufferIndex & 1];
        const int frames = g_markerFrames.load(std::memory_order_relaxed);
        const int bf = g_markerBlockFrames.load(std::memory_order_relaxed);
        int64_t out = g_markerOut.fetch_add(frames, std::memory_order_relaxed) + frames;
        bool chain = b != nullptr && frames > 0;
        const long v0 = b ? static_cast<long>(b[0] + 0.5f) : 0;
        for (int i = 1; chain && i < 4 && i < frames; ++i) {
            const long vi = static_cast<long>(b[i] + 0.5f);
            if (vi != (v0 + i) % 65536) chain = false;
        }
        if (chain && v0 >= 0 && v0 < 65536) {
            const int64_t wFrames =
                g_markerWrite->load(std::memory_order_acquire) * bf;
            int64_t d = (wFrames - v0) % 65536;
            if (d < 0) d += 65536;
            g_markerLag.store(d, std::memory_order_relaxed);
            g_markerFinal.store(d, std::memory_order_relaxed);
            int64_t mn = g_markerMin.load(std::memory_order_relaxed);
            if (mn < 0 || d < mn) g_markerMin.store(d, std::memory_order_relaxed);
            if (d > g_markerMax.load(std::memory_order_relaxed))
                g_markerMax.store(d, std::memory_order_relaxed);
            g_markerSum.store(g_markerSum.load(std::memory_order_relaxed) + double(d),
                              std::memory_order_relaxed);
            g_markerCount.fetch_add(1, std::memory_order_relaxed);
            if (out >= g_markerPeriodEnd.load(std::memory_order_relaxed)) {
                const long long n = g_markerCount.load(std::memory_order_relaxed);
                const double sum = g_markerSum.load(std::memory_order_relaxed);
                if (n > 0) g_markerSpanMean.store(sum / double(n), std::memory_order_relaxed);
                g_markerSum.store(0.0, std::memory_order_relaxed);
                g_markerCount.store(0, std::memory_order_relaxed);
                g_markerPeriodEnd.store(
                    out + g_markerPeriod.load(std::memory_order_relaxed),
                    std::memory_order_relaxed);
            }
        }
    }

    LiveCaptures* live = g_live;
    if (live) {
        const long seq = live->count.load(std::memory_order_relaxed);
        if (seq < kLiveMaxCaptures) {
            const int frames = live->frames;
            const size_t at = static_cast<size_t>(seq) * static_cast<size_t>(frames);
            for (int ch = 0; ch < 2; ++ch) {
                const float* src = g_inBuf[ch][doubleBufferIndex & 1];
                float* dst = (ch == 0 ? live->left.data() : live->right.data()) + at;
                if (src)
                    memcpy(dst, src, sizeof(float) * frames);
                else
                    memset(dst, 0, sizeof(float) * frames);
            }
            const int64_t w = g_liveWrite->load(std::memory_order_acquire);
            live->writeIndex[seq] = w;
            const int span = g_liveCapacity < kLiveMaxRefBlocks ? g_liveCapacity
                                                               : kLiveMaxRefBlocks;
            const int64_t blockFloats =
                static_cast<int64_t>(live->blockFrames) * live->channels;
            int kept = 0;
            for (int k = 0; k < span; ++k) {
                const int64_t idx = w - 1 - k;
                if (idx < 0) break;
                float* dst = live->ref.data() +
                             (static_cast<size_t>(seq) * kLiveMaxRefBlocks + k) * blockFloats;
                memcpy(dst, g_livePayload + (idx % g_liveCapacity) * g_liveBlockBytes,
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
    int blockFrames = kBusFrames;
    int capacityBlocks = kBusCapacity;
    wchar_t mapName[160] = {};

    bool create(const wchar_t* busName, int framesPerBlock = kBusFrames,
                int capacity = kBusCapacity) {
        blockFrames = framesPerBlock;
        capacityBlocks = capacity;
        swprintf_s(mapName, L"Local\\JockeyBox.Bus.%s", busName);
        const int64_t size = jb::asio::bus::kPayloadOffset +
                             int64_t(capacityBlocks) * blockFrames * kBusChannels * 4;
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
        put32(jb::asio::bus::kBlockFramesOffset, blockFrames);
        put32(jb::asio::bus::kCapacityBlocksOffset, capacityBlocks);
        put32(jb::asio::bus::kReaderSlotCountFieldOffset, jb::asio::bus::kReaderSlotCount);
        put32(jb::asio::bus::kPayloadOffsetFieldOffset, jb::asio::bus::kPayloadOffset);

        write = reinterpret_cast<std::atomic<int64_t>*>(
            base + jb::asio::bus::kWriteBlockIndexOffset);
        write->store(0, std::memory_order_release);
        payload = base + jb::asio::bus::kPayloadOffset;
        blockBytes = int64_t(blockFrames) * kBusChannels * 4;
        return true;
    }

    bool marker = false;  // soak: write a per-frame delay marker instead of sampleFor

    void publish(int64_t block) {
        float* dst = reinterpret_cast<float*>(
            payload + (block % capacityBlocks) * blockBytes);
        for (int f = 0; f < blockFrames; ++f)
            for (int c = 0; c < kBusChannels; ++c)
                dst[f * kBusChannels + c] =
                    marker ? static_cast<float>((block * blockFrames + f) % 65536)
                           : sampleFor(block, f, c);
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

    // The host buffer is an explicit size from the command line (128/256/512/1024)
    // when one was given, so the same live run can be repeated at every ASIO
    // buffer size, otherwise the next power of two at or above the ring block.
    // The reader is sample-accurate, so the host buffer has no relation to the
    // ring block and the whole buffer is real audio, not a block plus padding.
    long bufferSize = 1;
    while (bufferSize < live.blockFrames) bufferSize <<= 1;
    if (g_liveRequestedFrames > 0) bufferSize = g_liveRequestedFrames;
    if (bufferSize > kLiveMaxFrames) {
        wprintf(L"FATAL: host buffer %ld exceeds the harness capture (%d)\n",
                bufferSize, kLiveMaxFrames);
        live.close();
        return 1;
    }

    const size_t slots = static_cast<size_t>(kLiveMaxCaptures);
    captures->frames = static_cast<int>(bufferSize);
    captures->blockFrames = live.blockFrames;
    captures->channels = live.channels;
    captures->capacity = live.capacity;
    captures->writeIndex.assign(slots, 0);
    captures->left.assign(slots * static_cast<size_t>(bufferSize), 0.0f);
    captures->right.assign(slots * static_cast<size_t>(bufferSize), 0.0f);
    captures->refCount.assign(slots, 0);
    captures->ref.assign(slots * kLiveMaxRefBlocks *
                            static_cast<size_t>(live.blockFrames) * live.channels,
                        0.0f);

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

    // Verify the served stream at absolute ring positions, not block by block.
    // The reader streams `frames` frames per callback across ring blocks, so the
    // only honest question is: are the frames the host was handed, concatenated,
    // one contiguous run of the stream the app published, with nothing skipped
    // and nothing repeated?
    const int frames = captures->frames;
    const int B = live.blockFrames;
    const int CH = live.channels;
    const size_t refStride = static_cast<size_t>(kLiveMaxRefBlocks) * B * CH;

    // The copy of ring block `blk` snapshotted at callback i, or null if the ring
    // had already dropped it. The snapshot holds the kLiveMaxRefBlocks newest
    // blocks, i.e. indices w-1 down to w-kept.
    auto refBlock = [&](long i, int64_t blk) -> const float* {
        const int64_t w = captures->writeIndex[i];
        const int64_t age = w - 1 - blk;
        if (age < 0 || age >= captures->refCount[i]) return nullptr;
        return captures->ref.data() + static_cast<size_t>(i) * refStride +
               static_cast<size_t>(age) * B * CH;
    };
    auto framesEqual = [&](long i, int64_t blk, int off, int n, const float* left,
                           const float* right) -> bool {
        const float* rb = refBlock(i, blk);
        if (!rb) return false;
        for (int j = 0; j < n; ++j) {
            if (rb[(off + j) * CH] != left[j]) return false;
            if (CH > 1 && rb[(off + j) * CH + 1] != right[j]) return false;
        }
        return true;
    };
    // Where in the ring stream does this callback start? Searches the snapshot's
    // blocks and every intra-block offset. The probe is long (up to 64 frames, two
    // channels) so a repeated passage cannot be mistaken for the same audio.
    auto locate = [&](long i, int realFrames, const float* left, const float* right) -> int64_t {
        const int probe = realFrames < 64 ? realFrames : 64;
        if (probe <= 0) return -1;
        const int64_t w = captures->writeIndex[i];
        for (int age = 0; age < captures->refCount[i]; ++age) {
            const int64_t blk = w - 1 - age;
            if (blk < 0) continue;
            for (int off = 0; off + probe <= B; ++off)
                if (framesEqual(i, blk, off, probe, left, right)) return blk * B + off;
        }
        return -1;
    };

    long matchedCb = 0, mismatchCb = 0, lapCb = 0, silentCb = 0;
    long laps = 0;
    long firstLapCb = -1;
    long matchedFrames = 0, mismatchFrames = 0, silentFrames = 0;
    int64_t pos = -1;  // global frame position of the next expected frame
    for (long i = 0; i < captured; ++i) {
        const float* left = captures->left.data() + static_cast<size_t>(i) * frames;
        const float* right = captures->right.data() + static_cast<size_t>(i) * frames;

        int last = -1;
        for (int j = 0; j < frames; ++j)
            if (left[j] != 0.0f || right[j] != 0.0f) last = j;
        if (last < 0) {  // the writer had nothing: silence, not a mismatch
            silentCb++;
            silentFrames += frames;
            pos = -1;
            continue;
        }
        const int real = last + 1;
        silentFrames += frames - real;

        if (pos < 0) pos = locate(i, real, left, right);

        bool ok = pos >= 0;
        int bad = 0;
        if (ok) {
            for (int j = 0; j < real; ++j) {
                const int64_t p = pos + j;
                const int64_t blk = p / B;
                const int off = static_cast<int>(p % B);
                const float* rb = refBlock(i, blk);
                if (!rb || rb[off * CH] != left[j] || (CH > 1 && rb[off * CH + 1] != right[j]))
                    bad++;
            }
            ok = bad == 0;
        }

        if (ok) {
            matchedCb++;
            matchedFrames += real;
            pos += frames;
            continue;
        }

        // Not where the stream should have been. If it can be found elsewhere,
        // the reader jumped: that is a ring lap, not corrupt audio.
        mismatchFrames += bad > 0 ? bad : real;
        const int64_t elsewhere = locate(i, real, left, right);
        if (elsewhere >= 0 && elsewhere != pos) {
            lapCb++;
            ++laps;
            if (firstLapCb < 0) firstLapCb = i;
            pos = elsewhere + frames;
        } else {
            mismatchCb++;
            pos = -1;
        }
    }

    const int64_t published = writeAfter - writeBefore;
    const double blockRate = live.sampleRate / double(live.blockFrames);
    const double expectedCadence = live.sampleRate / double(bufferSize);
    const double callbacksPerSecond = captured / seconds;
    const double publishedPerSecond = published / seconds;
    const long totalFrames = captured * frames;
    const double nonSilentRatio = totalFrames ? double(totalFrames - silentFrames) / totalFrames : 0.0;

    wprintf(L"\n-- %.2f s watched while the app rendered --\n", seconds);
    wprintf(L"buffer size       : %ld frames (whole buffer is audio; ring block is %d)\n",
            bufferSize, B);
    wprintf(L"blocks published  : %lld by the app (%.1f/s; block rate is %.0f/s)\n",
            static_cast<long long>(published), publishedPerSecond, blockRate);
    wprintf(L"bufferSwitch      : %ld calls (%.2f/s; expected %.2f/s)\n", captured,
            callbacksPerSecond, expectedCadence);
    wprintf(L"matched callbacks : %ld/%ld (%ld frames)\n", matchedCb, captured, matchedFrames);
    wprintf(L"mismatched        : %ld callbacks (%ld frames)\n", mismatchCb, mismatchFrames);
    wprintf(L"ring laps         : %ld\n", laps);
    if (firstLapCb >= 0)
        wprintf(L"first lap at      : callback %ld of %ld (%.0f ms in)\n", firstLapCb,
                captured, 1000.0 * firstLapCb * frames / live.sampleRate);
    wprintf(L"silent callbacks  : %ld (%ld frames, underrun)\n", silentCb, silentFrames);
    wprintf(L"non-silent ratio  : %.4f\n", nonSilentRatio);

    const bool cadenceOk = callbacksPerSecond > expectedCadence * 0.98 &&
                           callbacksPerSecond < expectedCadence * 1.02;
    const bool ringFed = publishedPerSecond > blockRate * 0.9;
    // The whole point of the fix: no lap and no mismatch at any buffer size.
    const bool clean = laps == 0 && mismatchCb == 0 && mismatchFrames == 0 &&
                       silentCb == 0 && nonSilentRatio > 0.999;
    const bool passed = clean && cadenceOk && ringFed;

    wprintf(L"\nlive feed         : %s\n",
            passed ? L"PASS (zero laps, zero mismatches, cadence on target)"
                   : (clean ? L"FAIL (stream clean but cadence or feed off)"
                            : L"FAIL (laps or mismatched frames)"));
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
    // Reported input latency must equal bufferSize + the ring cushion the reader
    // actually joined behind the writer (not a hard-coded buffer count).
    bool latencyRan = false;
    bool latencyPassed = false;
    long latencyInput = 0;
    long latencyExpectedInput = 0;
    long latencyOutput = 0;
    // Strict no-op: at an exact rate match (the lockstep pass) the trim must not
    // fire, or it would be a new click source. Asserted, not inferred.
    long long trimsMatched = -1;
    long long trimsFinal = -1;
    bool trimNoOpPassed = false;
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

    // --- 0. reported latency follows the cushion ------------------------------
    // TestBus is 16 blocks of 128 frames and createBuffers asked for 128. The
    // driver requests 3*128 frames of cushion; the reader floors that at three
    // blocks, so input = 128 + 3*128 = 512 while the virtual output stays 128.
    {
        long inLat = 0, outLat = 0;
        driver->getLatencies(&inLat, &outLat);
        const long expectedCushion = 3 * kBusFrames;
        const long expectedIn = kBusFrames + expectedCushion;
        r.latencyRan = true;
        r.latencyInput = inLat;
        r.latencyExpectedInput = expectedIn;
        r.latencyOutput = outLat;
        r.latencyPassed = (inLat == expectedIn && outLat == kBusFrames);
        wprintf(L"reported latency  : in %ld (expected %ld), out %ld (expected %ld) %s\n",
                inLat, expectedIn, outLat, static_cast<long>(kBusFrames),
                r.latencyPassed ? L"OK" : L"WRONG");
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

    // Strict no-op assertion: the lockstep pass holds both sides at exactly the
    // same rate, so the rate trim must not have slipped a single sample.
    const long long trimsMatched =
        claimedSlot >= 0 ? bus.slotValue(claimedSlot, jb::asio::bus::kSlotTrims) : -1;
    r.trimsMatched = trimsMatched;
    r.trimNoOpPassed = (trimsMatched == 0);

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
    r.trimsFinal = claimedSlot >= 0 ? bus.slotValue(claimedSlot, jb::asio::bus::kSlotTrims) : -1;

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

// Pins the read guard at exactly `capacity` blocks of lag. At that distance the
// cursor's payload slot is the same slot the writer is about to memcpy (the C#
// writer advances lapped readers before CopyTo, i.e. while the index still names
// the previous block). A reader that only corrects at `> capacity` would serve a
// half-overwritten block, so this drives the writer's memcpy half-done and
// asserts the served frames are the newest complete block or silence, never a
// torn mix of the two blocks sharing that slot.
bool runExactCapacityLagTest() {
    wprintf(L"\n== torn-read guard at exactly capacity ==\n");
    const wchar_t* nameW = L"torn-capacity";
    const char* nameA = "torn-capacity";
    TestBus bus;
    if (!bus.create(nameW)) {
        wprintf(L"exact-capacity  : FAIL (could not create section)\n");
        return false;
    }
    for (int64_t b = 0; b < kBusCapacity; ++b) bus.publish(b);

    jb::asio::bus::RingReader reader;
    if (!reader.attach(nameA, 0)) {
        wprintf(L"exact-capacity  : FAIL (reader attach)\n");
        bus.close();
        return false;
    }
    // Publish `capacity` more blocks with no read: lag is exactly capacity.
    for (int64_t b = kBusCapacity; b < 2 * kBusCapacity; ++b) bus.publish(b);
    const int64_t next = 2 * kBusCapacity;  // the writer's next publish
    // Emulate that publish's payload memcpy in flight: first half of the new
    // block lands in the slot the cursor points at, second half is the old block.
    float* slot = reinterpret_cast<float*>(
        bus.payload + (next % kBusCapacity) * bus.blockBytes);
    const int half = kBusFrames / 2;
    for (int f = 0; f < half; ++f)
        for (int c = 0; c < kBusChannels; ++c)
            slot[f * kBusChannels + c] = sampleFor(next, f, c);

    float out[kBusChannels][kBusFrames] = {};
    float* dst[kBusChannels] = {out[0], out[1]};
    reader.read(dst, kBusChannels, kBusFrames);

    // Newest complete block is next-1. Torn means a nonzero frame that is neither
    // that block's sample nor zero; the in-progress block's samples must not leak.
    bool torn = false, sawData = false;
    for (int f = 0; f < kBusFrames && !torn; ++f) {
        for (int c = 0; c < kBusChannels; ++c) {
            const float v = out[c][f];
            if (v == 0.0f) continue;
            sawData = true;
            if (v != sampleFor(next - 1, f, c)) {
                torn = true;
                break;
            }
        }
    }
    bus.close();
    const bool passed = !torn;
    wprintf(L"exact-capacity  : %s (lag %d blocks = capacity, %s)\n",
            passed ? L"PASS" : L"FAIL",
            kBusCapacity,
            torn ? L"served a torn block"
                 : (sawData ? L"served the newest complete block"
                            : L"served silence"));
    return passed;
}

// ---- drift soak: matched vs mixed route, app geometry and harness ring ------
//
// A mixed route paces the writer off a physical device's WASAPI clock while the
// driver's reader runs on QPC, so a fixed ppm offset between them is the defect.
// The writer here publishes at 48 kHz * (1 + ppm/1e6) on QPC; the reader stays at
// 48 kHz. Condition A is ppm 0 (matched), condition B is a real offset.

struct SoakCell {
    const wchar_t* label = L"";
    int bufferSize = 0;
    int blockFrames = 0;
    int capacity = 0;
    int ppm = 0;
    double seconds = 0.0;
    long callbacks = 0;
    long long underruns = 0;
    long long corrections = 0;
    long long trims = 0;
    long long lagMin = 0;
    long long lagMax = 0;
    long long lagFinal = 0;
    long long lagTarget = 0;
    long reportedLatency = 0;   // getLatencies() input, frames
    long actualLatency = 0;     // bufferSize + observed steady-state lag, frames
    bool latencyOk = false;
    // Cumulative counters at the midpoint, for the first/second half comparison.
    long long halfUnderruns = 0;
    long long halfCorrections = 0;
    long long halfTrims = 0;
    long long halfLag = 0;
    bool halfCaptured = false;
    bool ran = false;
    bool passed = false;
};

struct DriftWriter {
    TestBus* bus = nullptr;
    double rate = 48000.0;
    int64_t prePublished = 0;
    std::atomic<bool> stop{false};
    std::thread thread;

    void start() { thread = std::thread([this] { run(); }); }
    void join() { if (thread.joinable()) thread.join(); }

    void run() noexcept {
        // The test writer must hold a 3-block cushion on a 16-block ring, so a
        // single 10 ms scheduling hiccup is an underrun. Same priority trick the
        // driver's clock thread uses.
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        const long long t0 = qpcTicks();
        int64_t published = prePublished;
        const double bf = double(bus->blockFrames);
        while (!stop.load(std::memory_order_relaxed)) {
            const double due = double(published - prePublished + 1) * bf / rate;
            const double now = qpcSeconds(qpcTicks() - t0);
            if (now >= due) {
                bus->publish(published++);
            } else {
                // Pure spin for the whole wait. Sleep(1) still occasionally
                // returns after a 15.6 ms tick on this host, which at a 3-block
                // cushion is a burst of underruns and a lag jump that the trim
                // then has to chase. The writer is a test instrument, so spend
                // the CPU to make it deterministic.
                YieldProcessor();
            }
        }
    }
};

bool runSoakCell(const wchar_t* label, int blockFrames, int capacity, int bufferSize,
                 int ppm, double seconds, SoakCell& c) {
    c.label = label;
    c.blockFrames = blockFrames;
    c.capacity = capacity;
    c.bufferSize = bufferSize;
    c.ppm = ppm;
    c.seconds = seconds;

    wchar_t nameW[128];
    char nameA[128];
    swprintf_s(nameW, L"soak-%lu-%d-%d-%d-%d", GetCurrentProcessId(), blockFrames,
               capacity, bufferSize, ppm);
    sprintf_s(nameA, "soak-%lu-%d-%d-%d-%d", GetCurrentProcessId(), blockFrames,
              capacity, bufferSize, ppm);

    TestBus bus;
    if (!bus.create(nameW, blockFrames, capacity)) {
        wprintf(L"FATAL: soak could not create section %s\n", bus.mapName);
        return false;
    }

    jb::asio::IASIO* driver = openDriver(nameA);
    if (!driver) {
        wprintf(L"FATAL: soak could not create the driver\n");
        bus.close();
        return false;
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
    if (driver->createBuffers(infos, 4, bufferSize, &callbacks) != jb::asio::kAseOk) {
        wprintf(L"FATAL: soak createBuffers(%d) failed\n", bufferSize);
        driver->Release();
        bus.close();
        return false;
    }
    for (int ch = 0; ch < kBusChannels; ++ch) {
        g_inBuf[ch][0] = static_cast<const float*>(infos[ch].buffers[0]);
        g_inBuf[ch][1] = static_cast<const float*>(infos[ch].buffers[1]);
    }

    // Marker mode: every input frame carries its absolute index (mod 65536), so
    // the delivered buffer names the exact input frame it holds. Delay is
    // writerFrame - deliveredFrame, independent of the driver's lag arithmetic.
    bus.marker = true;
    g_markerActive.store(false);
    g_markerWrite = bus.write;
    g_markerBlockFrames.store(blockFrames);
    g_markerFrames.store(bufferSize);
    g_markerOut.store(0);
    g_markerPeriod.store((4 * 48000 / blockFrames) * blockFrames);
    g_markerPeriodEnd.store((4 * 48000 / blockFrames) * blockFrames);
    g_markerSum.store(0.0);
    g_markerCount.store(0);
    g_markerSpanMean.store(-1.0);
    g_markerLag.store(0);
    g_markerMin.store(-1);
    g_markerMax.store(0);
    g_markerFinal.store(0);

    // Seed half a ring and start the writer *before* the driver, so the reader
    // attaches into a stream that is already running: no startup drain of the
    // cushion and no spurious underrun/correction in the first few callbacks.
    const int64_t pre = capacity / 2;
    for (int64_t b = 0; b < pre; ++b) bus.publish(b);

    DriftWriter writer;
    writer.bus = &bus;
    writer.rate = 48000.0 * (1.0 + double(ppm) / 1e6);
    writer.prePublished = pre;
    writer.start();
    Sleep(50);

    g_capture = nullptr;
    g_callbacks.store(0);
    g_markerActive.store(true);
    if (driver->start() != jb::asio::kAseOk) {
        wprintf(L"FATAL: soak start failed\n");
        writer.stop.store(true);
        writer.join();
        driver->Release();
        bus.close();
        return false;
    }

    int slot = -1;
    for (int i = 0; i < 200 && slot < 0; ++i) {
        slot = bus.activeSlot();
        if (slot < 0) Sleep(1);
    }
    if (slot < 0) {
        wprintf(L"FATAL: soak has no reader slot\n");
        driver->stop();
        driver->disposeBuffers();
        driver->Release();
        bus.close();
        return false;
    }

    long latIn = 0, latOut = 0;
    driver->getLatencies(&latIn, &latOut);
    c.lagTarget = latIn - bufferSize;  // the ring cushion, in frames

    const long long t0 = qpcTicks();
    while (qpcSeconds(qpcTicks() - t0) < seconds) {
        // The marker callback publishes the frame-exact delay it measured.
        Sleep(10);
        const long long lag = g_markerFinal.load(std::memory_order_relaxed);
        if (!c.halfCaptured && qpcSeconds(qpcTicks() - t0) >= seconds * 0.5) {
            c.halfUnderruns = bus.slotValue(slot, jb::asio::bus::kSlotUnderruns);
            c.halfCorrections = bus.slotValue(slot, jb::asio::bus::kSlotCorrections);
            c.halfTrims = bus.slotValue(slot, jb::asio::bus::kSlotTrims);
            c.halfLag = lag;
            c.halfCaptured = true;
        }
    }

    c.callbacks = g_callbacks.load();
    c.underruns = bus.slotValue(slot, jb::asio::bus::kSlotUnderruns);
    c.corrections = bus.slotValue(slot, jb::asio::bus::kSlotCorrections);
    c.trims = bus.slotValue(slot, jb::asio::bus::kSlotTrims);
    c.lagMin = g_markerMin.load();
    c.lagMax = g_markerMax.load();
    c.lagFinal = g_markerFinal.load();
    c.ran = true;
    g_markerActive.store(false);
    bus.marker = false;

    // Independent reference: the means are taken over the same 4 s frame span
    // the driver reports on, so a wandering lag cancels instead of biasing one
    // side. 16 frames is the target; the marker names the exact frame.
    long repIn = 0, repOut = 0;
    driver->getLatencies(&repIn, &repOut);
    c.reportedLatency = repIn;
    const double spanMean = g_markerSpanMean.load();
    const long meanLag = spanMean >= 0
                             ? static_cast<long>(spanMean + 0.5)
                             : static_cast<long>(c.lagFinal);
    c.actualLatency = bufferSize + meanLag;
    c.latencyOk = labs((repIn - bufferSize) - meanLag) <= 16;

    // A matched cell must show no corrections, no underruns and no trims; a mixed
    // cell additionally proves the trim actually tracked (trims > 0) and kept the
    // lag inside the ring (never at capacity, never empty for long).
    c.passed = c.latencyOk &&
               (c.underruns == 0) && (c.corrections == 0) &&
               (ppm == 0 ? c.trims == 0 : c.trims > 0) &&
               c.lagMin >= 0 &&
               c.lagMax <= (long long)capacity * blockFrames;

    writer.stop.store(true);
    writer.join();
    driver->stop();
    driver->disposeBuffers();
    driver->Release();
    bus.close();
    return true;
}

static void printCell(const SoakCell& c) {
    wprintf(L"callbacks %ld  underruns %lld  corrections %lld  trims %lld\n",
            c.callbacks, c.underruns, c.corrections, c.trims);
    wprintf(L"first half   underruns %lld  corrections %lld  trims %lld  lag %lld\n",
            c.halfUnderruns, c.halfCorrections, c.halfTrims, c.halfLag);
    wprintf(L"second half  underruns %lld  corrections %lld  trims %lld\n",
            c.underruns - c.halfUnderruns, c.corrections - c.halfCorrections,
            c.trims - c.halfTrims);
    wprintf(L"lag frames min/max/final %lld/%lld/%lld  target %lld  %ls\n",
            c.lagMin, c.lagMax, c.lagFinal, c.lagTarget, c.passed ? L"PASS" : L"FAIL");
    wprintf(L"latency reported %ld  actual(buffer+lag) %ld  %ls\n",
            c.reportedLatency, c.actualLatency, c.latencyOk ? L"OK" : L"MISMATCH");
}

int runSoakMatrix(double seconds, int ppm) {
    // 1 ms timer resolution so the writer's Sleep(1) is not a 15.6 ms stall.
    timeBeginPeriod(1);
    const wchar_t* sizesLabel[4] = {L"128", L"256", L"512", L"1024"};
    const int sizes[4] = {128, 256, 512, 1024};

    wprintf(L"\n== drift soak: %.0f s per cell, mixed offset %d ppm ==\n", seconds, ppm);
    wprintf(L"condition A is 0 ppm (matched); B is %d ppm. Matched ring = host "
            L"buffer size x 16; app ring = 480 x 64.\n", ppm);

    int failures = 0;
    for (int geom = 0; geom < 2; ++geom) {
        for (int cond = 0; cond < 2; ++cond) {
            const int cellPpm = cond == 0 ? 0 : ppm;
            for (int s = 0; s < 4; ++s) {
                // Matched: ring block == host buffer, so the reader consumes
                // exactly one block per callback and the block-quantised lag
                // reference is exact. App: 480-frame producer blocks.
                const int blockFrames = geom == 0 ? sizes[s] : 480;
                const int capacity = geom == 0 ? 16 : 64;
                SoakCell c;
                const wchar_t* label = cond == 0 ? L"A matched" : L"B mixed";
                wprintf(L"\n-- %ls | %ls | buffer %d | ring %d x %d --\n", label,
                        geom == 0 ? L"matched" : L"app", sizes[s], blockFrames, capacity);
                if (!runSoakCell(label, blockFrames, capacity, sizes[s], cellPpm, seconds, c)) {
                    failures++;
                    continue;
                }
                printCell(c);
                if (!c.passed) failures++;
            }
        }
    }
    wprintf(L"\nsoak result: %s (%d failing cells)\n",
            failures == 0 ? L"PASS" : L"FAIL", failures);
    timeEndPeriod(1);
    return failures;
}

// One cell at the literal spec duration: --soak-one seconds ppm geom buffer cond
// (geom 0 = harness 128x16, 1 = app 480x64; cond 0 = A, 1 = B).
static int runSoakOne(double seconds, int ppm, int geom, int bufferSize, int cond) {
    const int blockFrames = geom == 0 ? bufferSize : 480;
    const int capacity = geom == 0 ? 16 : 64;
    const int cellPpm = cond == 0 ? 0 : ppm;
    const wchar_t* label = cond == 0 ? L"A matched" : L"B mixed";
    SoakCell c;
    wprintf(L"\n== single soak cell: %.0f s at %d ppm ==\n", seconds, cellPpm);
    wprintf(L"-- %ls | %ls | buffer %d | ring %d x %d --\n", label,
            geom == 0 ? L"matched" : L"app", bufferSize, blockFrames, capacity);
    timeBeginPeriod(1);
    const bool ok = runSoakCell(label, blockFrames, capacity, bufferSize, cellPpm,
                                seconds, c);
    timeEndPeriod(1);
    if (!ok) {
        wprintf(L"FATAL: cell could not start\n");
        return 1;
    }
    printCell(c);
    return c.passed ? 0 : 2;
}

}  // namespace

// --soak [seconds] [ppm]: the drift matrix, no normal suite. Seconds defaults to
// 45 (shortened from the 1200 s spec; pass 1200 for the full run), ppm to 800.
static bool parseSoakArgs(int argc, wchar_t** argv, double& seconds, int& ppm) {
    seconds = 45.0;
    ppm = 800;
    for (int i = 1; i < argc; ++i) {
        if (wcscmp(argv[i], L"--soak") == 0) {
            if (i + 1 < argc) seconds = _wtof(argv[i + 1]);
            if (i + 2 < argc) ppm = _wtoi(argv[i + 2]);
            return true;
        }
    }
    return false;
}

static bool parseSoakOneArgs(int argc, wchar_t** argv, double& seconds, int& ppm,
                             int& geom, int& buffer, int& cond) {
    for (int i = 1; i < argc; ++i) {
        if (wcscmp(argv[i], L"--soak-one") == 0) {
            if (i + 5 < argc) {
                seconds = _wtof(argv[i + 1]);
                ppm = _wtoi(argv[i + 2]);
                geom = _wtoi(argv[i + 3]);
                buffer = _wtoi(argv[i + 4]);
                cond = _wtoi(argv[i + 5]);
            }
            return true;
        }
    }
    return false;
}

int wmain(int argc, wchar_t** argv) {
    wchar_t dllPath[MAX_PATH] = {};
    if (argc > 1 && wcscmp(argv[1], L"--soak") != 0 &&
        wcscmp(argv[1], L"--soak-one") != 0) {
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

    double soakSeconds = 0.0;
    int soakPpm = 0;
    double oneSeconds = 0.0;
    int onePpm = 0, oneGeom = 0, oneBuffer = 0, oneCond = 0;
    if (parseSoakOneArgs(argc, argv, oneSeconds, onePpm, oneGeom, oneBuffer, oneCond)) {
        const int failures = runSoakOne(oneSeconds, onePpm, oneGeom, oneBuffer, oneCond);
        CoUninitialize();
        unregisterInHkcu();
        return failures;
    }

    if (parseSoakArgs(argc, argv, soakSeconds, soakPpm)) {
        const int failures = runSoakMatrix(soakSeconds, soakPpm);
        CoUninitialize();
        unregisterInHkcu();
        return failures == 0 ? 0 : 2;
    }

    // --attach [busName] [hostFrames]: read-only watch of the section a running
    // app publishes. Never creates the section and never writes a block, so the
    // numbers are the app's own output and not the harness's. hostFrames picks
    // the ASIO buffer size (128/256/512/1024) so one live run can be repeated at
    // every size the driver accepts.
    if (argc > 2 && wcscmp(argv[2], L"--attach") == 0) {
        const wchar_t* busName = argc > 3 ? argv[3] : L"main";
        if (argc > 4) {
            wchar_t* end = nullptr;
            const long n = wcstol(argv[4], &end, 10);
            if (!end || *end != L'\0' || (n != 128 && n != 256 && n != 512 && n != 1024)) {
                wprintf(L"FATAL: host frames must be 128, 256, 512 or 1024\n");
                CoUninitialize();
                unregisterInHkcu();
                return 1;
            }
            g_liveRequestedFrames = n;
        }
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
    const bool capacityLagPassed = runExactCapacityLagTest();

    wprintf(L"\n== audio path summary ==\n");
    wprintf(L"bit-exact blocks  : %ld/%ld matched, %ld mismatched\n", audio.matched,
            audio.expectedBlocks, audio.mismatched);
    wprintf(L"gap -> silence    : %s (underruns %lld -> %lld)\n",
            audio.gapPassed ? L"yes" : L"NO",
            audio.underrunsBeforeGap, audio.underrunsAfterGap);
    wprintf(L"cadence streaming : %.1f callbacks/s\n", audio.callbacksPerSecond);
    wprintf(L"reported latency  : in %ld (expected %ld), out %ld %s\n",
            audio.latencyInput, audio.latencyExpectedInput, audio.latencyOutput,
            audio.latencyPassed ? L"OK" : L"WRONG");
    wprintf(L"rate trim         : matched %lld, final %lld (no-op at match: %s)\n",
            audio.trimsMatched, audio.trimsFinal, audio.trimNoOpPassed ? L"yes" : L"NO");
    if (audio.crossLanguageRan) {
        wprintf(L"cross-language    : ran (%ld matched, %ld mismatched, %ld silence)\n",
                audio.crossLanguageMatched, audio.crossLanguageMismatched,
                audio.crossLanguageSilent);
    } else {
        wprintf(L"cross-language    : not run\n");
    }
    wprintf(L"exact-capacity    : %s\n", capacityLagPassed ? L"PASS" : L"FAIL");

    const bool audioPassed = audio.bitExactPassed && audio.gapPassed &&
                             audio.latencyPassed && audio.trimNoOpPassed &&
                             capacityLagPassed &&
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
    return (calls > 0 && audioPassed) ? 0 : 2;
}
