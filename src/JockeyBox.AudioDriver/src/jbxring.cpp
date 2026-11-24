/* Global\JockeyBox.Bus.Pair1 section creation, DACL and header init. */
#include <ntifs.h>      // Rtl*Sid/Acl and SECURITY_*_RID, not in wdm.h

#include "jbxring.h"

// User-visible name: BusLayout.MapNameFor("Pair1", BusScope.Global).
// The kernel object manager path drops the user-mode "Global\" prefix, which
// resolves to \BaseNamedObjects. The INF publishes this literal in the service
// key's SectionName value, so the app side reads it instead of guessing.
static const WCHAR * const JBX_SECTION_NAME = L"Global\\JockeyBox.Bus.Pair1";
static const WCHAR * const JBX_SECTION_OBJECT_NAME =
    L"\\BaseNamedObjects\\JockeyBox.Bus.Pair1";

static const SIZE_T JBX_MAX_SID_SIZE = 68;

/* Grants system and admins full control and authenticated users (i.e. a normal
 * interactive logon) query + map rights. Mapping is what the app needs; without
 * at least SECTION_MAP_READ|SECTION_MAP_WRITE the open fails with
 * STATUS_ACCESS_DENIED even though the name resolves. */
static NTSTATUS JbxBuildSectionSecurityDescriptor(
    _Out_ PSECURITY_DESCRIPTOR SecurityDescriptor,
    _Out_ PACL * Acl)
{
    UCHAR systemSid[JBX_MAX_SID_SIZE];
    UCHAR adminSid[JBX_MAX_SID_SIZE];
    UCHAR usersSid[JBX_MAX_SID_SIZE];
    SID_IDENTIFIER_AUTHORITY ntAuthority = SECURITY_NT_AUTHORITY;

    NTSTATUS status = RtlInitializeSid(systemSid, &ntAuthority, 1);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    *RtlSubAuthoritySid(systemSid, 0) = SECURITY_LOCAL_SYSTEM_RID;

    status = RtlInitializeSid(adminSid, &ntAuthority, 2);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    *RtlSubAuthoritySid(adminSid, 0) = SECURITY_BUILTIN_DOMAIN_RID;
    *RtlSubAuthoritySid(adminSid, 1) = DOMAIN_ALIAS_RID_ADMINS;

    status = RtlInitializeSid(usersSid, &ntAuthority, 1);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    *RtlSubAuthoritySid(usersSid, 0) = SECURITY_AUTHENTICATED_USER_RID;

    ULONG aclSize = (ULONG)(sizeof(ACL) + 3 * (sizeof(ACCESS_ALLOWED_ACE) - sizeof(ULONG)) +
        RtlLengthSid(systemSid) + RtlLengthSid(adminSid) + RtlLengthSid(usersSid));

    PACL acl = (PACL)ExAllocatePoolWithTag(NonPagedPoolNx, aclSize, JBX_POOL_TAG);
    if (acl == NULL)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = RtlCreateAcl(acl, aclSize, ACL_REVISION);
    if (NT_SUCCESS(status))
    {
        status = RtlAddAccessAllowedAce(acl, ACL_REVISION, SECTION_ALL_ACCESS, systemSid);
    }
    if (NT_SUCCESS(status))
    {
        status = RtlAddAccessAllowedAce(acl, ACL_REVISION, SECTION_ALL_ACCESS, adminSid);
    }
    if (NT_SUCCESS(status))
    {
        status = RtlAddAccessAllowedAce(
            acl,
            ACL_REVISION,
            SECTION_QUERY | SECTION_MAP_READ | SECTION_MAP_WRITE | READ_CONTROL,
            usersSid);
    }
    if (!NT_SUCCESS(status))
    {
        ExFreePoolWithTag(acl, JBX_POOL_TAG);
        return status;
    }

    status = RtlCreateSecurityDescriptor(SecurityDescriptor, SECURITY_DESCRIPTOR_REVISION);
    if (NT_SUCCESS(status))
    {
        status = RtlSetDaclSecurityDescriptor(SecurityDescriptor, TRUE, acl, FALSE);
    }
    if (!NT_SUCCESS(status))
    {
        ExFreePoolWithTag(acl, JBX_POOL_TAG);
        return status;
    }

    *Acl = acl;
    return STATUS_SUCCESS;
}

CJbxRing::CJbxRing()
    : m_hSection(NULL),
      m_pBase(NULL),
      m_ViewSize(0),
      m_PayloadBytes(0)
{
}

CJbxRing::~CJbxRing()
{
    Close();
}

void CJbxRing::Close()
{
    if (m_pBase != NULL)
    {
        ZwUnmapViewOfSection(NtCurrentProcess(), m_pBase);
        m_pBase = NULL;
    }
    if (m_hSection != NULL)
    {
        ZwClose(m_hSection);
        m_hSection = NULL;
    }
    m_ViewSize = 0;
}

NTSTATUS CJbxRing::Create()
{
    PAGED_CODE();

    if (IsOpen())
    {
        return STATUS_SUCCESS;
    }

    m_PayloadBytes = JBX_RING_BLOCK_FRAMES * JBX_RING_CHANNELS * sizeof(float) *
        JBX_RING_CAPACITY_BLOCKS;

    LARGE_INTEGER regionSize;
    regionSize.QuadPart = JBX_RING_HEADER_SIZE + m_PayloadBytes;

    SECURITY_DESCRIPTOR securityDescriptor;
    PACL acl = NULL;
    NTSTATUS status = JbxBuildSectionSecurityDescriptor(&securityDescriptor, &acl);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    UNICODE_STRING objectName;
    RtlInitUnicodeString(&objectName, JBX_SECTION_OBJECT_NAME);

    OBJECT_ATTRIBUTES attributes;
    InitializeObjectAttributes(
        &attributes,
        &objectName,
        OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE,
        NULL,
        &securityDescriptor);

    status = ZwCreateSection(
        &m_hSection,
        SECTION_ALL_ACCESS,
        &attributes,
        &regionSize,
        PAGE_READWRITE,
        SEC_COMMIT,
        NULL);

    ExFreePoolWithTag(acl, JBX_POOL_TAG);

    // A previous instance of the driver may have left the named object behind
    // while this one was reloaded; adopting it keeps the app's open working.
    if (status == STATUS_OBJECT_NAME_COLLISION)
    {
        status = ZwOpenSection(
            &m_hSection,
            SECTION_ALL_ACCESS | SECTION_MAP_READ | SECTION_MAP_WRITE,
            &attributes);
    }
    if (!NT_SUCCESS(status))
    {
        m_hSection = NULL;
        return status;
    }

    PVOID base = NULL;
    SIZE_T viewSize = 0;
    status = ZwMapViewOfSection(
        m_hSection,
        NtCurrentProcess(),
        &base,
        0,
        (SIZE_T)regionSize.QuadPart,
        NULL,
        &viewSize,
        ViewUnmap,
        0,
        PAGE_READWRITE);
    if (!NT_SUCCESS(status))
    {
        ZwClose(m_hSection);
        m_hSection = NULL;
        return status;
    }

    m_pBase = base;
    m_ViewSize = viewSize;

    InitializeHeader();
    return STATUS_SUCCESS;
}

void CJbxRing::InitializeHeader()
{
    PUCHAR base = (PUCHAR)m_pBase;

    *(ULONG *)(base + JBX_RING_OFF_MAGIC) = JBX_RING_MAGIC;
    *(LONG *)(base + JBX_RING_OFF_VERSION) = JBX_RING_VERSION;
    *(LONG *)(base + JBX_RING_OFF_HEADER_SIZE) = (LONG)JBX_RING_HEADER_SIZE;
    *(LONG *)(base + JBX_RING_OFF_SAMPLE_RATE) = (LONG)JBX_SAMPLE_RATE;
    *(LONG *)(base + JBX_RING_OFF_CHANNELS) = (LONG)JBX_RING_CHANNELS;
    *(LONG *)(base + JBX_RING_OFF_BLOCK_FRAMES) = (LONG)JBX_RING_BLOCK_FRAMES;
    *(LONG *)(base + JBX_RING_OFF_CAPACITY_BLOCKS) = (LONG)JBX_RING_CAPACITY_BLOCKS;
    *(LONG *)(base + JBX_RING_OFF_READER_SLOT_COUNT) = (LONG)JBX_RING_READER_SLOTS;
    *(LONG *)(base + JBX_RING_OFF_PAYLOAD_OFFSET) = (LONG)JBX_RING_PAYLOAD_OFFSET;

    InterlockedExchange64((volatile LONG64 *)(base + JBX_RING_OFF_WRITE_BLOCK_INDEX), 0);
    InterlockedExchange64((volatile LONG64 *)(base + JBX_RING_OFF_OVERRUN_COUNT), 0);

    // Reader slots and payload start free and silent, also when adopting an
    // object left behind by a previous load.
    RtlZeroMemory(
        base + JBX_RING_READER_SLOT_BASE,
        JBX_RING_READER_SLOTS * JBX_RING_READER_SLOT_SIZE);
    RtlZeroMemory(base + JBX_RING_PAYLOAD_OFFSET, m_PayloadBytes);
}

BOOLEAN CJbxRing::AnyReaderActive() const
{
    if (m_pBase == NULL)
    {
        return FALSE;
    }

    PUCHAR base = (PUCHAR)m_pBase;
    for (ULONG slot = 0; slot < JBX_RING_READER_SLOTS; slot++)
    {
        volatile LONG *active = (volatile LONG *)(base + JBX_RING_READER_SLOT_BASE +
            slot * JBX_RING_READER_SLOT_SIZE + JBX_RING_OFF_SLOT_ACTIVE);
        if (*active != 0)
        {
            return TRUE;
        }
    }
    return FALSE;
}

// Kept in one place so the reported name and the created name cannot drift.
extern "C" const WCHAR * JbxSectionName()
{
    return JBX_SECTION_NAME;
}
