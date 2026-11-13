using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Dsp;
using NAudio.Wave;

namespace JockeyBox.Audio;

/// <summary>
/// Writes the engine's post-fan-out program mix to a 16-bit stereo WAV and then
/// measures that file.
///
/// <para><b>Why a file.</b> Every number reported so far is an in-process peak, and
/// an in-process peak cannot be heard. A file the operator can open in any player
/// is the one piece of evidence that does not depend on this program's own opinion
/// of what it is producing: if the WAV is audible, the rendered content is right
/// and the fault is below the audio engine; if it is silent, the content is broken
/// and the search is upstream of WASAPI.</para>
/// </summary>
public sealed class WavDump : IDisposable
{
    private readonly WaveFileWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public WavDump(string path, int sampleRate = 48000, int channels = 2)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _writer = new WaveFileWriter(Path, new WaveFormat(sampleRate, 16, channels));
        SampleRate = sampleRate;
        Channels = channels;
    }

    public string Path { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long FramesWritten { get; private set; }

    /// <summary>Engine callback: one interleaved float block.</summary>
    public void Write(float[] buffer, int frames)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var pcm = new byte[frames * Channels * 2];
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < Channels; c++)
                {
                    float sample = buffer[f * Channels + c];
                    short q = (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
                    pcm[o++] = (byte)(q & 0xFF);
                    pcm[o++] = (byte)((q >> 8) & 0xFF);
                }
            }
            _writer.Write(pcm, 0, pcm.Length);
            FramesWritten += frames;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Dispose(); } catch { }
        }
    }

    /// <summary>A measured summary of a WAV on disk: duration, per-channel levels, DC and band RMS.</summary>
    public readonly record struct Summary(
        string Path, double Seconds, long Frames, int SampleRate, int Channels,
        float PeakL, float PeakR, double RmsL, double RmsR,
        double DcL, double DcR, double Correlation, IReadOnlyList<(string Band, double Rms)> Bands);

    /// <summary>Re-open the file and measure what actually landed on disk.</summary>
    public static Summary Measure(string path)
    {
        using var reader = new WaveFileReader(path);
        int channels = reader.WaveFormat.Channels;
        int rate = reader.WaveFormat.SampleRate;

        var left = new List<float>();
        var right = new List<float>();
        var block = new float[reader.WaveFormat.BlockAlign / 2 * 512];
        var samples = new List<float>();

        // Read everything as 16-bit PCM (the format this class writes).
        byte[] raw = new byte[reader.Length];
        int read = 0;
        while (read < raw.Length)
        {
            int n = reader.Read(raw, read, raw.Length - read);
            if (n <= 0) break;
            read += n;
        }

        int frameBytes = channels * 2;
        int frames = read / frameBytes;
        double sumL2 = 0, sumR2 = 0, sumLR = 0, sumL = 0, sumR = 0;
        float peakL = 0, peakR = 0;
        for (int f = 0; f < frames; f++)
        {
            float l = BitConverter.ToInt16(raw, f * frameBytes) / 32768f;
            float r = channels > 1 ? BitConverter.ToInt16(raw, f * frameBytes + 2) / 32768f : l;
            left.Add(l);
            right.Add(r);
            peakL = Math.Max(peakL, Math.Abs(l));
            peakR = Math.Max(peakR, Math.Abs(r));
            sumL2 += (double)l * l;
            sumR2 += (double)r * r;
            sumLR += (double)l * r;
            sumL += l;
            sumR += r;
        }

        double denom = Math.Sqrt(sumL2 * sumR2);
        IReadOnlyList<(string, double)> bands = frames > 0
            ? BandRms(left, rate)
            : Array.Empty<(string, double)>();

        return new Summary(
            System.IO.Path.GetFullPath(path),
            frames / (double)rate,
            frames, rate, channels,
            peakL, peakR,
            frames > 0 ? Math.Sqrt(sumL2 / frames) : 0,
            frames > 0 ? Math.Sqrt(sumR2 / frames) : 0,
            frames > 0 ? sumL / frames : 0,
            frames > 0 ? sumR / frames : 0,
            denom > 1e-12 ? sumLR / denom : 0,
            bands);
    }

    private static readonly (string Name, double Low, double High)[] BandDefs =
    {
        ("sub-60Hz", 0, 60),
        ("60-250Hz", 60, 250),
        ("250-2k", 250, 2000),
        ("2k-8k", 2000, 8000),
        ("8k-16k", 8000, 16000),
    };

    /// <summary>
    /// Band RMS by averaging magnitude spectra over 4096-sample frames. Enough to
    /// show musical content spread across bands rather than a single tone or DC.
    /// </summary>
    private static IReadOnlyList<(string, double)> BandRms(List<float> channel, int rate)
    {
        const int fftLength = 4096;
        var window = new float[fftLength];
        var sums = new double[BandDefs.Length];
        int windowCount = 0;

        for (int start = 0; start + fftLength <= channel.Count; start += fftLength)
        {
            var buffer = new Complex[fftLength];
            for (int i = 0; i < fftLength; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (fftLength - 1));
                buffer[i].X = (float)(channel[start + i] * w);
                buffer[i].Y = 0;
            }
            FastFourierTransform.FFT(true, (int)Math.Log2(fftLength), buffer);

            for (int bin = 1; bin < fftLength / 2; bin++)
            {
                double hz = bin * rate / (double)fftLength;
                double mag = Math.Sqrt(buffer[bin].X * buffer[bin].X + buffer[bin].Y * buffer[bin].Y);
                for (int b = 0; b < BandDefs.Length; b++)
                {
                    if (hz >= BandDefs[b].Low && hz < BandDefs[b].High)
                    {
                        sums[b] += mag * mag;
                        break;
                    }
                }
            }
            windowCount++;
        }

        var result = new List<(string, double)>(BandDefs.Length);
        for (int b = 0; b < BandDefs.Length; b++)
        {
            double rms = windowCount > 0 ? Math.Sqrt(sums[b] / (windowCount * (fftLength / 2.0))) : 0;
            result.Add((BandDefs[b].Name, rms));
        }
        return result;
    }
}
