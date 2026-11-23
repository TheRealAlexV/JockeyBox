#pragma once

#include "JockeyBoxAsioAbi.h"
#include "RingReader.h"

#include <atomic>
#include <cstdint>
#include <thread>

namespace jb::asio {

// Tracer-bullet virtual ASIO driver: owns the COM object, the double buffers
// and the clock thread. Everything the callback path touches is allocated
// before start(), so a running driver never allocates.
class VirtualAsioDriver final : public IASIO {
public:
    static constexpr long kChannelsPerDirection = 2;
    static constexpr long kMinBufferSize = 128;
    static constexpr long kMaxBufferSize = 1024;
    static constexpr long kPreferredBufferSize = 128;
    static constexpr AsioSampleRate kSampleRate = 48000.0;

    VirtualAsioDriver();
    ~VirtualAsioDriver();

    HRESULT JB_COM_CALL QueryInterface(REFIID riid, void** ppv) override;
    ULONG JB_COM_CALL AddRef() override;
    ULONG JB_COM_CALL Release() override;

    AsioBool JB_ASIO_CALL init(void* sysHandle) override;
    void JB_ASIO_CALL getDriverName(char* name) override;
    long JB_ASIO_CALL getDriverVersion() override;
    void JB_ASIO_CALL getErrorMessage(char* text) override;

    AsioError JB_ASIO_CALL start() override;
    AsioError JB_ASIO_CALL stop() override;

    AsioError JB_ASIO_CALL getChannels(long* numInputChannels,
                                       long* numOutputChannels) override;
    AsioError JB_ASIO_CALL getLatencies(long* inputLatency,
                                        long* outputLatency) override;
    AsioError JB_ASIO_CALL getBufferSize(long* minSize, long* maxSize,
                                         long* preferredSize,
                                         long* granularity) override;

    AsioError JB_ASIO_CALL canSampleRate(AsioSampleRate sampleRate) override;
    AsioError JB_ASIO_CALL getSampleRate(AsioSampleRate* sampleRate) override;
    AsioError JB_ASIO_CALL setSampleRate(AsioSampleRate sampleRate) override;

    AsioError JB_ASIO_CALL getClockSources(AsioClockSource* clocks,
                                           long* numSources) override;
    AsioError JB_ASIO_CALL setClockSource(long reference) override;

    AsioError JB_ASIO_CALL getSamplePosition(AsioSamples* sPos,
                                             AsioTimeStamp* tStamp) override;
    AsioError JB_ASIO_CALL getChannelInfo(AsioChannelInfo* info) override;

    AsioError JB_ASIO_CALL createBuffers(AsioBufferInfo* bufferInfos,
                                         long numChannels, long bufferSize,
                                         AsioCallbacks* callbacks) override;
    AsioError JB_ASIO_CALL disposeBuffers() override;

    AsioError JB_ASIO_CALL controlPanel() override;
    AsioError JB_ASIO_CALL future(long selector, void* opt) override;
    AsioError JB_ASIO_CALL outputReady() override;

    long JB_ASIO_CALL asioMessage(long selector, long value, void* message,
                                  double* opt) override;

private:
    void clockLoop();
    void fire(long bufferIndex);
    static bool isAcceptableBufferSize(long size);

    // Bus name from JOCKEYBOX_ASIO_BUS (default "main"), resolved in start().
    void resolveBusName() noexcept;
    // Opens/claims the ring. Called from start() and from clockLoop's
    // once-a-second retry, never from the callback.
    void attachBus() noexcept;

    std::atomic<long> m_refs{1};

    // Set by the host, read by the clock thread. Written before start() and
    // cleared after the thread is joined, so no locking is needed.
    AsioCallbacks* m_callbacks = nullptr;
    long m_bufferSize = kPreferredBufferSize;

    std::atomic<bool> m_running{false};
    std::atomic<bool> m_buffersCreated{false};
    std::atomic<int64_t> m_frames{0};

    std::thread m_clock;
    HANDLE m_timer = nullptr;
    void* m_sysHandle = nullptr;
    bool m_initialised = false;

    bool m_activeIn[kChannelsPerDirection] = {};
    bool m_activeOut[kChannelsPerDirection] = {};

    // Zeroed once at construction. The input halves are filled from the ring on
    // every buffer switch; the output halves are never written, so the ASIO
    // outputs stay silent (this driver is an input path, not a playback path).
    float m_in[kChannelsPerDirection][2][kMaxBufferSize] = {};
    float m_out[kChannelsPerDirection][2][kMaxBufferSize] = {};

    // Read by the clock thread after start(), so no locking is needed. Only the
    // clock thread calls read()/attachBus(); start/stop own the lifetime.
    bus::RingReader m_ring;
    char m_busName[72] = {};
    enum BusState : long { kBusAbsent = 0, kBusAttached = 1 };
    std::atomic<long> m_busState{kBusAbsent};
    long long m_lastBusAttempt = 0;  // QPC ticks, clock thread only
    LARGE_INTEGER m_qpcFreq{};
};

}  // namespace jb::asio
