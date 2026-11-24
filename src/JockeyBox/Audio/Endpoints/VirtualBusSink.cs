using System;
using System.Diagnostics;
using System.Threading;
using JockeyBox.Ipc;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// The app's producer for the virtual ASIO transport ring: one bus, published to
/// <c>Local\JockeyBox.Bus.&lt;name&gt;</c> so the native driver has audio to serve.
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
        int blockFrames, out string? error, int capacityBlocks = DefaultCapacityBlocks)
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
            var writer = VirtualBusWriter.Open(sectionName, sampleRate, channels, blockFrames,
                capacityBlocks);
            Log.Info($"virtual ASIO ring '{sectionName}': {channels} ch @ {sampleRate} Hz, " +
                     $"block {blockFrames}, {capacityBlocks} blocks " +
                     $"({BusLayout.MapNameFor(sectionName, BusScope.Local)})");
            return new VirtualBusSink(sectionName, writer, channels, blockFrames, capacityBlocks);
        }
        catch (Exception ex)
        {
            // Once per route, on the route thread - never per block on the render thread.
            error = ex.Message;
            Log.Warn($"virtual ASIO ring '{sectionName}' not opened: {ex.Message}; " +
                     "rendering continues without it");
            return null;
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

    public void Dispose()
    {
        var writer = _writer;
        _writer = null;
        try { writer?.Dispose(); } catch { }
    }
}
