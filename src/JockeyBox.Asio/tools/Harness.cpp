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

#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <thread>

namespace {

std::atomic<long> g_callbacks{0};
long g_lastIndex = -1;

void onBufferSwitch(long doubleBufferIndex, jb::asio::AsioBool directProcess) {
    (void)directProcess;
    g_lastIndex = doubleBufferIndex;
    g_callbacks.fetch_add(1, std::memory_order_relaxed);
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
    CoUninitialize();

    if (argc > 2 && wcscmp(argv[2], L"--keep") == 0) {
        wprintf(L"\nHKCU registration kept.\n");
    } else {
        unregisterInHkcu();
        wprintf(L"\nHKCU registration removed.\n");
    }
    return (calls > 0) ? 0 : 2;
}
