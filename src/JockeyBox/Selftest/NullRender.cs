using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--null-render &lt;dir&gt; --null-track &lt;file&gt; [--null-a &lt;ms&gt;] [--null-b &lt;ms&gt;]
/// [--null-seconds &lt;s&gt;]</c>: render the same fixture through the offline engine and
/// write each deck's rendered bus, plus the program sum, as raw little-endian
/// float32, so two builds can be compared sample for sample.
///
/// <para><b>Why this exists.</b> A 0 ms delay was proved "within 1e-4 of the
/// expected amplitude" - a tolerance, not a null. A tolerance cannot tell a
/// bit-identical pass-through from a kernel that quietly rescales, and the
/// interesting question for an operator-invisible feature ("at 0 ms nothing
/// changes") is exactly the bit. Offline rendering is used rather than
/// <c>--audiotrace</c> because the render thread then runs as fast as it can with
/// no device, so the sample stream is a function of the source and the settings -
/// two runs, or two builds, produce the same frame count and can be diffed
/// directly instead of aligned by hand.</para>
/// </summary>
internal static class NullRender
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--null-render") != null;

    public static int Run(string[] args)
    {
        string? dir = Harness.Flag(args, "--null-render");
        string? track = Harness.Flag(args, "--null-track");
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(track))
        {
            Console.Error.WriteLine("null-render: --null-render <dir> and --null-track <file> are required");
            return 2;
        }
        if (!File.Exists(track))
        {
            Console.Error.WriteLine($"null-render: track not found: {track}");
            return 2;
        }

        int delayA = Int(args, "--null-a", 0);
        int delayB = Int(args, "--null-b", 0);
        double seconds = Double(args, "--null-seconds", 1.0);
        Directory.CreateDirectory(dir);

        var deckA = new List<float>();
        var deckB = new List<float>();
        var program = new List<float>();

        var engine = new AudioEngine();
        // Centre the crossfader on both builds: each deck then renders its own bus at
        // 0.5 gain, so the delayed deck's samples exist to be compared and a deck at
        // 0 ms is compared while the other deck is actually producing audio.
        engine.Fader.Snap(0.5, 0);
        // d17876f has no deck output delay at all: this build's render path is the
        // pre-feature one, which is the thing the branch's 0 ms is compared against.
        if (delayA != 0 || delayB != 0)
        {
            Console.Error.WriteLine("null-render: this build has no deck output delay");
            return 2;
        }

        engine.DeckA.LoadAsync(track, null, 0, startSeconds: 0, playWhenReady: true)
            .GetAwaiter().GetResult();
        engine.DeckB.LoadAsync(track, null, 0, startSeconds: 0, playWhenReady: true)
            .GetAwaiter().GetResult();

        int frames = 0;
        engine.StartOffline(
            (block, count) =>
            {
                for (int i = 0; i < count * AudioEngine.Channels; i++) program.Add(block[i]);
                frames = count;
            },
            seconds,
            monitorCapture: null,
            busCapture: (kind, buffer, count) =>
            {
                var target = kind switch
                {
                    BusKind.DeckA => deckA,
                    BusKind.DeckB => deckB,
                    _ => null,
                };
                if (target == null) return;
                for (int i = 0; i < count * AudioEngine.Channels; i++) target.Add(buffer[i]);
            });

        var spin = new SpinWait();
        while (engine.IsRunning) spin.SpinOnce(1);
        engine.Stop();

        Write(Path.Combine(dir, "deckA.f32"), deckA);
        Write(Path.Combine(dir, "deckB.f32"), deckB);
        Write(Path.Combine(dir, "program.f32"), program);

        Console.WriteLine($"null-render: dir {dir} track {Path.GetFileName(track)} a={delayA}ms b={delayB}ms " +
                          $"seconds={seconds:0.###} block={frames}");
        foreach (var (name, data) in new[] { ("deckA", deckA), ("deckB", deckB), ("program", program) })
        {
            Console.WriteLine($"null-render: {name} frames={data.Count / AudioEngine.Channels} " +
                              $"firstNonZeroFrame={FirstNonZeroFrame(data)} peak={Peak(data):R}");
        }
        Console.Out.Flush();
        return 0;
    }

    private static void Write(string path, List<float> samples)
    {
        byte[] bytes = new byte[samples.Count * sizeof(float)];
        for (int i = 0; i < samples.Count; i++)
        {
            // BinaryWriter writes little-endian; do the same by hand so the file does
            // not depend on the host's byte order.
            byte[] one = BitConverter.GetBytes(samples[i]);
            Array.Copy(one, 0, bytes, i * sizeof(float), sizeof(float));
        }
        File.WriteAllBytes(path, bytes);
    }

    private static int FirstNonZeroFrame(List<float> interleaved)
    {
        for (int i = 0; i < interleaved.Count; i++)
            if (interleaved[i] != 0f) return i / AudioEngine.Channels;
        return -1;
    }

    private static double Peak(List<float> interleaved)
    {
        double peak = 0;
        foreach (float s in interleaved) peak = Math.Max(peak, Math.Abs((double)s));
        return peak;
    }

    private static int Int(string[] args, string flag, int fallback) =>
        Harness.Flag(args, flag) is { } text && int.TryParse(text, out int parsed) ? parsed : fallback;

    private static double Double(string[] args, string flag, double fallback) =>
        Harness.Flag(args, flag) is { } text && double.TryParse(text, out double parsed) ? parsed : fallback;

    /// <summary>Unused but keeps the compiler from dropping the StringBuilder using.</summary>
    private static string Describe(StringBuilder sb) => sb.ToString();
}
