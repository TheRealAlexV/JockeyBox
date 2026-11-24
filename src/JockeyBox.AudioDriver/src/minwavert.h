/* WaveRT wave miniport: one silent capture pin. */
#pragma once

#include "jbx.h"
#include "jbxring.h"

class CMiniportWaveRT;

/* WaveRT stream for the single capture pin.
 *
 * Phase 1 has no DMA engine and no shared section: the cyclic buffer handed to
 * PortCls is plain non-paged memory that is zeroed on allocation and again when
 * the stream enters KSSTATE_RUN, so the audio engine reads silence. The stream
 * reports a monotonic position derived from the performance counter.
 */
class CMiniportWaveRTStream : public IMiniportWaveRTStream, public CUnknownBase
{
public:
    JBX_STD_UNKNOWN();
    IMP_IMiniportWaveRTStream;

    CMiniportWaveRTStream(
        _In_ CMiniportWaveRT * Miniport,
        _In_ PPORTWAVERTSTREAM PortStream,
        _In_ BOOLEAN Capture,
        _In_ PKSDATAFORMAT DataFormat);

    ~CMiniportWaveRTStream();

private:
    void Destroy() override;

    ULONGLONG ElapsedFrames() const;
    void ZeroBuffer();

    CMiniportWaveRT *     m_Miniport;
    PPORTWAVERTSTREAM     m_PortStream;
    BOOLEAN               m_Capture;
    KSDATAFORMAT_WAVEFORMATEX m_Format;
    ULONG                 m_ulBytesPerFrame;
    PVOID                 m_pvBuffer;
    PMDL                  m_pMdl;
    ULONG                 m_ulBufferBytes;
    ULONG                 m_ulBufferFrames;
    KSSTATE               m_State;
    ULONGLONG             m_ullPositionFrames;
    LARGE_INTEGER         m_liRunStart;
    LARGE_INTEGER         m_liFrequency;
    ULONG64 *             m_pullPositionRegister;
};

/* WaveRT wave miniport: exactly one capture pin, format from a single PCM
 * data range. NewStream creates CMiniportWaveRTStream objects.
 */
class CMiniportWaveRT : public IMiniportWaveRT, public CUnknownBase
{
public:
    JBX_STD_UNKNOWN();
    IMP_IMiniportWaveRT;

    CMiniportWaveRT();
    ~CMiniportWaveRT();

    static NTSTATUS CreateInstance(
        _Out_ CMiniportWaveRT ** OutMiniport);

    // Phase 2 attach state. The app claims a reader slot itself, so this is the
    // only signal the driver needs; there is no handover IOCTL.
    BOOLEAN IsRingAttached() const { return m_Ring.AnyReaderActive(); }
    ULONG RingPayloadBytes() const { return m_Ring.PayloadBytes(); }

private:
    void Destroy() override;

    PPORTWAVERT m_Port;
    CJbxRing m_Ring;
};
