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

/// <summary>
/// The alignment delay for one interleaved channel: a ring the render path reads and writes,
/// holding up to <see cref="MaxFrames"/> frames of padding.
/// <para>
/// Capacity is not fixed. <see cref="SetPadding"/> sizes the ring from the pad it is asked to
/// apply, and that call runs on the settings/route path, never per block. <see cref="Process"/>
/// only reads and writes the array it already has, so the render path never allocates. A pad
/// change ramps over <see cref="FadeFrames"/> frames, so a device slides instead of stepping.
/// </para>
/// <para>
/// Past <see cref="MaxFrames"/> the pad is clamped and <see cref="Clamped"/> stays raised. That
/// flag is the point: two sources further apart than the ceiling cannot be aligned, and the
/// mix must say so rather than silently comb-filter.
/// </para>
/// </summary>
public sealed class PadRing
{
    /// <summary>Hard ceiling on alignment padding: 20 ms at 48 kHz.</summary>
    public const int MaxFrames = 960;

    /// <summary>A ramp covers 480 frames, so a pad change slides over 10 ms at 48 kHz.</summary>
    private const int FadeFrames = 480;

    /// <summary>Headroom above the largest pad, so a small change needs no regrow.</summary>
    private const int MarginFrames = 64;

    /// <summary>Floor for the ring: the single-limiter case (240 frames) never regrows.</summary>
    private const int MinFrames = 240;

    private readonly int _channels;
    private float[] _ring;
    private int _write;
    private int _target;
    private float _current;
    private float _step;

    public PadRing(int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _ring = new float[MinFrames * channels];
    }

    /// <summary>True when this ring delays at all; false means the buffer passes through untouched.</summary>
    public bool Active => _target > 0 || _current > 0f;

    /// <summary>The pad the ring is applying or ramping towards, in frames.</summary>
    public int TargetFrames => _target;

    /// <summary>Frames the ring can currently hold, without growing.</summary>
    public int CapacityFrames => _ring.Length / _channels;

    /// <summary>
    /// True once a requested pad exceeded <see cref="MaxFrames"/> and was clamped. Read by the
    /// diagnostics window: the mix is knowingly misaligned, never silently so.
    /// </summary>
    public bool Clamped { get; private set; }

    /// <summary>
    /// Sets the pad, growing the ring to hold it. Call this from the settings or route path:
    /// it may allocate. Above <see cref="MaxFrames"/> the pad is clamped and
    /// <see cref="Clamped"/> is raised, so the caller can log it.
    /// </summary>
    public void SetPadding(int frames)
    {
        if (frames > MaxFrames)
        {
            frames = MaxFrames;
            Clamped = true;
        }

        if (frames < 0) frames = 0;
        if (frames > CapacityFrames) Grow(frames);
        if (frames == _target) return;

        _target = frames;
        _step = (frames - _current) / FadeFrames;
    }

    /// <summary>
    /// Applies the pad to one interleaved block, in place. Reads the tap at the ramp's floor and
    /// ceiling so a pad change crossfades instead of clicking. Allocates nothing.
    /// </summary>
    public void Process(float[] buffer, int offset, int channels, int frames)
    {
        var ring = _ring;
        int ringFrames = ring.Length / _channels;
        float current = _current;
        float step = _step;
        int write = _write;

        for (int frame = 0; frame < frames; frame++)
        {
            int d0 = (int)current;
            float frac = current - d0;
            int d1 = frac > 0f ? d0 + 1 : d0;

            int baseIndex = offset + frame * channels;
            for (int c = 0; c < _channels; c++)
            {
                int bi = baseIndex + c;
                float input = buffer[bi];

                float s0;
                if (d0 == 0)
                {
                    s0 = input;
                }
                else
                {
                    int i0 = write - d0;
                    if (i0 < 0) i0 += ringFrames;
                    s0 = ring[i0 * _channels + c];
                }

                float outSample;
                if (d1 == d0 || d1 > ringFrames)
                {
                    outSample = s0;
                }
                else
                {
                    int i1 = write - d1;
                    if (i1 < 0) i1 += ringFrames;
                    outSample = s0 + (ring[i1 * _channels + c] - s0) * frac;
                }

                ring[write * _channels + c] = input;
                buffer[bi] = outSample;
            }

            write++;
            if (write == ringFrames) write = 0;

            if (step > 0f)
            {
                current += step;
                if (current >= _target) { current = _target; step = 0f; }
            }
            else if (step < 0f)
            {
                current += step;
                if (current <= _target) { current = _target; step = 0f; }
            }
        }

        _write = write;
        _current = current;
        _step = step;
    }

    /// <summary>
    /// Re-lays the ring out at a larger size. The sample written <c>d</c> frames ago is moved to
    /// the slot that is <c>d</c> frames behind the new write point, so a running pad keeps
    /// reading real audio through the swap instead of a hole of zeros.
    /// </summary>
    private void Grow(int frames)
    {
        int size = Math.Min(MaxFrames, Math.Max(frames + MarginFrames, CapacityFrames * 2));
        var next = new float[size * _channels];

        int oldSize = CapacityFrames;
        int keep = Math.Min(oldSize, size - 1);
        for (int d = 1; d <= keep; d++)
        {
            int from = _write - d;
            if (from < 0) from += oldSize;
            int to = size - d;
            for (int c = 0; c < _channels; c++)
                next[to * _channels + c] = _ring[from * _channels + c];
        }

        _ring = next;
        _write = 0;
    }
}
