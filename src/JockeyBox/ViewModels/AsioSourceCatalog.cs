using System;
using System.Collections.Generic;
using JockeyBox.Audio.Endpoints;
using NAudio.Wave;

namespace JockeyBox.ViewModels;

/// <summary>What a driver answered when the picker asked about it.</summary>
public enum AsioDriverState
{
    /// <summary>Opened or joined: the channel count and names below are the driver's own.</summary>
    Ready,

    /// <summary>
    /// Could not be opened because ASIO is single-client and something holds it. A normal
    /// state, not an error: the running app's own strips show up here too.
    /// </summary>
    InUse,

    /// <summary>Present in the registry but it reports nothing usable (0 inputs, or a failure).</summary>
    Unavailable,
}

/// <summary>
/// One ASIO driver as the source picker needs it: how many inputs it reports, what it calls
/// them, and whether it could be read at all.
///
/// <para>Channel names come from the driver (<see cref="AsioOut.AsioInputChannelName"/>), so a
/// 4i4 that reports six inputs reads as "Input 1"…"Input 4", "Loopback 1", "Loopback 2". A
/// driver that reports no names falls back to a numbered label; index-only is never the default
/// when the driver did answer.</para>
/// </summary>
public sealed record AsioDriverInfo(
    string Name,
    int Channels,
    IReadOnlyList<string> ChannelNames,
    AsioDriverState State,
    string? Detail)
{
    public bool Ready => State == AsioDriverState.Ready;

    /// <summary>The driver's own name for an input, or "Input N" when it gave none.</summary>
    public string ChannelLabel(int offset)
    {
        if (offset >= 0 && offset < ChannelNames.Count)
        {
            string named = ChannelNames[offset];
            if (!string.IsNullOrWhiteSpace(named)) return named;
        }

        return $"Input {offset + 1}";
    }
}

/// <summary>
/// Reads the installed ASIO drivers and, for each, its input count and per-input names.
///
/// <para>Reading the count and names needs the driver's vtable, and ASIO allows one client, so
/// the probe either opens the driver for the instant it takes to ask and disposes it, or joins
/// this process's own ref-counted client when a strip already holds it. A driver another
/// application holds fails to open: that is <see cref="AsioDriverState.InUse"/>, reported as a
/// state to the operator, not thrown.</para>
/// </summary>
public static class AsioSourceCatalog
{
    /// <summary>
    /// The drivers installed on this machine. An empty list means "no ASIO driver on this
    /// machine", which the picker must say in words; it never throws for that reason.
    /// </summary>
    public static IReadOnlyList<AsioDriverInfo> Probe()
    {
        string[] names;
        try
        {
            names = AsioOut.GetDriverNames();
        }
        catch
        {
            // ASIO itself unavailable (no driver component, non-Windows, registry unreadable).
            return Array.Empty<AsioDriverInfo>();
        }

        var drivers = new List<AsioDriverInfo>(names.Length);
        foreach (string name in names)
        {
            if (!string.IsNullOrWhiteSpace(name)) drivers.Add(ProbeDriver(name));
        }

        return drivers;
    }

    private static AsioDriverInfo ProbeDriver(string name)
    {
        // Already open by this process (an enabled strip): join the shared client so the probe
        // opens no second client and does not interrupt the running capture.
        try
        {
            if (AsioDriverRegistry.Shared.FramesPerBuffer(name) > 0)
            {
                AsioOut device = AsioDriverRegistry.Shared.Acquire(name, out int channels, out _);
                try
                {
                    return channels <= 0
                        ? new AsioDriverInfo(name, 0, Array.Empty<string>(), AsioDriverState.Unavailable,
                            "the open driver reports no inputs")
                        : new AsioDriverInfo(name, channels, ReadNames(device, channels), AsioDriverState.Ready,
                            "in use by JockeyBox");
                }
                finally
                {
                    AsioDriverRegistry.Shared.Release(name);
                }
            }
        }
        catch
        {
            // Fall through to a fresh probe; the join is an optimisation, not a requirement.
        }

        AsioOut? probe = null;
        try
        {
            // The constructor loads the driver and builds its capabilities without starting
            // playback, so this is a read of what the driver reports, not a capture.
            probe = new AsioOut(name);
            int channels = probe.DriverInputChannelCount;
            if (channels <= 0)
            {
                return new AsioDriverInfo(name, 0, Array.Empty<string>(), AsioDriverState.Unavailable,
                    "the driver reports no inputs");
            }

            return new AsioDriverInfo(name, channels, ReadNames(probe, channels), AsioDriverState.Ready, null);
        }
        catch (Exception ex)
        {
            // Single client: this is the ordinary "something else holds the interface" state.
            return new AsioDriverInfo(name, 0, Array.Empty<string>(), AsioDriverState.InUse, Shorten(ex));
        }
        finally
        {
            try { probe?.Dispose(); } catch { /* the probe's disposal must not mask the answer */ }
        }
    }

    private static IReadOnlyList<string> ReadNames(AsioOut device, int channels)
    {
        var names = new string[channels];
        for (int i = 0; i < channels; i++)
        {
            string? name = null;
            try { name = device.AsioInputChannelName(i); } catch { /* index-only fallback below */ }
            names[i] = name ?? "";
        }

        return names;
    }

    private static string Shorten(Exception ex)
    {
        string message = ex.Message?.Trim() ?? "";
        if (message.Length == 0) return "could not be opened";
        return message.Length <= 120 ? message : message[..120];
    }
}
