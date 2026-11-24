using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// The directory every fixture WAV in this run is written to.
    ///
    /// <para><b>Not %TEMP%.</b> Fixtures used to be created straight in %TEMP%
    /// under a <c>jb-</c> name, and something outside this process deletes files
    /// <i>and directories</i> there: a fixture could disappear between the write
    /// and the deck opening it, so a test failed with "audio file not found" for
    /// a file it had just written, and a whole class failed at 1 ms each once a
    /// <c>%TEMP%</c> subdirectory was removed under it. <c>%LOCALAPPDATA%</c> is
    /// ordinary cleaners' blind spot, which is what makes it usable here.</para>
    ///
    /// <para><b>Why per process.</b> Four lanes run suites on this host at the
    /// same time; a shared directory would let one run's cleanup delete another
    /// run's fixtures, which is the same failure with a different culprit.</para>
    /// </summary>
    private static readonly string FixtureDir = CreateFixtureDir();

    private static string CreateFixtureDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JockeyBox-test",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        // Throwaway fixtures are megabytes each; the exit hook keeps a few
        // thousand of them from piling up in a directory nothing sweeps.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static string NewFixturePath()
    {
        // Recreated on demand on purpose. A cleaner outside this process has
        // removed this directory in the middle of a suite run, which turned 23
        // unrelated tests into 1 ms failures on a file they had just asked for.
        // One syscall here makes that a non-event; it is not a retry of anything
        // that failed, and the pair tests still assert their own fixtures are on
        // disk at the settle point, so a deletion during a test stays visible.
        Directory.CreateDirectory(FixtureDir);
        return Path.Combine(FixtureDir, $"jb-{Guid.NewGuid():N}.wav");
    }

    /// <summary>
    /// Announce the fake output on both decks. A load issued before this arrives
    /// has no route to play on, so the channel fails it, and the pair guard in
    /// <c>OnUiTick</c> then reads "stopped with nothing loaded" and drops the
    /// pair. Any test that loads a deck therefore calls this first.
    /// </summary>
    public static void PrimeOutput(JockeyBox.ViewModels.MainViewModel vm) =>
        UiThread.Run(() =>
        {
            vm.Transport.DeckA.SetOutputAvailable(true);
            vm.Transport.DeckB.SetOutputAvailable(true);
        });

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
        string path = NewFixturePath();
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
        string path = NewFixturePath();
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
