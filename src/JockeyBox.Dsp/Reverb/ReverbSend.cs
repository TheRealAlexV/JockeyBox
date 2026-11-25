namespace JockeyBox.Dsp.Reverb;

/// <summary>
/// Per-device reverb send. Owns two independent <see cref="Freeverb"/> chains, one per side,
/// with the stereo spread baked into the right chain's delay lengths, so two devices in a duet
/// never share a tail.
/// <para>
/// Stereo in, stereo out. The dry signal is only read, never modified, and the send adds no
/// latency, so the caller sums the wet return downstream and can skip that sum entirely when
/// <see cref="Active"/> is false.
/// </para>
/// <para>
/// The wet return is high-passed at 200 Hz on both sides so low end from the send cannot muddy
/// the mix. Every buffer this class needs is allocated in the constructor; <see cref="Process"/>
/// only touches existing state.
/// </para>
/// </summary>
public sealed class ReverbSend
{
    /// <summary>Corner of the wet high-pass. Low end stays out of the reverb return.</summary>
    public const double WetHighPassHz = 200;

    private readonly double _sampleRate;
    private readonly Freeverb _left;
    private readonly Freeverb _right;
    private readonly Biquad _wetHighPassLeft = new();
    private readonly Biquad _wetHighPassRight = new();

    private ReverbParameters _parameters;

    public ReverbSend(double sampleRate, ReverbParameters? parameters = null, string id = "reverb.freeverb")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Id = id;
        _sampleRate = sampleRate;

        _left = new Freeverb(sampleRate);
        _right = new Freeverb(sampleRate, Freeverb.StereoSpreadFrames);

        _wetHighPassLeft.SetHighPass(sampleRate, WetHighPassHz);
        _wetHighPassRight.SetHighPass(sampleRate, WetHighPassHz);

        SetParameters(parameters ?? ReverbParameters.Neutral);
    }

    /// <summary>Stable identifier, e.g. "reverb.freeverb".</summary>
    public string Id { get; }

    /// <summary>The send is a parallel return, not a chain stage, so it delays nothing.</summary>
    public int LatencyFrames => 0;

    /// <summary>True while <see cref="Process"/> actually works. False means it writes silence.</summary>
    public bool Active => !_parameters.IsNeutral;

    /// <summary>Inverse of <see cref="Active"/>, for callers that prefer the negative test.</summary>
    public bool IsNeutral => _parameters.IsNeutral;

    /// <summary>Current clamped parameters.</summary>
    public ReverbParameters Parameters => _parameters;

    /// <summary>Applies parameters. Every field is clamped, so <see cref="ReverbParameters.Wet"/> cannot exceed its cap.</summary>
    public void SetParameters(ReverbParameters parameters)
    {
        bool wasActive = Active;

        _parameters = parameters.Clamp();

        double size = _parameters.Size;
        _left.SetRoomSize(size);
        _right.SetRoomSize(size);

        // Clearing both chains is ~110 KB of memset and the UI writes on every slider tick.
        // Only a change of state needs it: going idle must drop the tail so a later enable
        // cannot dump old audio, and going live starts from silence. A tweak while the send is
        // already running keeps the tail, which is what dragging the size knob expects.
        if (wasActive != Active) Reset();
    }

    /// <summary>
    /// Reads the dry interleaved buffer and fills the wet interleaved buffer with
    /// <c>frames * 2</c> samples. When the send is neutral the wet buffer is cleared and the
    /// delay lines are not touched at all.
    /// </summary>
    public void Process(ReadOnlySpan<float> dryInterleaved, Span<float> wetInterleaved, int frames)
    {
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames));

        int samples = frames * 2;
        if (dryInterleaved.Length < samples || wetInterleaved.Length < samples)
        {
            throw new ArgumentException($"Buffers must hold {samples} interleaved samples.", nameof(wetInterleaved));
        }

        if (!Active)
        {
            wetInterleaved[..samples].Clear();
            return;
        }

        float wet = (float)_parameters.Wet;

        for (int i = 0; i < samples; i += 2)
        {
            float left = _wetHighPassLeft.ProcessSample(_left.ProcessSample(dryInterleaved[i])) * wet;
            float right = _wetHighPassRight.ProcessSample(_right.ProcessSample(dryInterleaved[i + 1])) * wet;
            wetInterleaved[i] = left;
            wetInterleaved[i + 1] = right;
        }
    }

    /// <summary>Clears both chains and both high-passes. Called when the send changes activity state.</summary>
    public void Reset()
    {
        _left.Reset();
        _right.Reset();
        _wetHighPassLeft.Reset();
        _wetHighPassRight.Reset();
    }
}
