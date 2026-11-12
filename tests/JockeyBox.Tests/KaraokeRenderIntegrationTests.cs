using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Tools;
using Xunit;
using Xunit.Abstractions;

namespace JockeyBox.Tests;

/// <summary>
/// The operator's actual requirement, measured on real audio instead of asserted
/// against an argv: a karaoke render must have the source's duration, sample
/// rate, channel count and bitrate, and must decode cleanly. Every other test in
/// <see cref="KaraokeRenderTests"/> only proves what the engine is *told*; this
/// one runs the separator and the encoder for real and ffprobes what came out.
///
/// <para><b>Gated.</b> Skipped unless <c>JOCKEYBOX_KARAOKE_CLIP</c> names a real
/// audio file with vocals, so the ordinary suite stays fast and offline. Run it
/// alone, because it repoints <c>JOCKEYBOX_HOME</c> at a throwaway directory for
/// the duration of the three renders:</para>
/// <code>
/// set JOCKEYBOX_KARAOKE_CLIP=C:\Users\Alex\Music\Daft Punk\Get Lucky.mp3
/// dotnet test tests\JockeyBox.Tests --filter "FullyQualifiedName~KaraokeRenderIntegrationTests"
/// </code>
///
/// <para><b>Isolation.</b> The source file is only ever read: it is copied into a
/// fresh <c>%TEMP%\jb-render-&lt;random&gt;\</c> and the copy is trimmed to 25 s
/// with <c>-ss 30 -t 25 -c copy</c>, so the trim is frame-aligned and inherits
/// the source's rate, channels and bitrate. That trimmed file is the baseline
/// every output is measured against. The throwaway home gets directory junctions
/// to the real <c>uvr-venv</c> and <c>uvr-models</c> so the engine has its tools
/// without copying gigabytes and without writing to the operator's profile; both
/// junctions and the whole render directory are deleted at the end.</para>
/// </summary>
[Collection(nameof(KaraokeRenderCollection))]
public sealed class KaraokeRenderIntegrationTests
{
    internal const string ClipVariable = "JOCKEYBOX_KARAOKE_CLIP";

    /// <summary>One MPEG-1 Layer III frame at 44.1 kHz: the largest honest skew.</summary>
    private const double FrameSeconds = 26.0 / 1000.0;

    private readonly ITestOutputHelper _log;

    public KaraokeRenderIntegrationTests(ITestOutputHelper log) => _log = log;

    [KaraokeClipFact]
    public async Task Three_modes_render_a_playable_mp3_that_keeps_the_source_audio_facts()
    {
        string source = Environment.GetEnvironmentVariable(ClipVariable)!.Trim();
        Assert.True(File.Exists(source), $"{ClipVariable} does not name a file: {source}");

        // The real home, not AppPaths.Home: the module initializer already
        // pointed that at a throwaway. The venv and models are read from here
        // through junctions, never copied and never written.
        string realHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JockeyBox");
        string toolHome = File.Exists(Path.Combine(AppPaths.Home, "uvr-venv", "Scripts", "python.exe"))
            ? AppPaths.Home
            : realHome;
        string venv = Path.Combine(toolHome, "uvr-venv");
        string models = Path.Combine(toolHome, "uvr-models");
        Assert.True(
            File.Exists(Path.Combine(venv, "Scripts", "audio-separator.exe"))
                && File.Exists(Path.Combine(venv, "Scripts", "python.exe")),
            $"Vocal removal engine is not installed under {toolHome}.");

        string ffmpeg = ToolBootstrap.ResolveFfmpeg(new AppSettings())
            ?? throw new InvalidOperationException("ffmpeg was not found on PATH.");
        string ffprobe = FfprobeFor(ffmpeg);

        string root = Path.Combine(Path.GetTempPath(), "jb-render-" + Guid.NewGuid().ToString("n")[..8]);
        string home = Path.Combine(root, "home");
        string? previousHome = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(home);
            Junction(Path.Combine(home, "uvr-venv"), venv);
            Junction(Path.Combine(home, "uvr-models"), models);
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);

            // ---- baseline: a frame-aligned trim of a copy of the real track ----
            string copy = Path.Combine(root, "source-copy.mp3");
            File.Copy(source, copy, overwrite: false);
            string baselinePath = Path.Combine(root, "baseline.mp3");
            Exec(ffmpeg, "-v", "error", "-y", "-ss", "30", "-i", copy,
                "-t", "25", "-c", "copy", baselinePath);

            Facts baseline = Probe(ffprobe, baselinePath);
            _log.WriteLine($"source:   {source}");
            _log.WriteLine($"baseline: {baselinePath} (copy trimmed 30 s + 25 s, -c copy)");
            _log.WriteLine($"tools:    {toolHome} (cuda dlls present: {SeparationEngine.CudaReady(venv)})");
            _log.WriteLine("");

            var engine = new SeparationEngine(new AppSettings { FfmpegPath = ffmpeg });
            var rows = new List<(string Label, Facts Facts, double WallSeconds, string Provider, string Md5)>
            {
                ("baseline (source copy, -c copy)", baseline, 0, "n/a", Md5(baselinePath)),
            };
            var outputs = new List<(KaraokeStemMode Mode, string Path, string Md5)>();
            var facts = SourceAudioFacts.Read(baselinePath);

            foreach (var mode in new[]
                     {
                         KaraokeStemMode.Instrumental,
                         KaraokeStemMode.InstrumentalKeepBacking,
                         KaraokeStemMode.VocalsOnly,
                     })
            {
                string output = Path.Combine(root, $"baseline.{mode}.mp3");
                var request = new SeparationEngine.SeparationRequest(
                    baselinePath, output, mode, facts.SampleRate, facts.Channels, facts.BitrateKbps);

                var clock = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                string produced = await engine.SeparateAsync(
                    request, new Progress<(int percent, string phase)>(_ => { }), cts.Token);
                clock.Stop();

                Assert.True(File.Exists(produced), $"{mode} produced no file at {produced}");
                Facts got = Probe(ffprobe, produced);
                (int exit, _, string stderr) = Exec(ffmpeg, "-v", "error", "-i", produced, "-f", "null", "-");
                Assert.True(exit == 0, $"{mode}: ffmpeg decode exited {exit}: {stderr}");
                Assert.True(string.IsNullOrWhiteSpace(stderr),
                    $"{mode}: decode wrote to stderr: {stderr}");

                double wall = clock.Elapsed.TotalSeconds;
                string provider = ProviderFromLog();
                rows.Add((mode.ToString(), got, wall, provider, Md5(produced)));
                outputs.Add((mode, produced, Md5(produced)));

                // The source's own audio facts must survive the round trip.
                Assert.Equal(baseline.SampleRate, got.SampleRate);
                Assert.Equal(baseline.Channels, got.Channels);
                Assert.InRange(Math.Abs(got.Duration - baseline.Duration), 0, FrameSeconds);
                if (baseline.StreamBitrate is long expectedBitrate)
                    Assert.Equal(expectedBitrate, got.StreamBitrate);
                else
                    Assert.InRange(
                        Math.Abs(got.EffectiveKbps - baseline.EffectiveKbps) / baseline.EffectiveKbps,
                        0, 0.01);
            }

            // Three models, three stems: identical files would mean the mode is
            // ignored. md5 difference is necessary, not sufficient -- see the
            // volume hint below and the caveat printed with the table.
            foreach (var pair in outputs.CombinePairs())
            {
                Assert.NotEqual(pair.First.Md5, pair.Second.Md5);
            }

            Report(rows, baseline);

            _log.WriteLine("weak hint only: mean/max volume per output (loudness, not stem content)");
            foreach (var (mode, path, _) in outputs)
            {
                _log.WriteLine($"  {mode,-24} {Volume(ffmpeg, path)}");
            }
            _log.WriteLine(
                "  VocalsOnly keeping backing vocals is NOT proven here: md5 and loudness "
                + "differ, but only a spectral/band comparison would show what was kept.");

            _log.WriteLine("");
            _log.WriteLine("passed: duration/rate/channels/bitrate match the baseline, all decode clean, all md5 differ.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, previousHome);
            RemoveJunction(Path.Combine(home, "uvr-venv"));
            RemoveJunction(Path.Combine(home, "uvr-models"));
            DeleteTree(root);

            bool gone = !Directory.Exists(root);
            bool venvIntact = File.Exists(Path.Combine(venv, "Scripts", "python.exe"))
                              && Directory.Exists(models);
            _log.WriteLine($"cleanup: {root} removed={gone}; real venv/models intact={venvIntact}");
            Assert.True(gone, $"temp render dir survived cleanup: {root}");
            Assert.True(venvIntact, "cleanup damaged the real uvr-venv or uvr-models");
        }
    }

    private void Report(
        IReadOnlyList<(string Label, Facts Facts, double WallSeconds, string Provider, string Md5)> rows,
        Facts baseline)
    {
        _log.WriteLine("");
        _log.WriteLine("| output | bytes | dur s | Δdur ms | rate | ch | kbps nom/eff | wall s | provider | md5 |");
        _log.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var row in rows)
        {
            double deltaMs = (row.Facts.Duration - baseline.Duration) * 1000.0;
            _log.WriteLine(
                $"| {row.Label} | {row.Facts.Size} | {row.Facts.Duration:F3} | {deltaMs:+0.0;-0.0;0.0} "
                + $"| {row.Facts.SampleRate} | {row.Facts.Channels} "
                + $"| {row.Facts.StreamBitrate?.ToString() ?? "-"}/{row.Facts.EffectiveKbps:F0} "
                + $"| {row.WallSeconds:F1} | {row.Provider} | {row.Md5[..8]} |");
        }
        _log.WriteLine("");
        _log.WriteLine(
            $"baseline: {baseline.SampleRate} Hz, {baseline.Channels} ch, "
            + $"{baseline.StreamBitrate?.ToString() ?? "?"} b/s nominal, {baseline.EffectiveKbps:F0} kbps effective, "
            + $"{baseline.Duration:F3} s; tolerance ±{FrameSeconds * 1000:F0} ms (one MP3 frame).");
    }

    /// <summary>The provider the last run logged ("cuda", or "cpu (…)" with the reason).</summary>
    private static string ProviderFromLog()
    {
        const string marker = "audio-separator: ";
        try
        {
            string? line = File.Exists(Log.FilePath)
                ? File.ReadLines(Log.FilePath).LastOrDefault(l => l.Contains(marker, StringComparison.Ordinal))
                : null;
            if (line == null) return "unknown (no log line)";
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            return line[(at + marker.Length)..].Trim();
        }
        catch (Exception ex)
        {
            return "unknown (" + ex.GetType().Name + ")";
        }
    }

    private sealed record Facts(
        double Duration, int SampleRate, int Channels, long? StreamBitrate, long Size)
    {
        public double EffectiveKbps => Duration <= 0 ? 0 : Size * 8.0 / Duration / 1000.0;
    }

    private static Facts Probe(string ffprobe, string path)
    {
        (int exit, string stdout, string stderr) = Exec(
            ffprobe,
            "-v", "error",
            "-select_streams", "a:0",
            "-show_entries", "stream=sample_rate,channels,bit_rate,duration:format=duration,size,bit_rate",
            "-of", "json",
            path);
        Assert.True(exit == 0, $"ffprobe exited {exit} on {path}: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var stream = doc.RootElement.GetProperty("streams")[0];
        var format = doc.RootElement.GetProperty("format");

        static double Number(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)) return 0;
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDouble(out double number) ? number : 0,
                JsonValueKind.String => double.TryParse(value.GetString(), out double parsed) ? parsed : 0,
                _ => 0,
            };
        }

        long? StreamBitrate()
        {
            double bits = Number(stream, "bit_rate");
            return bits > 0 ? (long)bits : null;
        }

        double duration = Number(format, "duration");
        if (duration <= 0) duration = Number(stream, "duration");

        return new Facts(
            duration,
            (int)Number(stream, "sample_rate"),
            (int)Number(stream, "channels"),
            StreamBitrate(),
            (long)Number(format, "size"));
    }

    /// <summary>foobar: mean and max volume, from ffmpeg's volumedetect filter.</summary>
    private static string Volume(string ffmpeg, string path)
    {
        (_, _, string stderr) = Exec(ffmpeg, "-hide_banner", "-i", path, "-af", "volumedetect", "-f", "null", "-");
        var mean = stderr.Split('\n').FirstOrDefault(l => l.Contains("mean_volume"));
        var max = stderr.Split('\n').FirstOrDefault(l => l.Contains("max_volume"));
        return $"{(mean?.Trim() ?? "mean=?"),-28} {(max?.Trim() ?? "max=?")}";
    }

    private static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream));
    }

    private static string FfprobeFor(string ffmpeg)
    {
        string sibling = Path.Combine(
            Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe" + Path.GetExtension(ffmpeg));
        return File.Exists(sibling) ? sibling : "ffprobe";
    }

    /// <summary>
    /// A directory junction needs no elevation, unlike a symlink. Nothing is
    /// written through it: the models are already downloaded and the venv is only
    /// read.
    /// </summary>
    private static void Junction(string link, string target)
    {
        (int exit, _, string stderr) = Exec("cmd.exe", "/c", "mklink", "/J", link, target);
        Assert.True(exit == 0, $"mklink /J {link} -> {target} failed ({exit}): {stderr}");
    }

    /// <summary>Removes the reparse point only; the junction's target is untouched.</summary>
    private static void RemoveJunction(string path)
    {
        if (!Directory.Exists(path)) return;
        Exec("cmd.exe", "/c", "rmdir", path);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Exec("cmd.exe", "/c", "rmdir", "/s", "/q", path);
        }
    }

    private static (int Exit, string Stdout, string Stderr) Exec(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(300_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not exit within 300 s");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr);
    }
}

/// <summary>
/// Keeps the render test away from every other test: it repoints
/// <c>JOCKEYBOX_HOME</c> process-wide for the duration of three real separations,
/// and a parallel fixture writing into that home would race it.
/// </summary>
[CollectionDefinition(nameof(KaraokeRenderCollection), DisableParallelization = true)]
public sealed class KaraokeRenderCollection
{
}

/// <summary>
/// A fact that is skipped, not failed, when no clip is supplied. xunit 2.x has no
/// runtime skip; deciding at discovery time is the supported way to keep the
/// default suite fast and offline.
/// </summary>
internal sealed class KaraokeClipFactAttribute : FactAttribute
{
    public KaraokeClipFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(KaraokeRenderIntegrationTests.ClipVariable)))
        {
            Skip = "Set " + KaraokeRenderIntegrationTests.ClipVariable
                + " to a real track with vocals to run the render integration test.";
        }
    }
}

internal static class KaraokeRenderTestExtensions
{
    /// <summary>Every unordered pair, so md5 distinctness is checked pairwise.</summary>
    public static IEnumerable<(T First, T Second)> CombinePairs<T>(this IReadOnlyList<T> items)
    {
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
                yield return (items[i], items[j]);
    }
}
