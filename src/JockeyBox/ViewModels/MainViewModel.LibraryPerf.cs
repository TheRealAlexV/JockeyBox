using JockeyBox.Sources;

namespace JockeyBox.ViewModels;

/// <summary>
/// Measurement hooks for <c>--perf</c> (<see cref="Selftest.PerfProbe"/>). Kept in
/// their own partial so the production view model does not grow probe fields:
/// everything here is a read-only view of state the UI already owns, plus one
/// mutation helper that calls the same public store API a command calls.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The playlist store the browser reads, for the perf probe.</summary>
    public PlaylistStore PlaylistStoreForPerf => _playlistStore;
}
