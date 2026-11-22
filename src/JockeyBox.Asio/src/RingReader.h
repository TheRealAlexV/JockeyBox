#pragma once

// Reader side of the JockeyBox transport ring, for the ASIO COM DLL.
//
// The offsets below are a transcription of src/JockeyBox.Ipc/BusLayout.cs, which
// is the single source of truth: the WaveRT driver, the C# app and this reader
// all read the same bytes, and the C# tests pin every offset. Change them only
// together with that file.
//
// Direction: the app WRITES into the ring; this driver READS it and presents the
// blocks as its ASIO *inputs*. The ASIO outputs are not part of this path.
//
// There is no polling thread and no lock: read() is called from the buffer
// switch, touches only the mapped region, never blocks and never allocates.

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <iterator>

namespace jb::asio::bus {

// --- header (BusLayout.cs) -------------------------------------------------
constexpr uint32_t kMagic = 0x5355424A;  // 'J' 'B' 'U' 'S' little-endian
constexpr int32_t kVersion = 1;
constexpr int32_t kHeaderSize = 1024;
constexpr int32_t kPayloadOffset = 1024;

constexpr int32_t kMagicOffset = 0;
constexpr int32_t kVersionOffset = 4;
constexpr int32_t kHeaderSizeOffset = 8;
constexpr int32_t kSampleRateOffset = 12;
constexpr int32_t kChannelsOffset = 16;
constexpr int32_t kBlockFramesOffset = 20;
constexpr int32_t kCapacityBlocksOffset = 24;
constexpr int32_t kWriteBlockIndexOffset = 32;
constexpr int32_t kOverrunCountOffset = 40;
constexpr int32_t kReaderSlotCountFieldOffset = 48;
constexpr int32_t kPayloadOffsetFieldOffset = 52;

// --- reader slots ----------------------------------------------------------
constexpr int32_t kReaderSlotCount = 8;
constexpr int32_t kReaderSlotSize = 64;
constexpr int32_t kReaderSlotBase = 64;
constexpr int32_t kSlotReadBlockIndex = 0;
constexpr int32_t kSlotCorrections = 8;
constexpr int32_t kSlotUnderruns = 16;
constexpr int32_t kSlotActive = 24;

// The section lives in the calling session, so an in-process host (obs-asio)
// sees the same one the app created.
constexpr wchar_t kMapNamePrefix[] = L"Local\\JockeyBox.Bus.";

inline std::atomic<int64_t>* asI64(void* p) noexcept {
    static_assert(sizeof(std::atomic<int64_t>) == sizeof(int64_t),
                  "atomic<i64> must not gain a lock word: the region is shared");
    static_assert(std::atomic<int64_t>::is_always_lock_free);
    return static_cast<std::atomic<int64_t>*>(p);
}

inline std::atomic<int32_t>* asI32(void* p) noexcept {
    static_assert(sizeof(std::atomic<int32_t>) == sizeof(int32_t),
                  "atomic<i32> must not gain a lock word: the region is shared");
    static_assert(std::atomic<int32_t>::is_always_lock_free);
    return static_cast<std::atomic<int32_t>*>(p);
}

// One attached ring: mapped region, validated header and a claimed reader slot.
// A default-constructed reader is detached and means "no bus"; read() then
// produces silence, which is exactly what the driver must serve before the app
// starts or while it is away.
class RingReader {
public:
    RingReader() = default;
    RingReader(const RingReader&) = delete;
    RingReader& operator=(const RingReader&) = delete;
    ~RingReader() { detach(); }

    bool attached() const noexcept { return m_payload != nullptr; }

    // Opens an existing section and claims a free reader slot. Returns false,
    // leaving the reader detached, when the section is absent, the header does
    // not validate, or every slot is taken. Allocates: call it from start() or
    // from the once-a-second retry, never from the callback.
    bool attach(const char* busName) noexcept {
        detach();
        if (!busName || !*busName) return false;
        if (::lstrlenA(busName) > 64) return false;
        for (const char* p = busName; *p; ++p) {
            if (*p == '\\' || *p == '/') return false;  // would escape the namespace
        }

        wchar_t name[128];
        if (::wcscpy_s(name, kMapNamePrefix) != 0) return false;
        const int used = ::lstrlenW(name);
        const int room = static_cast<int>(std::size(name)) - used - 1;
        if (room <= 0) return false;
        if (::MultiByteToWideChar(CP_UTF8, 0, busName, -1, name + used, room) <= 0)
            return false;

        HANDLE map = ::OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
        if (!map) return false;

        auto* base = static_cast<uint8_t*>(::MapViewOfFile(
            map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0));
        if (!base) {
            ::CloseHandle(map);
            return false;
        }

        if (!headerValid(base)) {
            ::UnmapViewOfFile(base);
            ::CloseHandle(map);
            return false;
        }

        const int32_t channels = *reinterpret_cast<const int32_t*>(base + kChannelsOffset);
        const int32_t blockFrames = *reinterpret_cast<const int32_t*>(base + kBlockFramesOffset);
        const int32_t capacity = *reinterpret_cast<const int32_t*>(base + kCapacityBlocksOffset);
        const int32_t payloadOffset = *reinterpret_cast<const int32_t*>(base + kPayloadOffsetFieldOffset);

        int slot = -1;
        for (int i = 0; i < kReaderSlotCount; ++i) {
            int32_t expected = 0;
            if (asI32(base + kReaderSlotBase + i * kReaderSlotSize + kSlotActive)
                    ->compare_exchange_strong(expected, 1, std::memory_order_acq_rel)) {
                slot = i;
                break;
            }
        }
        if (slot < 0) {
            ::UnmapViewOfFile(base);
            ::CloseHandle(map);
            return false;
        }

        uint8_t* slotBase = base + kReaderSlotBase + slot * kReaderSlotSize;
        m_base = base;
        m_map = map;
        m_slot = slot;
        m_payload = base + payloadOffset;
        m_blockBytes = static_cast<int64_t>(blockFrames) * channels * 4;
        m_blockFrames = blockFrames;
        m_channels = channels;
        m_capacity = capacity;
        m_write = asI64(base + kWriteBlockIndexOffset);
        m_read = asI64(slotBase + kSlotReadBlockIndex);
        m_corrections = asI64(slotBase + kSlotCorrections);
        m_underruns = asI64(slotBase + kSlotUnderruns);
        m_slotActive = asI32(slotBase + kSlotActive);

        // Join at the newest block: attaching mid-stream must not replay old
        // audio into the host.
        m_read->store(m_write->load(std::memory_order_acquire), std::memory_order_release);
        return true;
    }

    void detach() noexcept {
        if (m_slotActive) m_slotActive->store(0, std::memory_order_release);
        if (m_base) ::UnmapViewOfFile(m_base);
        if (m_map) ::CloseHandle(m_map);
        m_base = nullptr;
        m_map = nullptr;
        m_payload = nullptr;
        m_slotActive = nullptr;
        m_write = m_read = m_corrections = m_underruns = nullptr;
        m_slot = -1;
    }

    // Copies the next unconsumed block into `dst` as deinterleaved float32 and
    // advances the slot. No data -> silence and one underruns tick. Falling
    // further behind than the ring can hold -> jump to the newest block and one
    // corrections tick; the reader never rewinds and never replays.
    // Never blocks, never allocates, never throws.
    void read(float* const* dst, int dstChannels, int frames) noexcept {
        if (!dst || dstChannels <= 0 || frames <= 0) return;
        for (int c = 0; c < dstChannels; ++c)
            if (dst[c]) std::memset(dst[c], 0, sizeof(float) * frames);
        if (!m_payload) return;

        const int64_t written = m_write->load(std::memory_order_acquire);
        const int64_t read = m_read->load(std::memory_order_relaxed);

        if (written <= read) {
            m_underruns->fetch_add(1, std::memory_order_relaxed);
            return;
        }
        if (written - read > m_capacity) {
            m_read->store(written, std::memory_order_release);
            m_corrections->fetch_add(1, std::memory_order_relaxed);
            return;
        }

        const auto* src = reinterpret_cast<const float*>(
            m_payload + static_cast<int64_t>(read % m_capacity) * m_blockBytes);
        const int n = frames < m_blockFrames ? frames : m_blockFrames;
        const int ch = dstChannels < m_channels ? dstChannels : m_channels;
        for (int c = 0; c < ch; ++c) {
            float* d = dst[c];
            if (!d) continue;
            for (int i = 0; i < n; ++i) d[i] = src[i * m_channels + c];
        }

        m_read->store(read + 1, std::memory_order_release);
    }

    int32_t channels() const noexcept { return m_channels; }
    int32_t blockFrames() const noexcept { return m_blockFrames; }
    int slot() const noexcept { return m_slot; }
    int64_t readBlockIndex() const noexcept {
        return m_read ? m_read->load(std::memory_order_acquire) : 0;
    }

private:
    static bool headerValid(const uint8_t* base) noexcept {
        const auto* magic = reinterpret_cast<const uint32_t*>(base + kMagicOffset);
        if (*magic != kMagic) return false;
        if (*reinterpret_cast<const int32_t*>(base + kVersionOffset) != kVersion) return false;
        if (*reinterpret_cast<const int32_t*>(base + kHeaderSizeOffset) < kHeaderSize) return false;
        if (*reinterpret_cast<const int32_t*>(base + kSampleRateOffset) <= 0) return false;

        const int32_t channels = *reinterpret_cast<const int32_t*>(base + kChannelsOffset);
        const int32_t blockFrames = *reinterpret_cast<const int32_t*>(base + kBlockFramesOffset);
        const int32_t capacity = *reinterpret_cast<const int32_t*>(base + kCapacityBlocksOffset);
        const int32_t payloadOffset = *reinterpret_cast<const int32_t*>(base + kPayloadOffsetFieldOffset);

        if (channels <= 0 || channels > 64) return false;
        if (blockFrames <= 0 || blockFrames > 8192) return false;
        if (capacity <= 0 || capacity > (1 << 20)) return false;
        if (payloadOffset < kHeaderSize) return false;
        return true;
    }

    uint8_t* m_base = nullptr;
    HANDLE m_map = nullptr;
    uint8_t* m_payload = nullptr;
    int m_slot = -1;
    int32_t m_channels = 0;
    int32_t m_blockFrames = 0;
    int32_t m_capacity = 0;
    int64_t m_blockBytes = 0;

    std::atomic<int64_t>* m_write = nullptr;
    std::atomic<int64_t>* m_read = nullptr;
    std::atomic<int64_t>* m_corrections = nullptr;
    std::atomic<int64_t>* m_underruns = nullptr;
    std::atomic<int32_t>* m_slotActive = nullptr;
};

}  // namespace jb::asio::bus
