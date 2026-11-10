using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.Views;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--uishot &lt;file.png&gt; [--uishot-wait ms]</c>: builds the real main window,
/// lays it out, and writes a PNG of it with no desktop involved.
///
/// <para><b>Why.</b> Verification must never drive the operator's console session
/// (mouse, focus, windows). This runs in whatever session the process started in -
/// including a non-interactive service session - with software rendering, so a
/// screenshot is produced without a visible window and without a logged-on user
/// watching it happen.</para>
/// </summary>
internal static class UiShot
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--uishot") != null;

    public static int Run(string[] args)
    {
        string? path = Harness.Flag(args, "--uishot");
        if (string.IsNullOrWhiteSpace(path)) return 2;

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // This method closes the window before it returns its exit code, and the
        // default OnLastWindowClose mode shuts the application down on that close:
        // App's Shutdown(code) then arrives too late and the process reports 0.
        // Measured: a bad --uishot-playlist printed its error and exited 0, so a
        // gate reading the exit code would have called a failed capture a success.
        // Pin the mode so Shutdown(code) is the only thing that ends the process
        // and 2 (bad args), 3 (clamped) and 4 (unsettled) mean what they say.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Derived from Harness.QuietMs: a capture needs the pre-pump plus up to
        // three quiet windows, so the old 2500 fixed budget could not cover a
        // re-raise at the top of the documented 200-700 ms distribution.
        int waitMs = Harness.DefaultWaitMs;
        string? waitText = Harness.Flag(args, "--uishot-wait");
        if (waitText != null && int.TryParse(waitText, out int parsed)) waitMs = Math.Clamp(parsed, 200, 60000);

        string target = (Harness.Flag(args, "--uishot-window") ?? "main").ToLowerInvariant();

        // Provenance: a capture is only evidence if you know which settings file
        // the window read. Record home, resolved path and its timestamp before the
        // window is constructed, so a later rewrite of settings.json cannot be
        // mistaken for the state in the picture.
        string settingsPath = Settings.AppSettings.SettingsPath;
        string settingsStamp = File.Exists(settingsPath)
            ? File.GetLastWriteTime(settingsPath).ToString("yyyy-MM-dd HH:mm:ss")
            : "absent";
        Console.Out.WriteLine(
            $"uishot: home {AppPaths.Home}, settings {settingsPath}, last-write {settingsStamp}");
        Console.Out.Flush();

        Window window = target == "settings"
            ? new SettingsWindow(Settings.AppSettings.Load())
            : new MainWindow();

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -6000;
        window.Top = -6000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;

        // Opt-in: the console starts collapsed, so its job rows are never laid out
        // and a capture (or the text dump) cannot show them. Expanding it before
        // the pump makes those rows part of the layout the settle then covers.
        if (Harness.Flag(args, "--uishot-console") != null && window is MainWindow mainWindow)
            mainWindow.ConsoleToggle.IsChecked = true;



        window.Show();

        // Settle, do not sleep. The library body sits under a "Loading playlist…"
        // border while MainViewModel.IsPlaylistLoading is true, and a fixed pump
        // races that load: the capture then shows the overlay and reads as a stuck
        // app. Poll the flag the overlay binds, inside the same --uishot-wait
        // budget, and say so loudly when the budget runs out instead of writing a
        // PNG that lies about what the app was doing.
        string? playlistName = Harness.Flag(args, "--uishot-playlist");
        if (playlistName != null && window.DataContext is not MainViewModel)
        {
            Console.Error.WriteLine(
                $"uishot: --uishot-playlist needs the main window (target '{target}' has no MainViewModel)");
            Console.Error.Flush();
            window.Close();
            return 2;
        }

        var settleWatch = Stopwatch.StartNew();
        bool settled = true;

        // Named in the settle line so a future reader can tell "settled in 50 ms
        // because nothing was ever busy" from "settled because the scan finished".
        string waitedOn = "fixed pump (no view model)";
        string busyState = "no view model to watch";
        // Both reported in the settle line: the quiet window that was required, and
        // whether it was ever interrupted by a flag rising inside it.
        string quietNote = "";
        bool roseInQuietWindow = false;
        if (window.DataContext is MainViewModel settleVm)
        {
            if (playlistName != null)
            {
                // Same command a click runs, so the captured state is the real
                // busy overlay and then the resolved set - never a hand-built one.
                // Match on the row's display title, not on Playlist: the ALL
                // TRACKS row is the one row whose Playlist is null (its title is
                // the literal "ALL TRACKS", UserPlaylist.cs PlaylistRailItem.Title),
                // and it is the row every capture of the library needs. Matching
                // Playlist.Name alone made that row unselectable by any spelling.
                string wanted = playlistName.Trim();
                var row = settleVm.PlaylistRail.FirstOrDefault(r =>
                    string.Equals(r.Title, wanted, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    // The inventory is every row the rail shows, INCLUDING the
                    // null-playlist one, so a typo reports what exists instead of
                    // hiding the row the caller most likely wanted.
                    string available = string.Join(", ", settleVm.PlaylistRail.Select(r => r.Title));
                    Console.Error.WriteLine(
                        $"uishot: no playlist named '{wanted}' (available: {(available.Length == 0 ? "none" : available)})");
                    Console.Error.Flush();
                    window.Close();
                    return 2;
                }

                settleVm.SelectPlaylistCommand.Execute(row);
            }

            // One frame first, so work already queued (startup scan, a selection
            // restored by the window) has had the chance to raise the flags before
            // the first reading; otherwise the capture settles one frame before
            // the load it was meant to wait for even starts.
            UiPump.Pump(Harness.PrePumpMs);

            // Opt-in: a state that appears after startup work the two settle flags
            // do not watch (the beat analyser probes for python a few seconds in,
            // then disables itself) would otherwise be photographed before it
            // existed. A fixed pre-settle pump waits for it without inventing one
            // settle condition per subsystem.
            string? delayText = Harness.Flag(args, "--uishot-delay");
            if (delayText != null && int.TryParse(delayText, out int delayMs) && delayMs > 0)
                UiPump.Pump(Math.Clamp(delayMs, 0, 30000));

            // Two independent surfaces, composed: a playlist's membership resolving,
            // and the library rescan that fills the tree. Neither covers the other -
            // a bare --uishot starts no playlist load, so keying off
            // IsPlaylistLoading alone returns in one frame while a cold-start
            // rescan is still building a partly populated tree.
            //
            // A single reading is not a state. Both flags are debounced: the scan
            // clears IsLibraryScanning, then the "scan completed" invalidation
            // reloads the playlist Harness.ReRaiseWorstMs later and raises
            // IsPlaylistLoading again. A capture that stops at the first "both
            // false" lands in that gap and photographs the empty body under
            // "Loading playlist...". So settled means QUIET: both flags false and
            // held false for Harness.QuietMs while the dispatcher keeps pumping.
            // If either rises inside the window, that reading was a gap, not a
            // settle: the window restarts inside the same budget. The window is
            // derived from the re-raise worst case (Harness.cs), because a bare
            // literal here has already been set below the latency it covers.
            quietNote = $" ({Harness.QuietMs}ms quiet)";
            bool sawPlaylistLoad = false, sawLibraryScan = false;
            bool quiet = false;
            while (true)
            {
                int remaining = waitMs - (int)settleWatch.ElapsedMilliseconds;
                if (remaining <= 0) break;

                bool idle = UiPump.PumpUntil(() =>
                {
                    if (settleVm.IsPlaylistLoading) sawPlaylistLoad = true;
                    if (settleVm.IsLibraryScanning) sawLibraryScan = true;
                    return !settleVm.IsPlaylistLoading && !settleVm.IsLibraryScanning;
                }, remaining);
                if (!idle) break;

                var quietWatch = Stopwatch.StartNew();
                bool rose = false;
                while (quietWatch.ElapsedMilliseconds < Harness.QuietMs)
                {
                    UiPump.Pump(25);
                    if (settleVm.IsPlaylistLoading) { sawPlaylistLoad = true; rose = true; }
                    if (settleVm.IsLibraryScanning) { sawLibraryScan = true; rose = true; }
                    if (rose) break;
                }

                if (!rose) { quiet = true; break; }
                roseInQuietWindow = true;
                // Fall through: the flags rose inside the window, so that window
                // proved nothing. Wait for idle again inside the same budget.
            }

            // Re-check at the end: the quiet loop can only leave with both false,
            // but reading the flags once more here keeps the claim and the
            // evidence in one place.
            settled = quiet
                && !settleVm.IsPlaylistLoading
                && !settleVm.IsLibraryScanning;

            waitedOn = sawPlaylistLoad || sawLibraryScan
                ? string.Join("+", new[]
                  {
                      sawPlaylistLoad ? "playlist-load" : null,
                      sawLibraryScan ? "library-scan" : null,
                  }.Where(part => part is not null))
                : "nothing outstanding";
            busyState =
                $"IsPlaylistLoading={settleVm.IsPlaylistLoading}, " +
                $"IsLibraryScanning={settleVm.IsLibraryScanning}";
        }
        else
        {
            // No view model to watch (the settings window): the old fixed pump is
            // all there is, and hanging on nothing would be worse.
            UiPump.Pump(waitMs);
        }

        settleWatch.Stop();
        long settleMs = settleWatch.ElapsedMilliseconds;

        window.UpdateLayout();
        UiPump.Pump(200);

        // The render is driven at an explicit size so the result does not depend
        // on the window station's virtual screen (a service session reports
        // 1024x768 and would otherwise clip the layout).
        int width = 1400;
        int height = 900;
        string? sizeText = Harness.Flag(args, "--uishot-size");
        if (sizeText != null)
        {
            string[] parts = sizeText.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                width = Math.Clamp(w, 320, 5120);
                height = Math.Clamp(h, 240, 2880);
            }
        }

        // Opt-in second render path (Variant B): no detach, no re-host. The window is
        // already shown, so give it the target size, force a layout pass on it, and
        // render the window visual itself. Both PNGs come from the same settled state
        // in one run, which is what makes the comparison evidence and not anecdote.
        RenderTargetBitmap? directBitmap = null;
        string? directPath = null;
        string directNote = string.Empty;
        if (Harness.Flag(args, "--uishot-direct") != null)
        {
            directPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                Path.GetFileNameWithoutExtension(path) + "-direct.png");
            try
            {
                window.Measure(new Size(width, height));
                window.Arrange(new Rect(0, 0, width, height));
                window.UpdateLayout();

                var direct = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                string rendered;
                try
                {
                    direct.Render(window);
                    rendered = "window";
                }
                catch (Exception windowError)
                {
                    // A visual that was never presented cannot be rendered as a window
                    // (no presentation source). Fall back to the content, still in
                    // place, so the run yields an image instead of nothing.
                    if (window.Content is not Visual inPlace)
                    {
                        throw new InvalidOperationException(
                            $"window render failed ({windowError.Message}) and window.Content is not a Visual",
                            windowError);
                    }
                    direct.Render(inPlace);
                    rendered = $"window.Content in place (window render failed: {windowError.Message})";
                }

                directBitmap = direct;
                directNote = $"rendered {rendered}, window arranged " +
                    $"{OneDecimal(window.ActualWidth)}x{OneDecimal(window.ActualHeight)}";
            }
            catch (Exception ex)
            {
                directPath = null;
                Console.Error.WriteLine($"uishot: direct render failed, primary capture continues - {ex.Message}");
                Console.Error.Flush();
            }
        }

        // The window owns an HWND, and the service session clamps its client area
        // (1024x768 desktop minus chrome). Layout has to leave that tree entirely:
        // detach the content and host it in a Border that is its own layout root,
        // then lay that out at the requested size. Measuring content while it is
        // still parented is discarded, because UpdateLayout() walks up to the HWND
        // root and re-arranges everything at the client size.
        if (window.Content is not FrameworkElement content)
        {
            Console.Error.WriteLine("uishot: window has no framework content element to render");
            Console.Error.Flush();
            window.Close();
            return 3;
        }

        window.Content = null;
        // Re-parenting drops the inherited DataContext: it came from the Window,
        // and the Border below is not one. Without this the hosted tree binds to
        // nothing, so every list renders empty and every "busy" overlay falls back
        // to Visible - a capture of a fully loaded app that shows "Loading
        // playlist..." over an empty grid. Hand the view model to the new root so
        // the PNG shows the app rather than the detach.
        var host = new Border
        {
            Background = window.Background,
            Child = content,
            DataContext = window.DataContext,
        };
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        UiPump.Pump(150);

        // Opt-in, after the settle and before the render: a pixel scan cannot tell
        // a binding that resolved to nothing from one that resolved and then laid
        // out at zero width, because both leave no glyphs behind. The visual tree
        // separates them, so keep the dump rather than re-deriving it per capture.
        string? textDumpPath = Harness.Flag(args, "--uishot-textdump");
        if (!string.IsNullOrWhiteSpace(textDumpPath)) DumpTextBlocks(host, textDumpPath);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        double arrangedWidth = host.ActualWidth;
        double arrangedHeight = host.ActualHeight;
        bool clamped = Math.Abs(arrangedWidth - width) > 1 || Math.Abs(arrangedHeight - height) > 1;

        // A frame captured before the app settled is evidence of the harness, not
        // of the app, so it must not leave a file behind: any consumer that reads
        // "the PNG exists" as success would be reading a lie. The encode still
        // happens, only the write is gated on settle. stderr below still names the
        // path it would have written.
        if (settled)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = File.Create(path);
            encoder.Save(stream);

            if (directBitmap != null && directPath != null)
            {
                var directEncoder = new PngBitmapEncoder();
                directEncoder.Frames.Add(BitmapFrame.Create(directBitmap));
                using (var directStream = File.Create(directPath))
                {
                    directEncoder.Save(directStream);
                }
                long directBytes = new FileInfo(directPath).Length;
                Console.Out.WriteLine(
                    $"uishot: direct {width}x{height} ({directNote}) {directBytes} bytes -> {directPath}");
            }
        }
        else if (directBitmap != null)
        {
            // Same rule as the primary frame: a picture of an unsettled app is not
            // evidence, so the direct variant must not leave a file behind either.
            Console.Error.WriteLine($"uishot: direct PNG not written, capture unsettled - {directPath}");
            Console.Error.Flush();
        }

        Console.Out.WriteLine(
            $"uishot: canvas {width}x{height}, arranged {arrangedWidth}x{arrangedHeight} -> {path}");
        Console.Out.WriteLine(
            $"uishot: settle {settleMs}ms of {waitMs}ms budget{quietNote}, waited on {waitedOn}" +
            $"{(playlistName == null ? "" : $", playlist '{playlistName}'")}" +
            $"{(roseInQuietWindow ? ", a flag rose inside the quiet window (window restarted)" : "")}" +
            $"{(settled ? "" : " (budget exhausted)")}");
        if (clamped)
        {
            Console.Error.WriteLine(
                $"uishot: CLAMPED - requested {width}x{height}, arranged {arrangedWidth}x{arrangedHeight}");
            Console.Error.Flush();
        }

        if (!settled)
        {
            // A picture taken before the app finished loading is evidence of the
            // harness, not of the app. Say which surface never settled and fail,
            // the way CLAMPED does for size.
            Console.Error.WriteLine(
                $"uishot: UNSETTLED - {target} window still loading after {waitMs}ms " +
                $"({busyState}{(playlistName == null ? "" : $", playlist '{playlistName}'")}); " +
                $"{path} is not evidence");
            Console.Error.Flush();
        }

        Console.Out.Flush();

        window.Close();
        if (!settled) return 4;
        return clamped ? 3 : 0;
    }

    /// <summary>
    /// <c>--uishot-textdump &lt;file&gt;</c> support: one tab-separated line per
    /// <see cref="TextBlock"/> in the hosted tree, depth-first, then a summary.
    ///
    /// <para><b>Why.</b> A capture showing no glyphs has three causes that the
    /// pixels cannot separate: the binding never got a DataContext, it resolved to
    /// nothing, or it resolved and then laid out at zero width. Each leaves the
    /// same empty space, so the tree has to be read instead of the PNG. Kept as a
    /// switch rather than a throwaway patch so the next such question costs a run
    /// and not an edit.</para>
    /// </summary>
    private static void DumpTextBlocks(Visual? root, string path)
    {
        int total = 0, nullContext = 0, emptyText = 0, textWithoutWidth = 0;
        var lines = new StringBuilder();

        void Walk(DependencyObject? node, int depth)
        {
            if (node == null) return;

            if (node is TextBlock textBlock)
            {
                total++;
                string text = textBlock.Text ?? string.Empty;
                if (textBlock.DataContext == null) nullContext++;
                if (text.Length == 0) emptyText++;
                if (text.Length > 0 && textBlock.ActualWidth <= 0) textWithoutWidth++;

                lines.Append(depth.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(string.IsNullOrEmpty(textBlock.Name) ? "-" : textBlock.Name).Append('\t')
                    .Append(Escaped(text)).Append('\t')
                    .Append(OneDecimal(textBlock.ActualWidth)).Append('x')
                    .Append(OneDecimal(textBlock.ActualHeight)).Append('\t')
                    .Append(textBlock.Visibility.ToString()).Append('\t')
                    .Append(textBlock.DataContext?.GetType().Name ?? "null").Append('\t')
                    .Append(BrushName(textBlock.Foreground)).Append('\t')
                    .Append(RootOffset(textBlock, root)).Append('\t')
                    .Append(ClipState(textBlock, root)).Append('\n');
            }

            if (node is not Visual && node is not System.Windows.Media.Media3D.Visual3D) return;

            int childCount;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(node);
            }
            catch (Exception)
            {
                // Not every DependencyObject is walkable as a visual.
                return;
            }

            for (int i = 0; i < childCount; i++)
            {
                DependencyObject? child;
                try
                {
                    child = VisualTreeHelper.GetChild(node, i);
                }
                catch (Exception)
                {
                    continue;
                }
                Walk(child, depth + 1);
            }
        }

        Walk(root, 0);

        lines.Append("summary\ttotal ").Append(total)
            .Append("\tnull-datacontext ").Append(nullContext)
            .Append("\tempty-text ").Append(emptyText)
            .Append("\ttext-with-zero-width ").Append(textWithoutWidth)
            .Append('\n');

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, lines.ToString());
            Console.Out.WriteLine($"uishot: textdump {total} TextBlock(s) -> {path}");
        }
        catch (Exception ex)
        {
            // The dump is a diagnostic, not the product: a path that cannot be
            // written must not abort the capture it was asked to explain.
            Console.Error.WriteLine($"uishot: textdump failed, capture continues - {path}: {ex.Message}");
            Console.Error.Flush();
        }
    }

    /// <summary>Top-left of the element in the hosted root's coordinates, or <c>not-connected</c>.</summary>
    private static string RootOffset(TextBlock textBlock, Visual? root)
    {
        if (root == null) return "not-connected";
        try
        {
            Point origin = textBlock.TransformToAncestor(root).Transform(new Point(0, 0));
            return $"{OneDecimal(origin.X)},{OneDecimal(origin.Y)}";
        }
        catch (Exception)
        {
            // TransformToAncestor throws when the two are not in one visual tree.
            return "not-connected";
        }
    }

    /// <summary>
    /// <c>clipped</c> when the element falls outside a boundary that can cut it off,
    /// else <c>inside</c>. A boundary is the hosted root itself (the render surface)
    /// plus any ancestor whose <see cref="UIElement.ClipToBounds"/> is set or that has
    /// a non-null <see cref="UIElement.Clip"/>; a non-rectangular clip is treated as
    /// its bounding box, which is enough to separate "outside" from "inside".
    /// </summary>
    private static string ClipState(TextBlock textBlock, Visual? root)
    {
        if (root is not FrameworkElement rootElement) return "unknown";

        Rect region = new Rect(0, 0, rootElement.ActualWidth, rootElement.ActualHeight);
        Rect bounds;
        try
        {
            Point origin = textBlock.TransformToAncestor(rootElement).Transform(new Point(0, 0));
            bounds = new Rect(origin, new Size(textBlock.ActualWidth, textBlock.ActualHeight));
        }
        catch (Exception)
        {
            return "unknown";
        }

        DependencyObject? current = VisualTreeHelper.GetParent(textBlock);
        while (current != null)
        {
            if (current is FrameworkElement element && (element.ClipToBounds || element.Clip != null))
            {
                try
                {
                    Point origin = element.TransformToAncestor(rootElement).Transform(new Point(0, 0));
                    region.Intersect(new Rect(origin, new Size(element.ActualWidth, element.ActualHeight)));
                }
                catch (Exception)
                {
                    return "unknown";
                }
            }

            if (ReferenceEquals(current, rootElement)) break;
            current = VisualTreeHelper.GetParent(current);
        }

        return region.Contains(bounds) ? "inside" : "clipped";
    }

    /// <summary>One field of the dump: newlines and tabs escaped, empty as <c>''</c>, 60 chars max.</summary>
    private static string Escaped(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "''";

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.Length > 60 ? sb.ToString(0, 60) : sb.ToString();
    }

    private static string OneDecimal(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string BrushName(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        null => "null",
        _ => brush.GetType().Name,
    };
}
