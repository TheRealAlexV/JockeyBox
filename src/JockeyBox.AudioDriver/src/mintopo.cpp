/* Topology miniport: one bridge pair for the silent capture endpoint.
 *
 * Pin 0 is the bridge to the wave filter's capture pin; pin 1 is the physical
 * "line in" side of the pair. There are no nodes (no volume, no mute) in the
 * phase 1 skeleton, so the only internal route is pin 1 -> pin 0.
 */
#include "mintopo.h"

static PCPIN_DESCRIPTOR g_TopoPins[] =
{
    // Pin 0: bridge sink towards the wave filter.
    {
        1, 1, 0, NULL,
        {
            0, NULL, 0, NULL, 0, NULL,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_BRIDGE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    },
    // Pin 1: physical source side of the cable.
    {
        1, 1, 0, NULL,
        {
            0, NULL, 0, NULL, 0, NULL,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_BRIDGE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    }
};

static const KSTOPOLOGY_CONNECTION g_TopoConnections[] =
{
    { PCFILTER_NODE, 1, PCFILTER_NODE, 0 }
};

static const GUID g_TopoFilterCategories[] =
{
    KSCATEGORY_AUDIO,
    KSCATEGORY_TOPOLOGY
};

static PCFILTER_DESCRIPTOR g_TopoFilterDescriptor =
{
    0,                                              // Version
    NULL,                                           // AutomationTable
    sizeof(PCPIN_DESCRIPTOR),                       // PinSize
    (ULONG)SIZEOF_ARRAY(g_TopoPins),                // PinCount
    g_TopoPins,
    sizeof(PCNODE_DESCRIPTOR),                      // NodeSize
    0,                                              // NodeCount
    NULL,                                           // Nodes
    (ULONG)SIZEOF_ARRAY(g_TopoConnections),         // ConnectionCount
    g_TopoConnections,
    (ULONG)SIZEOF_ARRAY(g_TopoFilterCategories),    // CategoryCount
    g_TopoFilterCategories
};

CMiniportTopology::CMiniportTopology()
    : m_Port(NULL)
{
}

void CMiniportTopology::Destroy()
{
    this->~CMiniportTopology();
    ExFreePoolWithTag(this, JBX_POOL_TAG);
}

CMiniportTopology::~CMiniportTopology()
{
    if (m_Port != NULL)
    {
        m_Port->Release();
        m_Port = NULL;
    }
}

NTSTATUS CMiniportTopology::CreateInstance(_Out_ CMiniportTopology ** OutMiniport)
{
    PAGED_CODE();

    if (OutMiniport == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }
    *OutMiniport = NULL;

    PVOID memory = ExAllocatePoolWithTag(
        NonPagedPoolNx, sizeof(CMiniportTopology), JBX_POOL_TAG);
    if (memory == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    *OutMiniport = new(memory) CMiniportTopology();
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportTopology::NonDelegatingQueryInterface(REFIID riid, PVOID * ppvObject)
{
    if (ppvObject == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    if (IsEqualGUIDAligned(riid, IID_IUnknown) ||
        IsEqualGUIDAligned(riid, IID_IMiniport) ||
        IsEqualGUIDAligned(riid, IID_IMiniportTopology))
    {
        *ppvObject = (IMiniportTopology *)this;
        JbxAddRef();
        return STATUS_SUCCESS;
    }

    *ppvObject = NULL;
    return STATUS_INVALID_PARAMETER;
}

NTSTATUS CMiniportTopology::Init(
    PUNKNOWN UnknownAdapter,
    PRESOURCELIST ResourceList,
    PPORTTOPOLOGY Port)
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
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportTopology::GetDescription(PPCFILTER_DESCRIPTOR * Description)
{
    PAGED_CODE();

    if (Description == NULL)
    {
        return STATUS_INVALID_PARAMETER;
    }

    *Description = &g_TopoFilterDescriptor;
    return STATUS_SUCCESS;
}

NTSTATUS CMiniportTopology::DataRangeIntersection(
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
    UNREFERENCED_PARAMETER(MatchingDataRange);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(ResultantFormat);
    UNREFERENCED_PARAMETER(ResultantFormatLength);

    // Topology pins carry no data ranges in this skeleton.
    return STATUS_NOT_SUPPORTED;
}
