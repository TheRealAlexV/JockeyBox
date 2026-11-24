using System.Runtime.InteropServices;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace JockeyBox.Ipc;

/// <summary>
/// One consumer of the shared transport ring. Many readers may share one writer.
///
/// Reads never block the writer. No new block means silence; falling more than the
/// configured lag behind means jump to the newest block and count one correction,
/// never replaying stale audio. In step, the copy is bit-exact.
///
/// No allocation after construction.
/// </summary>
public sealed unsafe class VirtualBusReader : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly long _slotOffset;
    private readonly int _capacity;
    private readonly int _maxLag;
    private readonly int _blockSamples;
    private readonly int _blockBytes;
    private int _disposed;

    private VirtualBusReader(
        string name,
        int slot,
        int maxLag,
        MemoryMappedFile map,
        MemoryMappedViewAccessor view,
        int sampleRate,
        int channels,
        int blockFrames,
        int capacityBlocks)
    {
        _map = map;
        _view = view;
        _slotOffset = BusLayout.SlotOffset(slot);
        _capacity = capacityBlocks;
        _maxLag = maxLag;
        _blockSamples = blockFrames * channels;
        _blockBytes = _blockSamples * sizeof(float);
        Name = name;
        Slot = slot;
        MaxLagBlocks = maxLag;
        SampleRate = sampleRate;
        Channels = channels;
        BlockFrames = blockFrames;
        CapacityBlocks = capacityBlocks;

        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr + view.PointerOffset;
    }

    public string Name { get; }

    /// <summary>Slot this reader holds.</summary>
    public int Slot { get; }

    /// <summary>Blocks this reader may fall behind before a correction jump.</summary>
    public int MaxLagBlocks { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BlockFrames { get; }

    public int CapacityBlocks { get; }

    /// <summary>Blocks consumed by this reader.</summary>
    public long BlocksRead => Volatile.Read(ref LongRef(_slotOffset + BusLayout.SlotReadBlockIndex));

    /// <summary>Jumps this reader was forced into (overrun by the writer or excess lag).</summary>
    public long Corrections => Volatile.Read(ref LongRef(_slotOffset + BusLayout.SlotCorrections));

    /// <summary>
    /// One-frame rate trims this reader applied. Written by the native ASIO reader,
    /// not by this side, and not a correction count (corrections are the jumps forced
    /// by overrun or excess lag). Zero for a slot the native reader never claimed.
    /// </summary>
    public long RateTrims => Volatile.Read(ref LongRef(_slotOffset + BusLayout.SlotRateTrims));

    /// <summary>Reads that found no new block.</summary>
    public long Underruns => Volatile.Read(ref LongRef(_slotOffset + BusLayout.SlotUnderruns));

    /// <summary>Blocks currently waiting for this reader.</summary>
    public long LagBlocks
    {
        get
        {
            ThrowIfDisposed();
            long written = Volatile.Read(ref LongRef(BusLayout.WriteBlockIndexOffset));
            return written - Volatile.Read(ref LongRef(_slotOffset + BusLayout.SlotReadBlockIndex));
        }
    }

    /// <summary>
    /// Joins the bus on the first free reader slot and starts at the newest block
    /// (history is never replayed).
    /// </summary>
    /// <param name="maxLagBlocks">
    /// How far behind the writer this reader tolerates before jumping. Defaults to the ring
    /// capacity, so only a real overrun forces a correction.
    /// </param>
    public static VirtualBusReader Open(string name, int maxLagBlocks = 0, BusScope scope = BusScope.Local)
        => Join(name, slot: -1, maxLagBlocks, scope);

    /// <summary>Joins a specific reader slot (0..7) instead of the first free one.</summary>
    public static VirtualBusReader Open(string name, int slot, int maxLagBlocks, BusScope scope = BusScope.Local)
        => Join(name, slot, maxLagBlocks, scope);

    /// <summary>
    /// Attaches to a ring someone else created: the driver's Global\ section on Windows, or an
    /// existing region on Linux. Validates the header and never initialises it, so a live
    /// writer's cursor stays exactly where it is.
    /// </summary>
    /// <param name="channels">
    /// The channel count this consumer expects. When given, a header declaring a different
    /// count is refused instead of adopted.
    /// </param>
    /// <param name="blockFrames">Expected block frames; refused when the header differs.</param>
    /// <param name="capacityBlocks">Expected ring capacity; refused when the header differs.</param>
    public static VirtualBusReader Attach(
        string name,
        int maxLagBlocks = 0,
        BusScope scope = BusScope.Local,
        int? channels = null,
        int? blockFrames = null,
        int? capacityBlocks = null)
        => Join(name, slot: -1, maxLagBlocks, scope, channels, blockFrames, capacityBlocks);

    /// <summary>Attaches to a specific reader slot (0..7).</summary>
    public static VirtualBusReader Attach(
        string name,
        int slot,
        int maxLagBlocks,
        BusScope scope = BusScope.Local,
        int? channels = null,
        int? blockFrames = null,
        int? capacityBlocks = null)
        => Join(name, slot, maxLagBlocks, scope, channels, blockFrames, capacityBlocks);

    private static VirtualBusReader Join(
        string name,
        int slot,
        int maxLagBlocks,
        BusScope scope,
        int? expectedChannels = null,
        int? expectedBlockFrames = null,
        int? expectedCapacityBlocks = null)
    {
        BusLayout.ValidateName(name);
        if (maxLagBlocks < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLagBlocks), maxLagBlocks, "Maximum lag cannot be negative.");
        if (slot >= BusLayout.ReaderSlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Reader slots are 0..{BusLayout.ReaderSlotCount - 1}.");

        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        bool claimed = false;
        int chosenSlot = -1;
        try
        {
            map = BusLayout.OpenExisting(name, scope);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            var geometry = BusLayout.ReadHeader(view, name);
            int sampleRate = geometry.SampleRate;
            int channels = geometry.Channels;
            int blockFrames = geometry.BlockFrames;
            int capacityBlocks = geometry.CapacityBlocks;

            // Refuse before claiming a slot: a header that does not match this consumer's
            // geometry would be read with the wrong stride.
            BusLayout.RequireGeometry(name, channels, blockFrames, capacityBlocks,
                expectedChannels, expectedBlockFrames, expectedCapacityBlocks);

            int chosen;
            if (slot >= 0)
            {
                if (!TryClaimSlot(view, slot))
                    throw new VirtualBusException($"Reader slot {slot} on bus '{name}' is already claimed by another reader.");
                chosen = slot;
            }
            else
            {
                chosen = ClaimFreeSlot(view, name);
            }

            claimed = true;
            chosenSlot = chosen;
            int lag = maxLagBlocks == 0 ? capacityBlocks : maxLagBlocks;

            var reader = new VirtualBusReader(name, chosen, lag, map, view, sampleRate, channels, blockFrames, capacityBlocks);
            reader.PublishRead(Volatile.Read(ref reader.LongRef(BusLayout.WriteBlockIndexOffset)));
            return reader;
        }
        catch
        {
            if (view is not null)
            {
                if (claimed)
                    TryReleaseSlot(view, chosenSlot);

                view.Dispose();
            }

            map?.Dispose();
            throw;
        }
    }

    private static int ClaimFreeSlot(MemoryMappedViewAccessor view, string name)
    {
        for (int i = 0; i < BusLayout.ReaderSlotCount; i++)
        {
            if (TryClaimSlot(view, i))
                return i;
        }

        throw new VirtualBusException($"All {BusLayout.ReaderSlotCount} reader slots on bus '{name}' are claimed; dispose a reader first.");
    }

    private static bool TryClaimSlot(MemoryMappedViewAccessor view, int slot)
    {
        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            byte* b = ptr + view.PointerOffset;
            long offset = BusLayout.SlotOffset(slot);
            ref int active = ref Unsafe.AsRef<int>(b + offset + BusLayout.SlotActive);
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                return false;

            // Fresh counters for the new reader; the writer may bump them at any time.
            Unsafe.WriteUnaligned(b + offset + BusLayout.SlotCorrections, 0L);
            Unsafe.WriteUnaligned(b + offset + BusLayout.SlotUnderruns, 0L);
            return true;
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static void TryReleaseSlot(MemoryMappedViewAccessor view, int slot)
    {
        if (slot < 0 || slot >= BusLayout.ReaderSlotCount)
            return;

        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            byte* b = ptr + view.PointerOffset + BusLayout.SlotOffset(slot);
            Unsafe.WriteUnaligned(b + BusLayout.SlotActive, 0);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>
    /// Copies the next block into <paramref name="destination"/>, or reports silence or a
    /// correction. <paramref name="destination"/> must hold at least
    /// <c>blockFrames * channels</c> samples; extra samples are left untouched.
    /// </summary>
    public VirtualBusStatus Read(Span<float> destination)
    {
        ThrowIfDisposed();
        if (destination.Length < _blockSamples)
        {
            throw new ArgumentException(
                $"Destination must hold {_blockSamples} samples ({BlockFrames} frames x {Channels} channels); got {destination.Length}.",
                nameof(destination));
        }

        long written = Volatile.Read(ref LongRef(BusLayout.WriteBlockIndexOffset));
        long read = Volatile.Read(ref LongRef(_slotOffset + BusLayout.SlotReadBlockIndex));

        if (written <= read)
        {
            destination[.._blockSamples].Clear();
            Interlocked.Increment(ref LongRef(_slotOffset + BusLayout.SlotUnderruns));
            return VirtualBusStatus.Silence;
        }

        if (written - read > _maxLag || written - read > _capacity)
        {
            // Jump the gap instead of replaying blocks the writer already overwrote.
            Advance(ref LongRef(_slotOffset + BusLayout.SlotReadBlockIndex), written);
            Interlocked.Increment(ref LongRef(_slotOffset + BusLayout.SlotCorrections));
            destination[.._blockSamples].Clear();
            return VirtualBusStatus.Correction;
        }

        long blockIndex = read + 1;
        int slotIndex = (int)((blockIndex - 1) % _capacity);
        MemoryMarshal.Cast<byte, float>(
            new Span<byte>(_base + BusLayout.PayloadOffset + ((long)slotIndex * _blockBytes), _blockBytes))
            .CopyTo(destination);

        PublishRead(blockIndex);
        return VirtualBusStatus.Data;
    }

    private void PublishRead(long value) => Advance(ref LongRef(_slotOffset + BusLayout.SlotReadBlockIndex), value);

    private static void Advance(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private ref long LongRef(long offset) => ref Unsafe.AsRef<long>(_base + offset);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VirtualBusReader));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Release the slot first so the writer stops tracking it, then unmap.
        try
        {
            Unsafe.WriteUnaligned(_base + _slotOffset + BusLayout.SlotReadBlockIndex, 0L);
            Unsafe.WriteUnaligned(_base + _slotOffset + BusLayout.SlotActive, 0);
        }
        finally
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _map.Dispose();
        }
    }
}
