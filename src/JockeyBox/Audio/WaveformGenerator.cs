using System.IO;
using NAudio.Wave;

namespace JockeyBox.Audio;

/// <summary>
/// Reads an audio file and produces a downsampled amplitude array suitable
/// for rendering a mini waveform. Peak values are in [0..1].
/// </summary>
public class WaveformGenerator
{
    /// <summary>Number of points the waveform is reduced to.</summary>
    public const int TargetPointCount = 2000;

    /// <summary>
    /// Generate a peak-amplitude waveform from an audio file. Null if the
    /// file can't be read.
    /// </summary>
    public static List<float>? Generate(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            using var reader = new MediaFoundationReader(filePath);
            int bytesPerSample = reader.WaveFormat.BitsPerSample / 8;
            int channels = reader.WaveFormat.Channels;
            int framesPerBucket = Math.Max(1, (int)(reader.Length / bytesPerSample / channels / TargetPointCount));
            var buffer = new byte[framesPerBucket * bytesPerSample * channels];
            var peaks = new List<float>(TargetPointCount);
            float maxPeak = 0.001f;

            while (reader.Position < reader.Length)
            {
                int read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                int bytesRead = read;

                float peak = 0;
                int sampleBytes = bytesPerSample * channels;
                for (int pos = 0; pos + sampleBytes <= bytesRead; pos += sampleBytes)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = pos + ch * bytesPerSample;
                        float sample = 0;
                        if (bytesPerSample == 4) // 32-bit float
                            sample = BitConverter.ToSingle(buffer, offset);
                        else if (bytesPerSample == 2) // 16-bit PCM
                            sample = BitConverter.ToInt16(buffer, offset) / 32768f;
                        else if (bytesPerSample == 1) // 8-bit
                            sample = (buffer[offset] - 128) / 128f;

                        sum += Math.Abs(sample);
                    }
                    peak = Math.Max(peak, sum / channels);
                }
                peaks.Add(peak);
                if (peak > maxPeak) maxPeak = peak;
            }

            // Normalize
            for (int i = 0; i < peaks.Count; i++)
                peaks[i] = Math.Clamp(peaks[i] / maxPeak, 0f, 1f);

            return peaks;
        }
        catch
        {
            return null;
        }
    }
}
