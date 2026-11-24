using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace JockeyBox.Ipc;

/// <summary>
/// Who names the section, and therefore who may create it.
/// <see cref="Local"/> lives in the calling session, so the app can create it and a reader in
/// the same session can open it. <see cref="Global"/> is visible to a session-0 driver, but only
/// a process holding <c>SeCreateGlobalPrivilege</c> may create one — so for Global the driver
/// creates and the app attaches.
/// </summary>
public enum BusScope
{
    Local,
    Global,
}

/// <summary>
/// Byte layout of the shared region, at offset 0 of the mapped area.
/// This class is the single source of truth: the WaveRT driver and the ASIO COM DLL
/// read the same offsets, so every field is a named constant.
///
/// Header (fixed, 1024 bytes):
///   0    4  magic            u32   0x5355424A, bytes 'J' 'B' 'U' 'S'
///   4    4  version          i32   1
///   8    4  headerSize       i32   1024 (bytes)
///  12    4  sampleRate       i32
///  16    4  channels         i32
///  20    4  blockFrames      i32
///  24    4  capacityBlocks   i32
///  28    4  reserved         i32   0
///  32    8  writeBlockIndex  i64   blocks published, monotonic (release-written)
///  40    8  overrunCount     i64   total times any reader was lapped
///  48    4  readerSlotCount  i32   8
///  52    4  payloadOffset    i32   1024
///  56   40  reserved
///  96    0  Reader slot 0 at 64, each 64 bytes, 8 slots (64..575), then reserved to 1024.
///
/// Reader slot (64 bytes, at <see cref="ReaderSlotBase"/> + slot * <see cref="ReaderSlotSize"/>):
///   0    8  readBlockIndex   i64  blocks consumed, monotonic
///   8    8  corrections      i64  jumps forced by overrun or excessive lag
///  16    8  underruns        i64  reads that found no new block
///  24    4  active           i32  0 = free, 1 = claimed by a reader
///  28    4  reserved
///  32    8  rateTrims        i64  written by the native reader: one-frame rate trims, not corrections
///  40   24  reserved
///
/// <para><b>rateTrims.</b> The native ASIO reader owns and writes this counter; the
/// app never writes it. It counts one-frame rate trims, which are not corrections
/// (those are the overrun/excess-lag jumps at <see cref="SlotCorrections"/>). It is
/// declared here so a future C# field cannot claim offset 32 and clobber it. The rest
/// of the slot tail (40..63) is still free; the header does not change.</para>
///
/// Payload follows the header at <see cref="PayloadOffset"/>:
///   capacityBlocks * blockFrames * channels float32 values, interleaved by frame.
/// </summary>
public static class BusLayout
{
    /// <summary>ASCII 'J','B','U','S' read little-endian.</summary>
    public const uint Magic = 0x5355424A;

    public const int Version = 1;

    public const int HeaderSize = 1024;
    public const int PayloadOffset = 1024;

    public const int ReaderSlotCount = 8;
    public const int ReaderSlotSize = 64;
    public const int ReaderSlotBase = 64;

    /// <summary>
    /// The widest device the transport and the ASIO driver both accept. The device
    /// geometry itself is derived from its Line list (see VirtualDeviceConfig), but no
    /// path has storage for more than this.
    /// </summary>
    public const int MaxChannels = 32;

    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int HeaderSizeOffset = 8;
    public const int SampleRateOffset = 12;
    public const int ChannelsOffset = 16;
    public const int BlockFramesOffset = 20;
    public const int CapacityBlocksOffset = 24;
    public const int WriteBlockIndexOffset = 32;
    public const int OverrunCountOffset = 40;
    public const int ReaderSlotCountFieldOffset = 48;
    public const int PayloadOffsetFieldOffset = 52;

    public const int SlotReadBlockIndex = 0;
    public const int SlotCorrections = 8;
    public const int SlotUnderruns = 16;
    public const int SlotActive = 24;

    /// <summary>
    /// Per-reader rate-trim counter, written by the native ASIO reader. Counts
    /// one-frame rate trims, not corrections (see <see cref="SlotCorrections"/>).
    /// Declared so no future C# field reuses the bytes; the app does not write it.
    /// </summary>
    public const int SlotRateTrims = 32;

    /// <summary>Start of reader slot <paramref name="slot"/> within the region.</summary>
    public static long SlotOffset(int slot)
    {
        if ((uint)slot >= ReaderSlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Reader slots are 0..{ReaderSlotCount - 1}.");
        return ReaderSlotBase + ((long)slot * ReaderSlotSize);
    }

    /// <summary>Total mapped bytes for the given geometry.</summary>
    public static long RegionSize(int sampleRate, int channels, int blockFrames, int capacityBlocks)
    {
        _ = sampleRate;
        return PayloadOffset + ((long)capacityBlocks * blockFrames * channels * sizeof(float));
    }

    /// <summary>Name of the section for <paramref name="name"/> in the given scope.</summary>
    public static string MapNameFor(string name, BusScope scope) =>
        scope == BusScope.Global
            ? @"Global\JockeyBox.Bus." + name
            : @"Local\JockeyBox.Bus." + name;

    /// <summary>Directory holding the backing regions (one file per bus name).</summary>
    public static string DirectoryFor() => Path.Combine(Path.GetTempPath(), "JockeyBox.Bus");

    /// <summary>Path of the backed region for <paramref name="name"/>.</summary>
    public static string PathFor(string name)
    {
        ValidateName(name);
        string dir = DirectoryFor();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".bus");
    }

    /// <summary>Path of the single-writer claim file for <paramref name="name"/>.</summary>
    public static string LockPathFor(string name)
    {
        ValidateName(name);
        string dir = DirectoryFor();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".lock");
    }

    /// <summary>Rejects names that could escape the backing directory.</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Bus name must not be empty.", nameof(name));
        foreach (char c in name)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';
            if (!ok)
                throw new ArgumentException($"Bus name '{name}' may only contain letters, digits, '_', '-' and '.'.", nameof(name));
        }
    }

    /// <summary>
    /// Opens an existing region by name. Nothing is created here: on Windows the section is
    /// opened by name (the driver owns Global, the app owns Local); on Linux the backing file
    /// is opened.
    /// </summary>
    internal static MemoryMappedFile OpenExisting(string name, BusScope scope)
    {
        ValidateName(name);
        string mapName = MapNameFor(name, scope);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
            }
            catch (FileNotFoundException ex)
            {
                throw new VirtualBusException(
                    $"No section named '{mapName}' exists. A Global\\ bus is created by the driver before the app attaches; " +
                    "a Local\\ bus is created by the app before a reader attaches.", ex);
            }
        }

        string path = PathFor(name);
        if (!File.Exists(path))
            throw new VirtualBusException($"No bus named '{name}' exists; expected region file {path}.");

        return MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>
    /// Validates the header of an already-created region and returns its geometry.
    /// Never writes a byte: the creator owns the header.
    /// </summary>
    internal static unsafe (int SampleRate, int Channels, int BlockFrames, int CapacityBlocks) ReadHeader(
        MemoryMappedViewAccessor view, string name)
    {
        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            return ReadHeader(ptr + view.PointerOffset, view.Capacity, name);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe (int SampleRate, int Channels, int BlockFrames, int CapacityBlocks) ReadHeader(
        byte* region, long regionBytes, string name)
    {
        if (regionBytes < HeaderSize)
        {
            throw new VirtualBusException(
                $"Bus '{name}' region is {regionBytes} bytes; expected at least the {HeaderSize}-byte header.");
        }

        int magic = Unsafe.ReadUnaligned<int>(region + MagicOffset);
        if (magic != unchecked((int)Magic))
        {
            throw new VirtualBusException(
                $"Bus '{name}' has magic 0x{unchecked((uint)magic):X8}; expected 0x{Magic:X8} (bytes 'J','B','U','S'). " +
                "The region is not a JockeyBox transport ring.");
        }

        int version = Unsafe.ReadUnaligned<int>(region + VersionOffset);
        if (version != Version)
        {
            throw new VirtualBusException(
                $"Bus '{name}' speaks protocol version {version}; expected version {Version}.");
        }

        int headerSize = Unsafe.ReadUnaligned<int>(region + HeaderSizeOffset);
        int payloadOffset = Unsafe.ReadUnaligned<int>(region + PayloadOffsetFieldOffset);
        int slotCount = Unsafe.ReadUnaligned<int>(region + ReaderSlotCountFieldOffset);
        if (headerSize != HeaderSize || payloadOffset != PayloadOffset || slotCount != ReaderSlotCount)
        {
            throw new VirtualBusException(
                $"Bus '{name}' has headerSize={headerSize}, payloadOffset={payloadOffset}, readerSlotCount={slotCount}; " +
                $"expected {HeaderSize}, {PayloadOffset}, {ReaderSlotCount}.");
        }

        int sampleRate = Unsafe.ReadUnaligned<int>(region + SampleRateOffset);
        int channels = Unsafe.ReadUnaligned<int>(region + ChannelsOffset);
        int blockFrames = Unsafe.ReadUnaligned<int>(region + BlockFramesOffset);
        int capacityBlocks = Unsafe.ReadUnaligned<int>(region + CapacityBlocksOffset);
        if (sampleRate <= 0 || channels <= 0 || channels > MaxChannels || blockFrames <= 0 ||
            capacityBlocks <= 0)
        {
            throw new VirtualBusException(
                $"Bus '{name}' has invalid geometry: sampleRate={sampleRate}, channels={channels}, " +
                $"blockFrames={blockFrames}, capacityBlocks={capacityBlocks}. Channels are 1..{MaxChannels}.");
        }

        long needed = RegionSize(sampleRate, channels, blockFrames, capacityBlocks);
        if (regionBytes < needed)
        {
            throw new VirtualBusException(
                $"Bus '{name}' region is {regionBytes} bytes; its header describes {needed} bytes " +
                $"({capacityBlocks} blocks x {blockFrames} frames x {channels} channels from offset {PayloadOffset}).");
        }

        return (sampleRate, channels, blockFrames, capacityBlocks);
    }
}
