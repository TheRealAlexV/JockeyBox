using System.Runtime.InteropServices;

namespace JockeyBox.Dsp.Tests;

/// <summary>Signal generators and measurements shared by the DSP tests.</summary>
internal static class TestSignals
{
    public static float[] Sine(int length, double frequencyHz, double sampleRate, double amplitude)
    {
        var buffer = new float[length];
        double step = 2 * Math.PI * frequencyHz / sampleRate;
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (float)(amplitude * Math.Sin(step * i));
        }

        return buffer;
    }

    /// <summary>Linear frequency sweep from <paramref name="fromHz"/> to <paramref name="toHz"/>.</summary>
    public static float[] Chirp(int length, double fromHz, double toHz, double sampleRate, double amplitude)
    {
        var buffer = new float[length];
        double phase = 0;
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / length;
            double frequency = fromHz + (toHz - fromHz) * t;
            phase += 2 * Math.PI * frequency / sampleRate;
            buffer[i] = (float)(amplitude * Math.Sin(phase));
        }

        return buffer;
    }

    public static float[] WhiteNoise(int length, int seed)
    {
        var buffer = new float[length];
        var random = new Random(seed);
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return buffer;
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        return Math.Sqrt(sum / samples.Length);
    }

    public static double RmsDb(ReadOnlySpan<float> samples) => Db(Rms(samples));

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    /// <summary>
    /// Gain in dB a processor applies to a steady sine, measured over the second half of the
    /// buffer so the ramp and the filter transient are both settled.
    /// </summary>
    public static double SteadyStateGainDb(IAudioProcessor processor, double frequencyHz, double sampleRate, int length)
    {
        var input = Sine(length, frequencyHz, sampleRate, 0.25);
        var output = (float[])input.Clone();
        processor.Process(output);
        int start = length / 2;
        return RmsDb(output.AsSpan(start)) - RmsDb(input.AsSpan(start));
    }

    /// <summary>Compares the raw bytes, so a bit-exact claim really is bit-exact.</summary>
    public static void AssertBitExact(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string because)
    {
        if (expected.Length != actual.Length)
        {
            throw new Xunit.Sdk.XunitException($"{because}: length {actual.Length} != {expected.Length}");
        }

        Assert.True(
            MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)),
            because);
    }

    /// <summary>Finds the peak index of |samples|; ties resolve to the first index.</summary>
    public static int IndexOfPeak(ReadOnlySpan<float> samples)
    {
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Abs(samples[i]);
            if (value > bestValue)
            {
                bestValue = value;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Lag in frames at which <paramref name="reference"/> best matches <paramref name="other"/>,
    /// searching +/- maxLag. Positive lag means other is delayed relative to reference.
    /// </summary>
    public static int BestLag(ReadOnlySpan<float> reference, ReadOnlySpan<float> other, int maxLag)
    {
        int bestLag = 0;
        double bestScore = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            int start = Math.Max(0, -lag);
            int end = Math.Min(reference.Length, other.Length - lag);
            double sum = 0;
            for (int n = start; n < end; n++)
            {
                sum += (double)reference[n] * other[n + lag];
            }

            if (sum > bestScore)
            {
                bestScore = sum;
                bestLag = lag;
            }
        }

        return bestLag;
    }
}
