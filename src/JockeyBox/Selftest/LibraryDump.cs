using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using JockeyBox.Library;
using JockeyBox.Tools;
using JockeyBox.ViewModels;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--uishot-dump [--uishot-playlist &lt;name&gt;] [--uishot-wait ms]</c>: one
/// stdout line per library row, printing the values the row bindings read.
///
/// <para><b>Why.</b> <c>library.json</c> is the TagCache, so its <c>Bpm: 0</c>
/// means "the file states no TBPM tag", not "the resolver fell through". The
/// resolved BPM and key exist only on the loaded row objects, and those are what
/// a capture can only show as pixels. This reads the same objects the tree binds
/// (<see cref="BrowserNode.Track"/> leaves under
/// <see cref="MainViewModel.BrowserRoots"/>, the source of the row templates in
/// Views/Shared/TrackRows.xaml) and prints <c>Track.BpmText</c> and
/// <c>Track.KeyText</c> with the provenance behind it (<c>src=tag|estimate|none</c>),
/// so the answer is not a reimplementation of the
/// resolvers and cannot disagree with the column.</para>
///
/// <para>Waits on the same two flags as <see cref="UiShot"/> with the same quiet
/// window, because a dump read in the gap between the scan clearing and the
/// playlist invalidation would print an empty library and prove nothing.</para>
/// </summary>
internal static class LibraryDump
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--uishot-dump") != null;

    public static int Run(string[] args)
    {
        int waitMs = Harness.DumpDefaultWaitMs;
        string? waitText = Harness.Flag(args, "--uishot-wait");
        if (waitText != null && int.TryParse(waitText, out int parsed)) waitMs = Math.Clamp(parsed, 200, 60000);

        string? playlistName = Harness.Flag(args, "--uishot-playlist");
        // --uishot-dump's value is the path the caller asked for. The file is
        // additive: stdout keeps every line it printed before, so a consumer
        // parsing stdout is unaffected. A blank value means stdout only.
        string? dumpPath = Harness.Flag(args, "--uishot-dump");
        if (string.IsNullOrWhiteSpace(dumpPath)) dumpPath = null;
        var mirror = dumpPath is null ? null : new StringBuilder();

        // Which binary printed these rows, before any row: the mirror file carries
        // the same lines, so a dump read off disk is attributable by itself.
        string[]? identity = CaptureIdentity.Header();
        if (identity is null)
        {
            Console.Error.WriteLine("dump: UNIDENTIFIED - cannot read the running exe, so the rows would be unattributable");
            Console.Error.Flush();
            return CaptureIdentity.UnidentifiedExitCode;
        }
        foreach (string line in identity) Echo(line);

        Echo($"dump: home {AppPaths.Home}");
        Console.Out.Flush();

        var vm = new MainViewModel();
        var watch = Stopwatch.StartNew();

        string? error = WaitQuiet(vm, waitMs - (int)watch.ElapsedMilliseconds);
        if (error is not null)
        {
            Console.Error.WriteLine($"dump: UNSETTLED - {error}");
            Console.Error.Flush();
            return 4;
        }

        string selected = "ALL TRACKS";
        if (playlistName != null)
        {
            string wanted = playlistName.Trim();
            // Same matcher as --uishot: on the row's display title, so the
            // null-playlist ALL TRACKS row is selectable.
            var row = vm.PlaylistRail.FirstOrDefault(r =>
                string.Equals(r.Title, wanted, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                string available = string.Join(", ", vm.PlaylistRail.Select(r => r.Title));
                Console.Error.WriteLine(
                    $"dump: no playlist named '{wanted}' (available: {(available.Length == 0 ? "none" : available)})");
                Console.Error.Flush();
                return 2;
            }

            // The real command a click runs: the rows printed are the rows the
            // tree then holds, not a filter this tool applies itself.
            vm.SelectPlaylistCommand.Execute(row);
            selected = row.Title;

            error = WaitQuiet(vm, waitMs - (int)watch.ElapsedMilliseconds);
            if (error is not null)
            {
                Console.Error.WriteLine($"dump: UNSETTLED - {error}");
                Console.Error.Flush();
                return 4;
            }
        }

        var leaves = new List<BrowserNode>();
        foreach (var root in vm.BrowserRoots) CollectLeaves(root, leaves);

        Echo($"dump: {leaves.Count} rows, playlist '{selected}', settled {watch.ElapsedMilliseconds}ms");

        foreach (var leaf in leaves)
        {
            // Name, Artist, BpmText and Key are the exact binding paths in
            // LibraryNodeRow; "-" marks a blank so an unresolved column is visible
            // instead of an empty field the reader has to interpret.
            TrackEntry? track = leaf.Track;
            string title = Blank(leaf.Name);
            string artist = Blank(track?.Artist ?? "");
            string bpm = Blank(track?.BpmText ?? "");
            // The cell's own text, so the marker is provable without pixels, plus
            // the provenance behind it: tag = the file states the key, estimate =
            // the analyser guessed it. "-" means no key at all.
            string key = Blank(track?.KeyText ?? "");
            string src = track is null || string.IsNullOrEmpty(track.Key)
                ? "none"
                : track.KeyEstimated ? "estimate" : "tag";
            Echo($"row: {title} | {artist} | bpm={bpm} | key={key} | src={src}");
        }

        Console.Out.Flush();

        if (mirror is not null)
        {
            try
            {
                // UTF-8 without BOM: titles carry apostrophes and "feat." punctuation,
                // and a default encoding mangles them for whoever reads the file.
                string full = Path.GetFullPath(dumpPath!);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(full, mirror.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // A caller that asked for a file and got none must not see exit 0.
                Console.Error.WriteLine($"dump: cannot write '{dumpPath}' - {ex.Message}");
                Console.Error.Flush();
                return 3;
            }
        }

        return 0;

        void Echo(string line)
        {
            Console.Out.WriteLine(line);
            mirror?.Append(line).Append('\n');
        }

        static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? "-" : text.Trim();
    }

    private static void CollectLeaves(BrowserNode node, List<BrowserNode> leaves)
    {
        if (node.Track != null)
        {
            leaves.Add(node);
            return;
        }
        foreach (var child in node.Children) CollectLeaves(child, leaves);
    }

    /// <summary>
    /// Both flags false and held false for a quiet window, re-checked at the end.
    /// Returns null when quiet, else the live flag values as the failure text.
    /// </summary>
    private static string? WaitQuiet(MainViewModel vm, int budgetMs)
    {
        var watch = Stopwatch.StartNew();
        int remaining = budgetMs;

        while (true)
        {
            remaining = budgetMs - (int)watch.ElapsedMilliseconds;
            if (remaining <= 0) return Busy(vm);

            bool idle = UiPump.PumpUntil(() => !vm.IsPlaylistLoading && !vm.IsLibraryScanning, remaining);
            if (!idle) return Busy(vm);

            var quietWatch = Stopwatch.StartNew();
            bool rose = false;
            while (quietWatch.ElapsedMilliseconds < Harness.QuietMs)
            {
                UiPump.Pump(25);
                if (vm.IsPlaylistLoading || vm.IsLibraryScanning) { rose = true; break; }
            }

            if (!rose && !vm.IsPlaylistLoading && !vm.IsLibraryScanning) return null;
            // A flag rose inside the window: that was a gap, not a settle.
        }
    }

    private static string Busy(MainViewModel vm) =>
        $"IsPlaylistLoading={vm.IsPlaylistLoading}, IsLibraryScanning={vm.IsLibraryScanning}";
}
