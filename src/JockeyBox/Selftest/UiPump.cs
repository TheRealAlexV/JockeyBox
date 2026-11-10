using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace JockeyBox.Selftest;

/// <summary>
/// The one dispatcher pump the self-test surfaces share: run the queue for a
/// while, or run it in slices until a condition holds or a budget runs out.
///
/// <para><b>Why shared.</b> The perf probe and the screenshot wait on the same
/// view-model flags (<c>IsPlaylistLoading</c>); two copies of the wait loop would
/// be two chances to observe a different app and disagree about whether it had
/// settled.</para>
/// </summary>
internal static class UiPump
{
    /// <summary>
    /// Pump in 25 ms slices until <paramref name="condition"/> holds or
    /// <paramref name="timeoutMs"/> elapses. Returns the final reading, so a
    /// caller that needs to know whether it settled must not ignore the result.
    /// </summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
            Pump(25);
        return condition();
    }

    /// <summary>
    /// Run the dispatcher for about <paramref name="milliseconds"/>, then return.
    /// </summary>
    public static void Pump(int milliseconds)
    {
        int budget = Math.Max(1, milliseconds);
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;

        // The release has to be a hard bound. A DispatcherTimer at any priority is
        // queued beside the work it is meant to interrupt, so a continuous stream
        // of higher-priority work postpones its tick and PushFrame keeps pumping
        // well past the budget; in a headless capture that turns a slow settle
        // into a hang where the caller expected a budget verdict. A background
        // timer does not compete with the queue at all: it fires off the
        // dispatcher and posts the stop at Send, which is above every priority the
        // app itself queues. Release is then bounded by the budget plus one queue
        // hop, and the frame still ends as soon as the predicate's caller has
        // pumped that long, so an early settle stays early.
        using var timer = new Timer(_ =>
        {
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
            }
            catch (InvalidOperationException)
            {
                // The dispatcher is shutting down and its loop is ending anyway.
            }
        }, null, budget, Timeout.Infinite);

        Dispatcher.PushFrame(frame);
    }
}
