#include "VirtualAsioDriver.h"

#include <cstdio>
#include <cstring>

// Timer flag from the 1803 SDK onwards; older SDK headers lack it.
#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

// Defined in dllmain.cpp. Declared here so the destructor can tell the DLL
// module that one fewer live object remains (DllCanUnloadNow).
void JockeyBoxAsioDriverReleased();

namespace jb::asio {

namespace {

// Local GUID constants so the DLL links without uuid.lib.
constexpr GUID kIidUnknown = {0x00000000, 0x0000, 0x0000,
                              {0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};

long long qpcNow(LARGE_INTEGER freq) {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

double seconds(LARGE_INTEGER freq, long long ticks) {
    return double(ticks) / double(freq.QuadPart);
}

}  // namespace

VirtualAsioDriver::VirtualAsioDriver() = default;

VirtualAsioDriver::~VirtualAsioDriver() {
    stop();
    if (m_timer) CloseHandle(m_timer);
    JockeyBoxAsioDriverReleased();
}

HRESULT JB_COM_CALL VirtualAsioDriver::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    // ASIO hosts pass the driver CLSID as the riid, not IID_IUnknown.
    if (IsEqualGUID(riid, kIidUnknown) || IsEqualGUID(riid, kDriverClsid)) {
        *ppv = static_cast<IASIO*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

ULONG JB_COM_CALL VirtualAsioDriver::AddRef() {
    return static_cast<ULONG>(m_refs.fetch_add(1) + 1);
}

ULONG JB_COM_CALL VirtualAsioDriver::Release() {
    const long remaining = m_refs.fetch_sub(1) - 1;
    if (remaining == 0) {
        delete this;
        return 0;
    }
    return static_cast<ULONG>(remaining);
}

AsioBool JB_ASIO_CALL VirtualAsioDriver::init(void* sysHandle) {
    m_sysHandle = sysHandle;  // unused in v1: there is no window to parent
    resolveBusName();         // so getErrorMessage names the bus before start()
    m_initialised = true;
    return kAsioTrue;
}

void JB_ASIO_CALL VirtualAsioDriver::getDriverName(char* name) {
    if (!name) return;
    strcpy_s(name, 32, kDriverNameText);
}

long JB_ASIO_CALL VirtualAsioDriver::getDriverVersion() { return 1; }

void JB_ASIO_CALL VirtualAsioDriver::getErrorMessage(char* text) {
    if (!text) return;
    if (m_busState.load(std::memory_order_relaxed) == kBusAttached) {
        _snprintf_s(text, 124, _TRUNCATE, "JockeyBox Virtual ASIO: reading bus '%s'", m_busName);
    } else {
        _snprintf_s(text, 124, _TRUNCATE,
                    "JockeyBox Virtual ASIO: bus '%s' not attached; serving silence, "
                    "retrying once a second", m_busName);
    }
}

void VirtualAsioDriver::resolveBusName() noexcept {
    if (m_busName[0]) return;
    char value[64] = {};
    const DWORD n = GetEnvironmentVariableA("JOCKEYBOX_ASIO_BUS", value, sizeof(value));
    if (n == 0 || n >= sizeof(value)) {
        strcpy_s(m_busName, "main");
    } else {
        strcpy_s(m_busName, value);
    }
}

void VirtualAsioDriver::attachBus() noexcept {
    if (m_ring.attach(m_busName))
        m_busState.store(kBusAttached, std::memory_order_relaxed);
    else
        m_busState.store(kBusAbsent, std::memory_order_relaxed);
}

AsioError JB_ASIO_CALL VirtualAsioDriver::start() {
    if (m_running.load()) return kAseOk;
    if (!m_buffersCreated.load() || !m_callbacks) return kAseInvalidMode;

    if (!m_timer) {
        m_timer = CreateWaitableTimerExW(nullptr, nullptr,
                                         CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                         TIMER_ALL_ACCESS);
        if (!m_timer) m_timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
    }

    resolveBusName();
    // Attach before the clock starts, so the first callback already has data
    // when the app is up. If the section is missing, the clock thread retries
    // once a second; the callback itself never touches the filesystem.
    QueryPerformanceFrequency(&m_qpcFreq);
    m_lastBusAttempt = 0;
    attachBus();
    m_lastBusAttempt = qpcNow(m_qpcFreq);

    m_frames.store(0);
    m_running.store(true);
    try {
        m_clock = std::thread(&VirtualAsioDriver::clockLoop, this);
    } catch (...) {
        m_running.store(false);
        return kAseNoMemory;
    }
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::stop() {
    m_running.store(false);
    if (m_clock.joinable()) m_clock.join();
    // Release the reader slot so the app sees one reader fewer, not a dead one.
    m_ring.detach();
    m_busState.store(kBusAbsent, std::memory_order_relaxed);
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getChannels(long* numInputChannels,
                                                      long* numOutputChannels) {
    if (!numInputChannels || !numOutputChannels) return kAseInvalidParameter;
    *numInputChannels = kChannelsPerDirection;
    *numOutputChannels = kChannelsPerDirection;
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getLatencies(long* inputLatency,
                                                       long* outputLatency) {
    if (!inputLatency || !outputLatency) return kAseInvalidParameter;
    *inputLatency = m_bufferSize;
    *outputLatency = m_bufferSize;
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getBufferSize(long* minSize,
                                                        long* maxSize,
                                                        long* preferredSize,
                                                        long* granularity) {
    if (!minSize || !maxSize || !preferredSize || !granularity)
        return kAseInvalidParameter;
    *minSize = kMinBufferSize;
    *maxSize = kMaxBufferSize;
    *preferredSize = kPreferredBufferSize;
    *granularity = -1;  // -1 means "powers of two"
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::canSampleRate(AsioSampleRate rate) {
    return (rate == kSampleRate) ? kAseOk : kAseNoClock;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getSampleRate(
    AsioSampleRate* sampleRate) {
    if (!sampleRate) return kAseInvalidParameter;
    *sampleRate = kSampleRate;
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::setSampleRate(AsioSampleRate rate) {
    return (rate == kSampleRate) ? kAseOk : kAseNoClock;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getClockSources(
    AsioClockSource* clocks, long* numSources) {
    if (!numSources) return kAseInvalidParameter;
    // No selectable clock: the virtual device is always internal.
    *numSources = 0;
    (void)clocks;
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::setClockSource(long reference) {
    (void)reference;
    return kAseNotPresent;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getSamplePosition(
    AsioSamples* sPos, AsioTimeStamp* tStamp) {
    if (sPos) {
        const int64_t frames = m_frames.load(std::memory_order_relaxed);
        sPos->lo = static_cast<uint32_t>(frames & 0xffffffffu);
        sPos->hi = static_cast<uint32_t>(frames >> 32);
    }
    if (tStamp) {
        LARGE_INTEGER freq;
        QueryPerformanceFrequency(&freq);
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        const int64_t ns = (now.QuadPart / freq.QuadPart) * 1000000000LL +
                           ((now.QuadPart % freq.QuadPart) * 1000000000LL) /
                               freq.QuadPart;
        tStamp->lo = static_cast<uint32_t>(ns & 0xffffffffu);
        tStamp->hi = static_cast<uint32_t>(uint64_t(ns) >> 32);
    }
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getChannelInfo(AsioChannelInfo* info) {
    if (!info) return kAseInvalidParameter;
    if (info->channel < 0 || info->channel >= kChannelsPerDirection)
        return kAseInvalidParameter;

    const long ch = info->channel;
    info->isActive = info->isInput
                         ? (m_activeIn[ch] ? kAsioTrue : kAsioFalse)
                         : (m_activeOut[ch] ? kAsioTrue : kAsioFalse);
    info->channelGroup = 0;
    info->type = kAsioStFloat32Lsb;
    char label[32];
    sprintf_s(label, "%s %ld", info->isInput ? "JockeyBox In" : "JockeyBox Out",
              ch + 1);
    strcpy_s(info->name, 32, label);
    return kAseOk;
}

bool VirtualAsioDriver::isAcceptableBufferSize(long size) {
    return size == 128 || size == 256 || size == 512;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::createBuffers(
    AsioBufferInfo* bufferInfos, long numChannels, long bufferSize,
    AsioCallbacks* callbacks) {
    if (!bufferInfos || !callbacks) return kAseInvalidParameter;
    if (numChannels <= 0 || numChannels > 2 * kChannelsPerDirection)
        return kAseInvalidParameter;
    if (!isAcceptableBufferSize(bufferSize)) return kAseInvalidMode;
    if (m_running.load()) return kAseInvalidMode;

    disposeBuffers();

    for (long i = 0; i < kChannelsPerDirection; ++i) {
        m_activeIn[i] = false;
        m_activeOut[i] = false;
    }

    for (long i = 0; i < numChannels; ++i) {
        AsioBufferInfo& info = bufferInfos[i];
        if (info.channelNum < 0 || info.channelNum >= kChannelsPerDirection) {
            disposeBuffers();
            return kAseInvalidParameter;
        }
        float* base = info.isInput ? &m_in[info.channelNum][0][0]
                                   : &m_out[info.channelNum][0][0];
        info.buffers[0] = base;
        info.buffers[1] = base + kMaxBufferSize;
        if (info.isInput)
            m_activeIn[info.channelNum] = true;
        else
            m_activeOut[info.channelNum] = true;
    }

    m_bufferSize = bufferSize;
    m_callbacks = callbacks;
    m_buffersCreated.store(true);
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::disposeBuffers() {
    if (m_running.load()) stop();
    m_callbacks = nullptr;
    m_buffersCreated.store(false);
    for (long i = 0; i < kChannelsPerDirection; ++i) {
        m_activeIn[i] = false;
        m_activeOut[i] = false;
    }
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::controlPanel() { return kAseNotPresent; }

AsioError JB_ASIO_CALL VirtualAsioDriver::future(long selector, void* opt) {
    (void)selector;
    (void)opt;
    return kAseNotPresent;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::outputReady() { return kAseNotPresent; }

long JB_ASIO_CALL VirtualAsioDriver::asioMessage(long selector, long value,
                                                 void* message, double* opt) {
    (void)message;
    (void)opt;
    switch (selector) {
        case kAsioSelectorSupported:
            switch (value) {
                case kAsioSelectorSupported:
                case kAsioEngineVersion:
                case kAsioSupportsTimeInfo:
                case kAsioSupportsTimeCode:
                case kAsioSupportsInputMonitor:
                    return 1;
                default:
                    return 0;
            }
        case kAsioEngineVersion:
            return 2;  // ASIO 2 host API
        // v1 only ever fires bufferSwitch, so it offers no time info or
        // time code and must say so.
        case kAsioSupportsTimeInfo:
        case kAsioSupportsTimeCode:
        case kAsioSupportsInputMonitor:
            return 0;
        default:
            return 0;
    }
}

void VirtualAsioDriver::clockLoop() {
    LARGE_INTEGER freq;
    QueryPerformanceFrequency(&freq);

    const double period = double(m_bufferSize) / kSampleRate;
    const long long start = qpcNow(freq);
    double next = seconds(freq, start);
    long index = 0;

    while (m_running.load(std::memory_order_relaxed)) {
        // Reattach at most once a second: a missing section must not turn into
        // per-callback work, and the retry allocates. Until then, silence.
        if (!m_ring.attached()) {
            const long long nowTicks = qpcNow(freq);
            if (nowTicks - m_lastBusAttempt >= freq.QuadPart) {
                m_lastBusAttempt = nowTicks;
                attachBus();
            }
        }

        const double now = seconds(freq, qpcNow(freq));
        const double remain = next - now;

        if (remain > 0.0004) {
            const long long dueMs = static_cast<long long>((remain - 0.0003) * 1000.0);
            if (dueMs >= 1 && m_timer) {
                LARGE_INTEGER due;
                due.QuadPart = -dueMs * 10000;  // relative, 100 ns units
                SetWaitableTimer(m_timer, &due, 0, nullptr, nullptr, FALSE);
                WaitForSingleObject(m_timer, static_cast<DWORD>(dueMs + 4));
            }
            // Spin the last fraction of a millisecond: a waitable timer alone
            // gives ~0.5 ms granularity, which at 128 frames (2.67 ms) would
            // smear the cadence noticeably.
            while (m_running.load(std::memory_order_relaxed) &&
                   seconds(freq, qpcNow(freq)) < next) {
                YieldProcessor();
            }
            if (!m_running.load(std::memory_order_relaxed)) break;
        }

        index ^= 1;  // the two halves of the double buffer
        fire(index);

        next += period;
        const double after = seconds(freq, qpcNow(freq));
        if (after - next > 0.25) next = after + period;  // drop a backlog
    }
}

void VirtualAsioDriver::fire(long bufferIndex) {
    m_frames.fetch_add(m_bufferSize, std::memory_order_relaxed);
    AsioCallbacks* callbacks = m_callbacks;
    if (!callbacks || !callbacks->bufferSwitch) return;

    // Fill the inputs for the half the host is about to read. read() never
    // blocks, allocates or throws: no block in the ring leaves the buffers
    // silent and ticks the reader slot's underrun counter.
    const int frames = static_cast<int>(m_bufferSize);
    float* dst[kChannelsPerDirection] = {m_in[0][bufferIndex], m_in[1][bufferIndex]};
    m_ring.read(dst, static_cast<int>(kChannelsPerDirection), frames);

    callbacks->bufferSwitch(bufferIndex, kAsioFalse);
}

}  // namespace jb::asio
