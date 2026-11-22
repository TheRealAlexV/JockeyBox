namespace JockeyBox.Dsp;

/// <summary>Per-channel latency figures, in frames, at the current sample rate.</summary>
public sealed record ChannelLatency(string ChannelId, int ChainFrames, int DeviceFrames, int UserOffsetFrames)
{
    /// <summary>Total latency this channel carries: chain + device + user offset.</summary>
    public int TotalFrames => ChainFrames + DeviceFrames + UserOffsetFrames;
}

/// <summary>
/// Tracks latency per channel and says how many frames of padding each channel needs so
/// every channel arrives at the same instant.
/// <para>
/// Sign convention: latency is always a positive number of frames that must be ADDED to a
/// channel. <see cref="PaddingFor"/> is never negative and there is no "negative offset"
/// path, so a channel can only be delayed, never advanced. Compensation therefore happens
/// by padding the fastest channels up to <see cref="MaxLatencyFrames"/>, which keeps every
/// stream causal.
/// </para>
/// </summary>
public sealed class LatencyLedger
{
    private readonly List<ChannelLatency> _entries = new();
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);

    /// <summary>Every channel entry, in registration order.</summary>
    public IReadOnlyList<ChannelLatency> Entries => _entries;

    /// <summary>Highest total latency in frames across all channels; 0 when empty.</summary>
    public int MaxLatencyFrames
    {
        get
        {
            int max = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                int total = _entries[i].TotalFrames;
                if (total > max) max = total;
            }

            return max;
        }
    }

    /// <summary>Registers or replaces a channel's latency figures. Values must be non-negative.</summary>
    public void Set(string channelId, int chainFrames, int deviceFrames, int userOffsetFrames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        if (chainFrames < 0 || deviceFrames < 0 || userOffsetFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chainFrames), "Latency frames must not be negative.");
        }

        var entry = new ChannelLatency(channelId, chainFrames, deviceFrames, userOffsetFrames);
        if (_indexById.TryGetValue(channelId, out int existing))
        {
            _entries[existing] = entry;
            return;
        }

        _indexById[channelId] = _entries.Count;
        _entries.Add(entry);
    }

    /// <summary>
    /// Frames to prepend to this channel so it lines up with <see cref="MaxLatencyFrames"/>.
    /// Always zero or positive; padding is added as leading silence, never subtracted.
    /// </summary>
    public int PaddingFor(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        if (!_indexById.TryGetValue(channelId, out int index))
        {
            throw new KeyNotFoundException($"No latency entry registered for channel '{channelId}'.");
        }

        return MaxLatencyFrames - _entries[index].TotalFrames;
    }

    public bool TryGet(string channelId, out ChannelLatency? entry)
    {
        if (_indexById.TryGetValue(channelId, out int index))
        {
            entry = _entries[index];
            return true;
        }

        entry = null;
        return false;
    }
}
