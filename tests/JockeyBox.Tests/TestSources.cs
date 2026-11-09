using NAudio.Wave;

namespace JockeyBox.Tests;

/// <summary>
/// Sample providers used only by the offline graph tests. They never touch a
/// device, they are deterministic, and they can reproduce the two shapes the
/// engine has to survive: a provider that returns short reads, and one that is
/// simply exhausted (returns 0, which NAudio treats as end-of-stream).
/// </summary>
internal static class TestSources
{
    public const int Rate = 48_000;

    public static WaveFormat Stereo { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

    /// <summary>Hands out <c>data</c>, one short read at a time, then 0 forever.</summary>
    internal sealed class Finite : ISampleProvider
    {
        private readonly float[] _data;
        private readonly int _maxPerRead;
        private int _pos;

        public Finite(float[] data, WaveFormat? format = null, int maxPerRead = int.MaxValue)
        {
            _data = data;
            _maxPerRead = Math.Max(1, maxPerRead);
            WaveFormat = format ?? Stereo;
        }

        public WaveFormat WaveFormat { get; }

        public int Reads { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            Reads++;
            int n = Math.Min(Math.Min(count, _maxPerRead), _data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    /// <summary>Produces a constant value forever (an "endless deck").</summary>
    internal sealed class Endless : ISampleProvider
    {
        private readonly float _value;

        public Endless(float value, WaveFormat? format = null)
        {
            _value = value;
            WaveFormat = format ?? Stereo;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, _value, offset, count);
            return count;
        }
    }
}
