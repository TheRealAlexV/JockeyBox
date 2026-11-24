using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The failure modes that only show up as silence: a ring that did not open being
/// reported as "every bus is set to None", a name the driver can never attach to
/// being saved, a second flagged bus dropped without a word, the render thread
/// reading a sink and a bus from different routes, and a ring too short for the
/// driver's cushion.
/// </summary>
public sealed class VirtualRingGuardTests
{
    private const int Rate = 48_000;
    private const int Block = 480;
    private const int Channels = 2;

    private static string NewName() => "guard" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_failed_ring_open_is_reported_as_a_ring_failure_not_as_every_bus_none()
    {
        using var engine = new AudioEngine();
        var lines = new ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            // A name the driver cannot accept stands in for any failed open: the
            // engine refuses it before the writer, and must say which bus and why.
            await engine.SetRouteAsync(new AudioRoute
            {
                VirtualBuses = new[] { BusKind.Monitor },
                VirtualBusName = new string('a', AppSettings.VirtualAsioBusNameMaxLength + 1),
            });

            engine.Start();

            Assert.False(engine.IsRunning);
            Assert.True(engine.VirtualRingFailed);
            Assert.Contains(lines, line =>
                line.Contains("bus Monitor") && line.Contains("cannot publish") &&
                line.Contains("64"));
            Assert.DoesNotContain(lines, line => line.Contains("every bus is set to None"));
        }
        finally
        {
            Log.LineWritten -= Handler;
        }
    }

    [Fact]
    public async Task Start_with_no_device_and_no_flagged_bus_still_says_every_bus_is_none()
    {
        using var engine = new AudioEngine();
        var lines = new ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            await engine.SetRouteAsync(new AudioRoute());
            engine.Start();

            Assert.False(engine.IsRunning);
            Assert.False(engine.VirtualRingFailed);
            Assert.Contains(lines, line => line.Contains("every bus is set to None"));
        }
        finally
        {
            Log.LineWritten -= Handler;
        }
    }

    [Fact]
    public void A_ring_name_longer_than_the_driver_buffer_is_rejected()
    {
        Assert.Null(AppSettings.ValidateVirtualAsioBusName(
            new string('a', AppSettings.VirtualAsioBusNameMaxLength)));

        var reason = AppSettings.ValidateVirtualAsioBusName(
            new string('a', AppSettings.VirtualAsioBusNameMaxLength + 1));
        Assert.NotNull(reason);
        Assert.Contains(AppSettings.VirtualAsioBusNameMaxLength.ToString(), reason);

        Assert.NotNull(AppSettings.ValidateVirtualAsioBusName("has space"));
        Assert.NotNull(AppSettings.ValidateVirtualAsioBusName(""));
    }

    [Fact]
    public void A_second_flagged_bus_is_normalized_away_so_it_cannot_be_dropped_silently()
    {
        var settings = new AppSettings
        {
            VirtualAsioDeckA = true,
            VirtualAsioDeckB = true,
            VirtualAsioMonitor = true,
        };

        Assert.Equal(3, settings.VirtualAsioBusCount);
        Assert.Equal(2, settings.NormalizeVirtualAsioBuses());

        Assert.True(settings.VirtualAsioDeckA);
        Assert.False(settings.VirtualAsioDeckB);
        Assert.False(settings.VirtualAsioMonitor);
        Assert.Equal(1, settings.VirtualAsioBusCount);
    }

    [Fact]
    public async Task A_route_change_swaps_the_sink_and_its_bus_as_one_pair()
    {
        using var engine = new AudioEngine();
        string nameMonitor = NewName();
        string nameDeckA = NewName();

        await engine.SetRouteAsync(new AudioRoute
        {
            VirtualBuses = new[] { BusKind.Monitor },
            VirtualBusName = nameMonitor,
        });
        var first = engine.Ring;
        Assert.NotNull(first);
        Assert.Equal(BusKind.Monitor, first!.Taps.TapArray[0].Bus);
        Assert.True(first.Taps.IsLegacy);
        Assert.Equal(nameMonitor, first.Sink.SectionName);
        await engine.SetRouteAsync(new AudioRoute
        {
            VirtualBuses = new[] { BusKind.DeckA },
            VirtualBusName = nameDeckA,
        });
        var second = engine.Ring;
        Assert.NotNull(second);
        Assert.Equal(BusKind.DeckA, second!.Taps.TapArray[0].Bus);
        Assert.Equal(nameDeckA, second.Sink.SectionName);
        Assert.NotSame(first.Sink, second.Sink);
    }

    [Fact]
    public async Task A_snapshot_read_during_route_swaps_never_mixes_sink_and_bus()
    {
        using var engine = new AudioEngine();
        string nameMonitor = NewName();
        string nameDeckA = NewName();
        var monitor = new AudioRoute
        {
            VirtualBuses = new[] { BusKind.Monitor },
            VirtualBusName = nameMonitor,
        };
        var deckA = new AudioRoute
        {
            VirtualBuses = new[] { BusKind.DeckA },
            VirtualBusName = nameDeckA,
        };
        await engine.SetRouteAsync(monitor);

        int torn = 0;
        int stop = 0;
        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (engine.Ring is not { } ring) continue;
                bool monitorPlan = ring.Taps.TapArray[0].Bus == BusKind.Monitor;
                string expected = monitorPlan ? nameMonitor : nameDeckA;
                if (ring.Sink.SectionName != expected) Interlocked.Increment(ref torn);
            }
        });

        for (int i = 0; i < 8; i++)
            await engine.SetRouteAsync(i % 2 == 0 ? deckA : monitor);

        Volatile.Write(ref stop, 1);
        await reader;
        Assert.Equal(0, torn);
    }

    [Fact]
    public void A_tight_ring_capacity_is_raised_to_hold_the_driver_cushion()
    {
        int needed = VirtualBusSink.CapacityForCushion(Block);

        // 3 x 4096 frames of cushion must fit inside half the 480-frame ring.
        Assert.True(needed >= 3 * VirtualBusSink.MaxHostBufferFrames * 2 / Block);
        Assert.True(VirtualBusSink.DefaultCapacityBlocks >= needed);

        string name = NewName();
        var lines = new ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            using var sink = VirtualBusSink.TryOpen(name, Rate, Channels, Block, capacityBlocks: 2);
            Assert.NotNull(sink);
            Assert.Equal(needed, sink!.CapacityBlocks);
        }
        finally
        {
            Log.LineWritten -= Handler;
        }

        Assert.Contains(lines, line => line.Contains("cannot hold the driver's"));
    }
}
