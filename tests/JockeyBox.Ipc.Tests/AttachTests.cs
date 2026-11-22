using System.IO.MemoryMappedFiles;
using JockeyBox.Ipc;

namespace JockeyBox.Ipc.Tests;

/// <summary>
/// Ownership: the creator (the driver for Global\, an app for Local\) writes the header, and
/// everyone else attaches to it. Attach validates the header and never reinitialises it.
/// </summary>
public sealed class AttachTests
{
    private static string NewBus(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public void Attach_To_A_Ring_Created_By_Open_Is_Bit_Exact()
    {
        const int BlockFrames = 480;
        const int Channels = 2;
        const int Blocks = 64;
        string name = NewBus("attach-exact");

        using var writer = VirtualBusWriter.Open(name, 48000, Channels, BlockFrames, capacityBlocks: 8);
        using var reader = VirtualBusReader.Attach(name);

        var tx = new float[BlockFrames * Channels];
        var rx = new float[BlockFrames * Channels];

        for (int block = 1; block <= Blocks; block++)
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
    }

    [Fact]
    public void Attach_To_A_Missing_Ring_Throws_The_Reason_Not_A_Null_Dereference()
    {
        string name = NewBus("attach-missing");

        var writerError = Assert.Throws<VirtualBusException>(() => VirtualBusWriter.Attach(name));
        var readerError = Assert.Throws<VirtualBusException>(() => VirtualBusReader.Attach(name));

        Assert.Contains(name, writerError.Message);
        Assert.Contains(name, readerError.Message);
    }

    [Fact]
    public void Attach_To_A_Wrong_Version_Ring_Throws()
    {
        string name = NewBus("attach-version");
        string path = BusLayout.PathFor(name);
        long size = BusLayout.RegionSize(48000, 2, 64, 4);

        try
        {
            // Create the section the way the driver will: header first, then publish.
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            using (var map = MemoryMappedFile.CreateFromFile(
                       file, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true))
            using (var view = map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite))
            {
                view.Write(BusLayout.MagicOffset, unchecked((int)BusLayout.Magic));
                view.Write(BusLayout.VersionOffset, BusLayout.Version + 1);
                view.Write(BusLayout.HeaderSizeOffset, BusLayout.HeaderSize);
                view.Write(BusLayout.SampleRateOffset, 48000);
                view.Write(BusLayout.ChannelsOffset, 2);
                view.Write(BusLayout.BlockFramesOffset, 64);
                view.Write(BusLayout.CapacityBlocksOffset, 4);
                view.Write(BusLayout.ReaderSlotCountFieldOffset, BusLayout.ReaderSlotCount);
                view.Write(BusLayout.PayloadOffsetFieldOffset, BusLayout.PayloadOffset);
            }

            var writerError = Assert.Throws<VirtualBusException>(() => VirtualBusWriter.Attach(name));
            var readerError = Assert.Throws<VirtualBusException>(() => VirtualBusReader.Attach(name));

            Assert.Contains("version", writerError.Message);
            Assert.Contains("version", readerError.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Attach_Does_Not_Reset_A_Live_Writers_Header()
    {
        const int BlockFrames = 64;
        const int Channels = 2;
        string name = NewBus("attach-noreset");

        using var creator = VirtualBusWriter.Open(name, 48000, Channels, BlockFrames, capacityBlocks: 8);

        var block = new float[BlockFrames * Channels];
        for (int i = 1; i <= 5; i++)
        {
            TestSignal.Fill(block, i);
            creator.Write(block);
        }

        Assert.Equal(5, creator.BlocksWritten);

        using var attachedWriter = VirtualBusWriter.Attach(name);
        Assert.Equal(5, attachedWriter.BlocksWritten);
        Assert.Equal(5, creator.BlocksWritten);

        using var reader = VirtualBusReader.Attach(name);

        // The attachment picked up the cursor and can carry on from it.
        TestSignal.Fill(block, 6);
        attachedWriter.Write(block);
        Assert.Equal(6, attachedWriter.BlocksWritten);

        var rx = new float[BlockFrames * Channels];
        Assert.Equal(VirtualBusStatus.Data, reader.Read(rx));
        Assert.True(TestSignal.BitEqual(block, rx), TestSignal.FirstDifference(block, rx));
    }

    [Fact]
    public void MapNameFor_Is_Pinned_For_Both_Scopes()
    {
        Assert.Equal(@"Local\JockeyBox.Bus.main", BusLayout.MapNameFor("main", BusScope.Local));
        Assert.Equal(@"Global\JockeyBox.Bus.main", BusLayout.MapNameFor("main", BusScope.Global));
    }

    [Fact]
    public void A_Non_Elevated_App_Never_Creates_A_Global_Section()
    {
        string name = NewBus("global-create");

        if (!OperatingSystem.IsWindows())
        {
            // Linux has no object namespace: both scopes land in the same region file.
            using var writer = VirtualBusWriter.Open(name, 48000, 2, 64, 4, BusScope.Global);
            Assert.Equal(0, writer.BlocksWritten);
            return;
        }

        var error = Assert.Throws<VirtualBusException>(
            () => VirtualBusWriter.Open(name, 48000, 2, 64, 4, BusScope.Global));

        Assert.Contains("SeCreateGlobalPrivilege", error.Message);
        Assert.Contains("driver", error.Message);
    }
}
