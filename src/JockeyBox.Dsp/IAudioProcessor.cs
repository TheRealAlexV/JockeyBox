namespace JockeyBox.Dsp;

/// <summary>
/// A mono, per-channel audio processor.
/// <para>
/// <see cref="Process"/> receives a single channel: <c>samples.Length</c> is the frame
/// count for that one channel, not an interleaved frame count. Callers de-interleave
/// before calling and re-interleave afterwards.
/// </para>
/// </summary>
public interface IAudioProcessor
{
    /// <summary>Stable identifier, e.g. "eq.tenband".</summary>
    string Id { get; }

    /// <summary>Delay this processor adds, in frames, while it is active.</summary>
    int LatencyFrames { get; }

    /// <summary>When true, <see cref="Process"/> must leave the buffer untouched.</summary>
    bool Bypass { get; set; }

    /// <summary>Processes one channel in place.</summary>
    void Process(Span<float> samples);

    /// <summary>Clears filter and envelope state.</summary>
    void Reset();
}
