using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using NAudio.Wave;

namespace JockeyBox.Tests;

/// <summary>
/// Helpers shared by the bus/mixer/split tests: a synthetic mic source, constant
/// WAV files and an offline engine harness that records every bus block.
/// </summary>
internal static class TestAudio
{
    /// <summary>An <see cref="IMicSource"/> that produces a constant sample forever.</summary>
    internal sealed class ConstantMic : IMicSource
    {
        private readonly float _value;

        public ConstantMic(float value) => _value = value;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, _value, offset, count);
            return count;
        }

        public void Maintain(int blockFrames) { }
    }

    /// <summary>Write a stereo float WAV holding a constant sample value.</summary>
    public static string WriteConstantWav(float value, double seconds = 2.0, int rate = 48_000)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jb-{Guid.NewGuid():N}.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        int frames = (int)(seconds * rate);
        var data = new float[frames * 2];
        Array.Fill(data, value);

        using (var writer = new WaveFileWriter(path, format))
            writer.WriteSamples(data, 0, data.Length);

        return path;
    }

    /// <summary>Write a stereo float WAV holding a ramp so alignment is visible sample by sample.</summary>
    public static string WriteRampWav(double seconds = 3.0, double frequency = 3.0, int rate = 48_000)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jb-{Guid.NewGuid():N}.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        int frames = (int)(seconds * rate);
        var data = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * f / rate));
            data[f * 2] = v;
            data[f * 2 + 1] = v;
        }

        using (var writer = new WaveFileWriter(path, format))
            writer.WriteSamples(data, 0, data.Length);

        return path;
    }

    public static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }

    /// <summary>Last rendered block of every bus, captured offline.</summary>
    public sealed class BusTap
    {
        private readonly object _gate = new();
        private readonly Dictionary<BusKind, float[]> _last = new();
        private readonly Dictionary<BusKind, int> _blocks = new();
        private readonly Dictionary<BusKind, float[]> _first = new();
        private readonly Dictionary<BusKind, List<float>> _history = new();

        public void OnBus(BusKind kind, float[] buffer, int frames)
        {
            int count = frames * 2;
            lock (_gate)
            {
                if (!_blocks.ContainsKey(kind))
                {
                    var first = new float[count];
                    Array.Copy(buffer, first, count);
                    _first[kind] = first;
                    _blocks[kind] = 0;
                    _history[kind] = new List<float>();
                }

                _blocks[kind] = _blocks[kind] + 1;
                _history[kind].Add(JockeyBox.Audio.Graph.Samples.Peak(buffer, 0, count));

                if (!_last.TryGetValue(kind, out var last) || last.Length != count)
                    last = new float[count];
                Array.Copy(buffer, last, count);
                _last[kind] = last;
            }
        }

        public IReadOnlyList<float> History(BusKind kind)
        {
            lock (_gate) return _history.TryGetValue(kind, out var h) ? h.ToArray() : Array.Empty<float>();
        }

        /// <summary>Index of the first block whose peak is audible, or -1.</summary>
        public int FirstAudibleBlock(BusKind kind, float threshold = 1e-3f)
        {
            var history = History(kind);
            for (int i = 0; i < history.Count; i++) if (history[i] > threshold) return i;
            return -1;
        }

        public int Blocks(BusKind kind)
        {
            lock (_gate) return _blocks.TryGetValue(kind, out int n) ? n : 0;
        }

        public float[] Last(BusKind kind)
        {
            lock (_gate) return _last.TryGetValue(kind, out var b) ? b : Array.Empty<float>();
        }

        public float[] First(BusKind kind)
        {
            lock (_gate) return _first.TryGetValue(kind, out var b) ? b : Array.Empty<float>();
        }

        public float Peak(BusKind kind) => JockeyBox.Audio.Graph.Samples.Peak(Last(kind), 0, Last(kind).Length);
    }

    /// <summary>Run an engine offline for a fixed number of blocks and wait for it to finish.</summary>
    public static void RenderBlocks(AudioEngine engine, BusTap tap, int blocks)
    {
        double seconds = (blocks + 1) * (double)AudioEngine.BlockFrames / AudioEngine.Rate;
        using var done = new ManualResetEventSlim(false);

        engine.StartOffline(
            capture: (_, _) => { },
            seconds: seconds,
            monitorCapture: null,
            busCapture: (kind, buffer, frames) =>
            {
                tap.OnBus(kind, buffer, frames);
                if (tap.Blocks(kind) >= blocks) done.Set();
            });

        if (!done.Wait(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("offline engine did not render the requested blocks");

        engine.Stop();
    }
}
