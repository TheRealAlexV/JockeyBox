using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--audiotrace [ms]</c>: open the real endpoints from the configured route
/// (or an explicit override), play real files through the real render thread, and
/// let the engine's per-second audio trace say what actually reached each device.
///
/// <para><b>Why it is a mode.</b> The operator reported silence while the clock
/// advanced. Nothing in the process proved a non-zero sample reached a WASAPI
/// session, so the graph could not be exonerated and the device could not be
/// accused. This mode opens those sessions exactly as the app does, for a bounded
/// number of seconds, in a sandbox home, and exits with the numbers in the log.</para>
///
/// <para><c>--probe</c> additionally dumps every render endpoint with its own
/// volume and mute, and every session on it with that session's volume and mute -
/// which is where a Windows per-app mute for JockeyBox.exe would show up.</para>
/// </summary>
internal static class AudioTrace
{
    public static bool Requested(string[] args) => IndexOf(args, "--audiotrace") >= 0;

    public static int Run(string[] args)
    {
        int ms = 6000;
        int at = IndexOf(args, "--audiotrace");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int parsed) && parsed > 0)
            ms = parsed;

        string? track = ValueOf(args, "--track");
        if (track == null)
        {
            Console.Error.WriteLine("audiotrace: --track <file> is required");
            return 2;
        }
        if (!File.Exists(track))
        {
            Console.Error.WriteLine($"audiotrace: track not found: {track}");
            return 2;
        }
        string? karaoke = ValueOf(args, "--karaoke");
        if (karaoke != null && !File.Exists(karaoke))
        {
            Console.Error.WriteLine($"audiotrace: karaoke stem not found: {karaoke}");
            return 2;
        }
        string? trackB = ValueOf(args, "--trackB");
        if (trackB != null && !File.Exists(trackB))
        {
            Console.Error.WriteLine($"audiotrace: deck B track not found: {trackB}");
            return 2;
        }
        string? wavPath = ValueOf(args, "--wav");

        var settings = AppSettings.Load();
        var route = DualDeckTransport.BuildRouteFromAppSettings(settings);

        // Per-run overrides: the point is to drive one candidate endpoint at a time
        // without touching the operator's stored routing.
        route = route with
        {
            DeckADeviceId = ValueOf(args, "--deckA-device") ?? route.DeckADeviceId,
            DeckBDeviceId = ValueOf(args, "--deckB-device") ?? route.DeckBDeviceId,
            MonitorDeviceId = ValueOf(args, "--monitor-device") ?? route.MonitorDeviceId,
        };

        double fader = 0.5;
        if (double.TryParse(ValueOf(args, "--fader"), out double f)) fader = Math.Clamp(f, 0, 1);

        Console.WriteLine($"audiotrace: home {AppPaths.Home}");
        Console.WriteLine($"audiotrace: track {track}" + (trackB != null ? $" / B {trackB}" : ""));
        Console.WriteLine($"audiotrace: route {route}");

        using var echo = new LogEcho(Console.Out);
        var engine = new AudioEngine();
        WavDump? dump = null;
        int exit = 0;
        try
        {
            engine.MasterVolume = settings.MasterVolume;
            engine.DeckA.SetVolume(settings.DeckAVolume);
            engine.DeckB.SetVolume(settings.DeckBVolume);
            engine.MonitorEnabled = Has(args, "--mon");
            if (float.TryParse(ValueOf(args, "--monA"), out float monA)) engine.MonitorAGain = monA;
            if (float.TryParse(ValueOf(args, "--monB"), out float monB)) engine.MonitorBGain = monB;
            engine.Fader.Snap(fader, 0);

            var open = engine.SetRouteAsync(route);
            if (!open.Wait(TimeSpan.FromSeconds(20)))
            {
                Console.Error.WriteLine("audiotrace: opening endpoints timed out");
                return 3;
            }
            if (!route.AnyOutput)
            {
                Console.Error.WriteLine("audiotrace: no bus has a device - nothing to open");
                return 3;
            }

            if (wavPath != null)
            {
                dump = new WavDump(wavPath);
                engine.ProgramCapture = dump.Write;
                Console.WriteLine($"audiotrace: writing program mix to {dump.Path}");
            }

            engine.Start();
            engine.DeckA.LoadAsync(track, karaokePath: karaoke, playWhenReady: true)
                .GetAwaiter().GetResult();
            if (trackB != null)
                engine.DeckB.LoadAsync(trackB, playWhenReady: true).GetAwaiter().GetResult();

            Console.WriteLine($"audiotrace: deck A {engine.DeckA.State} B {engine.DeckB.State} " +
                              $"fader={fader:0.00} mon={engine.MonitorEnabled}");

            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            bool probed = false;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
                if (!probed && Has(args, "--probe") &&
                    DateTime.UtcNow > deadline.AddMilliseconds(-(ms - 2500)))
                {
                    probed = true;
                    try
                    {
                        foreach (string line in AudioProbe.Describe()) Console.WriteLine(line);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"probe: unreadable ({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message})");
                    }
                }
                if (engine.DeckA.State == DeckSource.DeckState.Failed) break;
            }

            Console.WriteLine($"audiotrace: deck A ended at {engine.DeckA.State} " +
                              $"pos {engine.DeckA.PositionSeconds:0.00}s " +
                              $"B {engine.DeckB.State} {engine.DeckB.PositionSeconds:0.00}s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"audiotrace: failed: {ex}");
            exit = 1;
        }
        finally
        {
            engine.Stop();
            engine.Dispose();

            if (dump != null)
            {
                dump.Dispose();
                var s = WavDump.Measure(dump.Path);
                Console.WriteLine($"wav: {s.Path}");
                Console.WriteLine($"wav: {s.Seconds:0.000}s {s.Frames} frames @ {s.SampleRate} Hz {s.Channels} ch");
                Console.WriteLine($"wav: peakL={s.PeakL:0.0000} peakR={s.PeakR:0.0000} " +
                                  $"rmsL={s.RmsL:0.0000} rmsR={s.RmsR:0.0000} " +
                                  $"dcL={s.DcL:+0.000000;-0.000000;0.000000} dcR={s.DcR:+0.000000;-0.000000;0.000000} " +
                                  $"corr={s.Correlation:+0.000;-0.000;0.000}");
                Console.WriteLine("wav: bands " +
                    string.Join(" ", s.Bands.Select(b => $"{b.Band}={b.Rms:0.000000}")));
            }
        }

        return exit;
    }

    private static bool Has(string[] args, string flag) => IndexOf(args, flag) >= 0;

    private static int IndexOf(string[]? args, string flag)
    {
        if (args == null) return -1;
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string? ValueOf(string[] args, string flag)
    {
        int i = IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Mirror log lines to the console so a sandbox run reports without a second read.</summary>
    private sealed class LogEcho : IDisposable
    {
        private readonly TextWriter _out;

        public LogEcho(TextWriter output)
        {
            _out = output;
            Log.LineWritten += OnLine;
        }

        private void OnLine(string line) => _out.WriteLine(line);

        public void Dispose() => Log.LineWritten -= OnLine;
    }
}
