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

// One diagnostic line per event, to the debugger/debug log. The driver has no
// console of its own and must never throw from the clock thread.
void logOnce(const char* message) noexcept {
    char line[256];
    _snprintf_s(line, sizeof(line), _TRUNCATE, "JockeyBox ASIO: %s\n", message);
    ::OutputDebugStringA(line);
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

    // The device geometry is the app's Line list, not a constant. A missing file
    // means "never configured" and keeps the legacy stereo pair; a file that will
    // not parse leaves the stereo default and is reported, never guessed at.
    config::loadForHost(m_device);
    m_channels = m_device.channels;
    m_configErrorLogged = false;
    m_geometryMismatchLogged = false;
    m_restartRequiredLogged = false;
    if (m_device.error[0]) {
        char line[256];
        _snprintf_s(line, sizeof(line), _TRUNCATE,
                    "virtual-device.json rejected (%s); using %ld channels and serving "
                    "silence until it is fixed",
                    m_device.error, m_channels);
        logOnce(line);
        m_configErrorLogged = true;
    }

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
    switch (m_busState.load(std::memory_order_relaxed)) {
        case kBusAttached:
            _snprintf_s(text, 124, _TRUNCATE,
                        "JockeyBox Virtual ASIO: reading bus '%s'", m_busName);
            break;
        case kBusNameInvalid:
            // Distinct from "not attached": retrying cannot help a name the ring
            // namespace rejects, so tell the host why the bus never appears.
            _snprintf_s(text, 124, _TRUNCATE,
                        "JockeyBox Virtual ASIO: JOCKEYBOX_ASIO_BUS is longer than 63 "
                        "characters; bus not attached, serving silence");
            break;
        case kBusGeometryMismatch:
            // The one failure that looks like a healthy driver but is silence:
            // the app publishes a different width than the line list this driver
            // was configured with. Never read across the mismatch.
            _snprintf_s(text, 124, _TRUNCATE,
                        "JockeyBox Virtual ASIO: bus '%s' publishes %d channels but the "
                        "device is configured for %ld; restart OBS after fixing the line "
                        "list, serving silence",
                        m_busName, static_cast<int>(m_ring.lastSeenChannels()),
                        m_channels);
            break;
        default:
            _snprintf_s(text, 124, _TRUNCATE,
                        "JockeyBox Virtual ASIO: bus '%s' not attached; serving silence, "
                        "retrying once a second",
                        m_busName[0] ? m_busName : "main");
            break;
    }
}

void VirtualAsioDriver::resolveBusName() noexcept {
    if (m_busName[0]) return;
    if (m_busState.load(std::memory_order_relaxed) == kBusNameInvalid) return;
    // GetEnvironmentVariableA returns the required size, including the NUL, when
    // the buffer is too small. A result >= sizeof(value) means the configured
    // name does not fit the 64-char limit RingReader::attach enforces. Do not
    // fall back to "main": that silently attaches a bus the host never asked
    // for. Record the distinct state so getErrorMessage can explain it.
    char value[64] = {};
    const DWORD n = GetEnvironmentVariableA("JOCKEYBOX_ASIO_BUS", value, sizeof(value));
    if (n >= sizeof(value)) {
        m_busState.store(kBusNameInvalid, std::memory_order_relaxed);
        return;
    }
    if (n == 0) {
        strcpy_s(m_busName, "main");
    } else {
        strcpy_s(m_busName, value);
    }
}

void VirtualAsioDriver::attachBus() noexcept {
    if (m_busState.load(std::memory_order_relaxed) == kBusNameInvalid) return;

    // Re-read the line list on every attempt, not only in init(): the retry runs
    // once a second and is the only place a driver that started before the app can
    // learn the geometry. A malformed file never becomes audio.
    config::Device latest;
    config::loadForHost(latest);
    if (latest.error[0]) {
        if (!m_configErrorLogged) {
            char line[256];
            _snprintf_s(line, sizeof(line), _TRUNCATE,
                        "virtual-device.json rejected (%s); bus not attached, serving "
                        "silence",
                        latest.error);
            logOnce(line);
            m_configErrorLogged = true;
        }
        m_busState.store(kBusAbsent, std::memory_order_relaxed);
        return;
    }

    // Once the host has been told a channel count, a different config can only be
    // applied by restarting it. Say so instead of swapping bytes underneath it.
    if (m_negotiatedChannels > 0 && latest.channels != m_negotiatedChannels) {
        if (!m_restartRequiredLogged) {
            char line[256];
            _snprintf_s(line, sizeof(line), _TRUNCATE,
                        "virtual-device.json now asks for %d channels but the host opened "
                        "%ld; restart OBS to apply, serving silence",
                        latest.channels, m_negotiatedChannels);
            logOnce(line);
            m_restartRequiredLogged = true;
        }
        m_ring.detach();
        m_busState.store(kBusGeometryMismatch, std::memory_order_relaxed);
        return;
    }

    m_device = latest;
    m_channels = latest.channels;

    // Join a cushion sized from the host buffer: `3 * bufferSize` frames, floored
    // at three ring blocks and capped two blocks below capacity by
    // RingReader::attach. Scaling with bufferSize keeps a 1024-frame host more
    // than two callbacks of slack behind the writer; the old capacity/2 ceiling
    // gave a default 8-block ring only 4 blocks and starved on one late wake.
    const int32_t prefill = static_cast<int32_t>(3 * m_bufferSize);
    if (m_ring.attach(m_busName, prefill, static_cast<int32_t>(m_channels))) {
        const int32_t cushion = m_ring.cushionFrames();
        m_cushionFrames.store(cushion, std::memory_order_relaxed);
        const int32_t blockFrames = m_ring.blockFrames();
        char line[200];
        _snprintf_s(line, sizeof(line), _TRUNCATE,
                    "bus '%s' attached: %ld channels, cushion %ld frames (%ld blocks of "
                    "%ld), host buffer %ld",
                    m_busName, m_channels, static_cast<long>(cushion),
                    static_cast<long>(blockFrames > 0 ? cushion / blockFrames : 0),
                    static_cast<long>(blockFrames), static_cast<long>(m_bufferSize));
        logOnce(line);
        m_busState.store(kBusAttached, std::memory_order_relaxed);
        m_geometryMismatchLogged = false;
    } else if (m_ring.lastSeenChannels() > 0 &&
               m_ring.lastSeenChannels() != m_channels) {
        // The bus exists but publishes a different width. Reading it would use the
        // wrong stride, so serve silence until the app and the line list agree.
        if (!m_geometryMismatchLogged) {
            char line[256];
            _snprintf_s(line, sizeof(line), _TRUNCATE,
                        "bus '%s' publishes %d channels but the device is configured for "
                        "%ld; serving silence",
                        m_busName, m_ring.lastSeenChannels(), m_channels);
            logOnce(line);
            m_geometryMismatchLogged = true;
        }
        m_busState.store(kBusGeometryMismatch, std::memory_order_relaxed);
    } else {
        m_busState.store(kBusAbsent, std::memory_order_relaxed);
    }
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
    // One geometry for the whole device: inputs and outputs are the same width,
    // derived from the app's Line list at init().
    m_negotiatedChannels = m_channels;
    *numInputChannels = m_channels;
    *numOutputChannels = m_channels;
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::getLatencies(long* inputLatency,
                                                       long* outputLatency) {
    if (!inputLatency || !outputLatency) return kAseInvalidParameter;
    // Input the host actually records:
    //   inputLatency = bufferSize + steadyStateLagFrames
    // One host buffer is the block currently being filled. steadyStateLagFrames is
    // the mean buffered frames the reader's trim measured over its last 4 s span,
    // i.e. what the mixed route actually settles at; the trim can settle a few
    // blocks above the cushion, and reporting the cushion there would under-state
    // the delay by exactly that residual. Before the first span completes (or when
    // no bus is attached) it falls back to the cushion, then to one buffer.
    const int64_t measured = m_ring.measuredLagFrames();
    const long lag = measured >= 0
                         ? static_cast<long>(measured)
                         : m_cushionFrames.load(std::memory_order_relaxed);
    *inputLatency = m_bufferSize + lag;
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
    if (info->channel < 0 || info->channel >= m_channels)
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
    return size == 128 || size == 256 || size == 512 || size == 1024;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::createBuffers(
    AsioBufferInfo* bufferInfos, long numChannels, long bufferSize,
    AsioCallbacks* callbacks) {
    if (!bufferInfos || !callbacks) return kAseInvalidParameter;
    if (m_channels < kMinChannels || m_channels > kMaxChannels) return kAseInvalidMode;
    if (numChannels <= 0 || numChannels > 2 * m_channels)
        return kAseInvalidParameter;
    if (!isAcceptableBufferSize(bufferSize)) return kAseInvalidMode;
    if (m_running.load()) return kAseInvalidMode;

    disposeBuffers();

    // Allocated here, before start(), so no code on the callback path allocates.
    m_in = std::make_unique<float[]>(static_cast<size_t>(m_channels) * 2 * kMaxBufferSize);
    m_out = std::make_unique<float[]>(static_cast<size_t>(m_channels) * 2 * kMaxBufferSize);

    for (long i = 0; i < kMaxChannels; ++i) {
        m_activeIn[i] = false;
        m_activeOut[i] = false;
    }

    for (long i = 0; i < numChannels; ++i) {
        AsioBufferInfo& info = bufferInfos[i];
        if (info.channelNum < 0 || info.channelNum >= m_channels) {
            disposeBuffers();
            return kAseInvalidParameter;
        }
        float* base = info.isInput ? inputChannel(info.channelNum)
                                   : outputChannel(info.channelNum);
        info.buffers[0] = base;
        info.buffers[1] = base + kMaxBufferSize;
        if (info.isInput)
            m_activeIn[info.channelNum] = true;
        else
            m_activeOut[info.channelNum] = true;
    }

    m_negotiatedChannels = m_channels;
    m_bufferSize = bufferSize;
    m_callbacks = callbacks;
    m_buffersCreated.store(true);
    return kAseOk;
}

AsioError JB_ASIO_CALL VirtualAsioDriver::disposeBuffers() {
    if (m_running.load()) stop();
    m_callbacks = nullptr;
    m_buffersCreated.store(false);
    m_in.reset();
    m_out.reset();
    for (long i = 0; i < kMaxChannels; ++i) {
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

    // Raise this thread before the loop so a late wake cannot slip past a
    // callback. Both escalations are best-effort: a host or policy that forbids
    // them still gets a working clock, so failure is logged and swallowed,
    // never thrown. The final spin below stays as the last-resort clock.
    if (!::SetThreadPriority(::GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL))
        logOnce("SetThreadPriority(THREAD_PRIORITY_TIME_CRITICAL) failed; clock still runs");

    // MMCSS "Pro Audio" via avrt.dll, loaded dynamically so the DLL gains no
    // link dependency. Tolerate a missing export (older/preview Windows).
    using AvSetFn = HANDLE(WINAPI*)(LPCWSTR, LPDWORD);
    using AvRevertFn = BOOL(WINAPI*)(HANDLE);
    HMODULE avrt = ::LoadLibraryW(L"avrt.dll");
    HANDLE mmcss = nullptr;
    AvRevertFn mmcssRevert = nullptr;
    if (avrt) {
        FARPROC setProc = ::GetProcAddress(avrt, "AvSetMmThreadCharacteristicsW");
        FARPROC revertProc = ::GetProcAddress(avrt, "AvRevertMmThreadCharacteristics");
        AvSetFn mmcssSet = nullptr;
        static_assert(sizeof(mmcssSet) == sizeof(setProc), "FARPROC and function pointer differ");
        static_assert(sizeof(mmcssRevert) == sizeof(revertProc),
                      "FARPROC and function pointer differ");
        std::memcpy(&mmcssSet, &setProc, sizeof(mmcssSet));
        std::memcpy(&mmcssRevert, &revertProc, sizeof(mmcssRevert));
        if (mmcssSet) {
            DWORD taskIndex = 0;
            mmcss = mmcssSet(L"Pro Audio", &taskIndex);
        }
    }
    if (!mmcss)
        logOnce("MMCSS 'Pro Audio' unavailable; running on priority alone");

    const double period = double(m_bufferSize) / kSampleRate;
    const long long start = qpcNow(freq);
    double next = seconds(freq, start);
    long index = 0;

    while (m_running.load(std::memory_order_relaxed)) {
        // Reattach at most once a second: a missing section must not turn into
        // per-callback work, and the retry allocates. Until then, silence. An
        // invalid name can never attach, so skip the loop entirely.
        if (!m_ring.attached() &&
            m_busState.load(std::memory_order_relaxed) != kBusNameInvalid) {
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

    if (mmcss && mmcssRevert) mmcssRevert(mmcss);
    if (avrt) ::FreeLibrary(avrt);
}

void VirtualAsioDriver::fire(long bufferIndex) {
    m_frames.fetch_add(m_bufferSize, std::memory_order_relaxed);
    AsioCallbacks* callbacks = m_callbacks;
    if (!callbacks || !callbacks->bufferSwitch) return;

    // Fill the inputs for the half the host is about to read. read() never
    // blocks, allocates or throws: no block in the ring leaves the buffers
    // silent and ticks the reader slot's underrun counter.
    const int frames = static_cast<int>(m_bufferSize);
    if (!m_in) return;
    float* dst[config::kMaxChannels] = {};
    for (long c = 0; c < m_channels; ++c)
        dst[c] = inputChannel(c) + bufferIndex * kMaxBufferSize;
    m_ring.read(dst, static_cast<int>(m_channels), frames);

    callbacks->bufferSwitch(bufferIndex, kAsioFalse);
}

}  // namespace jb::asio
