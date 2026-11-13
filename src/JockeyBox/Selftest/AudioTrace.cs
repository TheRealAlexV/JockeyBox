using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--audiotrace [ms] [--track path]</c>: open the real endpoints from the
/// configured route, play a real file through the real render thread, and let the
/// engine's per-second audio trace write what actually reached each device.
///
/// <para><b>Why it is a mode.</b> The operator reported silence while the clock
/// advanced. Every other check in this folder listens to the graph in isolation
/// (<c>--selftest</c> is offline) or never plays at all, so nothing proved a
/// non-zero sample had reached a WASAPI session. This mode opens those sessions
/// exactly as the app does, for a bounded number of seconds, in a sandbox home,
/// and exits with the numbers in the log.</para>
///
/// <para>Exit code 0 means the run completed. It deliberately does not encode
/// "audio was audible": what the numbers say is the report's job, and a device
/// that receives a healthy signal yet makes no sound is a finding, not a crash.</para>
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

        var settings = AppSettings.Load();
        var route = DualDeckTransport.BuildRouteFromAppSettings(settings);
        string? karaoke = ValueOf(args, "--karaoke");
        if (karaoke != null && !File.Exists(karaoke))
        {
            Console.Error.WriteLine($"audiotrace: karaoke stem not found: {karaoke}");
            return 2;
        }

        Console.WriteLine($"audiotrace: home {AppPaths.Home}");
        Console.WriteLine($"audiotrace: track {track}");
        Console.WriteLine($"audiotrace: route {route}");

        using var echo = new LogEcho(Console.Out);
        var engine = new AudioEngine();
        int exit = 0;
        try
        {
            engine.MasterVolume = settings.MasterVolume;
            engine.DeckA.SetVolume(settings.DeckAVolume);
            engine.DeckB.SetVolume(settings.DeckBVolume);
            engine.Fader.Snap(0.5, 0);

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

            engine.Start();
            engine.DeckA.LoadAsync(track, karaokePath: karaoke, playWhenReady: true).GetAwaiter().GetResult();
            Console.WriteLine($"audiotrace: deck A {engine.DeckA.State} {engine.DeckA.DurationSeconds:0.0}s");

            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
                if (engine.DeckA.State == DeckSource.DeckState.Failed) break;
            }

            Console.WriteLine($"audiotrace: deck A ended at {engine.DeckA.State} " +
                              $"pos {engine.DeckA.PositionSeconds:0.00}s");
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
        }

        return exit;
    }

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
