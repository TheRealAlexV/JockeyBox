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
// Native-reader-private diagnostic, in the otherwise unused tail of the 64-byte
// reader slot (32..63). BusLayout.cs defines 0/8/16/24 only, so the app never
// reads this. It lives here rather than in BusLayout because the native tree
// owns the trim and the app tree is out of scope; it is not part of the shared
// contract and must not be treated as one.
constexpr int32_t kSlotTrims = 32;

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
    // `prefillFrames` asks the reader to join the stream that many frames behind
    // the writer instead of at the newest block. A host buffer that is not a
    // multiple of the ring block (512 vs 480) needs that cushion: both sides
    // average the same rate, but the writer publishes in whole blocks, so a
    // zero-cushion reader starves at every block edge. 0 keeps "join at newest".
    bool attach(const char* busName, int32_t prefillFrames = 0,
                int32_t expectedChannels = 0) noexcept {
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
        m_lastSeenChannels = channels;

        // Geometry is agreed before a slot is claimed: a writer publishing 2
        // channels into a device configured for 8 would be read with a stride the
        // payload does not have. Refuse the ring instead of reinterpreting bytes.
        if (expectedChannels > 0 && channels != expectedChannels) {
            ::UnmapViewOfFile(base);
            ::CloseHandle(map);
            return false;
        }

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
        m_trims = asI64(slotBase + kSlotTrims);
        m_trims->store(0, std::memory_order_release);

        // Join `prefillFrames` behind the writer. 0 = newest block: attaching
        // mid-stream must not replay old audio into the host.
        //
        // Floor the cushion at three blocks: the writer publishes in whole
        // blocks, so a reader one block behind sits exactly at the writer's own
        // block-quantisation amplitude and starves on every wrap. Cap it two
        // whole blocks below the ring's capacity, not capacity/2: a host buffer
        // as large as 1024 frames needs more than half a small ring to survive
        // one late wake, and at lag == capacity the cursor's slot aliases the
        // slot the writer is about to memcpy (the torn window read() guards).
        // RingReader::attach records the effective cushion for getLatencies() so
        // a dynamic choice is reported exactly, never as a hard-coded count.
        int64_t joined = m_write->load(std::memory_order_acquire);
        if (prefillFrames > 0) {
            int64_t blocks = (static_cast<int64_t>(prefillFrames) + blockFrames - 1) / blockFrames;
            if (blocks < 3) blocks = 3;
            int64_t cap = m_capacity > 3
                              ? static_cast<int64_t>(m_capacity) - 2
                              : (m_capacity > 1 ? static_cast<int64_t>(m_capacity) - 1 : 1);
            if (cap < 1) cap = 1;
            if (blocks > cap) blocks = cap;
            m_cushionFrames = static_cast<int32_t>(blocks * blockFrames);
            joined -= blocks;
            if (joined < 0) joined = 0;
        }
        m_cursorBlock = joined;
        m_cursorFrame = 0;
        m_trimTarget = m_cushionFrames;
        // 4 s span, kept a whole number of blocks so the sawtooth washes out.
        m_trimPeriodFrames =
            (blockFrames > 0 ? (4 * 48000 / blockFrames) * blockFrames : 4 * 48000);
        m_trimPeriodEnd = m_trimPeriodFrames;
        m_trimOutFrames = 0;
        m_trimLagSum = 0.0;
        m_trimLagCount = 0;
        m_trimLastMean = 0.0;
        m_trimTargetLag = 0.0;
        m_trimFramesBase = 0;
        m_trimSlipsSinceBase = 0;
        m_trimBaseSet = false;
        m_trimRate = 0.0;
        m_trimSlipAccum = 0.0;
        m_measuredLagFrames.store(-1, std::memory_order_release);
        m_read->store(joined, std::memory_order_release);
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
        m_cursorBlock = 0;
        m_cursorFrame = 0;
        m_cushionFrames = 0;
        m_trims = nullptr;
        m_trimTarget = 0;
        m_trimRate = 0.0;
        m_trimSlipAccum = 0.0;
        m_trimBaseSet = false;
        m_measuredLagFrames.store(-1, std::memory_order_release);
    }

    // Streams exactly `frames` frames into `dst` as deinterleaved float32,
    // starting wherever the cursor stopped and crossing ring-block boundaries
    // as needed. `frames` has no relation to the ring block size, so a 512-frame
    // host buffer reads cleanly from a 480-frame producer.
    //
    // The cursor is (m_cursorBlock, m_cursorFrame): the block being consumed and
    // the intra-block offset into it. It only ever moves forward. Silence is
    // served only when the writer has not published the next block yet (one
    // underruns tick per starved callback). A jump to the newest block and one
    // corrections tick happen only when the cursor block has already been
    // overwritten, i.e. the reader fell further behind than the ring holds.
    // Never blocks, never allocates, never throws.
    void read(float* const* dst, int dstChannels, int frames) noexcept {
        if (!dst || dstChannels <= 0 || frames <= 0) return;
        for (int c = 0; c < dstChannels; ++c)
            if (dst[c]) std::memset(dst[c], 0, sizeof(float) * frames);
        if (!m_payload) return;

        const int64_t written = m_write->load(std::memory_order_acquire);
        if (written <= m_cursorBlock) {
            m_underruns->fetch_add(1, std::memory_order_relaxed);
            m_read->store(m_cursorBlock, std::memory_order_release);
            return;
        }
        bool corrected = false;
        if (written - m_cursorBlock >= m_capacity) {
            // At exactly `capacity` blocks behind, `m_cursorBlock % capacity` is
            // the same payload slot as `written % capacity`: the slot the writer
            // is about to memcpy (it advances lapped readers before CopyTo). The
            // C# reader re-reads the published slot and is safe; matching it here
            // means correcting at >= capacity, not just >. Resume at the newest
            // published block rather than replaying overwritten audio.
            m_cursorBlock = written - 1;
            m_cursorFrame = 0;
            m_corrections->fetch_add(1, std::memory_order_relaxed);
            corrected = true;
        }

        const int32_t channels = m_channels;
        const int32_t blockFrames = m_blockFrames;

        // --- one-frame rate trim ------------------------------------------------
        // A mixed route paces the writer off the physical device's WASAPI clock
        // while this reader runs on QPC at 48 kHz, so the cursor creeps toward
        // empty (underrun) or toward capacity (a dropped block) as a periodic
        // click. Track the writer by slipping the cursor one input sample, never
        // more, so no discontinuity is ever larger than one sample.
        //
        // It is a *rate* trim, not a position jump. Over a span it measures the
        // drift directly: the lag moved dLag blocks and `slips` samples were
        // slipped, so the writer really moved dLag*blockFrames + slips frames
        // more than the reader. When that exceeds 1.5 blocks it is a real rate
        // difference, not the whole-block publication sawtooth, and the measured
        // rate (drift / span) is added to a *persistent* slip rate. A persistent
        // rate matters: a bang-bang trim stops when the lag returns and lets the
        // drift push it out again, so the lag creeps. With the rate held, the lag
        // stays flat and the residual is corrected on the next span.
        //
        // At an exact rate match dLag is 0 (or +-1 block from block quantisation),
        // no samples are slipped, and |drift| stays below the 1.5-block deadband
        // forever: strict no-op, so the trim cannot become a click source. A
        // sample is slipped at most once per read, inaudible at 48 kHz.
        //
        // The count equals the drift volume: zero when the clocks agree, one per
        // slipped frame when they do not (one slip cancels one frame of error). A
        // count that climbs is *reporting* a real difference, not masking it. A
        // difference too large for one slip per read (1/frames) is left to grow
        // into a correction on purpose. Trims never touch the corrections counter:
        // a trim is not a correction.
        int trimDir = 0;  // +1 drop one input frame, -1 hold one input frame
        if (m_trims) {
            if (corrected) {
                m_trimRate = 0.0;
                m_trimSlipAccum = 0.0;
                m_trimBaseSet = false;
                m_trimLagSum = 0.0;
                m_trimLagCount = 0;
                m_trimFramesBase = m_trimOutFrames;
                m_trimSlipsSinceBase = 0;
            } else if (m_trimTarget > 0) {
                // Mean lag over a span, not a block count: the sawtooth from
                // whole-block publication averages out, so the drift estimate is
                // sub-frame accurate. Quantising to whole blocks would turn a
                // 0.6-block drift into a 1-block estimate (1.6x too fast).
                const int64_t lagFrames =
                    (written - m_cursorBlock) * static_cast<int64_t>(blockFrames) - m_cursorFrame;
                m_trimLagSum += static_cast<double>(lagFrames);
                ++m_trimLagCount;

                m_trimSlipAccum += m_trimRate * static_cast<double>(frames);
                if (m_trimSlipAccum >= 1.0) {
                    trimDir = 1;
                    m_trimSlipAccum -= 1.0;
                    ++m_trimSlipsSinceBase;
                    m_trims->fetch_add(1, std::memory_order_relaxed);
                } else if (m_trimSlipAccum <= -1.0) {
                    trimDir = -1;
                    m_trimSlipAccum += 1.0;
                    --m_trimSlipsSinceBase;
                    m_trims->fetch_add(1, std::memory_order_relaxed);
                }

                m_trimOutFrames += frames;
                if (m_trimOutFrames >= m_trimPeriodEnd && m_trimLagCount > 0) {
                    const double mean = m_trimLagSum / static_cast<double>(m_trimLagCount);
                    // Publish the smoothed steady-state lag. getLatencies() reads
                    // this so the host is told the real input delay, not the
                    // nominal cushion: in a mixed route the trim settles the lag a
                    // few blocks above the cushion and reports must follow it.
                    m_measuredLagFrames.store(static_cast<int64_t>(mean + 0.5),
                                              std::memory_order_release);
                    // Bands. The feedforward deadband is a quarter block (32
                    // frames at the smallest block) so the true rate is
                    // re-estimated every span. A half-block deadband was larger
                    // than one span's drift at 100 ppm, so after a refill the
                    // feedforward could not fire again and the inflated rate
                    // persisted until it drained the cushion. The refill band is
                    // one block.
                    const double deadband =
                        blockFrames > 128 ? 0.25 * static_cast<double>(blockFrames) : 32.0;
                    const double refillGate = static_cast<double>(blockFrames);
                    if (!m_trimBaseSet) {
                        m_trimLastMean = mean;
                        m_trimTargetLag = mean;  // anchor at the lag we settled at
                        m_trimFramesBase = m_trimOutFrames;
                        m_trimSlipsSinceBase = 0;
                        m_trimBaseSet = true;
                    } else {
                        // Drift since the last span: how far the mean lag moved,
                        // plus the samples already slipped. That is the writer's
                        // real excess in frames, so drift/span is its own rate.
                        const double drift =
                            (mean - m_trimLastMean) + static_cast<double>(m_trimSlipsSinceBase);
                        const double err = mean - m_trimTargetLag;
                        const int64_t span = m_trimOutFrames - m_trimFramesBase;
                        if (span > 0) {
                            // Replace the rate every span; never accumulate. A
                            // rate left over from an earlier span cannot outlive
                            // the offset that justified it.
                            double rate = 0.0;
                            if (drift >= deadband || drift <= -deadband) {
                                rate = drift / static_cast<double>(span);
                            }
                            if (err >= refillGate || err <= -refillGate) {
                                // Bounded proportional refill: at most half the
                                // offset per span, clamped to 1e-3 (1000 ppm).
                                // It stops as soon as the lag is back inside the
                                // band, and it is replaced, not added, so it can
                                // never wind up against a stale rate.
                                double corr = 0.5 * err / static_cast<double>(span);
                                if (corr > 0.001) corr = 0.001;
                                if (corr < -0.001) corr = -0.001;
                                rate += corr;
                            }
                            // Bound the draining direction by the margin actually
                            // left. Draining is a positive rate (drop one input
                            // frame), and a span that over-estimates drains across
                            // its own length. The floor is the cushion plus one
                            // block: a whole block absorbs the span rounding,
                            // because m_trimOutFrames can overshoot the period by
                            // up to one host buffer, so the predicted lag at the
                            // end of the next span cannot reach the cushion, let
                            // alone zero. Clamp, do not merely compare: with no
                            // margin left the applied drain is held at zero while
                            // the estimate keeps updating, so the refill or the
                            // next span recovers. Adding lag needs no bound; it
                            // cannot underrun.
                            const double floor = static_cast<double>(m_trimTarget) +
                                                 static_cast<double>(blockFrames);
                            if (rate > 0.0) {
                                const double cap = (mean - floor) /
                                                   static_cast<double>(m_trimPeriodFrames);
                                if (rate > cap) rate = cap > 0.0 ? cap : 0.0;
                            }
                            m_trimRate = rate;
                            m_trimLastMean = mean;
                            m_trimFramesBase = m_trimOutFrames;
                            m_trimSlipsSinceBase = 0;
                        }
                    }
                    m_trimLagSum = 0.0;
                    m_trimLagCount = 0;
                    m_trimPeriodEnd = m_trimOutFrames + m_trimPeriodFrames;
                }
            }
        }
        if (trimDir > 0) {
            // Consume one frame with no output. A drop only fires after the lag
            // measured past the deadband, so it cannot step into an unpublished
            // block.
            ++m_cursorFrame;
            if (m_cursorFrame >= blockFrames) {
                m_cursorFrame = 0;
                ++m_cursorBlock;
            }
        }

        const int ch = dstChannels < channels ? dstChannels : channels;
        const int toConsume = trimDir < 0 ? frames - 1 : frames;
        int produced = 0;
        while (produced < toConsume && m_cursorBlock < written) {
            const int32_t avail = blockFrames - m_cursorFrame;
            int run = toConsume - produced;
            if (run > avail) run = avail;

            const auto* src = reinterpret_cast<const float*>(
                m_payload + static_cast<int64_t>(m_cursorBlock % m_capacity) * m_blockBytes);
            const int32_t base = m_cursorFrame * channels;
            for (int c = 0; c < ch; ++c) {
                float* d = dst[c];
                if (!d) continue;
                const float* s = src + base + c;
                float* o = d + produced;
                for (int i = 0; i < run; ++i) o[i] = s[i * channels];
            }

            produced += run;
            m_cursorFrame += run;
            if (m_cursorFrame >= blockFrames) {
                m_cursorFrame = 0;
                ++m_cursorBlock;
            }
        }

        if (trimDir < 0 && produced == toConsume && produced > 0) {
            // Hold the last input frame for the one output frame we did not read:
            // a single-sample hold, never a jump.
            for (int c = 0; c < ch; ++c) {
                float* d = dst[c];
                if (!d) continue;
                const float hold = d[produced - 1];
                for (int i = produced; i < frames; ++i) d[i] = hold;
            }
            produced = frames;
        }

        if (produced < frames) m_underruns->fetch_add(1, std::memory_order_relaxed);
        m_read->store(m_cursorBlock, std::memory_order_release);
    }

    int32_t channels() const noexcept { return m_channels; }
    // The last channel count seen in a header, kept even when the ring was
    // refused: the driver names it in the mismatch message.
    int32_t lastSeenChannels() const noexcept { return m_lastSeenChannels; }
    int32_t blockFrames() const noexcept { return m_blockFrames; }
    // Whole-block cushion actually used by attach(), in frames. 0 until attached,
    // or when attached at the newest block. getLatencies() adds this to the host
    // buffer to report the real input delay.
    int32_t cushionFrames() const noexcept { return m_cushionFrames; }
    // Mean buffered frames over the last completed trim span. -1 until the first
    // span completes, so the caller can fall back to the cushion.
    int64_t measuredLagFrames() const noexcept {
        return m_measuredLagFrames.load(std::memory_order_acquire);
    }
    int slot() const noexcept { return m_slot; }
    // The block currently being consumed. The frame offset inside it is not
    // published: `readBlockIndex` advances by one per whole block consumed and
    // may stand still across callbacks whose frames all fell inside one block.
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

    // Fractional cursor: block currently being consumed and the frame offset
    // into it. Forward-only; not atomic, because only the clock thread reads.
    int64_t m_cursorBlock = 0;
    int32_t m_cursorFrame = 0;
    int32_t m_cushionFrames = 0;
    // Rate trim state. m_trimTarget is the cushion in frames (the anchor, and
    // the enable gate). Drift is measured as the lag change plus the samples
    // already slipped; when it exceeds 1.5 blocks the measured rate is folded
    // into a persistent slip rate so the lag stops moving instead of creeping.
    int64_t m_trimTarget = 0;
    std::atomic<int64_t>* m_trims = nullptr;
    int64_t m_trimPeriodFrames = 0;   // measurement span, output frames (4 s)
    int64_t m_trimPeriodEnd = 0;
    int64_t m_trimOutFrames = 0;
    double m_trimLagSum = 0.0;        // lag frames summed over the span
    int64_t m_trimLagCount = 0;
    double m_trimLastMean = 0.0;      // mean lag at the last rate update
    double m_trimTargetLag = 0.0;     // fixed anchor: the mean lag of span one
    int64_t m_trimFramesBase = 0;
    int64_t m_trimSlipsSinceBase = 0; // signed: +1 per drop, -1 per hold
    bool m_trimBaseSet = false;
    double m_trimRate = 0.0;          // persistent slip rate, frames per frame
    double m_trimSlipAccum = 0.0;
    // Steady-state lag published for getLatencies(); -1 until the first span.
    std::atomic<int64_t> m_measuredLagFrames{-1};

    std::atomic<int64_t>* m_write = nullptr;
    std::atomic<int64_t>* m_read = nullptr;
    std::atomic<int64_t>* m_corrections = nullptr;
    std::atomic<int64_t>* m_underruns = nullptr;
    std::atomic<int32_t>* m_slotActive = nullptr;
    int32_t m_lastSeenChannels = 0;
};

}  // namespace jb::asio::bus
