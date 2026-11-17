using System;
using System.Diagnostics;
using System.Threading;

namespace JockeyBox.Tools;

/// <summary>
/// Two things the UI thread leaves behind for <see cref="UiWatchdog"/>: a
/// heartbeat, and one line naming the operation it is about to start.
///
/// <para><b>Cost.</b> Both are a <c>Stopwatch.GetTimestamp</c> plus a volatile
/// reference store - no allocation, no lock, no formatting, no string building.
/// <see cref="NoteUiTick"/> runs 30 times a second and must stay in the tens of
/// nanoseconds; <see cref="Enter"/> is called at a handful of sites only, and is
/// the same cost plus writing one already-interned literal.</para>
///
/// <para><b>Who writes.</b> Only the UI thread, so the fields need no lock: a
/// torn read is impossible (reference and long writes are atomic), and a stale
/// value is exactly what the watchdog is looking for.</para>
/// </summary>
public static class UiBreadcrumb
{
    private static string _held = "";
    private static long _heldSinceTicks;
    private static long _lastTickTicks;

    /// <summary>Last heartbeat, in <see cref="Stopwatch"/> ticks (0 = never ticked).</summary>
    public static long LastTickTicks => Volatile.Read(ref _lastTickTicks);

    /// <summary>The operation the UI thread last announced, or "" when idle.</summary>
    public static string Held => Volatile.Read(ref _held);

    /// <summary>When <see cref="Held"/> was entered, in Stopwatch ticks.</summary>
    public static long HeldSinceTicks => Volatile.Read(ref _heldSinceTicks);

    /// <summary>The heartbeat: one store. Called from <c>MainViewModel.OnUiTick</c>.</summary>
    public static void NoteUiTick() => Volatile.Write(ref _lastTickTicks, Stopwatch.GetTimestamp());

    /// <summary>
    /// Announce what the UI thread is about to do. Dispose the scope to restore
    /// the previous breadcrumb, so nesting keeps the outermost useful name.
    /// </summary>
    public static Scope Enter(string what)
    {
        var previous = Volatile.Read(ref _held);
        Volatile.Write(ref _held, what);
        Volatile.Write(ref _heldSinceTicks, Stopwatch.GetTimestamp());
        return new Scope(previous);
    }

    /// <summary>Clear the breadcrumb without restoring anything (idle path).</summary>
    public static void Exit() => Volatile.Write(ref _held, "");

    /// <summary>A breadcrumb's lifetime. A struct: <c>using var</c> allocates nothing.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string _previous;

        internal Scope(string previous) => _previous = previous;

        public void Dispose() => Volatile.Write(ref _held, _previous);
    }
}
