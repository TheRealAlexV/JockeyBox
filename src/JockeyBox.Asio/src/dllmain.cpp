// COM in-proc entry points. Loaded through the CLSID's InprocServer32 value,
// so a host reaches these with CoCreateInstance rather than GetProcAddress.

#include "JockeyBoxAsioAbi.h"
#include "VirtualAsioDriver.h"

#include <windows.h>
#include <unknwn.h>

#include <atomic>
#include <new>

namespace {

std::atomic<long> g_driverObjects{0};
std::atomic<long> g_factoryLocks{0};

// ASIO hosts historically pass the driver's own CLSID as the riid to
// CoCreateInstance instead of IID_IUnknown, so the object answers both.
class ClassFactory final : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory)) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }  // static singleton
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid,
                                             void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;

        auto* driver = new (std::nothrow) jb::asio::VirtualAsioDriver();
        if (!driver) return E_OUTOFMEMORY;

        g_driverObjects.fetch_add(1);
        HRESULT hr = driver->QueryInterface(riid, ppv);
        if (FAILED(hr)) g_driverObjects.fetch_sub(1);
        driver->Release();  // factory holds no reference
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) g_factoryLocks.fetch_add(1);
        else g_factoryLocks.fetch_sub(1);
        return S_OK;
    }
};

ClassFactory g_factory;

}  // namespace

// Called by jb::asio::VirtualAsioDriver's destructor.
void JockeyBoxAsioDriverReleased() { g_driverObjects.fetch_sub(1); }

// combaseapi.h already declares both of these with C linkage and no
// dllexport, so they are defined against that declaration and exported by
// JockeyBox.Asio.def rather than with __declspec(dllexport).
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, jb::asio::kDriverClsid))
        return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() {
    return (g_driverObjects.load() == 0 && g_factoryLocks.load() == 0) ? S_OK
                                                                       : S_FALSE;
}

BOOL APIENTRY DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
