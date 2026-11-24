using System;
using System.Collections.Generic;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Audio.Virtual;

/// <summary>
/// One destination channel of the virtual device and where its samples come from:
/// a channel of one render bus. Built once on the control thread, never on the
/// render thread; <see cref="Gain"/> already includes the line's own gain and mute.
/// </summary>
public readonly struct VirtualTap
{
    public VirtualTap(BusKind bus, int sourceChannel, int destinationChannel, float gain)
    {
        Bus = bus;
        SourceChannel = sourceChannel;
        DestinationChannel = destinationChannel;
        Gain = gain;
    }

    /// <summary>Which render bus carries the signal.</summary>
    public BusKind Bus { get; }

    /// <summary>Channel inside that bus (0 = left, 1 = right).</summary>
    public int SourceChannel { get; }

    /// <summary>Interleaved channel of the ring this tap feeds.</summary>
    public int DestinationChannel { get; }

    /// <summary>Linear gain, mute folded in as 0.</summary>
    public float Gain { get; }
}

/// <summary>
/// The precomputed publish plan for the virtual device: <c>Channels</c> interleaved
/// ring channels, each fed by zero or more <see cref="VirtualTap"/>s.
///
/// <para><b>Single source of truth.</b> The width comes from
/// <see cref="VirtualDeviceConfig.ChannelCount"/> - the same clamp the native driver
/// applies to the same file - so the ring, the header and the driver can never
/// disagree about how wide the device is. An empty line list is the legacy stereo
/// pair, which is what an install that never configured lines must keep doing.</para>
///
/// <para>Immutable after construction and swapped by reference, so the render thread
/// only ever needs one volatile read.</para>
/// </summary>
public sealed class VirtualTapTable
{
    /// <summary>Width of a legacy (and of every) destination bus: the buses are stereo.</summary>
    public const int SourceChannels = 2;

    private readonly VirtualTap[] _taps;

    private VirtualTapTable(int channels, VirtualTap[] taps, bool isLegacy)
    {
        Channels = channels;
        _taps = taps;
        IsLegacy = isLegacy;
    }

    /// <summary>Interleaved channels the ring carries.</summary>
    public int Channels { get; }

    /// <summary>True for the untouched-install stereo pair.</summary>
    public bool IsLegacy { get; }

    /// <summary>The taps in build order (render thread: use <see cref="TapArray"/>).</summary>
    public IReadOnlyList<VirtualTap> Taps => _taps;

    /// <summary>The raw array, so the render loop does not pay for an interface call.</summary>
    public VirtualTap[] TapArray => _taps;

    /// <summary>
    /// The untouched-install plan: the one flagged bus, straight through as a stereo
    /// pair. Exactly what the app published before lines existed.
    /// </summary>
    public static VirtualTapTable Legacy(BusKind bus) => new(
        VirtualDeviceConfig.MinChannels,
        new[]
        {
            new VirtualTap(bus, 0, 0, 1f),
            new VirtualTap(bus, 1, 1, 1f),
        },
        isLegacy: true);

    /// <summary>
    /// Build the plan from the ordered line list. Destination channels are assigned
    /// in line order and stop at the device width, so a list that overflows the clamp
    /// truncates instead of shifting everything after it.
    /// </summary>
    /// <param name="lines">Ordered lines; empty is invalid here (use <see cref="Legacy"/>).</param>
    /// <param name="onUnknownSource">Called once per unresolvable source, on this thread.</param>
    public static VirtualTapTable FromLines(IReadOnlyList<VirtualLine> lines,
        Action<string>? onUnknownSource = null)
    {
        var channels = VirtualDeviceConfig.ChannelCount(lines);
        var taps = new List<VirtualTap>(channels);
        var destination = 0;

        for (var i = 0; i < lines.Count && destination < channels; i++)
        {
            var line = lines[i];
            var width = line.Channels;

            // A muted line still owns its channels; it just contributes nothing, so
            // the channels after it keep their positions.
            if (!line.Mute && ResolveSource(line.Source, onUnknownSource) is { } source)
            {
                var gain = NormalizeGain(line.Gain);
                var first = destination;
                for (var c = 0; c < width && first + c < channels; c++)
                {
                    var sourceChannel = Math.Min(source.Channel + c, SourceChannels - 1);
                    taps.Add(new VirtualTap(source.Bus, sourceChannel, first + c, gain));
                }
            }

            destination += width;
        }

        return new VirtualTapTable(channels, taps.ToArray(), isLegacy: false);
    }

    /// <summary>
    /// Resolve a line's source binding to a bus and a starting channel.
    ///
    /// <para>The vocabulary is the graph's, not the line table's:
    /// <c>deck:a</c>/<c>deck:b</c> are the deck buses, <c>mic:N</c> is channel N-1 of
    /// the recording (microphone sum) bus, <c>soundboard</c>, <c>monitor</c>/<c>cue</c>
    /// and <c>recording</c> are their buses, and <c>bus:&lt;name&gt;</c> names a bus
    /// directly. Case and <c>-</c>/<c>_</c> separators are ignored.</para>
    ///
    /// <para>An unknown source is silence plus one log line - never a guess. The
    /// committed fixture's third stereo line is <c>deck:c</c>, and this graph has no
    /// third deck, so that pair is silent by design rather than duplicated.</para>
    /// </summary>
    public static (BusKind Bus, int Channel)? ResolveSource(string? source,
        Action<string>? onUnknown = null)
    {
        var key = (source ?? "").Trim().ToLowerInvariant().Replace('-', ':');
        if (key.Length == 0) return null;

        if (key.StartsWith("bus:", StringComparison.Ordinal))
            key = key.Substring(4);

        switch (key)
        {
            case "deck:a" or "decka": return (BusKind.DeckA, 0);
            case "deck:b" or "deckb": return (BusKind.DeckB, 0);
            case "soundboard": return (BusKind.Soundboard, 0);
            case "monitor" or "cue": return (BusKind.Monitor, 0);
            case "recording" or "mic": return (BusKind.Recording, 0);
        }

        if (key.StartsWith("mic:", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(4), out var mic) && mic >= 1)
            return (BusKind.Recording, Math.Min(mic - 1, SourceChannels - 1));

        onUnknown?.Invoke(source ?? "");
        return null;
    }

    private static float NormalizeGain(double gain) =>
        double.IsFinite(gain) ? Math.Clamp((float)gain, 0f, 8f) : 1f;
}
