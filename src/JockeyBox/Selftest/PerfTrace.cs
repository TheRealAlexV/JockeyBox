using System.Diagnostics;

namespace JockeyBox.Selftest;

/// <summary>
/// Opt-in phase timings for the <c>--perf</c> probe. Off in every normal run:
/// <see cref="Measure"/> returns a default scope and <see cref="Record"/> is
/// never called, so the only production cost is one static bool read.
///
/// <para>This is deliberately not a general profiler. It answers one question -
/// "which phase of a playlist switch costs the milliseconds?" - by accumulating
/// named phases so a 5 000-row switch can be attributed without guessing.</para>
/// </summary>
public static class PerfTrace
{
    public static bool Enabled { get; set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (double Total, double Max, int Count)> Phases = new(StringComparer.Ordinal);

    public static void Clear()
    {
        lock (Gate) Phases.Clear();
    }

    /// <summary>Start a named phase; dispose to record it. No-op when disabled.</summary>
    public static Scope Measure(string name) => Enabled ? new Scope(name) : default;

    public static void Record(string name, double ms)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            Phases.TryGetValue(name, out var entry);
            Phases[name] = (entry.Total + ms, Math.Max(entry.Max, ms), entry.Count + 1);
        }
    }

    /// <summary>One line per phase, sorted by total time, for the probe's stdout.</summary>
    public static string Dump()
    {
        List<KeyValuePair<string, (double Total, double Max, int Count)>> rows;
        lock (Gate) rows = Phases.ToList();

        return string.Join(" ", rows
            .OrderByDescending(r => r.Value.Total)
            .Select(r => $"{r.Key}={r.Value.Total:F1}ms/{r.Value.Count}(max {r.Value.Max:F1})"));
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;

        internal Scope(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name is null) return;
            Record(_name, (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency);
        }
    }
}
