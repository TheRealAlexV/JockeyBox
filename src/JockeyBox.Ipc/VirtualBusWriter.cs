using System.Runtime.InteropServices;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace JockeyBox.Ipc;

/// <summary>
/// The single producer of a shared transport ring.
///
/// Publishing is lock-free: the payload is copied first, then
/// <c>writeBlockIndex</c> is raised with a release barrier. Readers never block the
/// writer; a reader the writer is about to lap is advanced to the newest block and its
/// correction counter is bumped, so a dead consumer cannot stall audio.
///
/// No allocation after construction. The hot path touches only the mapped region.
/// </summary>
public sealed unsafe class VirtualBusWriter : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly FileStream? _claim;
    private readonly bool _ownsRegion;
    private readonly string _path;
    private readonly byte* _base;
    private readonly int _capacity;
    private readonly int _blockSamples;
    private readonly int _blockBytes;
    private long _write;
    private int _disposed;

    private VirtualBusWriter(
        string name,
        int sampleRate,
        int channels,
        int blockFrames,
        int capacityBlocks,
        FileStream? claim,
        MemoryMappedFile map,
        MemoryMappedViewAccessor view,
        bool creator)
    {
        _claim = claim;
        _ownsRegion = creator;
        _map = map;
        _view = view;
        _path = BusLayout.PathFor(name);
        _capacity = capacityBlocks;
        _blockSamples = blockFrames * channels;
        _blockBytes = _blockSamples * sizeof(float);
        Name = name;
        SampleRate = sampleRate;
        Channels = channels;
        BlockFrames = blockFrames;
        CapacityBlocks = capacityBlocks;

        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr + view.PointerOffset;

        if (!creator)
        {
            // Attached: the region belongs to someone else. Continue publishing from wherever
            // the creator left off, without writing a single header byte.
            _write = Volatile.Read(ref WriteIndexRef());
            return;
        }

        Unsafe.WriteUnaligned(_base + BusLayout.MagicOffset, unchecked((int)BusLayout.Magic));
        Unsafe.WriteUnaligned(_base + BusLayout.VersionOffset, BusLayout.Version);
        Unsafe.WriteUnaligned(_base + BusLayout.HeaderSizeOffset, BusLayout.HeaderSize);
        Unsafe.WriteUnaligned(_base + BusLayout.SampleRateOffset, sampleRate);
        Unsafe.WriteUnaligned(_base + BusLayout.ChannelsOffset, channels);
        Unsafe.WriteUnaligned(_base + BusLayout.BlockFramesOffset, blockFrames);
        Unsafe.WriteUnaligned(_base + BusLayout.CapacityBlocksOffset, capacityBlocks);
        Unsafe.WriteUnaligned(_base + BusLayout.ReaderSlotCountFieldOffset, BusLayout.ReaderSlotCount);
        Unsafe.WriteUnaligned(_base + BusLayout.PayloadOffsetFieldOffset, BusLayout.PayloadOffset);
        Unsafe.WriteUnaligned(_base + BusLayout.OverrunCountOffset, 0L);

        for (int i = 0; i < BusLayout.ReaderSlotCount; i++)
        {
            long slot = BusLayout.SlotOffset(i);
            Unsafe.WriteUnaligned(_base + slot + BusLayout.SlotReadBlockIndex, 0L);
            Unsafe.WriteUnaligned(_base + slot + BusLayout.SlotCorrections, 0L);
            Unsafe.WriteUnaligned(_base + slot + BusLayout.SlotUnderruns, 0L);
            Unsafe.WriteUnaligned(_base + slot + BusLayout.SlotActive, 0);
        }

        // Publish the cursor last: a reader that opens now sees a complete header.
        Volatile.Write(ref WriteIndexRef(), 0L);
    }

    /// <summary>
    /// Attaches to a ring someone else created: the driver's Global\ section on Windows, or an
    /// existing region on Linux. Validates the header, then continues publishing from the
    /// creator's cursor. It never initialises or re-initialises a header field.
    /// </summary>
    /// <param name="channels">
    /// The channel count the configured device expects. When given, a header declaring a
    /// different count is refused instead of adopted.
    /// </param>
    /// <param name="blockFrames">Expected block frames; refused when the header differs.</param>
    /// <param name="capacityBlocks">Expected ring capacity; refused when the header differs.</param>
    /// <exception cref="VirtualBusException">
    /// No such region, its header is not a valid ring, or its geometry differs from the
    /// expectations given.
    /// </exception>
    public static VirtualBusWriter Attach(
        string name,
        BusScope scope = BusScope.Local,
        int? channels = null,
        int? blockFrames = null,
        int? capacityBlocks = null)
    {
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            map = BusLayout.OpenExisting(name, scope);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            var geometry = BusLayout.ReadHeader(view, name);
            BusLayout.RequireGeometry(name, geometry.Channels, geometry.BlockFrames,
                geometry.CapacityBlocks, channels, blockFrames, capacityBlocks);
            return new VirtualBusWriter(
                name,
                geometry.SampleRate,
                geometry.Channels,
                geometry.BlockFrames,
                geometry.CapacityBlocks,
                claim: null,
                map,
                view,
                creator: false);
        }
        catch
        {
            view?.Dispose();
            map?.Dispose();
            throw;
        }
    }

    public string Name { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BlockFrames { get; }

    public int CapacityBlocks { get; }

    /// <summary>Blocks published so far (the header's <c>writeBlockIndex</c>).</summary>
    public long BlocksWritten => Volatile.Read(ref WriteIndexRef());

    /// <summary>Total times the writer lapped a reader and dropped the oldest blocks.</summary>
    public long Overruns => Volatile.Read(ref LongRef(BusLayout.OverrunCountOffset));

    /// <summary>Corrections recorded for a reader slot, whoever caused them.</summary>
    public long Corrections(int slot) => Volatile.Read(ref LongRef(BusLayout.SlotOffset(slot) + BusLayout.SlotCorrections));

    /// <summary>True when a reader slot is claimed.</summary>
    public bool IsReaderActive(int slot) => Volatile.Read(ref IntRef(BusLayout.SlotOffset(slot) + BusLayout.SlotActive)) == 1;

    /// <summary>
    /// Takes the single-writer role and maps the region, creating or re-initialising it.
    /// On both platforms an existing region is reattached, so a writer that died without
    /// disposing is replaced by a creator restart that resets the header cursor. The claim file
    /// is the ownership gate: holding it is the licence to re-initialise.
    /// </summary>
    /// <exception cref="VirtualBusException">
    /// A writer is already open for this name, or an existing region carries a different
    /// protocol version.
    /// </exception>
    public static VirtualBusWriter Open(
        string name,
        int sampleRate,
        int channels,
        int blockFrames,
        int capacityBlocks,
        BusScope scope = BusScope.Local)
    {
        BusLayout.ValidateName(name);
        if (scope == BusScope.Global && OperatingSystem.IsWindows())
        {
            throw new VirtualBusException(
                $"Cannot create '{BusLayout.MapNameFor(name, scope)}' from this process: UAC strips " +
                "SeCreateGlobalPrivilege from a non-elevated token, so only the kernel driver may create a Global\\ section. " +
                "Let the driver create it, then call VirtualBusWriter.Attach(name, BusScope.Global).");
        }
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
        if (blockFrames <= 0) throw new ArgumentOutOfRangeException(nameof(blockFrames), blockFrames, "Block size must be positive.");
        if (capacityBlocks < 2) throw new ArgumentOutOfRangeException(nameof(capacityBlocks), capacityBlocks, "Capacity must hold at least two blocks.");
        if ((long)blockFrames * channels > int.MaxValue / sizeof(float))
            throw new ArgumentOutOfRangeException(nameof(blockFrames), "One block is too large to address.");

        FileStream claim;
        try
        {
            // Exclusive share mode is the portable single-writer claim: the OS drops it when
            // the process dies, unlike a named mutex, which stays held after a crash.
            claim = new FileStream(BusLayout.LockPathFor(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new VirtualBusException(
                $"A VirtualBusWriter is already open for '{name}' (one writer per bus). Claim file: {BusLayout.LockPathFor(name)}", ex);
        }

        FileStream? file = null;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            string path = BusLayout.PathFor(name);
            long regionBytes = BusLayout.RegionSize(sampleRate, channels, blockFrames, capacityBlocks);
            long capacity;
            if (OperatingSystem.IsWindows())
            {
                // A named, pagefile-backed Local\ section: a reader in this session opens it by
                // name. Global\ never reaches here; the driver creates that one.
                //
                // CreateOrOpen, not CreateNew: a section outlives the writer that made it while
                // any handle is still open (a reader, or our own crash survivor), and CreateNew
                // fails with ERROR_ALREADY_EXISTS on one. Reattaching is what lets the version
                // probe below run at all, so a foreign version is named instead of surfacing as
                // a raw IOException. Re-initialising the header afterwards is safe because the
                // claim file above is already held: a live same-version writer would have
                // refused it, so these bytes belong to a dead creator, and this is a creator
                // restart rather than a hijack. Attach never takes the claim and never writes a
                // header byte, so it can never be the side re-initialising here.
                capacity = regionBytes;
                map = MemoryMappedFile.CreateOrOpen(BusLayout.MapNameFor(name, scope), capacity);
            }
            else
            {
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                capacity = Math.Max(regionBytes, file.Length);
                map = MemoryMappedFile.CreateFromFile(file, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            }
            view = map.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);

            bool foreignVersion = false;
            int foundVersion = 0;
            byte* probe = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref probe);
            try
            {
                byte* b = probe + view.PointerOffset;
                if (Unsafe.ReadUnaligned<int>(b + BusLayout.MagicOffset) == unchecked((int)BusLayout.Magic))
                {
                    foundVersion = Unsafe.ReadUnaligned<int>(b + BusLayout.VersionOffset);
                    foreignVersion = foundVersion != BusLayout.Version;
                }
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            if (foreignVersion)
            {
                throw new VirtualBusException(
                    $"Bus '{name}' holds protocol version {foundVersion}; this build speaks version {BusLayout.Version}. " +
                    "Refusing to overwrite a region another version owns.");
            }

            return new VirtualBusWriter(name, sampleRate, channels, blockFrames, capacityBlocks, claim, map, view, creator: true);
        }
        catch
        {
            view?.Dispose();
            map?.Dispose();
            file?.Dispose();
            claim.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Publishes one block. <paramref name="block"/> must be exactly
    /// <c>blockFrames * channels</c> interleaved samples.
    /// </summary>
    /// <returns>Number of readers advanced because they were lapped (0 in normal operation).</returns>
    public int Write(ReadOnlySpan<float> block)
    {
        ThrowIfDisposed();
        if (block.Length != _blockSamples)
        {
            throw new ArgumentException(
                $"Block must be {_blockSamples} samples ({BlockFrames} frames x {Channels} channels); got {block.Length}.",
                nameof(block));
        }

        long next = _write + 1;
        long oldestKept = next - _capacity + 1;
        int lapped = 0;

        for (int i = 0; i < BusLayout.ReaderSlotCount; i++)
        {
            long slot = BusLayout.SlotOffset(i);
            if (Volatile.Read(ref IntRef(slot + BusLayout.SlotActive)) != 1)
                continue;

            long read = Volatile.Read(ref LongRef(slot + BusLayout.SlotReadBlockIndex));
            if (read < oldestKept)
            {
                // Drop the oldest: park the reader just before the block being published, so
                // its next Read takes the newest complete block and never replays stale audio.
                Advance(ref LongRef(slot + BusLayout.SlotReadBlockIndex), next - 1);
                Interlocked.Increment(ref LongRef(slot + BusLayout.SlotCorrections));
                lapped++;
            }
        }

        int slotIndex = (int)((next - 1) % _capacity);
        block.CopyTo(MemoryMarshal.Cast<byte, float>(
            new Span<byte>(_base + BusLayout.PayloadOffset + ((long)slotIndex * _blockBytes), _blockBytes)));

        // Release barrier: the payload above is visible before the index moves.
        Volatile.Write(ref WriteIndexRef(), next);
        _write = next;

        if (lapped != 0)
            Interlocked.Add(ref LongRef(BusLayout.OverrunCountOffset), lapped);

        return lapped;
    }

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

    private ref long WriteIndexRef() => ref Unsafe.AsRef<long>(_base + BusLayout.WriteBlockIndexOffset);

    private ref long LongRef(long offset) => ref Unsafe.AsRef<long>(_base + offset);

    private ref int IntRef(long offset) => ref Unsafe.AsRef<int>(_base + offset);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VirtualBusWriter));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        _claim?.Dispose();

        if (!_ownsRegion)
            return; // the creator still owns these bytes; leave them as they are

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A reader still has it open; the next writer re-initialises it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
