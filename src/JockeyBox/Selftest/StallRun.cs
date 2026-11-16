using System;
using System.Diagnostics;
using System.Windows;
using JockeyBox.ViewModels;
using JockeyBox.Views;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--stall-run &lt;seconds&gt;</c>: real MainWindow, real WASAPI device, real
/// library scan, held open so the stall watchdog can sample it in one process.
/// Disposable diagnostic mode: it exists only to give the instrument a workload.
/// </summary>
internal static class StallRun
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--stall-run") != null;

    public static int Run(string[] args)
    {
        int seconds = int.TryParse(Harness.Flag(args, "--stall-run"), out int s)
            ? Math.Clamp(s, 5, 900)
            : 60;

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();

        if (window.DataContext is not MainViewModel vm)
        {
            Console.Error.WriteLine("stall-run: no MainViewModel");
            return 1;
        }

        UiPump.PumpUntil(() => vm.Tracks.Count > 0, 120000);
        Console.WriteLine($"stall-run: tracks={vm.Tracks.Count}");

        if (vm.Tracks.Count > 0)
        {
            vm.SelectedTrack = vm.Tracks[0];
            vm.LoadSelectedTrackCommand.Execute(null);
            UiPump.Pump(1500);
            vm.PlayPauseCommand.Execute(null);
            Console.WriteLine("stall-run: deck loaded, play pressed");
        }

        // A scan over playback: this is the workload the instrument is meant to see.
        UiPump.Pump(2000);
        vm.RescanCommand.Execute(null);
        Console.WriteLine("stall-run: rescan queued");

        var clock = Stopwatch.StartNew();
        int lastPrinted = -1;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            UiPump.Pump(200);
            int whole = (int)clock.Elapsed.TotalSeconds;
            if (whole % 10 == 0 && whole != lastPrinted)
            {
                lastPrinted = whole;
                Console.WriteLine($"stall-run: t={whole}s");
            }
        }

        Console.WriteLine("stall-run: done");
        return 0;
    }
}
