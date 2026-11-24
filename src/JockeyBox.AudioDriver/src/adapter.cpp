/* JockeyBox Virtual Line 1 -- WDM PortCls function driver.
 *
 * DriverEntry hands the adapter to PortCls with PcInitializeAdapterDriver.
 * StartDevice builds one wave factory and one topology factory and registers
 * them with PcRegisterSubdevice, then wires the pair together with a physical
 * connection so the audio endpoint builder sees a single capture endpoint.
 */
// initguid.h turns the DEFINE_GUID declarations in portcls.h / ks.h into
// definitions, so IID_IMiniportWaveRT, CLSID_PortWaveRT and friends resolve
// without pulling in ksguid.lib. Included in exactly one translation unit.
#include <initguid.h>

#include "jbx.h"
#include "minwavert.h"
#include "mintopo.h"

static const ULONG JBX_MAX_MINIPORTS = 2;

static WCHAR g_WaveSubdeviceName[] = L"JockeyBoxLine1Wave";
static WCHAR g_TopoSubdeviceName[] = L"JockeyBoxLine1Topology";

static NTSTATUS JbxAddDevice(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PDEVICE_OBJECT PhysicalDeviceObject);

static NTSTATUS JbxStartDevice(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP Irp,
    _In_ PRESOURCELIST ResourceList);

extern "C" NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    PAGED_CODE();

    return PcInitializeAdapterDriver(DriverObject, RegistryPath, JbxAddDevice);
}

static NTSTATUS JbxAddDevice(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PDEVICE_OBJECT PhysicalDeviceObject)
{
    PAGED_CODE();

    return PcAddAdapterDevice(
        DriverObject,
        PhysicalDeviceObject,
        JbxStartDevice,
        JBX_MAX_MINIPORTS,
        0);
}

static NTSTATUS JbxStartDevice(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PIRP Irp,
    _In_ PRESOURCELIST ResourceList)
{
    PAGED_CODE();

    NTSTATUS status = STATUS_SUCCESS;

    PPORT wavePort = NULL;
    PPORT topoPort = NULL;
    CMiniportWaveRT *wave = NULL;
    CMiniportTopology *topo = NULL;

    // Wave filter factory: WaveRT, one silent capture pin.
    status = PcNewPort(&wavePort, CLSID_PortWaveRT);
    if (NT_SUCCESS(status))
    {
        status = wavePort->Init(DeviceObject, Irp, NULL, ResourceList, ResourceList);
    }
    if (NT_SUCCESS(status))
    {
        status = CMiniportWaveRT::CreateInstance(&wave);
    }
    if (NT_SUCCESS(status))
    {
        status = wave->Init(NULL, ResourceList, (PPORTWAVERT)wavePort);
    }
    if (NT_SUCCESS(status))
    {
        status = PcRegisterSubdevice(DeviceObject, g_WaveSubdeviceName, wave);
    }

    // Topology filter factory: the matching bridge pair.
    if (NT_SUCCESS(status))
    {
        status = PcNewPort(&topoPort, CLSID_PortTopology);
    }
    if (NT_SUCCESS(status))
    {
        status = topoPort->Init(DeviceObject, Irp, NULL, ResourceList, ResourceList);
    }
    if (NT_SUCCESS(status))
    {
        status = CMiniportTopology::CreateInstance(&topo);
    }
    if (NT_SUCCESS(status))
    {
        status = topo->Init(NULL, ResourceList, (PPORTTOPOLOGY)topoPort);
    }
    if (NT_SUCCESS(status))
    {
        status = PcRegisterSubdevice(DeviceObject, g_TopoSubdeviceName, topo);
    }

    // Pair wave pin 0 with topology pin 0. This is what makes the two
    // factories one endpoint instead of two orphaned filters.
    if (NT_SUCCESS(status))
    {
        status = PcRegisterPhysicalConnection(
            DeviceObject,
            wave,
            0,
            topo,
            0);
    }

    if (wave != NULL)
    {
        wave->Release();
    }
    if (topo != NULL)
    {
        topo->Release();
    }
    if (wavePort != NULL)
    {
        wavePort->Release();
    }
    if (topoPort != NULL)
    {
        topoPort->Release();
    }

    return status;
}
