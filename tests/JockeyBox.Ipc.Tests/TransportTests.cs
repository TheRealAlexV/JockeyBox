using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using JockeyBox.Ipc;

namespace JockeyBox.Ipc.Tests;

public sealed class TransportTests
{
    private static string NewBus(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public void Stream_Of_2812_Blocks_Is_Bit_Exact_With_No_Underruns_Or_Corrections()
    {
        // 2812 blocks x 480 frames = 1,349,760 frames = 28.12 s at 48 kHz (the brief's
        // "60 seconds" block count; the count is what is pinned, not the wall clock).
        const int BlockFrames = 480;
        const int Channels = 2;
        const long Blocks = 2812;
        string name = NewBus("bit-exact");

        using var writer = VirtualBusWriter.Open(name, 48000, Channels, BlockFrames, capacityBlocks: 8);
        using var reader = VirtualBusReader.Open(name);

        var tx = new float[BlockFrames * Channels];
        var rx = new float[BlockFrames * Channels];

        for (long block = 1; block <= Blocks; block++)
        {
            TestSignal.Fill(tx, block);
            writer.Write(tx);
            Assert.Equal(VirtualBusStatus.Data, reader.Read(rx));
            Assert.True(TestSignal.BitEqual(tx, rx), $"block {block}: {TestSignal.FirstDifference(tx, rx)}");
        }

        Assert.Equal(Blocks, writer.BlocksWritten);
        Assert.Equal(Blocks, reader.BlocksRead);
        Assert.Equal(0, reader.Underruns);
        Assert.Equal(0, reader.Corrections);
        Assert.Equal(0, writer.Overruns);
        Assert.Equal(0, reader.LagBlocks);
    }

    [Fact]
    public void Overflow_Drops_Oldest_Counts_Corrections_And_Does_Not_Block_The_Writer()
    {
        const int BlockFrames = 64;
        const int Capacity = 4;
        string name = NewBus("overflow");

        using var writer = VirtualBusWriter.Open(name, 48000, 1, BlockFrames, Capacity);
        using var reader = VirtualBusReader.Open(name);

        var block = new float[BlockFrames];
        var rx = new float[BlockFrames];
        var expected = new float[BlockFrames];

        var stopwatch = Stopwatch.StartNew();
        for (long index = 1; index <= 10; index++)
        {
            TestSignal.Fill(block, index);
            writer.Write(block);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"10 writes of 64 frames took {stopwatch.ElapsedMilliseconds} ms; the writer must never wait for a reader");

        Assert.Equal(10, writer.BlocksWritten);
        Assert.True(writer.Overruns > 0, "overruns must be counted");
        Assert.True(reader.Corrections > 0, "the lapped reader's corrections must be counted");
        Assert.Equal(9, reader.BlocksRead); // parked just before the block in flight

        // The newest block is still delivered, bit-exactly: nothing stale is replayed.
        Assert.Equal(VirtualBusStatus.Data, reader.Read(rx));
        TestSignal.Fill(expected, 10);
        Assert.True(TestSignal.BitEqual(expected, rx), TestSignal.FirstDifference(expected, rx));
        Assert.Equal(10, reader.BlocksRead);
        Assert.Equal(0, reader.Underruns);
    }

    [Fact]
    public void Abandoned_Reader_Does_Not_Stall_The_Writer_Or_Corrupt_Live_Readers()
    {
        const int BlockFrames = 128;
        const int Channels = 2;
        string name = NewBus("abandoned");

        using var writer = VirtualBusWriter.Open(name, 48000, Channels, BlockFrames, capacityBlocks: 8);
        using var abandoned = VirtualBusReader.Open(name); // opened, never read
        using var live = VirtualBusReader.Open(name);

        var tx = new float[BlockFrames * Channels];
        var rx = new float[BlockFrames * Channels];
        var stopwatch = Stopwatch.StartNew();

        for (long block = 1; block <= 50; block++)
        {
            TestSignal.Fill(tx, block);
            writer.Write(tx);
            Assert.Equal(VirtualBusStatus.Data, live.Read(rx));
            Assert.True(TestSignal.BitEqual(tx, rx), $"live reader block {block}: {TestSignal.FirstDifference(tx, rx)}");
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, "a reader that never reads must not slow the producer");

        Assert.Equal(50, live.BlocksRead);
        Assert.Equal(0, live.Corrections);
        Assert.Equal(0, live.Underruns);
        Assert.True(abandoned.Corrections > 0, "the abandoned reader must be corrected, not waited for");
        Assert.True(writer.Overruns > 0);
    }

    [Fact]
    public void Reader_Joining_Mid_Stream_Starts_At_Newest_Block_And_Does_Not_Replay_History()
    {
        const int BlockFrames = 32;
        string name = NewBus("midstream");

        using var writer = VirtualBusWriter.Open(name, 48000, 1, BlockFrames, capacityBlocks: 8);
        var block = new float[BlockFrames];
        for (long index = 1; index <= 20; index++)
        {
            TestSignal.Fill(block, index);
            writer.Write(block);
        }

        using var reader = VirtualBusReader.Open(name);

        Assert.Equal(20, reader.BlocksRead);
        Assert.Equal(0, reader.LagBlocks);

        var rx = new float[BlockFrames];
        Assert.Equal(VirtualBusStatus.Silence, reader.Read(rx));
        Assert.Equal(20, reader.BlocksRead); // already at the newest block: nothing consumed
        Assert.Equal(1, reader.Underruns);
        Assert.True(rx.AsSpan().IndexOfAnyExcept(0f) < 0, "silence must be zero-filled");

        TestSignal.Fill(block, 21);
        writer.Write(block);
        Assert.Equal(VirtualBusStatus.Data, reader.Read(rx));

        var expected = new float[BlockFrames];
        TestSignal.Fill(expected, 21);
        Assert.True(TestSignal.BitEqual(expected, rx), "first block after joining must be the newest one (21), not block 1");
        Assert.Equal(0, reader.Corrections);
    }

    [Fact]
    public void Excess_Lag_Config_Forces_A_Correction_Instead_Of_Replaying_Stale_Blocks()
    {
        const int BlockFrames = 32;
        string name = NewBus("maxlag");

        using var writer = VirtualBusWriter.Open(name, 48000, 1, BlockFrames, capacityBlocks: 32);
        using var reader = VirtualBusReader.Open(name, maxLagBlocks: 2);

        var block = new float[BlockFrames];
        for (long index = 1; index <= 5; index++)
        {
            TestSignal.Fill(block, index);
            writer.Write(block);
        }

        var rx = new float[BlockFrames];
        Assert.Equal(VirtualBusStatus.Correction, reader.Read(rx));
        Assert.Equal(1, reader.Corrections);
        Assert.True(rx.AsSpan().IndexOfAnyExcept(0f) < 0, "a correction must not hand out stale audio");

        TestSignal.Fill(block, 6);
        writer.Write(block);
        Assert.Equal(VirtualBusStatus.Data, reader.Read(rx));
        var expected = new float[BlockFrames];
        TestSignal.Fill(expected, 6);
        Assert.True(TestSignal.BitEqual(expected, rx));
    }

    [Fact]
    public void Wrong_Magic_Is_Rejected_With_Expected_And_Found_Values()
    {
        string name = NewBus("magic");
        RawRegion.Write(name, magic: 0xDEADBEEF, version: BusLayout.Version);
        try
        {
            var ex = Assert.Throws<VirtualBusException>(() => VirtualBusReader.Open(name));
            Assert.Contains("0xDEADBEEF", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"0x{BusLayout.Magic:X8}", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RawRegion.Delete(name);
        }
    }

    [Fact]
    public void Wrong_Version_Is_Rejected_With_Expected_And_Found_Values()
    {
        string name = NewBus("version");
        RawRegion.Write(name, magic: BusLayout.Magic, version: 99);
        try
        {
            var readerError = Assert.Throws<VirtualBusException>(() => VirtualBusReader.Open(name));
            Assert.Contains("99", readerError.Message);
            Assert.Contains($"version {BusLayout.Version}", readerError.Message);

            var writerError = Assert.Throws<VirtualBusException>(
                () => VirtualBusWriter.Open(name, 48000, 2, 480, 4));
            Assert.Contains("99", writerError.Message);
            Assert.Contains($"version {BusLayout.Version}", writerError.Message);
        }
        finally
        {
            RawRegion.Delete(name);
        }
    }

    [Fact]
    public void Second_Writer_On_The_Same_Name_Is_Refused()
    {
        string name = NewBus("one-writer");
        using var writer = VirtualBusWriter.Open(name, 48000, 2, 480, 4);

        var ex = Assert.Throws<VirtualBusException>(() => VirtualBusWriter.Open(name, 48000, 2, 480, 4));
        Assert.Contains("already open", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Header_Fields_Sit_At_The_Documented_Offsets()
    {
        const int SampleRate = 48000;
        const int Channels = 2;
        const int BlockFrames = 480;
        const int Capacity = 4;
        string name = NewBus("layout");
        string path = BusLayout.PathFor(name);

        using var writer = VirtualBusWriter.Open(name, SampleRate, Channels, BlockFrames, Capacity);

        using var map = RawRegion.OpenRead(name);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        Assert.Equal(BusLayout.HeaderSize, BusLayout.PayloadOffset);
        long regionSize = BusLayout.RegionSize(SampleRate, Channels, BlockFrames, Capacity);
        Assert.Equal(regionSize, OperatingSystem.IsWindows() ? view.Capacity : new FileInfo(path).Length);
        Assert.Equal(unchecked((uint)BusLayout.Magic), view.ReadUInt32(BusLayout.MagicOffset));
        Assert.Equal(BusLayout.Version, view.ReadInt32(BusLayout.VersionOffset));
        Assert.Equal(BusLayout.HeaderSize, view.ReadInt32(BusLayout.HeaderSizeOffset));
        Assert.Equal(SampleRate, view.ReadInt32(BusLayout.SampleRateOffset));
        Assert.Equal(Channels, view.ReadInt32(BusLayout.ChannelsOffset));
        Assert.Equal(BlockFrames, view.ReadInt32(BusLayout.BlockFramesOffset));
        Assert.Equal(Capacity, view.ReadInt32(BusLayout.CapacityBlocksOffset));
        Assert.Equal(BusLayout.ReaderSlotCount, view.ReadInt32(BusLayout.ReaderSlotCountFieldOffset));
        Assert.Equal(BusLayout.PayloadOffset, view.ReadInt32(BusLayout.PayloadOffsetFieldOffset));
        Assert.Equal(0, view.ReadInt64(BusLayout.WriteBlockIndexOffset));
        Assert.Equal(0, view.ReadInt64(BusLayout.OverrunCountOffset));

        for (int slot = 0; slot < BusLayout.ReaderSlotCount; slot++)
        {
            long offset = BusLayout.SlotOffset(slot);
            Assert.Equal(0L, view.ReadInt64(offset + BusLayout.SlotReadBlockIndex));
            Assert.Equal(0L, view.ReadInt64(offset + BusLayout.SlotCorrections));
            Assert.Equal(0, view.ReadInt32(offset + BusLayout.SlotActive));
        }

        // One published block lands in ring slot 0 at the payload offset.
        var block = new float[BlockFrames * Channels];
        TestSignal.Fill(block, 1);
        writer.Write(block);

        Assert.Equal(1, view.ReadInt64(BusLayout.WriteBlockIndexOffset));
        Assert.Equal(block[0], view.ReadSingle(BusLayout.PayloadOffset));
        Assert.Equal(block[^1], view.ReadSingle(BusLayout.PayloadOffset + ((block.Length - 1) * sizeof(float))));
    }

    [Fact]
    public void Hot_Path_Does_Not_Allocate_After_Construction()
    {
        const int BlockFrames = 64;
        string name = NewBus("noalloc");

        using var writer = VirtualBusWriter.Open(name, 48000, 1, BlockFrames, capacityBlocks: 8);
        using var reader = VirtualBusReader.Open(name);

        var tx = new float[BlockFrames];
        var rx = new float[BlockFrames];
        TestSignal.Fill(tx, 1);

        // Warm up so JIT work is outside the measurement.
        for (int i = 0; i < 32; i++)
        {
            writer.Write(tx);
            reader.Read(rx);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            writer.Write(tx);
            reader.Read(rx);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void Slot_Overload_Pins_The_Reader_To_A_Requested_Slot_And_Refuses_A_Claimed_One()
    {
        string name = NewBus("slots");
        using var writer = VirtualBusWriter.Open(name, 48000, 1, 32, 4);
        using var reader = VirtualBusReader.Open(name, slot: 3, maxLagBlocks: 0);

        Assert.Equal(3, reader.Slot);
        Assert.True(writer.IsReaderActive(3));
        Assert.False(writer.IsReaderActive(0));

        Assert.Throws<VirtualBusException>(() => VirtualBusReader.Open(name, slot: 3, maxLagBlocks: 0));
    }
}

internal static class RawRegion
{
    // Windows backs a Local\ bus with a named, pagefile-backed section, and that section lives
    // only while a handle is open. Seeding it therefore has to hold the map until Delete(),
    // or the section vanishes before the code under test can open it by name. On Linux the
    // backing file persists on disk and needs no such lifeline.
    private static readonly Dictionary<string, MemoryMappedFile> WindowsSections = new(StringComparer.Ordinal);

    public static void Write(string name, uint magic, int version, int sampleRate = 48000, int channels = 2, int blockFrames = 480, int capacityBlocks = 4)
    {
        long size = BusLayout.RegionSize(sampleRate, channels, blockFrames, capacityBlocks);

        MemoryMappedFile map;
        FileStream? backing = null;
        if (OperatingSystem.IsWindows())
        {
            lock (WindowsSections)
            {
                if (!WindowsSections.TryGetValue(name, out map!))
                {
                    map = MemoryMappedFile.CreateNew(BusLayout.MapNameFor(name, BusScope.Local), size);
                    WindowsSections[name] = map;
                }
            }
        }
        else
        {
            string path = BusLayout.PathFor(name);
            backing = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            backing.SetLength(size);
            map = MemoryMappedFile.CreateFromFile(backing, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        }

        try
        {
            using var view = map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

            view.Write(BusLayout.MagicOffset, magic);
            view.Write(BusLayout.VersionOffset, version);
            view.Write(BusLayout.HeaderSizeOffset, BusLayout.HeaderSize);
            view.Write(BusLayout.SampleRateOffset, sampleRate);
            view.Write(BusLayout.ChannelsOffset, channels);
            view.Write(BusLayout.BlockFramesOffset, blockFrames);
            view.Write(BusLayout.CapacityBlocksOffset, capacityBlocks);
            view.Write(BusLayout.ReaderSlotCountFieldOffset, BusLayout.ReaderSlotCount);
            view.Write(BusLayout.PayloadOffsetFieldOffset, BusLayout.PayloadOffset);
        }
        finally
        {
            backing?.Dispose();
        }
    }

    /// <summary>Maps the region a writer or seeder created, for read-only header inspection.</summary>
    public static MemoryMappedFile OpenRead(string name) =>
        OperatingSystem.IsWindows()
            ? MemoryMappedFile.OpenExisting(BusLayout.MapNameFor(name, BusScope.Local), MemoryMappedFileRights.Read)
            : MemoryMappedFile.CreateFromFile(BusLayout.PathFor(name), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

    public static void Delete(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            lock (WindowsSections)
            {
                if (WindowsSections.Remove(name, out var map))
                    map.Dispose();
            }
        }

        foreach (string path in new[] { BusLayout.PathFor(name), BusLayout.LockPathFor(name) })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
