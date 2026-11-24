using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Ipc;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;
using Xunit.Abstractions;

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

    private readonly ITestOutputHelper _output;

    public VirtualRingGuardTests(ITestOutputHelper output) => _output = output;

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

    // ------------------------------------------------------- re-apply: ring kept or swapped

    /// <summary>
    /// A route re-apply whose new bus cannot be opened (here: its one-writer claim is
    /// already held, which is what a second instance or a driver restart looks like)
    /// used to null the sink and flag the ring dead while the old writer was still
    /// publishing. The producer must survive the refused re-apply and keep writing.
    /// </summary>
    [Fact]
    public async Task A_route_reapply_whose_new_bus_is_already_held_keeps_the_old_ring_publishing()
    {
        using var engine = new AudioEngine();
        var lines = new ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            string live = NewName();
            string busy = NewName();

            await engine.SetRouteAsync(new AudioRoute
            {
                VirtualBuses = new[] { BusKind.Monitor },
                VirtualBusName = live,
            });
            engine.Start();
            Assert.True(engine.IsRunning);
            var first = engine.Ring;
            Assert.NotNull(first);
            Assert.True(SpinWait.SpinUntil(() => first!.Sink.BlocksPublished > 0,
                    TimeSpan.FromSeconds(5)),
                "the ring was not publishing before the re-apply");

            long before;
            using (VirtualBusWriter.Open(busy, Rate, Channels, Block,
                       VirtualBusSink.DefaultCapacityBlocks))
            {
                before = first!.Sink.BlocksPublished;

                // Diagnostics only (no assertion, no timing): is 'busy' claimed right
                // now, and does the sink's Global check see anything for it?
                var claimPath = BusLayout.LockPathFor(busy);
                _output.WriteLine($"busy='{busy}' local={BusLayout.MapNameFor(busy, BusScope.Local)} " +
                                  $"globalExists={BusLayout.GlobalSectionExists(busy)}");
                _output.WriteLine($"claim file '{claimPath}' exists={File.Exists(claimPath)}");
                try
                {
                    using var probe = new FileStream(claimPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    _output.WriteLine("claim probe: OPENED - no exclusive claim is held on 'busy'");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"claim probe: REFUSED {ex.GetType().Name}: {ex.Message}");
                }

                await engine.SetRouteAsync(new AudioRoute
                {
                    VirtualBuses = new[] { BusKind.DeckA },
                    VirtualBusName = busy,
                });

                var after = engine.Ring;
                _output.WriteLine($"after: ring={(after is null ? "null" : "not null")}" +
                                  (after is null
                                      ? ""
                                      : $" section='{after.Sink.SectionName}' dev='{after.Sink.DeviceName}' " +
                                        $"ch={after.Sink.Channels} sameAsFirst={ReferenceEquals(first.Sink, after.Sink)} " +
                                        $"taps0={(after.Taps.Taps.Count > 0 ? after.Taps.Taps[0].Bus.ToString() : "none")}"));
                foreach (var line in lines) _output.WriteLine("log: " + line);
                Assert.NotNull(after);
                Assert.Same(first.Sink, after!.Sink);
                Assert.Equal(live, after.Sink.SectionName);
                // The plan is what identifies the publishing bus now: a legacy route has
                // no single-bus field, it has one tap per channel naming its source bus.
                Assert.Equal(BusKind.Monitor, after.Taps.Taps[0].Bus);
                Assert.False(engine.VirtualRingFailed);

                Assert.True(SpinWait.SpinUntil(() => first.Sink.BlocksPublished > before,
                        TimeSpan.FromSeconds(5)),
                    "the ring stopped publishing after the refused re-apply");
            }

            var named = lines.Where(line => line.Contains(busy)).ToList();
            Assert.Single(named);
            Assert.Contains("keeping the ring", named[0]);
        }
        finally
        {
            Log.LineWritten -= Handler;
        }
    }

    /// <summary>
    /// The path this fix must not break: a section the driver owns, with no live app
    /// writer claiming it, still opens. A fix that refused on the mere <i>presence</i> of
    /// a claim file would refuse every real install the moment a writer had ever run on
    /// that name, and silence the ring.
    ///
    /// <para>The section is stood in for by a writer whose claim handle is released while
    /// the region stays open: the region is a driver-shaped ring with a stale (unheld)
    /// claim file, which is the state a driver-created section leaves behind.</para>
    /// </summary>
    [Fact]
    public void A_section_with_an_unheld_claim_file_still_opens()
    {
        string name = NewName();
        var lines = new ConcurrentQueue<string>();
        void Handler(string line) => lines.Enqueue(line);
        Log.LineWritten += Handler;
        try
        {
            using var creator = VirtualBusWriter.Open(name, Rate, Channels, Block,
                VirtualBusSink.DefaultCapacityBlocks);

            // Drop the single-writer claim, keep the region: the driver never creates a
            // claim file, so this is the driver's own state once a writer has ever run.
            var claim = typeof(VirtualBusWriter)
                .GetField("_claim",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(creator) as FileStream;
            Assert.NotNull(claim);
            claim!.Dispose();
            Assert.True(File.Exists(BusLayout.LockPathFor(name)),
                "the claim file is left behind, which is why existence cannot be the test");

            bool driverSectionVisible = BusLayout.GlobalSectionExists(name);

            using var sink = VirtualBusSink.TryOpen(name, Rate, Channels, Block, out string? error);
            Assert.Null(error);
            Assert.NotNull(sink);
            Assert.Equal(name, sink!.SectionName);

            // Where the driver's section is what the probe finds, the attach path is the
            // one that must still be taken (this is the path the fix could have closed).
            if (driverSectionVisible)
                Assert.Contains(lines, line => line.Contains("attached to the driver's"));
        }
        finally
        {
            Log.LineWritten -= Handler;
        }
    }

    /// <summary>
    /// Pins the namespace assumption the ring-guard canary rests on: a <c>Local\</c>
    /// section created by this process is reachable as <c>Global\</c> exactly when the
    /// process is in session 0, where the two namespaces alias. If a future host stops
    /// aliasing (or starts), this fails and the analysis behind the canary has changed.
    /// </summary>
    [Fact]
    public void A_Local_section_is_reachable_as_Global_only_in_session_0()
    {
        string name = NewName();
        using var local = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(
            BusLayout.MapNameFor(name, BusScope.Local), 4096);

        bool visibleAsGlobal;
        try
        {
            using var global = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
                BusLayout.MapNameFor(name, BusScope.Global));
            visibleAsGlobal = true;
        }
        catch (FileNotFoundException)
        {
            visibleAsGlobal = false;
        }

        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        if (session == 0)
            Assert.True(visibleAsGlobal,
                "session 0 must alias Local\\ and Global\\: the sink's driver check depends on it");
        else
            Assert.False(visibleAsGlobal,
                $"session {session} must keep Local\\ and Global\\ apart; if this fails the namespace assumption has changed");
    }

    /// <summary>
    /// The success path still swaps: a re-apply that names a different, free bus
    /// installs the new producer, disposes the old one exactly once, and leaves the
    /// old section claimable again (a live writer would still hold it).
    /// </summary>
    [Fact]
    public async Task A_route_reapply_that_opens_a_new_bus_swaps_the_sink_and_disposes_the_old_one_once()
    {
        using var engine = new AudioEngine();
        string first = NewName();
        string second = NewName();

        await engine.SetRouteAsync(new AudioRoute
        {
            VirtualBuses = new[] { BusKind.Monitor },
            VirtualBusName = first,
        });
        engine.Start();
        Assert.True(engine.IsRunning);
        var before = engine.Ring;
        Assert.NotNull(before);
        Assert.Equal(0, before!.Sink.DisposeCount);

        await engine.SetRouteAsync(new AudioRoute
        {
            VirtualBuses = new[] { BusKind.DeckA },
            VirtualBusName = second,
        });

        var after = engine.Ring;
        Assert.NotNull(after);
        Assert.NotSame(before.Sink, after!.Sink);
        Assert.Equal(second, after.Sink.SectionName);
        Assert.Equal(BusKind.DeckA, after.Taps.Taps[0].Bus);
        Assert.Equal(1, before.Sink.DisposeCount);
        Assert.Equal(0, after.Sink.DisposeCount);

        // The disposed producer stops writing, and its one-writer claim is released.
        long frozen = before.Sink.BlocksPublished;
        Thread.Sleep(100);
        Assert.Equal(frozen, before.Sink.BlocksPublished);
        using var reclaimed = VirtualBusWriter.Open(first, Rate, Channels, Block,
            VirtualBusSink.DefaultCapacityBlocks);
        Assert.True(SpinWait.SpinUntil(() => after.Sink.BlocksPublished > 0,
                TimeSpan.FromSeconds(5)),
            "the new ring never published after the swap");
    }

    /// <summary>
    /// A re-apply that names the bus already in use needs no open: one writer per
    /// bus means the open would be refused by our own claim, and the writer in
    /// place already publishes to exactly that section. It must keep it, not churn.
    /// </summary>
    [Fact]
    public async Task A_route_reapply_on_the_bus_already_publishing_keeps_its_writer()
    {
        using var engine = new AudioEngine();
        string name = NewName();

        await engine.SetRouteAsync(new AudioRoute
        {
            VirtualBuses = new[] { BusKind.Monitor },
            VirtualBusName = name,
        });
        var first = engine.Ring;
        Assert.NotNull(first);

        await engine.SetRouteAsync(new AudioRoute
        {
            VirtualBuses = new[] { BusKind.DeckA },
            VirtualBusName = name,
        });

        var second = engine.Ring;
        Assert.NotNull(second);
        Assert.Same(first!.Sink, second!.Sink);
        Assert.Equal(BusKind.DeckA, second.Taps.Taps[0].Bus);
        Assert.Equal(0, first.Sink.DisposeCount);
        Assert.False(engine.VirtualRingFailed);
    }
}
