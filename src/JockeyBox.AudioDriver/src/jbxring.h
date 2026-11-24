/* Driver-side owner of the Global\JockeyBox.Bus.Pair1 shared section.
 *
 * The driver creates the section because a Global\ object needs
 * SeCreateGlobalPrivilege in user mode; the app only opens it by name. The
 * header and payload geometry mirror JockeyBox.Ipc/../BusLayout.cs exactly:
 * 1024 byte header, float32 interleaved payload, one write cursor and eight
 * reader slots. Nothing here invents a second header format.
 */
#pragma once

#include "jbx.h"

// BusLayout: magic 'J','B','U','S' via InterlockedExchange64.
static const ULONG JBX_RING_MAGIC = 0x5355424A;
static const LONG  JBX_RING_VERSION = 1;
static const ULONG JBX_RING_HEADER_SIZE = 1024;
static const ULONG JBX_RING_PAYLOAD_OFFSET = 1024;
static const ULONG JBX_RING_READER_SLOTS = 8;
static const ULONG JBX_RING_READER_SLOT_SIZE = 64;
static const ULONG JBX_RING_READER_SLOT_BASE = 64;

// Header field offsets, identical to BusLayout.
static const ULONG JBX_RING_OFF_MAGIC = 0;
static const ULONG JBX_RING_OFF_VERSION = 4;
static const ULONG JBX_RING_OFF_HEADER_SIZE = 8;
static const ULONG JBX_RING_OFF_SAMPLE_RATE = 12;
static const ULONG JBX_RING_OFF_CHANNELS = 16;
static const ULONG JBX_RING_OFF_BLOCK_FRAMES = 20;
static const ULONG JBX_RING_OFF_CAPACITY_BLOCKS = 24;
static const ULONG JBX_RING_OFF_WRITE_BLOCK_INDEX = 32;
static const ULONG JBX_RING_OFF_OVERRUN_COUNT = 40;
static const ULONG JBX_RING_OFF_READER_SLOT_COUNT = 48;
static const ULONG JBX_RING_OFF_PAYLOAD_OFFSET = 52;
// Reader slot fields, identical to BusLayout's Slot* constants. The driver only
// reads SlotActive; the rest are declared so the contract is visible in one
// place and the next field cannot silently land on 32 (rateTrims).
static const ULONG JBX_RING_SLOT_READ_BLOCK_INDEX = 0;
static const ULONG JBX_RING_SLOT_CORRECTIONS = 8;
static const ULONG JBX_RING_SLOT_UNDERRUNS = 16;
static const ULONG JBX_RING_SLOT_ACTIVE = 24;
static const ULONG JBX_RING_SLOT_RATE_TRIMS = 32;

// Geometry of the pair: 10 ms blocks, 64 blocks of slack.
//
// A pair is stereo by construction (a cable pair is two conductors), so channels
// stays 2. Capacity is the app's own default (VirtualBusSink.DefaultCapacityBlocks
// = 64); the app refuses anything shorter than CapacityForCushion, which for 480
// frame blocks is max(16, ceil(6 * 4096 / 480)) = 52 blocks. A ring of 64 blocks
// is 30,720 frames; the eight-block ring this replaced was 3,840 frames.
static const ULONG JBX_RING_BLOCK_FRAMES = 480;
static const ULONG JBX_RING_CAPACITY_BLOCKS = 64;
static const ULONG JBX_RING_CHANNELS = 2;

class CJbxRing
{
public:
    CJbxRing();
    ~CJbxRing();

    // Creates (or re-opens, after a driver restart) the Global\ section with an
    // explicit DACL, maps it and rewrites the header. PASSIVE_LEVEL.
    NTSTATUS Create();

    bool IsOpen() const { return m_pBase != NULL; }

    // True once any reader has claimed a slot. This is the app's attach signal:
    // the section itself has no IOCTL, the reader writes its own slot.
    BOOLEAN AnyReaderActive() const;

    ULONG PayloadBytes() const { return m_PayloadBytes; }

private:
    void Close();
    void InitializeHeader();

    HANDLE m_hSection;
    PVOID  m_pBase;
    SIZE_T m_ViewSize;
    ULONG  m_PayloadBytes;
};
