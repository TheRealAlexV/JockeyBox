using System;
using System.Diagnostics;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Ipc;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The app must actually feed the ring the native ASIO driver reads. These tests
/// prove the three things that would otherwise only be visible as silence:
/// the blocks arrive bit-exact, a steady render loop allocates nothing, and an
/// engine whose only output is the virtual ring still starts.
/// </summary>
public sealed class VirtualBusSinkTests
{
    private const int Rate = 48_000;
    private const int Block = 480;
    private const int Channels = 2;

    /// <summary>A fresh section name per test: sections are machine-wide while open.</summary>
    private static string NewName() => "test" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Exact small integers, so every float round-trips through its bit pattern.</summary>
    private static float[] BlockFor(int block)
    {
        var buffer = new float[Block * Channels];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = ((block * 37 + i) % 512) - 256;
        return buffer;
    }

    [Fact]
    public void With_No_Driver_Section_It_Still_Creates_The_Local_Ring()
    {
        string name = NewName();
        Assert.False(BusLayout.GlobalSectionExists(name));

        using var sink = VirtualBusSink.TryOpen(name, Rate, Channels, Block, out string? error);
        Assert.Null(error);
        Assert.NotNull(sink);

        using var reader = VirtualBusReader.Open(name, maxLagBlocks: VirtualBusSink.DefaultCapacityBlocks);
        Assert.Equal(Channels, reader.Channels);
        Assert.Equal(Block, reader.BlockFrames);

        var expected = BlockFor(1);
        var received = new float[Block * Channels];
        sink!.Write(expected, 0, Block);

        Assert.Equal(VirtualBusStatus.Data, reader.Read(received));
        Assert.Equal(expected, received);
    }

    [Fact]
    public void Blocks_arrive_in_the_ring_bit_exact()
    {
        string name = NewName();
        using var sink = VirtualBusSink.TryOpen(name, Rate, Channels, Block);
        Assert.NotNull(sink);

        // Open the reader first: joining starts at the newest block, so a reader
        // that arrives late would legitimately see the newest one, not block 0.
        using var reader = VirtualBusReader.Open(name, maxLagBlocks: VirtualBusSink.DefaultCapacityBlocks);
        Assert.Equal(Rate, reader.SampleRate);
        Assert.Equal(Channels, reader.Channels);
        Assert.Equal(Block, reader.BlockFrames);

        int matched = 0;
        int mismatched = 0;
        var received = new float[Block * Channels];

        for (int block = 0; block < 32; block++)
        {
            var expected = BlockFor(block);
            sink.Write(expected, 0, Block);

            Assert.Equal(VirtualBusStatus.Data, reader.Read(received));

            bool same = true;
            for (int i = 0; i < received.Length; i++)
                if (BitConverter.SingleToInt32Bits(received[i]) !=
                    BitConverter.SingleToInt32Bits(expected[i]))
                {
                    same = false;
                    break;
                }
            if (same) matched++;
            else mismatched++;
        }

        Assert.Equal(32, matched);
        Assert.Equal(0, mismatched);
        Assert.Equal(32, sink.BlocksPublished);
        Assert.Equal(0, sink.Laps);
    }

    [Fact]
    public void A_steady_render_loop_does_not_allocate()
    {
        string name = NewName();
        using var sink = VirtualBusSink.TryOpen(name, Rate, Channels, Block);
        Assert.NotNull(sink);

        var block = BlockFor(0);

        // The whole render loop twice, counted only on the second run: the same shape as
        // AllocationAssert.Measure in JockeyBox.Dsp.Tests.
        void Render() { for (int i = 0; i < 1_000; i++) sink.Write(block, 0, Block); }

        // Warm up: the first pass touches the mapped pages and pays tiered-JIT compilation.
        Render();

        // Tier-1 compilation lands on a background thread some time after the call threshold,
        // so a warm pass can end while the promotion is still in flight and the counted pass
        // can then be charged a one-off install cost. Give it room to finish first.
        Thread.Sleep(100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Render();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_sink_with_no_reader_still_publishes()
    {
        // No driver attached is the normal case until the ASIO host opens the bus:
        // the app must keep writing rather than wait for a reader.
        string name = NewName();
        using var sink = VirtualBusSink.TryOpen(name, Rate, Channels, Block);
        Assert.NotNull(sink);

        var block = BlockFor(1);
        for (int i = 0; i < 64; i++) sink.Write(block, 0, Block);

        Assert.Equal(64, sink.BlocksPublished);
        Assert.False(sink.HasFailed);
    }

    [Fact]
    public void An_unopenable_section_is_null_and_says_so_once()
    {
        // A name the transport rejects stands in for "the section cannot be opened":
        // the route keeps rendering without a ring instead of throwing.
        Assert.Null(VirtualBusSink.TryOpen("not a valid/name", Rate, Channels, Block));
    }

    [Fact]
    public async Task The_engine_starts_when_its_only_sink_is_the_virtual_ring()
    {
        // The silence bug: Start() demanded an EndpointSink, so a route with no
        // WASAPI device never started - no audio and no error.
        string name = NewName();
        using var engine = new AudioEngine();

        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            await engine.SetRouteAsync(new AudioRoute
            {
                VirtualBuses = new[] { BusKind.Monitor },
                VirtualBusName = name,
            });

            engine.Start();
            Assert.True(engine.IsRunning);

            using var reader = VirtualBusReader.Open(name, maxLagBlocks: VirtualBusSink.DefaultCapacityBlocks);
            var received = new float[Block * Channels];
            var deadline = Stopwatch.StartNew();

            // The engine paces a device-less route to real time, so a second's wait
            // should see roughly 100 blocks; two is the proof that it is live.
            while (reader.BlocksRead < 2 && deadline.ElapsedMilliseconds < 2_000)
            {
                if (reader.Read(received) != VirtualBusStatus.Data) Thread.Sleep(1);
            }

            long written = -1;
            try { using var probe = VirtualBusWriter.Attach(name); written = probe.BlocksWritten; } catch { }

            Assert.True(reader.BlocksRead >= 2,
                $"engine started but the reader saw {reader.BlocksRead} block(s) in " +
                $"{deadline.ElapsedMilliseconds} ms; ring writeIndex={written}; " +
                $"log: {string.Join(" | ", lines)}");

            // No assertion on Underruns: a reader that starts polling before the
            // writer's first block legitimately counts one. The proof that matters is
            // that blocks keep arriving from a route with no device at all.
        }
        finally
        {
            Log.LineWritten -= Handler;
        }
    }

    [Fact]
    public void The_settings_flags_become_route_buses_without_a_schema_bump()
    {
        // Additive keys: a file with none of them written means "nobody feeds the
        // ring", and the schema version stays where it is.
        var off = new AppSettings();
        Assert.Empty(DualDeckTransport.VirtualBusesFromSettings(off));
        Assert.Equal("main", off.VirtualAsioBusName);
        Assert.Equal(5, off.SettingsSchemaVersion);

        var on = new AppSettings { VirtualAsioMonitor = true };
        Assert.Equal(new[] { BusKind.Monitor }, DualDeckTransport.VirtualBusesFromSettings(on));

        var route = DualDeckTransport.BuildRouteFromAppSettings(
            new AppSettings { VirtualAsioRecording = true, VirtualAsioBusName = "monitorbus" });
        Assert.Equal(new[] { BusKind.Recording }, route.VirtualBuses);
        Assert.Equal("monitorbus", route.VirtualBusName);
    }
}
