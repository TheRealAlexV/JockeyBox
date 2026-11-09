using System;
using System.IO;
using NAudio.Wave;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Selftest;

/// <summary>
/// The hermetic programme material for the self-test: every sample is generated
/// here, and no fixture file, no device and no network is involved.
///
/// <para><b>Why a temporary WAV.</b> The render path only ever sees an
/// <see cref="ISampleProvider"/>, but a deck's only public load entry point is
/// <see cref="DeckSource.LoadAsync"/>, which opens a path through
/// <see cref="StemReader"/> and Media Foundation. The samples are therefore
/// synthesised in memory and written to a 16-bit WAV in the system temp
/// directory (48 kHz stereo, the engine's own format, so no resampling is
/// inserted into the measurement). The bytes are identical on every run, the
/// directory is per-process and deleted afterwards, and nothing outside it is
/// read — which is what makes <c>--selftest</c> runnable on CI and from
/// WSL-built binaries on the Windows host.</para>
///
/// <para><b>Why 16-bit is enough.</b> The measuring tolerance is 1e-3 in gain
/// and 1.5 % in frequency; 16-bit quantisation is ~3e-5 in amplitude and adds
/// no frequency error, so it is three orders of magnitude below every
/// assertion.</para>
/// </summary>
public static class Tones
{
    /// <summary>Frames for a duration at the engine rate (rounded).</summary>
    public static int Frames(double seconds) => Math.Max(1, (int)Math.Round(seconds * AudioEngine.Rate));

    /// <summary>
    /// A sine on both channels of <paramref name="seconds"/> length, zero phase at
    /// frame 0. Amplitude is linear (1.0 = 0 dBFS), the caller converts from dB.
    /// </summary>
    public static float[] Sine(double hertz, double seconds, double amplitude) =>
        WithBursts(seconds, amplitude, (0.0, seconds, hertz));

    /// <summary>Silence of a given length.</summary>
    public static float[] Silence(double seconds) => new float[Frames(seconds) * AudioEngine.Channels];

    /// <summary>
    /// Silence with tone bursts at media offsets. Used for tempo and hand-off
    /// cases, where the <i>distance</i> between two bursts in the output is the
    /// measurement (it cancels the stretcher's constant group delay).
    /// </summary>
    public static float[] WithBursts(double seconds, double amplitude,
        params (double At, double Length, double Hertz)[] bursts)
    {
        var data = new float[Frames(seconds) * AudioEngine.Channels];

        foreach (var (at, length, hertz) in bursts)
        {
            int first = (int)Math.Round(at * AudioEngine.Rate);
            int count = (int)Math.Round(length * AudioEngine.Rate);
            double step = 2.0 * Math.PI * hertz / AudioEngine.Rate;

            for (int f = 0; f < count; f++)
            {
                int frame = first + f;
                if (frame < 0 || frame >= data.Length / AudioEngine.Channels) continue;
                float value = (float)(Math.Sin(step * f) * amplitude);
                for (int c = 0; c < AudioEngine.Channels; c++)
                    data[frame * AudioEngine.Channels + c] = value;
            }
        }

        return data;
    }

    /// <summary>
    /// A sample-exact impulse: one 0.5 frame at <paramref name="atSeconds"/> in an
    /// otherwise silent buffer. Its position in the captured programme names the
    /// exact frame the delay path placed it on.
    /// </summary>
    public static float[] Impulse(double seconds, double atSeconds)
    {
        var data = Silence(seconds);
        int frame = (int)Math.Round(atSeconds * AudioEngine.Rate);
        if (frame >= 0 && frame < data.Length / AudioEngine.Channels)
            for (int c = 0; c < AudioEngine.Channels; c++)
                data[frame * AudioEngine.Channels + c] = 0.5f;
        return data;
    }

    /// <summary>Write interleaved float samples as a canonical 16-bit PCM WAV.</summary>
    public static void WriteWave(string path, float[] interleaved)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        WaveFileWriter.CreateWaveFile16(path, new BufferProvider(interleaved));
    }

    private sealed class BufferProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _position;

        public BufferProvider(float[] data) => _data = data;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.Rate, AudioEngine.Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _data.Length - _position;
            int n = Math.Min(count, available);
            if (n <= 0) return 0;
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
