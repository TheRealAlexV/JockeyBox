namespace JockeyBox.Ipc;

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
///  28   36  reserved
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

    /// <summary>Name the mapping is published under, as the brief specifies.</summary>
    public static string MapNameFor(string name) => @"Local\JockeyBox.Bus." + name;

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
}
