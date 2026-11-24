/* JockeyBox Virtual Line 1 -- phase 1 skeleton audio driver.
 *
 * Shared kernel includes, pool tag and the small COM base used by the miniport
 * objects. Deliberately avoids <stdunk.h>: its CUnknown implementation is not
 * in the kit header, and its operator-new pragma message would add noise to a
 * build that must stay warning free.
 */
#pragma once

/* TargetVersion=Windows10 keeps NTDDI_VERSION below the gate for
 * ExAllocatePool2, so the classic allocator is the correct call here. */
#pragma warning(disable: 4996)
#pragma warning(disable: 4595)

#include <portcls.h>

/* Placement new for kernel C++: <new> is unusable with /kernel because the
 * WDK CRT header pulls in exception machinery. The only form used here is
 * construction into memory from ExAllocatePoolWithTag.
 */
inline void * __cdecl operator new(size_t, void * Place)
{
    return Place;
}

inline void __cdecl operator delete(void *, void *)
{
}

/* Never called: objects are destroyed through Destroy(), which frees the pool
 * block. Present so the compiler's deleting-destructor stubs link. */
inline void __cdecl operator delete(void *, size_t)
{
}

#define JBX_POOL_TAG 'ILBJ'     // 'JBLI' as four chars

// The single supported format of the skeleton: PCM, 48 kHz, 16 bit, stereo.
static const ULONG JBX_SAMPLE_RATE = 48000;
static const ULONG JBX_CHANNELS = 2;
static const ULONG JBX_BITS_PER_SAMPLE = 16;

/* Minimal IUnknown base for PortCls objects.
 *
 * The refcount starts at one: CreateInstance returns an owned reference, and
 * PcRegisterSubdevice takes its own. Destroy() releases the pool allocation,
 * so no operator delete is required.
 */
class CUnknownBase
{
public:
    CUnknownBase() : m_lRefCount(1) {}
    virtual ~CUnknownBase() {}

    STDMETHOD_(NTSTATUS, NonDelegatingQueryInterface)(REFIID riid, PVOID * ppvObject) = 0;

    ULONG JbxAddRef()
    {
        return (ULONG)InterlockedIncrement(&m_lRefCount);
    }

    ULONG JbxRelease()
    {
        LONG count = InterlockedDecrement(&m_lRefCount);
        if (count == 0)
        {
            Destroy();
        }
        return (ULONG)count;
    }

protected:
    virtual void Destroy() = 0;

private:
    LONG m_lRefCount;
};

/* Standard IUnknown thunks for a miniport class. NonDelegatingQueryInterface
 * is declared here and implemented by each class.
 */
#define JBX_STD_UNKNOWN()                                                    \
    STDMETHODIMP_(NTSTATUS) NonDelegatingQueryInterface(REFIID riid,         \
        PVOID * ppvObject) override;                                         \
    STDMETHODIMP_(NTSTATUS) QueryInterface(REFIID riid, PVOID * ppvObject)   \
        override                                                             \
    {                                                                        \
        return NonDelegatingQueryInterface(riid, ppvObject);                  \
    }                                                                        \
    STDMETHODIMP_(ULONG) AddRef() override                                    \
    {                                                                        \
        return JbxAddRef();                                                   \
    }                                                                        \
    STDMETHODIMP_(ULONG) Release() override                                   \
    {                                                                        \
        return JbxRelease();                                                  \
    }
