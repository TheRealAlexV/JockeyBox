using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using JockeyBox.Ipc;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// The app's producer for the virtual ASIO transport ring: one bus, published to the driver's
/// <c>Global\JockeyBox.Bus.&lt;name&gt;</c> when it exists, else to
/// <c>Local\JockeyBox.Bus.&lt;name&gt;</c> so the native driver has audio to serve.
///
/// <para><b>Who owns the ring.</b> With the kernel driver installed, the driver creates the
/// <c>Global\</c> section and the app attaches: it adopts the creator's geometry and writes no
/// header byte. Without a driver the app creates its <c>Local\</c> section exactly as before.
/// A driver section whose geometry does not match this device is refused here, never re-created.</para>
///
/// <para><b>Lifecycle.</b> The writer is opened once when the route is applied and
/// lives until the route changes or the engine stops. Nothing is created, copied
/// into a fresh buffer, or thrown on the render thread: the block goes straight
/// from the bus buffer into the mapped region.</para>
///
/// <para><b>No reader is not an error.</b> With no driver attached the writer keeps
/// publishing: lapping an idle reader costs one ring lap, whereas waiting for a
/// reader would stall the whole graph behind a device nobody opened.</para>
/// </summary>
public sealed class VirtualBusSink : IAudioSink
{
    /// <summary>
    /// The largest host buffer the driver is expected to negotiate. Its reader
    /// joins with a cushion of three host buffers (see the ASIO bridge README),
    /// clamped to half the ring, so the ring must be sized from this rather than
    /// from the app's 480-frame block alone.
    /// </summary>
    public const int MaxHostBufferFrames = 4096;

    /// <summary>
    /// Default ring capacity in blocks: 64 = 640 ms at 48 kHz. That is the
    /// smallest power of two that holds the driver's 3 x 4096-frame cushion
    /// (52 blocks at a 480-frame block) inside half the ring, with spare blocks
    /// for a late wake-up. Capacity is not latency: a reader that keeps up never
    /// sits more than its own cushion behind, so extra blocks cost mapped memory
    /// only.
    /// </summary>
    public const int DefaultCapacityBlocks = 64;

    /// <summary>
    /// Blocks needed so the driver's 3 x <see cref="MaxHostBufferFrames"/> cushion
    /// fits inside half the ring at <paramref name="blockFrames"/>.
    /// </summary>
    public static int CapacityForCushion(int blockFrames) =>
        Math.Max(16, (6 * MaxHostBufferFrames + blockFrames - 1) / blockFrames);

    private readonly int _blockSamples;
    private readonly int _channels;
    private readonly int _blockFrames;
    private VirtualBusWriter? _writer;
    private long _blocks;
    private long _laps;
    private int _quiet;
    private int _sizeWarned;

    private VirtualBusSink(string sectionName, VirtualBusWriter writer, int channels, int blockFrames,
        int capacityBlocks)
    {
        SectionName = sectionName;
        _writer = writer;
        _channels = channels;
        _blockFrames = blockFrames;
        _blockSamples = blockFrames * channels;
        CapacityBlocks = capacityBlocks;
    }

    /// <summary>Blocks of ring the writer was actually opened with (cushion-adjusted).</summary>
    public int CapacityBlocks { get; }

    /// <summary>
    /// Open (or re-create) the ring. Returns null when the section cannot be opened:
    /// the caller keeps rendering, the operator's other outputs are unaffected.
    /// </summary>
    public static VirtualBusSink? TryOpen(string sectionName, int sampleRate, int channels,
        int blockFrames, int capacityBlocks = DefaultCapacityBlocks)
        => TryOpen(sectionName, sampleRate, channels, blockFrames, out _, capacityBlocks);

    /// <summary>
    /// Same as <see cref="TryOpen(string,int,int,int,int)"/>, and reports why the
    /// ring could not be opened. The engine names the bus in its own line, so a
    /// failed open is never mistaken for "no bus flagged" (see AudioEngine.Start).
    /// </summary>
    public static VirtualBusSink? TryOpen(string sectionName, int sampleRate, int channels,
        int blockFrames, out string? error, int capacityBlocks = DefaultCapacityBlocks,
        bool logFailure = true)
    {
        error = null;

        // The driver's cushion is three host buffers and is clamped to half the
        // ring; a ring shorter than that starves at every wrap, which the operator
        // would only hear as an underrun. Raise it here, once per open, and say so.
        int needed = CapacityForCushion(blockFrames);
        if (capacityBlocks < needed)
        {
            Log.Warn($"virtual ASIO ring '{sectionName}': {capacityBlocks} blocks " +
                     $"({capacityBlocks * blockFrames} frames) cannot hold the driver's " +
                     $"{3 * MaxHostBufferFrames}-frame cushion at a {MaxHostBufferFrames}-frame " +
                     $"host buffer; opening with {needed} blocks instead");
            capacityBlocks = needed;
        }

        try
        {
            VirtualBusWriter writer;
            if (BusLayout.GlobalSectionExists(sectionName) && !AppWriterOwnsBus(sectionName))
            {
                // The driver publishes the pair's ring, so the app is the attacher: it adopts
                // the creator's geometry and never writes a header byte. A section that exists
                // but does not match this device's geometry is refused here rather than
                // falling back to a Local\ section, which would leave the pair silently mute.
                writer = VirtualBusWriter.Attach(sectionName, BusScope.Global,
                    channels: channels, blockFrames: blockFrames, capacityBlocks: capacityBlocks);
                Log.Info($"virtual ASIO ring '{sectionName}': attached to the driver's " +
                         $"{BusLayout.MapNameFor(sectionName, BusScope.Global)} " +
                         $"({writer.Channels} ch @ {writer.SampleRate} Hz, block {writer.BlockFrames}, " +
                         $"{writer.CapacityBlocks} blocks)");
            }
            else
            {
                // No driver section: nothing is installed, so create the Local\ ring exactly as
                // an app-only install always has.
                writer = VirtualBusWriter.Open(sectionName, sampleRate, channels, blockFrames,
                    capacityBlocks);
                Log.Info($"virtual ASIO ring '{sectionName}': {channels} ch @ {sampleRate} Hz, " +
                         $"block {blockFrames}, {capacityBlocks} blocks " +
                         $"({BusLayout.MapNameFor(sectionName, BusScope.Local)})");
            }
            return new VirtualBusSink(sectionName, writer, channels, blockFrames, capacityBlocks);
        }
        catch (Exception ex)
        {
            // Once per route, on the route thread - never per block on the render thread.
            // A caller that has its own line to write (the engine, naming the bus it
            // kept instead) suppresses this one so the operator sees the failure once.
            error = ex.Message;
            if (logFailure)
                Log.Warn($"virtual ASIO ring '{sectionName}' not opened: {ex.Message}; " +
                         "rendering continues without it");
            return null;
        }
    }

    /// <summary>
    /// Whether a live app writer owns this bus, which means the section the Global probe
    /// found above is <b>not</b> the driver's.
    ///
    /// <para><b>Why the claim file decides.</b> The driver never creates one: only
    /// <see cref="VirtualBusWriter.Open"/> takes <c>&lt;name&gt;.lock</c>, with
    /// <see cref="FileShare.None"/>. So a claim that is <i>held</i> is positive proof
    /// that an app created the section. That matters because <c>Local\</c> and
    /// <c>Global\</c> are the same namespace for a session 0 process (an elevated or
    /// service-hosted instance): the Global probe then finds our own Local ring, and
    /// attaching to it would skip the one-writer claim and let two producers publish the
    /// same ring. Attaching is only right when the driver owns the section and no app
    /// writer is claiming it.</para>
    ///
    /// <para>The <i>existence</i> of the file is not the question: it is never deleted
    /// when a writer closes, so a stale file would refuse a legitimate attach to the
    /// driver's ring and silence every real user. The exclusive share lock is what
    /// separates a live writer from a stale file; it is released immediately and the
    /// real claim is still taken by <see cref="VirtualBusWriter.Open"/> on the create
    /// path.</para>
    /// </summary>
    private static bool AppWriterOwnsBus(string sectionName)
    {
        try
        {
            using var probe = new FileStream(BusLayout.LockPathFor(sectionName),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // nobody holds it: the section is the driver's, attach is right
        }
        catch (IOException)
        {
            return true;  // a live writer holds the claim, so an app owns this bus
        }
        catch (Exception)
        {
            return false; // no claim file at all: the driver's own section
        }
    }

    /// <summary>The bus name in the section, without the <c>Local\JockeyBox.Bus.</c> prefix.</summary>
    public string SectionName { get; }

    public string DeviceName => $"virtual ASIO '{SectionName}'";

    /// <summary>Interleaved channels this sink was opened for (the device geometry).</summary>
    public int Channels => _channels;

    /// <summary>True once a Write failed; the engine then drops this sink like a dead device.</summary>
    public bool HasFailed => Volatile.Read(ref _quiet) != 0;

    /// <summary>Why <see cref="HasFailed"/> is true; null while healthy.</summary>
    public string? FailureReason =>
        HasFailed ? "the ring write failed (the driver stopped reading the section)" : null;

    /// <summary>Blocks published by this instance (the ring's own counter survives a restart).</summary>
    public long BlocksPublished => Interlocked.Read(ref _blocks);

    /// <summary>Times the writer lapped the driver because the app got ahead of it.</summary>
    public long Laps => Interlocked.Read(ref _laps);

    public void Write(float[] interleaved, int offset, int frames)
    {
        var writer = _writer;
        if (writer == null) return;

        // A block of the wrong size is a routing bug, not something to fix on the
        // render thread: skip it, and say so once so the bug does not become silence
        // with no line in the log.
        int samples = frames * _channels;
        if (samples != _blockSamples)
        {
            if (Interlocked.Exchange(ref _sizeWarned, 1) == 0)
                Log.Warn($"virtual ASIO ring '{SectionName}' got {samples} samples, " +
                         $"expected {_blockSamples}; those blocks are skipped");
            return;
        }

        try
        {
            _laps += writer.Write(new ReadOnlySpan<float>(interleaved, offset, samples));
            _blocks++;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _quiet, 1) == 0)
                Log.Warn($"virtual ASIO ring '{SectionName}' stopped publishing: {ex.Message}");
            _writer = null;
        }
    }

    /// <summary>
    /// True when this sink has published more than one block ahead of real time.
    ///
    /// <para>A route with no device has no device clock to pace against, so the
    /// cadence is the wall clock. Comparing against absolute elapsed time (never
    /// against the previous block) means a coarse timer wake-up catches up in one
    /// short burst instead of running permanently slow.</para>
    /// </summary>
    public bool IsAheadOfClock(long startTimestamp, int blockFrames, int sampleRate)
    {
        if (Volatile.Read(ref _quiet) != 0) return false;
        double dueBlocks = (Stopwatch.GetTimestamp() - startTimestamp) * (double)sampleRate
                           / (Stopwatch.Frequency * (double)blockFrames);
        return Interlocked.Read(ref _blocks) >= dueBlocks + 1.0;
    }

    /// <summary>Frames per block this sink was opened for.</summary>
    public int BlockFrames => _blockFrames;

    /// <summary>
    /// Times this sink has been disposed. A replaced or abandoned producer must be
    /// disposed exactly once, and a double dispose must not release a writer twice.
    /// </summary>
    private int _disposed;

    /// <summary>Test seam: 1 once disposed, so a swap can assert "exactly once".</summary>
    internal int DisposeCount => Volatile.Read(ref _disposed);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var writer = _writer;
        _writer = null;
        try { writer?.Dispose(); } catch { }
    }
}
