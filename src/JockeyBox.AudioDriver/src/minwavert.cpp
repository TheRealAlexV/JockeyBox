/* WaveRT wave miniport and its silent capture stream. */
#include "minwavert.h"

// ---------------------------------------------------------------------------
// Filter description: one capture pin, one PCM data range.
// ---------------------------------------------------------------------------

static KSDATARANGE_AUDIO g_WaveDataRanges[] =
{
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            0,                                              // Flags
            (JBX_CHANNELS * JBX_BITS_PER_SAMPLE) / 8,       // Block alignment
            0,                                              // Reserved
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        JBX_CHANNELS,           // MaximumChannels
        JBX_BITS_PER_SAMPLE,    // MinimumBitsPerSample
        JBX_BITS_PER_SAMPLE,    // MaximumBitsPerSample
        JBX_SAMPLE_RATE,        // MinimumSampleFrequency
        JBX_SAMPLE_RATE         // MaximumSampleFrequency
    }
};

static PKSDATARANGE g_WaveDataRangePointers[] =
{
    (PKSDATARANGE)&g_WaveDataRanges[0]
};

// Capture wave pin: a bridge source pin that the topology filter connects to.
static PCPIN_DESCRIPTOR g_WavePins[] =
{
    {
        1,                      // MaxGlobalInstanceCount
        1,                      // MaxFilterInstanceCount
        0,                      // MinFilterInstanceCount
        NULL,                   // AutomationTable
        {
            0,                                  // InterfacesCount
            NULL,                               // Interfaces
            0,                                  // MediumsCount
            NULL,                               // Mediums
            (ULONG)SIZEOF_ARRAY(g_WaveDataRangePointers),
            g_WaveDataRangePointers,
            KSPIN_DATAFLOW_OUT,                 // Capture: data flows to the client
            KSPIN_COMMUNICATION_BRIDGE,
            &KSCATEGORY_AUDIO,
            NULL,                               // Name
            0                                   // Reserved
        }
    }
};

static const GUID g_WaveFilterCategories[] =
{
    KSCATEGORY_AUDIO,
    KSCATEGORY_REALTIME,        // WaveRT
    KSCATEGORY_CAPTURE
};

static PCFILTER_DESCRIPTOR g_WaveFilterDescriptor =
{
    0,                                              // Version
    NULL,                                           // AutomationTable
    sizeof(PCPIN_DESCRIPTOR),                       // PinSize
    (ULONG)SIZEOF_ARRAY(g_WavePins),                // PinCount
    g_WavePins,
    sizeof(PCNODE_DESCRIPTOR),                      // NodeSize
    0,                                              // NodeCount
    NULL,                                           // Nodes
    0,                                              // ConnectionCount
    NULL,                                           // Connections
    (ULONG)SIZEOF_ARRAY(g_WaveFilterCategories),    // CategoryCount
    g_WaveFilterCategories
};

// ---------------------------------------------------------------------------
// CMiniportWaveRT
// ---------------------------------------------------------------------------

CMiniportWaveRT::CMiniportWaveRT()
    : m_Port(NULL)
{
}

void CMiniportWaveRT::Destroy()
{
    this->~CMiniportWaveRT();
    ExFreePoolWithTag(this, JBX_POOL_TAG);
}

CMiniportWaveRT::~CMiniportWaveRT()
{
    if (m_Port != NULL)
    {
        m_Port->Release();
        m_Port = NULL;
    }
}

NTSTATUS CMiniportWaveRT::CreateInstance(_Out_ CMiniportWaveRT ** OutMiniport)
{
    PAGED_CODE();

    if (OutMiniport == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }
    *OutMiniport = NULL;

    PVOID memory = ExAllocatePoolWithTag(NonPagedPoolNx, sizeof(CMiniportWaveRT), JBX_POOL_TAG);
    if (memory == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    *OutMiniport = new(memory) CMiniportWaveRT();
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRT::NonDelegatingQueryInterface(REFIID riid, PVOID * ppvObject)
{
    if (ppvObject == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    if (IsEqualGUIDAligned(riid, IID_IUnknown) ||
        IsEqualGUIDAligned(riid, IID_IMiniport) ||
        IsEqualGUIDAligned(riid, IID_IMiniportWaveRT))
    {
        *ppvObject = (IMiniportWaveRT *)this;
        JbxAddRef();
        return STATUS_SUCCESS;
    }

    *ppvObject = NULL;
    return STATUS_INVALID_PARAMETER;
}

NTSTATUS CMiniportWaveRT::Init(
    PUNKNOWN UnknownAdapter,
    PRESOURCELIST ResourceList,
    PPORTWAVERT Port)
{
    PAGED_CODE();

    UNREFERENCED_PARAMETER(UnknownAdapter);
    UNREFERENCED_PARAMETER(ResourceList);

    if (Port == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    Port->AddRef();
    m_Port = Port;

    // Phase 2: the driver owns the Global\ section; the app only opens it.
    // A failure here must not take the endpoint away, so it is logged and the
    // stream simply stays silent with no ring attached.
    NTSTATUS ringStatus = m_Ring.Create();
    if (!NT_SUCCESS(ringStatus))
    {
        DbgPrintEx(
            DPFLTR_IHVAUDIO_ID,
            DPFLTR_ERROR_LEVEL,
            "JockeyBox.Line: section create failed 0x%08X\n",
            ringStatus);
    }

    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRT::GetDescription(PPCFILTER_DESCRIPTOR * Description)
{
    PAGED_CODE();

    if (Description == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    *Description = &g_WaveFilterDescriptor;
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRT::DataRangeIntersection(
    ULONG PinId,
    PKSDATARANGE DataRange,
    PKSDATARANGE MatchingDataRange,
    ULONG OutputBufferLength,
    PVOID ResultantFormat,
    PULONG ResultantFormatLength)
{
    PAGED_CODE();

    UNREFERENCED_PARAMETER(PinId);
    UNREFERENCED_PARAMETER(DataRange);

    if (MatchingDataRange == NULL || ResultantFormatLength == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    *ResultantFormatLength = sizeof(KSDATAFORMAT_WAVEFORMATEX);

    // The skeleton supports exactly one format, so any request that does not
    // ask for PCM over WAVEFORMATEX cannot intersect.
    if (!IsEqualGUIDAligned(MatchingDataRange->SubFormat, KSDATAFORMAT_SUBTYPE_PCM) ||
        !IsEqualGUIDAligned(MatchingDataRange->Specifier, KSDATAFORMAT_SPECIFIER_WAVEFORMATEX))
    {
        return STATUS_NO_MATCH;
    }

    if (OutputBufferLength < sizeof(KSDATAFORMAT_WAVEFORMATEX) || ResultantFormat == NULL)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    KSDATAFORMAT_WAVEFORMATEX *format = (KSDATAFORMAT_WAVEFORMATEX *)ResultantFormat;
    RtlZeroMemory(format, sizeof(KSDATAFORMAT_WAVEFORMATEX));

    format->DataFormat.FormatSize = sizeof(KSDATAFORMAT_WAVEFORMATEX);
    format->DataFormat.Flags = 0;
    format->DataFormat.SampleSize = (JBX_CHANNELS * JBX_BITS_PER_SAMPLE) / 8;
    format->DataFormat.Reserved = 0;
    format->DataFormat.MajorFormat = KSDATAFORMAT_TYPE_AUDIO;
    format->DataFormat.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
    format->DataFormat.Specifier = KSDATAFORMAT_SPECIFIER_WAVEFORMATEX;

    format->WaveFormatEx.wFormatTag = WAVE_FORMAT_PCM;
    format->WaveFormatEx.nChannels = (WORD)JBX_CHANNELS;
    format->WaveFormatEx.nSamplesPerSec = JBX_SAMPLE_RATE;
    format->WaveFormatEx.wBitsPerSample = (WORD)JBX_BITS_PER_SAMPLE;
    format->WaveFormatEx.nBlockAlign = (WORD)format->DataFormat.SampleSize;
    format->WaveFormatEx.nAvgBytesPerSec = JBX_SAMPLE_RATE * format->DataFormat.SampleSize;
    format->WaveFormatEx.cbSize = 0;

    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRT::GetDeviceDescription(PDEVICE_DESCRIPTION DeviceDescription)
{
    PAGED_CODE();

    if (DeviceDescription == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    // No DMA: the stream buffer is ordinary non-paged memory. PortCls only
    // asks for this when it programs a real controller; a benign, zeroed
    // description keeps the object complete without pretending to be PCI.
    RtlZeroMemory(DeviceDescription, sizeof(DEVICE_DESCRIPTION));
    DeviceDescription->Version = DEVICE_DESCRIPTION_VERSION;
    DeviceDescription->Master = FALSE;
    DeviceDescription->ScatterGather = FALSE;
    DeviceDescription->Dma32BitAddresses = TRUE;
    DeviceDescription->InterfaceType = PNPBus;
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRT::NewStream(
    PMINIPORTWAVERTSTREAM * Stream,
    PPORTWAVERTSTREAM PortStream,
    ULONG Pin,
    BOOLEAN Capture,
    PKSDATAFORMAT DataFormat)
{
    PAGED_CODE();

    if (Stream == NULL || PortStream == NULL || DataFormat == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }
    *Stream = NULL;

    if (Pin != 0 || !Capture)
    {
        return STATUS_INVALID_PARAMETER;
    }

    if (DataFormat->FormatSize < sizeof(KSDATAFORMAT_WAVEFORMATEX) ||
        !IsEqualGUIDAligned(DataFormat->SubFormat, KSDATAFORMAT_SUBTYPE_PCM) ||
        !IsEqualGUIDAligned(DataFormat->Specifier, KSDATAFORMAT_SPECIFIER_WAVEFORMATEX))
    {
        return STATUS_INVALID_PARAMETER;
    }

    PVOID memory = ExAllocatePoolWithTag(
        NonPagedPoolNx, sizeof(CMiniportWaveRTStream), JBX_POOL_TAG);
    if (memory == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    *Stream = new(memory) CMiniportWaveRTStream(this, PortStream, Capture, DataFormat);
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// CMiniportWaveRTStream
// ---------------------------------------------------------------------------

CMiniportWaveRTStream::CMiniportWaveRTStream(
    CMiniportWaveRT * Miniport,
    PPORTWAVERTSTREAM PortStream,
    BOOLEAN Capture,
    PKSDATAFORMAT DataFormat)
    : m_Miniport(Miniport),
      m_PortStream(PortStream),
      m_Capture(Capture),
      m_ulBytesPerFrame(0),
      m_pvBuffer(NULL),
      m_pMdl(NULL),
      m_ulBufferBytes(0),
      m_ulBufferFrames(0),
      m_State(KSSTATE_STOP),
      m_ullPositionFrames(0),
      m_pullPositionRegister(NULL)
{
    PAGED_CODE();

    RtlZeroMemory(&m_Format, sizeof(m_Format));
    RtlZeroMemory(&m_liRunStart, sizeof(m_liRunStart));
    RtlZeroMemory(&m_liFrequency, sizeof(m_liFrequency));

    ULONG copySize = DataFormat->FormatSize;
    if (copySize > sizeof(KSDATAFORMAT_WAVEFORMATEX))
    {
        copySize = sizeof(KSDATAFORMAT_WAVEFORMATEX);
    }
    RtlCopyMemory(&m_Format, DataFormat, copySize);

    m_ulBytesPerFrame = (ULONG)m_Format.WaveFormatEx.nBlockAlign;
    if (m_ulBytesPerFrame == 0)
    {
        m_ulBytesPerFrame = (JBX_CHANNELS * JBX_BITS_PER_SAMPLE) / 8;
    }

    if (m_Miniport != NULL)
    {
        m_Miniport->AddRef();
    }
    if (m_PortStream != NULL)
    {
        m_PortStream->AddRef();
    }

    m_pullPositionRegister = (ULONG64 *)ExAllocatePoolWithTag(
        NonPagedPoolNx, sizeof(ULONG64), JBX_POOL_TAG);
    if (m_pullPositionRegister != NULL)
    {
        *m_pullPositionRegister = 0;
    }
}

void CMiniportWaveRTStream::Destroy()
{
    this->~CMiniportWaveRTStream();
    ExFreePoolWithTag(this, JBX_POOL_TAG);
}

CMiniportWaveRTStream::~CMiniportWaveRTStream()
{
    FreeAudioBuffer(m_pMdl, m_ulBufferBytes);

    if (m_pullPositionRegister != NULL)
    {
        ExFreePoolWithTag(m_pullPositionRegister, JBX_POOL_TAG);
        m_pullPositionRegister = NULL;
    }

    if (m_PortStream != NULL)
    {
        m_PortStream->Release();
        m_PortStream = NULL;
    }
    if (m_Miniport != NULL)
    {
        m_Miniport->Release();
        m_Miniport = NULL;
    }
}

NTSTATUS CMiniportWaveRTStream::NonDelegatingQueryInterface(REFIID riid, PVOID * ppvObject)
{
    if (ppvObject == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    if (IsEqualGUIDAligned(riid, IID_IUnknown) ||
        IsEqualGUIDAligned(riid, IID_IMiniportWaveRTStream))
    {
        *ppvObject = (IMiniportWaveRTStream *)this;
        JbxAddRef();
        return STATUS_SUCCESS;
    }

    *ppvObject = NULL;
    return STATUS_INVALID_PARAMETER;
}

void CMiniportWaveRTStream::ZeroBuffer()
{
    if (m_pvBuffer != NULL && m_ulBufferBytes != 0)
    {
        RtlZeroMemory(m_pvBuffer, m_ulBufferBytes);
    }
}

ULONGLONG CMiniportWaveRTStream::ElapsedFrames() const
{
    if (m_liFrequency.QuadPart <= 0)
    {
        return 0;
    }

    LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);
    LONGLONG delta = now.QuadPart - m_liRunStart.QuadPart;
    if (delta <= 0)
    {
        return 0;
    }

    ULONGLONG sampleRate = m_Format.WaveFormatEx.nSamplesPerSec;
    if (sampleRate == 0)
    {
        sampleRate = JBX_SAMPLE_RATE;
    }

    return ((ULONGLONG)delta * sampleRate) / (ULONGLONG)m_liFrequency.QuadPart;
}

NTSTATUS CMiniportWaveRTStream::SetFormat(PKSDATAFORMAT DataFormat)
{
    PAGED_CODE();

    if (DataFormat == NULL || DataFormat->FormatSize < sizeof(KSDATAFORMAT_WAVEFORMATEX))
    {
        return STATUS_INVALID_PARAMETER;
    }
    if (m_pvBuffer != NULL)
    {
        // The buffer is sized from the format; changing it mid-stream would
        // invalidate what PortCls already mapped.
        return STATUS_INVALID_DEVICE_STATE;
    }

    RtlZeroMemory(&m_Format, sizeof(m_Format));
    RtlCopyMemory(&m_Format, DataFormat, sizeof(KSDATAFORMAT_WAVEFORMATEX));

    m_ulBytesPerFrame = (ULONG)m_Format.WaveFormatEx.nBlockAlign;
    if (m_ulBytesPerFrame == 0)
    {
        return STATUS_INVALID_PARAMETER;
    }

    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRTStream::SetState(KSSTATE State)
{
    if (State == m_State)
    {
        return STATUS_SUCCESS;
    }

    if (State == KSSTATE_RUN)
    {
        // Silence, always: clear whatever the previous run left behind. Phase 3
        // will copy from the ring here once IsRingAttached() is true; until
        // then an attached ring changes nothing about what the engine reads.
        ZeroBuffer();
        if (m_Miniport != NULL && m_Miniport->IsRingAttached())
        {
            // Phase 3 fills this block from the ring payload.
        }
        m_liRunStart = KeQueryPerformanceCounter(&m_liFrequency);
    }
    else if (m_State == KSSTATE_RUN)
    {
        m_ullPositionFrames += ElapsedFrames();
    }

    m_State = State;
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRTStream::GetPosition(KSAUDIO_POSITION * Position)
{
    if (Position == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    ULONGLONG position = m_ullPositionFrames;
    if (m_State == KSSTATE_RUN)
    {
        position += ElapsedFrames();
    }

    Position->PlayOffset = position;
    Position->WriteOffset = 0;

    if (m_pullPositionRegister != NULL)
    {
        *m_pullPositionRegister = position;
    }

    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRTStream::AllocateAudioBuffer(
    ULONG RequestedSize,
    PMDL * AudioBufferMdl,
    ULONG * ActualSize,
    ULONG * OffsetFromFirstPage,
    MEMORY_CACHING_TYPE * CacheType)
{
    PAGED_CODE();

    if (AudioBufferMdl == NULL || ActualSize == NULL ||
        OffsetFromFirstPage == NULL || CacheType == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    if (m_pvBuffer != NULL)
    {
        return STATUS_UNSUCCESSFUL;
    }

    if (RequestedSize == 0 || RequestedSize > 0x40000000)
    {
        return STATUS_INVALID_PARAMETER;
    }

    ULONG size = (RequestedSize + PAGE_SIZE - 1) & ~(PAGE_SIZE - 1);

    PVOID buffer = ExAllocatePoolWithTag(NonPagedPoolNx, size, JBX_POOL_TAG);
    if (buffer == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlZeroMemory(buffer, size);

    PMDL mdl = IoAllocateMdl(buffer, size, FALSE, FALSE, NULL);
    if (mdl == NULL)
    {
        ExFreePoolWithTag(buffer, JBX_POOL_TAG);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    MmBuildMdlForNonPagedPool(mdl);

    m_pvBuffer = buffer;
    m_pMdl = mdl;
    m_ulBufferBytes = size;
    m_ulBufferFrames = (m_ulBytesPerFrame != 0) ? (size / m_ulBytesPerFrame) : 0;

    *AudioBufferMdl = mdl;
    *ActualSize = size;
    *OffsetFromFirstPage = 0;
    *CacheType = MmCached;

    return STATUS_SUCCESS;
}

void CMiniportWaveRTStream::FreeAudioBuffer(PMDL AudioBufferMdl, ULONG BufferSize)
{
    UNREFERENCED_PARAMETER(BufferSize);

    if (AudioBufferMdl != NULL)
    {
        if (m_pMdl == AudioBufferMdl)
        {
            m_pMdl = NULL;
        }
        IoFreeMdl(AudioBufferMdl);
    }

    if (m_pvBuffer != NULL)
    {
        ExFreePoolWithTag(m_pvBuffer, JBX_POOL_TAG);
        m_pvBuffer = NULL;
    }

    m_ulBufferBytes = 0;
    m_ulBufferFrames = 0;
}

void CMiniportWaveRTStream::GetHWLatency(KSRTAUDIO_HWLATENCY * HwLatency)
{
    if (HwLatency == NULL)
    {
        return;
    }

    // Virtual device: no FIFO and no codec, so only the buffer period remains.
    HwLatency->FifoSize = 0;
    HwLatency->ChipsetDelay = 0;
    HwLatency->CodecDelay = 0;
}

NTSTATUS CMiniportWaveRTStream::GetPositionRegister(KSRTAUDIO_HWREGISTER * Register)
{
    if (Register == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }
    if (m_pullPositionRegister == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    Register->Register = m_pullPositionRegister;
    Register->Width = sizeof(ULONG64);
    Register->Numerator = 1;
    Register->Denominator = 1;
    Register->Accuracy = 0;
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportWaveRTStream::GetClockRegister(KSRTAUDIO_HWREGISTER * Register)
{
    UNREFERENCED_PARAMETER(Register);

    // Optional in WaveRT; the position counter above is the clock.
    return STATUS_NOT_SUPPORTED;
}
