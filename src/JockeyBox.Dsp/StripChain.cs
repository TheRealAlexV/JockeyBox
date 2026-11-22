namespace JockeyBox.Dsp;

/// <summary>
/// An ordered chain of per-channel processors.
/// <para>
/// Hard rule: an empty chain, or a chain whose every processor is bypassed, leaves the
/// buffer bit-identical and makes no copy.
/// </para>
/// </summary>
public sealed class StripChain
{
    private readonly List<IAudioProcessor> _processors = new();

    /// <summary>The processors in processing order.</summary>
    public IReadOnlyList<IAudioProcessor> Processors => _processors;

    /// <summary>Sum of <see cref="IAudioProcessor.LatencyFrames"/> over the active processors.</summary>
    public int TotalLatencyFrames
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _processors.Count; i++)
            {
                if (!_processors[i].Bypass) total += _processors[i].LatencyFrames;
            }

            return total;
        }
    }

    /// <summary>True when the whole chain would leave the buffer untouched.</summary>
    public bool IsPassthrough
    {
        get
        {
            for (int i = 0; i < _processors.Count; i++)
            {
                if (!_processors[i].Bypass) return false;
            }

            return true;
        }
    }

    public StripChain Add(IAudioProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processors.Add(processor);
        return this;
    }

    public void Process(Span<float> samples)
    {
        // Nothing active: do not touch the buffer, do not copy it.
        if (IsPassthrough) return;

        for (int i = 0; i < _processors.Count; i++)
        {
            IAudioProcessor processor = _processors[i];
            if (processor.Bypass) continue;
            processor.Process(samples);
        }
    }

    public void Reset()
    {
        for (int i = 0; i < _processors.Count; i++)
        {
            _processors[i].Reset();
        }
    }
}
