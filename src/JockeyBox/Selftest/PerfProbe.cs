using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JockeyBox.Library;
using JockeyBox.ViewModels;
using JockeyBox.Views;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--perf [--perf-out &lt;txt&gt;]</c>: the numbers behind "browsing is laggy".
///
/// <para>Runs the real <see cref="MainWindow"/> in whatever session the process
/// started in (software rendering, no visible window, same rules as
/// <see cref="UiShot"/>), switches playlists through the real command, expands
/// the browser and scrolls it, and prints one <c>perf: key=value</c> line per
/// measurement plus the store's own load stats. Nothing here drives the mouse
/// or the operator's desktop.</para>
/// </summary>
internal static class PerfProbe
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--perf") != null;

    public static int Run(string[] args)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        PerfTrace.Enabled = true;

        var output = new List<string>();
        void Line(string text)
        {
            output.Add(text);
            Console.Out.WriteLine(text);
        }

        Line($"perf: run={DateTime.Now:yyyy-MM-dd HH:mm:ss} home={Environment.GetEnvironmentVariable("JOCKEYBOX_HOME")}");

        Window window;
        try
        {
            window = new MainWindow();
        }
        catch (Exception ex)
        {
            Line($"perf: window-failed {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -6000;
        window.Top = -6000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();

        int width = 1600, height = 1000;
        string? sizeText = Harness.Flag(args, "--perf-size");
        if (sizeText != null)
        {
            string[] parts = sizeText.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                width = Math.Clamp(w, 800, 5120);
                height = Math.Clamp(h, 600, 2880);
            }
        }

        var content = window.Content as FrameworkElement;
        if (content != null)
        {
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
        }

        var startup = Stopwatch.StartNew();
        var vm = window.DataContext as MainViewModel;
        if (vm is null)
        {
            Line("perf: no MainViewModel");
            return 1;
        }

        // First run on a fresh sandbox scans the fixture library; wait for it so
        // the switch numbers are measured against a non-empty library.
        int waitMs = 600000;
        var waited = UiPump.PumpUntil(() => vm.Tracks.Count > 0, waitMs);
        startup.Stop();
        UiPump.Pump(400);
        window.UpdateLayout();

        Line($"perf: startup total={startup.Elapsed.TotalMilliseconds:F0}ms tracks={vm.Tracks.Count} playlists={vm.PlaylistRail.Count} library-waited={waited}");

        var store = vm.PlaylistStoreForPerf;
        if (store?.LastLoadStats is { } load)
            Line($"perf: store {load.Summary}");
        else
            Line("perf: store stats unavailable");

        if (store != null)
        {
            var parse = Stopwatch.StartNew();
            var text = File.Exists(store.PerfFilePath) ? File.ReadAllText(store.PerfFilePath) : "";
            parse.Stop();
            var deser = Stopwatch.StartNew();
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var playlists = doc.RootElement.TryGetProperty("Playlists", out var pls) ? pls.GetArrayLength() : 0;
            var rows = 0;
            if (playlists > 0)
            {
                foreach (var p in pls.EnumerateArray())
                    rows += p.TryGetProperty("Tracks", out var tr) ? tr.GetArrayLength() : 0;
            }
            deser.Stop();
            Line($"perf: json bytes={text.Length} read={parse.Elapsed.TotalMilliseconds:F1}ms parse={deser.Elapsed.TotalMilliseconds:F1}ms playlists={playlists} rows={rows}");
        }

        // ---- playlist switches ---------------------------------------------
        var rowsByPlaylist = vm.PlaylistRail
            .Where(r => r.Playlist != null)
            .Select(r => (row: r, name: r.Playlist!.Name, stored: r.Playlist!.Tracks.Count))
            .OrderByDescending(x => x.stored)
            .ToList();

        Line($"perf: rail {string.Join(" | ", rowsByPlaylist.Select(x => $"{x.name}={x.stored}"))}");

        var treeCount = window.FindName("TrackTree") as TreeView;
        var switchResults = new List<(string Name, double VmMs, double SettleMs, int Stored)>();
        foreach (var entry in rowsByPlaylist)
        {
            // measure the VM-side switch (command + membership resolve)
            PerfTrace.Clear();
            var sw = Stopwatch.StartNew();
            vm.SelectPlaylistCommand.Execute(entry.row);
            var afterCommand = sw.Elapsed.TotalMilliseconds;
            UiPump.PumpUntil(() => !vm.IsPlaylistLoading, 120000);
            var settled = sw.Elapsed.TotalMilliseconds;

            window.UpdateLayout();
            var layout = Stopwatch.StartNew();
            content?.UpdateLayout();
            layout.Stop();

            int realized = treeCount == null ? 0 : CountContainers<TreeViewItem>(treeCount);
            Line($"perf: switch name={entry.name} stored={entry.stored} command={afterCommand:F1}ms settle={settled:F1}ms layout={layout.Elapsed.TotalMilliseconds:F1}ms realized={realized}");
            Line($"perf: switch phases {PerfTrace.Dump()}");
            switchResults.Add((entry.name, afterCommand, settled, entry.stored));
            UiPump.Pump(120);
        }

        // Warm repeats: every playlist is cached now, so this is the "clicking
        // around" cost the user actually feels.
        var warm = Stopwatch.StartNew();
        int warmSwitches = 0;
        for (int round = 0; round < 3; round++)
        {
            foreach (var entry in rowsByPlaylist)
            {
                vm.SelectPlaylistCommand.Execute(entry.row);
                UiPump.PumpUntil(() => !vm.IsPlaylistLoading, 120000);
                content?.UpdateLayout();
                warmSwitches++;
            }
        }
        warm.Stop();
        Line($"perf: warm switches={warmSwitches} total={warm.Elapsed.TotalMilliseconds:F0}ms per-switch={warm.Elapsed.TotalMilliseconds / Math.Max(1, warmSwitches):F1}ms");

        // ---- expand all + scroll -------------------------------------------
        var big = rowsByPlaylist.FirstOrDefault();
        if (big.row != null)
        {
            vm.SelectPlaylistCommand.Execute(big.row);
            UiPump.PumpUntil(() => !vm.IsPlaylistLoading, 120000);
            content?.UpdateLayout();

            var expand = Stopwatch.StartNew();
            var folders = 0;
            foreach (var root in vm.BrowserRoots.ToList())
                folders += ExpandAll(root);
            content?.UpdateLayout();
            expand.Stop();

            int realizedExpanded = treeCount == null ? 0 : CountContainers<TreeViewItem>(treeCount);
            Line($"perf: expand name={big.name} folders={folders} ms={expand.Elapsed.TotalMilliseconds:F0} realized={realizedExpanded}");

            var scroll = treeCount == null ? null : FindDescendant<ScrollViewer>(treeCount);
            if (scroll != null)
            {
                scroll.UpdateLayout();
                var extent = scroll.ExtentHeight;
                var viewport = scroll.ViewportHeight;
                Line($"perf: viewport={viewport:F0}px extent={extent:F0}px");

                // Two shapes, because they say different things:
                //  - flick: 40 jumps across the whole extent, the worst case for
                //    a recycle-per-jump panel;
                //  - wheel: 60 steps of 3 rows (66px), what a scrolling user's
                //    mouse wheel and trackpad actually deliver.
                var steps = 40;
                var scrollWatch = Stopwatch.StartNew();
                double worst = 0;
                for (int i = 1; i <= steps; i++)
                {
                    var step = Stopwatch.StartNew();
                    scroll.ScrollToVerticalOffset(extent * i / steps);
                    scroll.UpdateLayout();
                    step.Stop();
                    worst = Math.Max(worst, step.Elapsed.TotalMilliseconds);
                }
                scrollWatch.Stop();

                int realizedScrolled = treeCount == null ? 0 : CountContainers<TreeViewItem>(treeCount);

                scroll.ScrollToTop();
                scroll.UpdateLayout();
                var wheelSteps = 60;
                var wheelWatch = Stopwatch.StartNew();
                double wheelWorst = 0;
                for (int i = 0; i < wheelSteps; i++)
                {
                    var step = Stopwatch.StartNew();
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 66);
                    scroll.UpdateLayout();
                    step.Stop();
                    wheelWorst = Math.Max(wheelWorst, step.Elapsed.TotalMilliseconds);
                }
                wheelWatch.Stop();

                Line($"perf: scroll steps={steps} total={scrollWatch.Elapsed.TotalMilliseconds:F0}ms avg={scrollWatch.Elapsed.TotalMilliseconds / steps:F1}ms worst={worst:F1}ms realized-end={realizedScrolled}");
                Line($"perf: scroll.wheel steps={wheelSteps} total={wheelWatch.Elapsed.TotalMilliseconds:F0}ms avg={wheelWatch.Elapsed.TotalMilliseconds / wheelSteps:F1}ms worst={wheelWorst:F1}ms realized-end={(treeCount == null ? 0 : CountContainers<TreeViewItem>(treeCount))}");
            }
        }

        // ---- full-document save cost (the path every mutation used to take) --
        if (store != null && big.row?.Playlist is { } saveTarget)
        {
            var saveWatch = Stopwatch.StartNew();
            store.Save();
            saveWatch.Stop();
            Line($"perf: save.sync total={saveWatch.Elapsed.TotalMilliseconds:F1}ms {store.LastSaveStatsForPerf}");
        }

        // ---- mutation cost: create a scratch list, add 3 rows, delete it ------
        // A mutation is what the user feels when they drag a track onto a
        // playlist; it used to rewrite the whole 4.9 MB document on the UI
        // thread. The scratch list is deleted again so the fixture stays clean.
        if (store != null)
        {
            var scratch = store.Create("Perf Mut");
            var rows = Enumerable.Range(0, 3).Select(i => new Sources.SourceTrack
            {
                Id = $"perf:{Guid.NewGuid():N}",
                Title = $"Perf row {i}",
                Artist = "Perf",
                PlaylistName = scratch.Name,
                State = Sources.SourceTrackState.New,
            }).ToList();

            var addWatch = Stopwatch.StartNew();
            store.AddTracks(scratch, rows);
            addWatch.Stop();
            Line($"perf: mutation add3 ui-ms={addWatch.Elapsed.TotalMilliseconds:F1} rows={scratch.Tracks.Count} last-save={store.LastSaveStatsForPerf ?? "deferred"}");

            UiPump.Pump(1200);
            Line($"perf: mutation settled last-save={store.LastSaveStatsForPerf ?? "deferred"}");

            var deleteWatch = Stopwatch.StartNew();
            store.Remove(scratch);
            deleteWatch.Stop();
            Line($"perf: mutation delete ui-ms={deleteWatch.Elapsed.TotalMilliseconds:F1}");
            UiPump.Pump(1200);
        }

        // ---- drag burst: 20 reorders inside the 5 272-row list ---------------
        // A drag is the mutation users actually repeat: every move used to
        // serialise the whole 4.9 MB document. Each pair of moves restores the
        // order, so the fixture is unchanged afterwards.
        if (store != null && big.row?.Playlist is { } dragPlaylist && dragPlaylist.Tracks.Count > 64)
        {
            var rows = dragPlaylist.Tracks.Count;
            var writesBefore = store.JsonMirrorWritesForPerf;
            var dragWatch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                var from = (i * 37) % (rows - 2);
                store.MoveTrack(dragPlaylist, from, from + 1);
                store.MoveTrack(dragPlaylist, from + 1, from);
            }
            dragWatch.Stop();
            Line($"perf: drag moves=40 rows={rows} total={dragWatch.Elapsed.TotalMilliseconds:F0}ms per-move={dragWatch.Elapsed.TotalMilliseconds / 40:F1}ms");
            UiPump.Pump(1200);
            Line($"perf: drag mirror-writes={store.JsonMirrorWritesForPerf - writesBefore} last-save={store.LastSaveStatsForPerf ?? "deferred"}");

            // The flow that used to hurt most: edit the list, then switch away.
            // If the mirror's own write looked like an external edit, the next
            // switch would re-parse the whole document on the UI thread.
            var refreshWatch = Stopwatch.StartNew();
            var reloaded = store.RefreshFromDiskIfChanged();
            refreshWatch.Stop();
            Line($"perf: switch after-mutations reloaded={reloaded} check-ms={refreshWatch.Elapsed.TotalMilliseconds:F1}");
        }

        // ---- mutation write cost -------------------------------------------
        if (vm.PlaylistRail.FirstOrDefault(r => r.Playlist is { IsInternal: false })?.Playlist is { } target)
        {
            vm.SelectPlaylistCommand.Execute(vm.PlaylistRail.First(r => ReferenceEquals(r.Playlist, target)));
            UiPump.PumpUntil(() => !vm.IsPlaylistLoading, 120000);
            content?.UpdateLayout();

            var add = Stopwatch.StartNew();
            for (int i = 0; i < 3; i++)
            {
                store?.AddTracks(target, new[]
                {
                    new Sources.SourceTrack
                    {
                        Id = $"perf:{Guid.NewGuid():N}",
                        Title = $"Perf row {i}",
                        Artist = "Perf",
                        PlaylistName = target.Name,
                        State = Sources.SourceTrackState.New,
                    },
                });
            }
            add.Stop();
            Line($"perf: mutation add3 target={target.Name} rows={target.Tracks.Count} ui-ms={add.Elapsed.TotalMilliseconds:F1} store-rev={store?.Revision}");

            UiPump.Pump(2000);
            Line($"perf: mutation save {store?.LastSaveStatsForPerf ?? "n/a"}");
        }

        string? outPath = Harness.Flag(args, "--perf-out");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllLines(outPath, output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("perf: could not write " + outPath + ": " + ex.Message);
            }
        }

        Console.Out.Flush();
        window.Close();
        return 0;
    }

    private static int ExpandAll(BrowserNode node)
    {
        if (!node.IsFolder) return 0;
        node.IsExpanded = true;
        var count = 1;
        foreach (var child in node.Children.ToList()) count += ExpandAll(child);
        return count;
    }

    private static int CountContainers<T>(DependencyObject root) where T : DependencyObject
    {
        var count = 0;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) count++;
            count += CountContainers<T>(child);
        }
        return count;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deep = FindDescendant<T>(child);
            if (deep != null) return deep;
        }
        return null;
    }
}
