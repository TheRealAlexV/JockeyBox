using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--popup-probe</c>: opens and closes a real <see cref="ToolTip"/> and a bare
/// <see cref="Popup"/>, then prints
/// <c>POPUP OK</c> or <c>POPUP FAILED: &lt;type&gt;: &lt;message&gt;</c> and exits
/// non-zero on failure.
///
/// <para><b>Why.</b> The field crash was
/// <c>System.IO.FileNotFoundException: Accessibility, Version=4.0.0.0</c> raised from
/// <c>PopupSecurityHelper.ForceMsaaToUiaBridge</c> the moment a ToolTip opened. A
/// screenshot cannot open a ToolTip, and an assembly sitting in the publish output
/// is not proof that it resolves at run time. This drives the exact path -
/// <c>Popup.OnIsOpenChanged</c> to <c>Popup.CreateWindow</c> to
/// <c>ForceMsaaToUiaBridge</c> - so the claim is settled by execution.</para>
///
/// <para>Runs on the dispatcher thread the WPF application already owns (STA), with
/// <see cref="Application.Current"/> live, exactly as a real ToolTip would.</para>
/// </summary>
internal static class PopupProbe
{
    public static bool Requested(string[] args) =>
        Harness.Flag(args, "--popup-probe") != null ||
        Harness.Flag(args, "--popup-probe-delayed") != null;

    /// <summary>Exit code for a popup that could not be opened or closed.</summary>
    public const int FailureExitCode = 1;

    /// <summary>
    /// <c>--popup-probe-delayed &lt;ms&gt;</c>: run the same probe after <paramref name="args"/>
    /// says so many milliseconds, so another process can be started and its effect on this
    /// one's extraction directory observed before the popup opens. Null when the flag is
    /// absent; throws on a value that is not a delay, which the caller reports as a failure.
    /// </summary>
    public static int? DelayMs(string[] args)
    {
        string? raw = Harness.Flag(args, "--popup-probe-delayed");
        if (raw is null) return null;
        if (int.TryParse(raw, out int ms) && ms >= 0) return ms;
        throw new ArgumentException($"--popup-probe-delayed wants milliseconds, got '{raw}'");
    }

    /// <summary>
    /// Waits <paramref name="delayMs"/> on the dispatcher (a nested frame keeps this
    /// process's window work serviced, exactly as an idle app would), then runs the probe.
    /// Printed first: the extraction directory, because with
    /// <c>IncludeAllContentForSelfExtract</c> it is the base directory and it is the
    /// directory under observation.
    /// </summary>
    public static int RunAfter(int delayMs)
    {
        Console.Out.WriteLine($"POPUP WAITING: {delayMs} ms");
        Console.Out.WriteLine($"PROBE DIR: {AppContext.BaseDirectory}");
        Console.Out.Flush();

        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);

        return Run();
    }

    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            Console.Out.WriteLine(
                "POPUP FAILED: InvalidOperationException: probe is not on an STA thread, " +
                "so opening a window would not exercise the real path");
            return FailureExitCode;
        }

        try
        {
            // 1. The reported trigger: a ToolTip opening. ToolTip.Open owns a Popup,
            //    so this reaches Popup.CreateWindow the same way the field crash did.
            var tip = new ToolTip { Content = "popup-probe" };
            tip.IsOpen = true;
            Pump();

            tip.IsOpen = false;
            Pump();

            // 2. A bare Popup, so a fix that only satisfied the ToolTip wrapper cannot
            //    look like a pass.
            var popup = new Popup { Child = new TextBlock { Text = "popup-probe" } };
            popup.IsOpen = true;
            Pump();

            popup.IsOpen = false;
            Pump();
        }
        catch (Exception ex)
        {
            // The field failure was a load failure of Accessibility, which surfaces as
            // the exception type and the assembly identity it could not find. ToString,
            // not Type + Message: a FileNotFoundException raised by the resolver carries
            // an empty Message, so the trimmed form printed nothing after the colon and
            // threw away the stack that names the failing path.
            Console.Out.WriteLine($"POPUP FAILED: {ex}");
            for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.Out.WriteLine($"  inner: {inner.GetType().FullName}: {inner.Message}");
            Console.Out.Flush();
            Console.Error.Flush();
            return FailureExitCode;
        }

        Console.Out.WriteLine("POPUP OK");
        Console.Out.Flush();
        Console.Error.Flush();
        return 0;
    }

    /// <summary>
    /// Lets the posted window work run: <c>Popup.CreateWindow</c> creates the hwnd
    /// source and the load failure is raised while the dispatcher services it, not at
    /// the <c>IsOpen</c> assignment alone.
    /// </summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
