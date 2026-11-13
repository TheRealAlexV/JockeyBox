namespace JockeyBox.Lyrics;

/// <summary>
/// Per-lookup record of what each provider actually answered, so one log line can
/// explain a miss ("lrcmux 404, spotify-color no match, lrclib 404") instead of
/// leaving the operator to re-run the chain by hand.
///
/// <para><b>Why ambient.</b> <see cref="ILyricsProvider"/> returns a document or
/// null and nothing else; the status code never leaves the provider. Threading an
/// outcome sink through every provider signature would change the interface for
/// diagnostics only. An <see cref="AsyncLocal{T}"/> sink set by the service and
/// written by the providers keeps that detail out of the interface while staying
/// per-lookup: a lyrics fetch and a candidate search running at the same time each
/// see their own notes, and nothing leaks between tracks.</para>
///
/// <para>Notes are dropped when no lookup is active, so a provider used outside
/// <see cref="LyricsService"/> pays one null check per note and writes nothing.</para>
/// </summary>
internal static class LyricsTrace
{
    private static readonly AsyncLocal<List<(string Provider, string Outcome)>?> Current = new();

    /// <summary>Start collecting notes for the calling async flow.</summary>
    public static Scope Begin() => new(new List<(string Provider, string Outcome)>());

    /// <summary>Record one provider outcome for the active lookup, if any.</summary>
    public static void Note(string providerId, string outcome)
    {
        var sink = Current.Value;
        if (sink == null) return;

        // One note per provider per lookup: the first decisive reason is the
        // useful one, later "empty" fallbacks would only bury it.
        foreach (var (p, _) in sink)
            if (p == providerId) return;
        sink.Add((providerId, outcome));
    }

    public readonly struct Scope : IDisposable
    {
        private readonly List<(string Provider, string Outcome)>? _previous;

        internal Scope(List<(string Provider, string Outcome)> sink)
        {
            _previous = Current.Value;
            Current.Value = sink;
            Notes = sink;
        }

        /// <summary>Notes recorded so far, in provider order; empty when none.</summary>
        public IReadOnlyList<(string Provider, string Outcome)> Notes { get; }

        public void Dispose() => Current.Value = _previous;
    }
}
