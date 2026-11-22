namespace JockeyBox.Dsp;

/// <summary>Broadband gain in dB. Ramped over <c>rampFrames</c> frames (64 by default).</summary>
public sealed class GainStage : IAudioProcessor
{
    private readonly int _rampFrames;
    private Smoother _gain = new(1);
    private double _gainDb;

    public GainStage(string id = "gain", int rampFrames = 64)
    {
        Id = id;
        _rampFrames = Math.Max(0, rampFrames);
    }

    public string Id { get; }

    public int LatencyFrames => 0;

    public bool Bypass { get; set; }

    /// <summary>Target gain in dB, clamped to a sane range.</summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = Math.Clamp(value, -120, 24);
            _gain.SetTarget(Math.Pow(10, _gainDb / 20.0), _rampFrames);
        }
    }

    public void Process(Span<float> samples)
    {
        if (Bypass) return;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(samples[i] * _gain.Next());
        }
    }

    public void Reset() => _gain.Snap();
}
