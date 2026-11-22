using System;
using System.Collections.Generic;
using JockeyBox.Audio.Graph;
using JockeyBox.Tools;
using NAudio.Wave;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// One <see cref="AsioOut"/> per driver name, shared by every strip of that interface.
///
/// <para><b>Why sharing is not optional.</b> ASIO is single-client: a second
/// <c>InitRecordAndPlayback</c> on the same driver returns
/// <c>DRVERR_DEVICE_ALREADY_OPEN</c>, so a 4i4 exposing four inputs cannot be opened once per
/// channel. One client opens all channels and every strip fans out from its
/// <c>AudioAvailable</c> event, mapping only its own channel(s) out of the interleaved block.</para>
///
/// <para>The reference count is per driver name: the device is created on the first subscriber
/// and stopped and disposed when the last one releases, so removing the final channel of an
/// interface also gives it back to another application.</para>
/// </summary>
public sealed class AsioDriverRegistry : IDisposable
{
    /// <summary>The strip-key kind for an ASIO source: <c>asio:&lt;driver&gt;#ch&lt;n&gt;</c>.</summary>
    public const string Kind = "asio";

    /// <summary>The process-wide registry: the ASIO client belongs to the process, not a strip.</summary>
    public static AsioDriverRegistry Shared { get; } = new();

    private sealed class Entry
    {
        public Entry(AsioOut device, int sourceChannels, int framesPerBuffer)
        {
            Device = device;
            SourceChannels = sourceChannels;
            FramesPerBuffer = framesPerBuffer;
        }

        public AsioOut Device { get; }
        public int SourceChannels { get; }
        public int FramesPerBuffer { get; }
        public int References { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// Joins the driver's client, opening it on the first subscriber. The caller must subscribe
    /// to the returned <see cref="AsioOut.AudioAvailable"/> and call <see cref="Release"/> when
    /// it lets go.
    /// </summary>
    internal AsioOut Acquire(string driverName, out int sourceChannels, out int framesPerBuffer)
    {
        if (string.IsNullOrWhiteSpace(driverName))
        {
            throw new ArgumentException("an ASIO driver name is required", nameof(driverName));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_entries.TryGetValue(driverName, out Entry? entry))
            {
                entry = Open(driverName);
                _entries.Add(driverName, entry);
            }

            entry.References++;
            sourceChannels = entry.SourceChannels;
            framesPerBuffer = entry.FramesPerBuffer;
            return entry.Device;
        }
    }

    /// <summary>Drops one subscriber; the last one stops and disposes the driver.</summary>
    internal void Release(string driverName)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(driverName, out entry)) return;
            if (--entry.References > 0) return;
            _entries.Remove(driverName);
        }

        // Outside the lock: disposing can block while the driver thread drains.
        try { entry.Device.Stop(); }
        catch (Exception ex) { Log.Warn($"asio: '{driverName}' stop failed ({ex.Message})"); }

        try { entry.Device.Dispose(); }
        catch (Exception ex) { Log.Warn($"asio: '{driverName}' release failed ({ex.Message})"); }
    }

    /// <summary>
    /// The driver's buffer size in frames, or 0 when it is not open. ASIO publishes no input
    /// latency, so this is the figure a strip reports as the device's own delay.
    /// </summary>
    public int FramesPerBuffer(string driverName)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(driverName, out Entry? entry) ? entry.FramesPerBuffer : 0;
        }
    }

    public void Dispose()
    {
        List<KeyValuePair<string, Entry>> open;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            open = new List<KeyValuePair<string, Entry>>(_entries);
            _entries.Clear();
        }

        foreach (var pair in open)
        {
            try { pair.Value.Device.Stop(); } catch { }
            try { pair.Value.Device.Dispose(); } catch { }
        }
    }

    private static Entry Open(string driverName)
    {
        string[] names;
        try
        {
            names = AsioOut.GetDriverNames();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"ASIO is not available on this machine ({ex.Message}).", ex);
        }

        if (Array.IndexOf(names, driverName) < 0)
        {
            string available = names.Length == 0 ? "none installed" : string.Join(", ", names);
            throw new ArgumentException(
                $"ASIO driver '{driverName}' was not found. Installed ASIO drivers: {available}.",
                nameof(driverName));
        }

        AsioOut? device = null;
        try
        {
            // The ctor opens the driver vtable: ASIO is single-client, so this is the call that
            // fails while another application already holds the interface.
            device = new AsioOut(driverName);

            int sourceChannels = device.DriverInputChannelCount;
            if (sourceChannels <= 0)
            {
                throw new InvalidOperationException($"ASIO driver '{driverName}' exposes no input channels.");
            }

            // Capture only: no playback provider, every input channel, the engine's rate.
            device.InitRecordAndPlayback(null, sourceChannels, AudioEngine.Rate);
            device.Play();

            return new Entry(device, sourceChannels, device.FramesPerBuffer);
        }
        catch (Exception ex) when (IsDeviceAlreadyOpen(ex))
        {
            try { device?.Dispose(); } catch { }
            throw new InvalidOperationException(
                $"ASIO driver '{driverName}' is open in another application. ASIO is single-client, " +
                "so close the other application's audio (or its control panel) and try again.", ex);
        }
        catch (Exception ex)
        {
            try { device?.Dispose(); } catch { }
            throw new InvalidOperationException(
                $"ASIO driver '{driverName}' could not be opened at {AudioEngine.Rate} Hz: {ex.Message}", ex);
        }
    }

    private static bool IsDeviceAlreadyOpen(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("DRVERR_DEVICE_ALREADY_OPEN", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("already open", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
