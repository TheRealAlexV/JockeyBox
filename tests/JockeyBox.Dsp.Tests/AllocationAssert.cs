namespace JockeyBox.Dsp.Tests;

/// <summary>
/// Measures heap allocation on the calling thread for one render-path body.
/// <para>
/// The body runs twice and only the second run is counted. The first run pays tiered-JIT
/// compilation and any first-touch cost, so a cold gate run measures the same thing a warm
/// local run does. Every allocation assertion in this project goes through here rather than
/// counting allocations around a call it is also the first ever to make.
/// </para>
/// </summary>
internal static class AllocationAssert
{
    /// <summary>
    /// Runs <paramref name="body"/> once to warm it, then returns the bytes the second run
    /// allocated on this thread.
    /// </summary>
    public static long Measure(Action body)
    {
        body();

        // Tier-1 compilation lands on a background thread some time after the call threshold,
        // so a warm pass can end while the promotion is still in flight and the counted pass
        // can then be charged a one-off install cost. Give it room to finish first.
        Thread.Sleep(100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        body();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
