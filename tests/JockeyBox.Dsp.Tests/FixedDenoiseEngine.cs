using JockeyBox.Dsp.RnNoise;

namespace JockeyBox.Dsp.Tests;

/// <summary>
/// A denoise engine with a fixed, declared latency, so wiring tests can exercise the stage's
/// latency without the Windows DLL. It is not a denoiser: it returns the frame untouched, which
/// is enough for a test that only asks what the strip reports.
/// </summary>
internal sealed class FixedDenoiseEngine : IDenoiseFrameProcessor
{
    public FixedDenoiseEngine(int frameSize, int latencySamples)
    {
        FrameSize = frameSize;
        LatencySamples = latencySamples;
    }

    public int FrameSize { get; }
    public int LatencySamples { get; }
    public float ProcessFrame(float[] frame) => 1f;
    public void Dispose()
    {
    }
}
