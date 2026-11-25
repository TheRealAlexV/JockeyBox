using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JockeyBox.Activity;
using JockeyBox.Audio.Graph;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Sources;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using JockeyBox.Views.Shell;
using JockeyBox.YouTube;
using ActivityKind = JockeyBox.Activity.ActivityKind;

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

        // Before the window exists: a capture's numbers mean nothing without the
        // binary that produced them (see CaptureIdentity).
        string[]? identity = CaptureIdentity.Header();
        if (identity is null)
        {
            Console.Error.WriteLine(
                "uishot: UNIDENTIFIED - cannot read the running exe, so the capture would be unattributable");
            Console.Error.Flush();
            return CaptureIdentity.UnidentifiedExitCode;
        }
        foreach (string line in identity) Console.Out.WriteLine(line);
        Console.Out.Flush();

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

        // The private pop-out lyrics window is gone: LYRIC is a dock panel and leaves
        // through --uishot-popout Lyrics. Keep the retired name loud rather than letting
        // it fall through to the default and photograph the main window under a name
        // that claims to be something else.
        if (target == "lyricswindow")
        {
            Console.Error.WriteLine(
                "uishot: lyricswindow is retired; use --uishot-popout Lyrics (and --uishot-popout-return <file>)");
            Console.Error.Flush();
            return 3;
        }

        Window window = target switch
        {
            "settings" => new SettingsWindow(Settings.AppSettings.Load()),
            // Diagnostics against a fresh engine: an idle engine is the honest state of a
            // process that has not been asked to play anything, and it still exercises
            // every read on the panel.
            "diag" => new DiagnosticsWindow(new AudioEngine(), Settings.AppSettings.Load()),
            "youtubesearch" => new YouTubeSearchWindow(StubYouTubeSearch(Harness.Flag(args, "--uishot-audition"))),
            "youtubetab" => new MainWindow(),
            "spotifytab" => new MainWindow(),
            _ => new MainWindow(),
        };

        // --uishot-audition on the Spotify tab: SpotifyView.xaml binds its strip to
        // SpotifyViewModel.Preview, so the same stub state goes there. Nothing is
        // streamed; this is the strip's layout for one state.
        //   idle      - loaded, no length: the disabled slider.
        //   buffering - the reported state: 3:19 known, 0:43 in, plus a status line.
        //   nomedia   - nothing loaded: the tab's hint, and no slider.
        if (target == "spotifytab" && Harness.Flag(args, "--uishot-audition") is { } spotifyAudition
            && window.DataContext is MainViewModel spotifyViewModel)
        {
            PreviewBarViewModel strip = spotifyViewModel.Spotify.Preview;
            switch (spotifyAudition)
            {
                case "nomedia":
                    strip.IsAvailable = false;
                    break;
                case "idle":
                    strip.IsAvailable = true;
                    strip.DurationSeconds = 0;
                    strip.PositionSeconds = 0;
                    break;
                default:
                    strip.IsAvailable = true;
                    strip.DurationSeconds = 199;
                    strip.PositionSeconds = 43;
                    strip.IsPlaying = true;
                    if (spotifyAudition == "buffering") strip.StatusText = "Buffering 100%\u2026";
                    break;
            }
        }

        // A tab capture has to open on its tab. Selecting it before Show is how
        // the app looks the moment the tab is clicked, and needs no posted input:
        // this window is shown offscreen and never activated, so a synthesized
        // click would land on whatever has focus on the desktop instead.
        string? tabHeader = target switch
        {
            "youtubetab" => "YOUTUBE",
            "spotifytab" => "SPOTIFY",
            _ => null,
        };

        if (tabHeader != null && !SelectBrowserTab(window, tabHeader))
        {
            Console.Error.WriteLine($"uishot: no browser tab headed {tabHeader}");
            Console.Error.Flush();
            return 3;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -6000;
        window.Top = -6000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;

        // Opt-in: the mixer dock starts on its strips page and the jobs rows are only
        // laid out when their page is showing, so a capture (or the text dump) cannot
        // show them. Selecting the jobs page before the pump makes those rows part of
        // the layout the settle then covers.
        if (Harness.Flag(args, "--uishot-console") != null && window is MainWindow mainWindow)
            mainWindow.DockTabJobs.IsChecked = true;

        // Shell panes: the rail is a pointer surface, so a capture that has to show a
        // pane asks for it by name instead. Each flag is the same call the rail makes.
        if (window is MainWindow panes)
        {
            if (Harness.Flag(args, "--uishot-library") != null) panes.SetLibraryOpen(true);
            if (Harness.Flag(args, "--uishot-dock") is { } dockPage) panes.SelectDockPage(dockPage.ToLowerInvariant());
            if (Harness.Flag(args, "--uishot-flyout") is { } flyoutSection)
                panes.OpenFlyoutSection(flyoutSection.ToLowerInvariant());
        }




        // --uishot-rows: fill the YouTube tab's list with the same literal rows the
        // search window uses, so the row template, its density and its clipping are
        // photographable. Without it every YouTube capture shows an empty list and
        // nothing about a row has ever been proven. No search, no network, no yt-dlp.
        if (Harness.Flag(args, "--uishot-rows") != null && window.DataContext is MainViewModel rowsVm)
        {
            foreach (var entry in StubSearchRows) rowsVm.YouTube.Results.Add(entry);
            rowsVm.YouTube.SelectedResult = rowsVm.YouTube.Results[0];
        }

        // --uishot-spotify-rows N: fill the Spotify tab's table with N synthetic
        // rows. A virtualization claim needs a list long enough that realising all
        // of it costs something; a hundred rows would hide the difference between a
        // bounded viewport and an unbounded one. No API, no download, no ledger: the
        // same SourceTrack objects the tab binds, replaced in one Reset.
        if (Harness.Flag(args, "--uishot-spotify-rows") is { } spotifyRowsText
            && window.DataContext is MainViewModel spotifyRowsVm
            && int.TryParse(spotifyRowsText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int spotifyRows)
            && spotifyRows > 0)
        {
            var seeded = new List<SourceTrack>(spotifyRows);
            for (int i = 0; i < spotifyRows; i++)
            {
                seeded.Add(new SourceTrack
                {
                    Id = $"uishot-{i:D5}",
                    Source = MediaSource.Spotify,
                    Title = $"Synthetic Track {i + 1:D5}",
                    Artist = "Virtualization Fixture",
                    Album = "uishot",
                    DurationMs = 180_000 + (i % 137) * 1000,
                });
            }

            spotifyRowsVm.Spotify.Tracks.ReplaceAll(seeded);
            Console.Out.WriteLine($"uishot: spotify rows seeded {spotifyRows}");
            Console.Out.Flush();
        }

        // --uishot-activity: seed the console's JOBS list with the rows a karaoke
        // job produces, select one, and open its detail pane, so the row, the
        // selection border and the pane are photographed instead of asserted. No
        // engine, no files, no queue: the same rows a real job writes.
        if (Harness.Flag(args, "--uishot-activity") != null)
        {
            var feed = ActivityFeed.Shared;
            var running = feed.Start(
                ActivityKind.Karaoke,
                "karaoke:uishot-running",
                "Daft Punk - Get Lucky",
                source: "karaoke",
                outputPath: @"C:\Music\Daft Punk\Get Lucky.karaoke.mp3");
            feed.Update(running, DownloadState.Converting, 62, "62%");

            var created = feed.Start(
                ActivityKind.Karaoke,
                "karaoke:uishot-created",
                "Fleetwood Mac - Dreams",
                source: "karaoke",
                outputPath: @"C:\Music\Fleetwood Mac\Dreams.karaoke.mp3");
            feed.Finish(created, DownloadState.Done, "done");
            feed.SelectedRow = created;
        }

        // First frame is its own step with its own bound, and it is the signal the
        // settle was waiting on without naming it: the only thing that waited for
        // the first render was the pre-pump, and its cost landed inside the settle
        // budget. A cold SoftwareOnly main window measures ~7.6 s here, so the
        // default 3000 ms budget was already gone when the settle loop took its
        // first reading: it broke at "nothing outstanding" and exited UNSETTLED
        // with both flags false the whole time. Render cost is not settle cost.
        bool firstFrameRendered = false;
        var firstFrameWatch = Stopwatch.StartNew();
        window.ContentRendered += (_, _) => firstFrameRendered = true;
        window.Show();
        if (!UiPump.PumpUntil(() => firstFrameRendered, Harness.FirstFrameMs))
        {
            Console.Error.WriteLine(
                $"uishot: UNPRESENTED - {target} window rendered no first frame within {Harness.FirstFrameMs}ms");
            Console.Error.Flush();
            window.Close();
            return 4;
        }
        long firstFrameMs = firstFrameWatch.ElapsedMilliseconds;

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
        // Never true by construction: every branch below has to earn it, so a window
        // that is not ready fails the capture instead of passing on a sleep.
        bool settled = false;

        // Named in the settle line so a future reader can tell "settled in 50 ms
        // because nothing was ever busy" from "settled because the scan finished".
        // Both are overwritten on every path below; "unset" means a branch forgot.
        string waitedOn = "unset";
        string busyState = "unset";
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

            // The budget starts here, not before the window was shown or the
            // pre-pump ran: --uishot-wait is time allowed to become idle and stay
            // idle, and lap-time spent presenting frames or honouring an explicit
            // --uishot-delay is neither.
            settleWatch.Restart();

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
            // These windows have no MainViewModel, so there is no IsPlaylistLoading
            // to frame them. A fixed pump is a sleep wearing a settle's name - the
            // last captures reported "3015ms of 3000ms" from exactly that. Each
            // window names the state that is false until its data lands, and the
            // capture waits on it inside the same budget, then requires the signal
            // to hold for a quiet window as the main path does.
            (Func<bool> Ready, Func<string> State)? signal = null;
            if (window is SettingsWindow settingsWindow)
            {
                // The dialog stamps its controls in the constructor, so there is
                // nothing pending there by the time Show() returns. What is pending
                // is the post-layout pass: RenderMicRows queues its rendered flag at
                // DispatcherPriority.Loaded, which cannot have run before the
                // dispatcher does. False at start, true once the window finished
                // its own deferred work.
                signal = (() => settingsWindow.MicListRendered,
                          () => $"MicList rendered={settingsWindow.MicListRendered}");
                waitedOn = "settings post-layout pass (MicList)";
            }
            else if (window.DataContext is DiagnosticsViewModel diagVm)
            {
                // Refresh() runs once in the constructor; the window's 120 ms ticker
                // makes it two only after Loaded and a live dispatcher. One read is
                // construction, two is the running panel.
                signal = (() => diagVm.RefreshCount >= 2,
                          () => $"diagnostics reads={diagVm.RefreshCount}");
                waitedOn = "diagnostics snapshot read (2 of 2)";
            }

            if (signal is not { } watch)
            {
                // Nothing to wait on: no signal this window can offer. Do not invent
                // one that is true by construction - fail and say so.
                settled = false;
                waitedOn = $"none: window '{target}' offers no ready signal";
                busyState = "unknown";
            }
            else
            {
                bool readyReached = UiPump.PumpUntil(watch.Ready, waitMs);
                bool quiet = readyReached;
                if (readyReached)
                {
                    var quietWatch = Stopwatch.StartNew();
                    while (quietWatch.ElapsedMilliseconds < Harness.QuietMs)
                    {
                        UiPump.Pump(25);
                        if (!watch.Ready())
                        {
                            quiet = false;
                            break;
                        }
                    }
                }

                quietNote = $" ({Harness.QuietMs}ms quiet)";
                settled = readyReached && quiet;
                if (!readyReached) waitedOn += " (still waiting)";
                busyState = watch.State() + (quiet ? "" : ", signal fell inside the quiet window");
            }
        }

        settleWatch.Stop();
        long settleMs = settleWatch.ElapsedMilliseconds;

        // --uishot-track <title>: load a real library track so the lyrics panel
        // has a document in it. Without this every lyrics capture shows the empty
        // state ("Load a track to fetch lyrics") and proves nothing about how the
        // lines actually render.
        if (Harness.Flag(args, "--uishot-track") is { } wantedTrack
            && window.DataContext is MainViewModel trackVm)
        {
            var hit = trackVm.Tracks.FirstOrDefault(t =>
                t.Title.Contains(wantedTrack.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                Console.Error.WriteLine($"uishot: no library track titled '{wantedTrack}'");
                Console.Error.Flush();
                window.Close();
                return 2;
            }

            trackVm.SelectedTrack = hit;
            trackVm.LoadSelectedTrackCommand.Execute(null);
            bool gotLines = UiPump.PumpUntil(() => trackVm.LyricLines.Count > 0, 30000);
            Console.Out.WriteLine(
                $"uishot: track \"{hit.Artist} - {hit.Title}\" lines={trackVm.LyricLines.Count} "
                + $"ok={gotLines} status={trackVm.LyricsStatus}");

            // The deck opens the track on a worker, so a dump taken in the gap
            // photographs a card that is empty by timing rather than by design -
            // 0:00 / 0:00 and no KARAOKE row on a track that has a partner. Wait
            // for the deck itself to report loaded, then read the flags back so
            // the capture says which of the two states it saw.
            var decks = new[] { ("A", trackVm.DeckA), ("B", trackVm.DeckB) };
            var loaded = decks.FirstOrDefault(d => d.Item2.IsLoaded) is { Item2: not null } onDeck
                ? onDeck
                : decks[0];
            bool deckLoaded = UiPump.PumpUntil(() => loaded.Item2.IsLoaded, 30000);
            // One more quiet window: the flags land after the readers open, which
            // is a further worker hop behind "loaded".
            UiPump.Pump(Harness.QuietMs);
            Console.Out.WriteLine(
                $"uishot: deck {loaded.Item1} loaded={deckLoaded} IsLoaded={loaded.Item2.IsLoaded} "
                + $"HasKaraoke={loaded.Item2.HasKaraoke} readout=\"{loaded.Item2.TimeReadout}\"");
            Console.Out.Flush();
        }

        // --uishot-pair <title>: start the karaoke pair for a library row, because
        // a pair is the only state that renders both deck labels ("Deck A:
        // Karaoke" / "Deck B: Original"). The titles are printed here as text, so
        // the capture carries the rendered strings, not just a picture of them.
        if (Harness.Flag(args, "--uishot-pair") is { } pairTitle
            && window.DataContext is MainViewModel pairVm)
        {
            var pairHit = pairVm.Tracks.FirstOrDefault(t =>
                t.Title.Contains(pairTitle.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pairHit is null)
            {
                Console.Error.WriteLine($"uishot: no library track titled '{pairTitle}'");
                Console.Error.Flush();
                window.Close();
                return 2;
            }

            pairVm.StartKaraokePairForCapture(pairHit);
            bool bothLoaded = UiPump.PumpUntil(() => pairVm.DeckA.IsLoaded && pairVm.DeckB.IsLoaded, 30000);
            UiPump.Pump(Harness.QuietMs);
            Console.Out.WriteLine(
                $"uishot: pair \"{pairHit.Title}\" loaded={bothLoaded} " +
                $"A=\"{pairVm.DeckA.Title}\" (IsLoaded={pairVm.DeckA.IsLoaded}) " +
                $"B=\"{pairVm.DeckB.Title}\" (IsLoaded={pairVm.DeckB.IsLoaded})");
            Console.Out.Flush();
        }


        // --uishot-both-decks <title>: the same track's original on both decks, which
        // is the only state where both cards draw the karaoke strip's stem trim.
        // --uishot-pair cannot produce it (its deck A holds the karaoke render, which
        // has no sub-stem), and equal card heights only mean something at the worse
        // of the two states.
        if (Harness.Flag(args, "--uishot-both-decks") is { } bothTitle
            && window.DataContext is MainViewModel bothVm)
        {
            var bothHit = bothVm.Tracks.FirstOrDefault(t =>
                t.Title.Contains(bothTitle.Trim(), StringComparison.OrdinalIgnoreCase));
            if (bothHit is null)
            {
                Console.Error.WriteLine($"uishot: no library track titled '{bothTitle}'");
                Console.Error.Flush();
                window.Close();
                return 2;
            }

            bothVm.StartBothDecksForCapture(bothHit);
            bool bothLoaded = UiPump.PumpUntil(() => bothVm.DeckA.IsLoaded && bothVm.DeckB.IsLoaded, 30000);
            UiPump.Pump(Harness.QuietMs);
            Console.Out.WriteLine(
                $"uishot: both-decks \"{bothHit.Title}\" loaded={bothLoaded} " +
                $"A=\"{bothVm.DeckA.Title}\" (IsLoaded={bothVm.DeckA.IsLoaded} HasKaraoke={bothVm.DeckA.HasKaraoke}) " +
                $"B=\"{bothVm.DeckB.Title}\" (IsLoaded={bothVm.DeckB.IsLoaded} HasKaraoke={bothVm.DeckB.HasKaraoke})");
            Console.Out.Flush();
        }

        // window rules - one instance however often it is asked for, and no
        // survivor once the main window closes. A capture cannot show this: the
        // pop-out is a second HWND and only one window is ever in the bitmap.
        // Checked on LYRIC because it is the pane that used to own a private window;
        // PanelWindow is the single path now, so the rule is verified where it lives.
        if (Harness.Flag(args, "--uishot-popout-check") != null && window is MainWindow popoutMain)
        {
            int PanelCount() => Application.Current.Windows.OfType<PanelWindow>()
                .Count(w => w.Title == "LYRIC");

            popoutMain.PopOutPanel(ShellPanelId.Lyrics);
            UiPump.Pump(200);
            int first = PanelCount();

            // Second ask must focus the existing window, not open another.
            popoutMain.PopOutPanel(ShellPanelId.Lyrics);
            UiPump.Pump(200);
            int second = PanelCount();

            window.Close();
            UiPump.Pump(300);
            int afterMainClosed = PanelCount();

            Console.Out.WriteLine(
                $"uishot: popout first={first} second={second} after-main-close={afterMainClosed}");
            Console.Out.Flush();

            return first == 1 && second == 1 && afterMainClosed == 0 ? 0 : 5;
        }

        // Opt-in: the browser opens with its folders collapsed, so a library capture
        // shows folder rows and never a track row - and the Key column only exists on
        // track rows. Expanding the model now, after the scan has built the tree and
        // before the render, is the same IsExpanded walk PerfProbe uses to measure a
        // full tree: no click, no desktop, and no product code in the path.
        if (Harness.Flag(args, "--uishot-expand") != null && window.DataContext is MainViewModel expandVm)
            foreach (var root in expandVm.BrowserRoots) ExpandFolders(root);

        // Opt-in: the playlist edit strip is folded away on a card that has not been
        // opened for editing, so a Spotify capture shows the list and never the strip.
        // Opening it before the render goes through the toggle's own click path: a
        // routed RaiseEvent(ButtonBase.ClickEvent) never reaches ToggleButton.OnClick,
        // and assigning IsChecked skips the style triggers that read that same
        // property, so either shortcut photographs the folded state and reads as a
        // product bug. SpotifyView carries no x:Name in MainWindow, so the view is
        // reached by walking the visual tree, the same way YouTubeView is.
        if (Harness.Flag(args, "--uishot-spotify-edit") != null)
        {
            if (Descendants<SpotifyView>(window).FirstOrDefault() is not { } spotify)
            {
                Console.Error.WriteLine(
                    "uishot: spotify-edit MISSING SpotifyView, is the Spotify tab the one laid out?");
                Console.Error.Flush();
                window.Close();
                return 6;
            }

            if (spotify.FindName("EditFoldToggle") is not ToggleButton edit)
            {
                Console.Error.WriteLine("uishot: spotify-edit MISSING EditFoldToggle in SpotifyView");
                Console.Error.Flush();
                window.Close();
                return 6;
            }

            // In-process automation peer: this is an AutomationPeer object, not
            // desktop UIA, so it calls OnClick with no input queue, no focus change
            // and no logged-on session involved.
            var editPeer = UIElementAutomationPeer.CreatePeerForElement(edit);
            if (editPeer?.GetPattern(PatternInterface.Toggle) is not IToggleProvider editToggle)
            {
                Console.Error.WriteLine(
                    $"uishot: spotify-edit MISSING IToggleProvider peer={editPeer?.GetType().Name ?? "null"}");
                Console.Error.Flush();
                window.Close();
                return 6;
            }

            editToggle.Toggle();
            UiPump.Pump(200);

            // Evidence in text, so the open state does not depend on eyeballing the
            // PNG: the strip is the WrapPanel nearest above the toggle, and it is
            // that trigger, not the click, that decides whether it is on screen.
            var card = spotify.FindName("TrackCard") as FrameworkElement;
            WrapPanel? strip = null;
            for (DependencyObject? cursor = edit; cursor is not null;
                   cursor = VisualTreeHelper.GetParent(cursor))
                if (Descendants<WrapPanel>(cursor).FirstOrDefault() is { } found)
                {
                    strip = found;
                    break;
                }

            Console.Out.WriteLine(
                "uishot: spotify-edit toggle=found pattern=IToggleProvider OnClick=ran "
                + $"IsChecked={edit.IsChecked} "
                + $"card-height={card?.ActualHeight.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/a"} "
                + $"strip={strip?.Visibility.ToString() ?? "n/a"}");
            Console.Out.Flush();
        }

        window.UpdateLayout();
        UiPump.Pump(200);

        static void ExpandFolders(JockeyBox.Library.BrowserNode node)
        {
            node.IsExpanded = true;
            foreach (var child in node.Children.ToList()) ExpandFolders(child);
        }

        // Every track node in the browser tree, folders flattened away. Read-only
        // walk of the model, so it sees the same rows the tree binds.
        static IEnumerable<JockeyBox.Library.BrowserNode> FlattenTrackNodes(
            IEnumerable<JockeyBox.Library.BrowserNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Track is not null) yield return node;
                foreach (var child in FlattenTrackNodes(node.Children)) yield return child;
            }
        }

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

        // Opt-in: a right-click menu cannot appear in any capture path below. A
        // ContextMenu is a Popup with its own HWND and its own visual tree, and the
        // primary capture detaches the window content and renders it alone, so the
        // menu is never part of that tree. Open the real menu here, while the window
        // is still live, render it and its open submenu as separate bitmaps, and
        // composite them onto the window image afterwards. Nothing here runs for a
        // normal shot or for the application itself: it is behind --uishot-menu.
        BitmapSource? menuShot = null;
        BitmapSource? submenuShot = null;
        // Set when --uishot-menu could not open the submenu it was asked for: the
        // capture then exists but proves nothing, so the run must not report success.
        bool menuBlocked = false;
        double menuX = 0;
        double menuY = 0;
        double submenuX = 0;
        double submenuY = 0;
        if (Harness.Flag(args, "--uishot-menu") != null)
        {
            var live = window.Content as DependencyObject;

            // Which submenu this run proves: "play", "queue" or "karaoke" (the bare
            // flag, kept for the runs that already exist). One submenu per run,
            // because the composite carries one submenu bitmap; the pair of PNGs is
            // the evidence, and each run says in its log which one it opened.
            var which = Harness.Flag(args, "--uishot-menu");
            which = string.IsNullOrWhiteSpace(which) ? "karaoke" : which.Trim().ToLowerInvariant();

            // Play and Add to Queue only become submenus when the row has more than
            // one take, and the take lists are filled by OnSelectedTreeNodeChanged
            // -> RefreshTrackMenuVariants. So select the richest row on the model,
            // the way --uishot-expand expands folders: no click, no desktop, no
            // product code in the path. Without it the menu carries the flat
            // single-item Play and there is no submenu to open at all.
            if (window.DataContext is MainViewModel menuVm)
            {
                var richest = FlattenTrackNodes(menuVm.BrowserRoots)
                    .OrderByDescending(node => node.Track?.Variants.Count ?? 0)
                    .FirstOrDefault(node => (node.Track?.Variants.Count ?? 0) > 1);
                if (richest is not null)
                {
                    menuVm.SelectedTreeNode = richest;
                    window.UpdateLayout();
                    UiPump.Pump(150);
                    Console.Out.WriteLine(
                        $"uishot: menu row '{richest.Title}' takes={richest.Track!.Variants.Count}" +
                        $" badges={string.Join(" ", richest.Track.Variants.Badges.Select(b => b.Icon))}" +
                        $" play={menuVm.TrackMenu.PlayVariants.Count} queue={menuVm.TrackMenu.QueueVariants.Count}");
                    Console.Out.Flush();
                }
                else
                {
                    Console.Error.WriteLine("uishot: --uishot-menu found no row carrying more than one take");
                    Console.Error.Flush();
                }
            }
            // Pick the surface by the menu it carries, not by control type: several
            // trees in this window have their own menus (the queue's, the playlist
            // rail's) and only the track list carries the karaoke submenu. Choosing
            // the first TreeView found captured the wrong menu entirely.
            var hosts = live is null
                ? new List<FrameworkElement>()
                : Descendants<FrameworkElement>(live)
                    .Where(e => e.ContextMenu?.Items.OfType<MenuItem>().Any(i =>
                        (i.Header as string)?.StartsWith("Make Karaoke", StringComparison.Ordinal) == true) == true)
                    .ToList();
            var owner = hosts.OfType<TreeView>().FirstOrDefault() ?? hosts.FirstOrDefault();
            var menu = owner?.ContextMenu;
            if (live is null || owner is null || menu is null)
            {
                Console.Error.WriteLine(
                    "uishot: --uishot-menu found no surface carrying a Make Karaoke Version menu");
                Console.Error.Flush();
            }
            else
            {
                var tree = owner;
                menu.PlacementTarget = tree;
                menu.Placement = PlacementMode.Relative;
                menu.HorizontalOffset = 40;
                menu.VerticalOffset = 60;
                menu.IsOpen = true;
                UiPump.Pump(300);

                menuShot = RenderElement(menu);
                // Play / Add to Queue / Make Karaoke Version each render twice: a flat
                // single item, and a submenu of takes. The submenu is the one holding
                // items, so match on that and not on the header alone.
                var submenuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i =>
                    which switch
                    {
                        "play" => (i.Header as string) == "Play" && i.Items.Count > 0,
                        "queue" => (i.Header as string) == "Add to Queue" && i.Items.Count > 0,
                        _ => (i.Header as string)?.StartsWith("Make Karaoke", StringComparison.Ordinal) == true
                             && i.Items.Count > 0,
                    });
                if (menuShot is null)
                {
                    Console.Error.WriteLine("uishot: --uishot-menu opened but the menu laid out at zero size");
                    Console.Error.Flush();
                }
                else if (submenuItem is null)
                {
                    // A run that proved nothing must not read as a run that proved
                    // the icons: name the menu it did get and fail.
                    Console.Error.WriteLine(
                        $"uishot: --uishot-menu={which} opened the menu but found no '{which}' submenu " +
                        $"(items: {string.Join(" | ", menu.Items.OfType<MenuItem>().Select(i => $"{i.Header}[{i.Items.Count}]"))})");
                    Console.Error.Flush();
                    menuBlocked = true;
                }
                else
                {
                    submenuItem.IsSubmenuOpen = true;
                    UiPump.Pump(300);
                    // The theme's MenuItem template declares its submenu Popup with no
                    // x:Name, so FindName("PART_Popup") resolves to nothing and the
                    // three karaoke entries were silently dropped from the capture.
                    // Walk the template's visual tree to the Popup instead.
                    var popup = Descendants<Popup>(submenuItem).FirstOrDefault()
                        ?? submenuItem.Template?.FindName("PART_Popup", submenuItem) as Popup;
                    if (popup?.Child is FrameworkElement submenu)
                    {
                        submenuShot = RenderElement(submenu);
                        // The submenu opens level with its own item, so anchor it there
                        // rather than at the top of the parent, which is where it would
                        // land if it were bolted to the corner.
                        submenuY = submenuItem.TranslatePoint(new Point(0, 0), menu).Y;

                        // The icons are text, so the log can carry the proof: one line
                        // per item with the mark it resolved to. Play and Add to Queue
                        // put their item records straight in Items; the Make Karaoke
                        // modes are MenuItems whose Header is such a record. The code
                        // point is printed beside the glyph because a console that
                        // cannot encode ◆ prints "?", which would hide which mark the
                        // item resolved to.
                        Console.Out.WriteLine(
                            $"uishot: submenu {which} items={submenuItem.Items.Count} " +
                            string.Join(", ", submenuItem.Items.OfType<object>().Select(t => t switch
                            {
                                TrackVariantItem item =>
                                    $"'{item.Icon}' {(item.Icon.Length == 1 ? $"U+{(int)item.Icon[0]:X4}" : "?")} {item.Header}",
                                MenuItem { Header: KaraokeModeMenuItem mode } =>
                                    $"'{mode.Icon}' {(mode.Icon.Length == 1 ? $"U+{(int)mode.Icon[0]:X4}" : "?")} {mode.Header}",
                                _ => "?",
                            })));
                        Console.Out.Flush();
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"uishot: --uishot-menu={which} submenu popup had no child to render");
                        Console.Error.Flush();
                        menuBlocked = true;
                    }
                }

                menu.IsOpen = false;
                UiPump.Pump(50);

                // Anchor over the tree the menu belongs to, from its live position,
                // so the composite reads as "this menu on that panel". The capture
                // re-lays the content out at its own size, so scale across.
                if (menuShot is not null)
                {
                    if (live is FrameworkElement liveElement
                        && liveElement.ActualWidth > 0 && liveElement.ActualHeight > 0)
                    {
                        Point corner = tree.TranslatePoint(new Point(0, 0), liveElement);
                        menuX = corner.X * (width / liveElement.ActualWidth) + 48;
                        menuY = corner.Y * (height / liveElement.ActualHeight) + 80;
                    }

                    // Keep the pair inside the canvas: the submenu opens to the right
                    // and would otherwise be cut off, and the parent menu was clipped
                    // at the bottom edge before this clamp existed.
                    double need = menuShot.PixelWidth + (submenuShot?.PixelWidth ?? 0) + 24;
                    if (menuX + need > width) menuX = Math.Max(8, width - need);
                    if (menuX < 8) menuX = 8;
                    if (menuY + menuShot.PixelHeight > height)
                        menuY = Math.Max(8, height - menuShot.PixelHeight);
                    if (menuY < 8) menuY = 8;

                    submenuX = menuX + menuShot.PixelWidth - 3;
                    submenuY += menuY;
                    if (submenuShot is not null && submenuY + submenuShot.PixelHeight > height)
                        submenuY = Math.Max(8, height - submenuShot.PixelHeight);
                }
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

        // --uishot-popout <panel>: undock a panel and capture the window it lands in.
        // This is the phase-1 proof: the mechanism is only worth building if a panel
        // can leave the dock and come back without its content being rebuilt.
        //
        // The return trip is captured too (--uishot-popout-return <file>): the window
        // is closed, PanelWindow.OnClosing hands the element back, and the dock is
        // rendered afterwards. A capture of the popped window cannot show that half -
        // the reopen happens on another HWND - and it is the half the whole design
        // hangs on.
        MainWindow? popoutShell = null;
        PanelWindow? poppedPanel = null;
        ShellPanelId popoutPanelId = default;
        FrameworkElement? poppedElement = null;

        if (Harness.Flag(args, "--uishot-popout") is { } popoutName
            && window is MainWindow popoutHost
            && Enum.TryParse<ShellPanelId>(popoutName, ignoreCase: true, out var popoutId)
            && popoutHost.PopOutPanel(popoutId) is { } poppedWindow)
        {
            popoutShell = popoutHost;
            poppedPanel = poppedWindow;
            popoutPanelId = popoutId;
            poppedElement = popoutHost.PanelHost.ContentOf<FrameworkElement>(popoutId);

            poppedWindow.Width = width;
            poppedWindow.Height = height;
            poppedWindow.UpdateLayout();
            UiPump.Pump(200);
            window = poppedWindow;
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
        // Opt-in: the laid-out box of each track list. A list squeezed shut holds no
        // text, so the text dump cannot show how much of it there is and "how many
        // rows fit" would be a guess rather than a measurement.
        if (Harness.Flag(args, "--uishot-measure") != null) MeasureTrackLists(host);

        // Opt-in, and the only honest test of "is virtualization on": the flag in
        // the XAML says what was asked for, not what the panel did. A virtualizing
        // panel with a viewport realizes the rows that fit; one measured at infinite
        // height realizes every row it was given, whatever its flags say.
        if (Harness.Flag(args, "--uishot-realized") != null) ReportRealized(host);

        // Opt-in layout probe: the numbers a PNG cannot show. "The window grew" is a
        // statement about extents, not about drawn ink, and a scrollbar is a few dark
        // pixels at the edge of a capture. Printing viewport against extent says which
        // surface is carrying the overflow, and printing each slider's mark strip says
        // whether the marks cost the deck card any height.
        if (Harness.Flag(args, "--uishot-probe") != null) ProbeLayout(host);

        string? textDumpPath = Harness.Flag(args, "--uishot-textdump");
        if (!string.IsNullOrWhiteSpace(textDumpPath)) DumpTextBlocks(host, textDumpPath, identity);

        // Opt-in, next to the text dump and for the same reason: a Border, a Grid or
        // a panel carries no text, so the text dump cannot say where a card's frame
        // sits and a PNG leaves it to the eye. Runs here, before the render, so the
        // numbers are laid out - and the capture still renders afterwards.
        string? treeDumpPath = Harness.Flag(args, "--uishot-tree");
        if (!string.IsNullOrWhiteSpace(treeDumpPath)) DumpVisualTree(host, treeDumpPath);

        // A tab that exists but renders nothing must not pass as a capture. The tabs
        // that moved into LIB are the ones that can go empty without the strip looking
        // wrong, so they are the ones checked, and an empty root is a hard failure
        // rather than a note in the log.
        if (tabHeader != null)
        {
            var tabRoot = RealisedTabContent(window, tabHeader);
            Console.Out.WriteLine(
                $"uishot: tab {tabHeader} root " +
                (tabRoot == null
                    ? "absent"
                    : $"{OneDecimal(tabRoot.ActualWidth)}x{OneDecimal(tabRoot.ActualHeight)}"));
            Console.Out.Flush();

            if (tabRoot == null || tabRoot.ActualWidth <= 0 || tabRoot.ActualHeight <= 0)
            {
                Console.Error.WriteLine(
                    $"uishot: tab {tabHeader} realised no content root; the capture is not evidence");
                Console.Error.Flush();
                window.Close();
                return 4;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        // The menu bitmaps were taken from the live popups above; lay them over the
        // window now, in one drawing pass, so the saved PNG is a single image of the
        // window with its menu open. Menu-less captures never enter this block.
        if (menuShot is not null)
        {
            var composed = new DrawingVisual();
            using (DrawingContext dc = composed.RenderOpen())
            {
                dc.DrawImage(bitmap, new Rect(0, 0, width, height));
                dc.DrawImage(menuShot, new Rect(menuX, menuY, menuShot.PixelWidth, menuShot.PixelHeight));
                if (submenuShot is not null)
                {
                    dc.DrawImage(submenuShot,
                        new Rect(submenuX, submenuY, submenuShot.PixelWidth, submenuShot.PixelHeight));
                }
            }

            var merged = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            merged.Render(composed);
            bitmap = merged;
            Console.Out.WriteLine(
                $"uishot: menu composited at {menuX:0},{menuY:0} " +
                $"({menuShot.PixelWidth}x{menuShot.PixelHeight}), submenu " +
                (submenuShot is null ? "absent" : $"{submenuShot.PixelWidth}x{submenuShot.PixelHeight}"));
        }

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
        // Row heights: the shell sizes its elastic rows by hand, so a capture that
        // clips has to be able to say which row lost the pixels.
        if (window is MainWindow shellWindow)
            Console.Out.WriteLine(
                $"uishot: rows documents={shellWindow.DocumentsRow.ActualHeight:0.#}" +
                $" ({shellWindow.DocumentsRow.Height.Value:0.#}{shellWindow.DocumentsRow.Height.GridUnitType})" +
                $" waveform={shellWindow.WaveformRow.ActualHeight:0.#}" +
                $" ({shellWindow.WaveformRow.Height.Value:0.#}{shellWindow.WaveformRow.Height.GridUnitType})" +
                $" dock={shellWindow.MixerDock.ActualHeight:0.#}" +
                $" body={shellWindow.DockBody.ActualHeight:0.#}" +
                $" viewport={shellWindow.ShellScroll.ViewportHeight:0.#}" +
                $" extent={shellWindow.ShellScroll.ExtentHeight:0.#}" +
                $" pane={shellWindow.PanelHost.ActualHeight:0.#}/{shellWindow.PanelHost.Height:0.#}" +
                $" panew={shellWindow.PanelHost.ActualWidth:0.#}" +
                $" body={shellWindow.ShellBody.ActualHeight:0.#}");
        Console.Out.WriteLine(
            $"uishot: settle {settleMs}ms of {waitMs}ms budget{quietNote}, first frame {firstFrameMs}ms, waited on {waitedOn}" +
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

        // --uishot-popout-return <file>: close the panel's window and capture the dock it
        // comes back to. This is the half a capture of the popped window cannot show -
        // PanelWindow.OnClosing -> ReturnContent -> dock reopens happens on the main
        // HWND - and the reference check below is what separates a returned panel from a
        // rebuilt one that merely looks the same.
        if (Harness.Flag(args, "--uishot-popout-return") is { } returnPath
            && popoutShell is not null && poppedPanel is not null && poppedElement is not null)
        {
            poppedPanel.Close();
            bool reopened = UiPump.PumpUntil(
                () => popoutShell.PanelHost.IsOpen
                      && popoutShell.PanelHost.Current == popoutPanelId, 5000);
            UiPump.Pump(250);
            popoutShell.UpdateLayout();

            var dock = popoutShell.PanelHost;
            var returned = dock.ContentOf<FrameworkElement>(popoutPanelId);
            int texts = 0;
            void CountText(DependencyObject? node)
            {
                if (node == null) return;
                if (node is TextBlock { Text.Length: > 0 }) texts++;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    CountText(VisualTreeHelper.GetChild(node, i));
            }
            CountText(dock);

            Console.Out.WriteLine(
                $"uishot: popout-return reopened={reopened}" +
                $" current={dock.Current}" +
                $" sameInstance={ReferenceEquals(returned, poppedElement)}" +
                $" slotIsSame={ReferenceEquals(dock.ContentHost.Content, poppedElement)}" +
                $" dockw={dock.ActualWidth:0.#} dockh={dock.ActualHeight:0.#}" +
                $" nonEmptyTextBlocks={texts}");

            if (reopened && dock.ActualWidth > 1 && dock.ActualHeight > 1)
            {
                int dockWidth = (int)Math.Ceiling(dock.ActualWidth);
                int dockHeight = (int)Math.Ceiling(dock.ActualHeight);
                var dockBitmap = new RenderTargetBitmap(dockWidth, dockHeight, 96, 96, PixelFormats.Pbgra32);
                dockBitmap.Render(dock);

                var dockEncoder = new PngBitmapEncoder();
                dockEncoder.Frames.Add(BitmapFrame.Create(dockBitmap));
                string? dockDirectory = Path.GetDirectoryName(Path.GetFullPath(returnPath));
                if (!string.IsNullOrEmpty(dockDirectory)) Directory.CreateDirectory(dockDirectory);
                using var dockStream = File.Create(returnPath);
                dockEncoder.Save(dockStream);
                Console.Out.WriteLine(
                    $"uishot: popout-return {dockWidth}x{dockHeight} -> {returnPath}");
            }
            else
            {
                Console.Error.WriteLine(
                    $"uishot: popout-return PNG not written - dock {(reopened ? "has no size" : "did not reopen")}");
                Console.Error.Flush();
            }
        }

        Console.Out.Flush();

        window.Close();
        if (!settled) return 4;
        if (menuBlocked) return 6;
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
    private static void DumpTextBlocks(Visual? root, string path, string[] identity)
    {
        int total = 0, nullContext = 0, emptyText = 0, textWithoutWidth = 0;
        var lines = new StringBuilder();
        foreach (string line in identity) lines.Append(line).Append('\n');

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
                    // The text field is the one thing this dump exists to show, so
                    // it is the one field exempt from the 60-char cap: a status
                    // that explains itself in 131 characters is unreadable at 60.
                    .Append(Escaped(text, cap: 0)).Append('\t')
                    .Append(OneDecimal(textBlock.ActualWidth)).Append('x')
                    .Append(OneDecimal(textBlock.ActualHeight)).Append('\t')
                    .Append(textBlock.Visibility.ToString()).Append('\t')
                    .Append(textBlock.DataContext?.GetType().Name ?? "null").Append('\t')
                    .Append(BrushName(textBlock.Foreground)).Append('\t')
                    .Append(RootOffset(textBlock, root)).Append('\t')
                    .Append(ClipState(textBlock, root)).Append('\t')
                    .Append(LineFit(textBlock, text)).Append('\t')
                    .Append(ParentBox(textBlock)).Append('\n');
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

    /// <summary>
    /// <c>--uishot-tree &lt;file&gt;</c> support: one tab-separated line per element of
    /// the rendered visual tree, in tree order.
    ///
    /// <para>Columns: <c>depth</c>, <c>type</c>, <c>name</c> (or <c>-</c>), <c>WxH</c>,
    /// <c>root-offset x,y</c>, <c>Visibility</c>, <c>ClipState</c>. A Border or a Grid
    /// carries no text, so the text dump cannot show where a card frame sits, and two
    /// readings of the same PNG have disagreed about exactly that. Same walk, same
    /// root-origin space as the text dump and the same
    /// <c>inside</c>/<c>clipped</c>/<c>hidden</c> vocabulary, so a frame line and the
    /// text line it encloses compare directly.</para>
    ///
    /// <para><c>hidden</c> is not geometry: a collapsed subtree keeps the arrange
    /// coordinates it had before whatever collapsed it, so it means "took no part in
    /// layout", never "outside the window".</para>
    /// </summary>
    private static void DumpVisualTree(Visual? root, string path)
    {
        if (root == null) return;

        var lines = new StringBuilder();
        int total = 0;
        int hidden = 0;

        void Emit(int depth, DependencyObject node, string name, string size, string offset,
            string visibility, string clip)
        {
            total++;
            if (clip == "hidden") hidden++;
            lines.Append(depth.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(node.GetType().Name).Append('\t')
                .Append(string.IsNullOrEmpty(name) ? "-" : name).Append('\t')
                .Append(size).Append('\t')
                .Append(offset).Append('\t')
                .Append(visibility).Append('\t')
                .Append(clip).Append('\n');
        }

        void Walk(DependencyObject? node, int depth)
        {
            if (node == null) return;

            // Borders stay in on purpose: ClipToBounds is false by default, so a
            // plain border clips nothing - its frame is the whole point here.
            if (node is FrameworkElement element)
            {
                Emit(depth, element, element.Name,
                    $"{OneDecimal(element.ActualWidth)}x{OneDecimal(element.ActualHeight)}",
                    RootOffset(element, root), element.Visibility.ToString(), ClipState(element, root));
            }
            else if (node is FrameworkContentElement content)
            {
                // Content elements carry no measure and no Visibility of their own,
                // and this walk does not reach one: both fields say so rather than
                // inventing 0x0 and Visible.
                Emit(depth, content, content.Name, "-", "-", "-", "unknown");
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

        string rootSize = root is FrameworkElement rootElement
            ? $"{OneDecimal(rootElement.ActualWidth)}x{OneDecimal(rootElement.ActualHeight)}"
            : "-";
        lines.Append("summary\ttotal ").Append(total.ToString(CultureInfo.InvariantCulture))
            .Append("\thidden ").Append(hidden.ToString(CultureInfo.InvariantCulture))
            .Append("\troot ").Append(rootSize).Append('\n');

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, lines.ToString());
            Console.Out.WriteLine(
                $"uishot: tree {total} element(s), {hidden} hidden, root {rootSize} -> {path}");
        }
        catch (Exception ex)
        {
            // The dump is a diagnostic, not the product: a path that cannot be
            // written must not abort the capture it was asked to explain.
            Console.Error.WriteLine($"uishot: tree failed, capture continues - {path}: {ex.Message}");
            Console.Error.Flush();
        }
    }

    /// <summary>Top-left of the element in the hosted root's coordinates, or <c>not-connected</c>.</summary>
    private static string RootOffset(FrameworkElement element, Visual? root)
    {
        if (root == null) return "not-connected";
        try
        {
            Point origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
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
    ///
    /// <para><c>hidden</c> when the block took no part in layout at all: its own
    /// visibility is not <see cref="Visibility.Visible"/>, or an ancestor up to the
    /// root is collapsed. Such a block keeps the arrange coordinates it had before
    /// whatever collapsed it, so "outside the window" describes a stale rectangle
    /// rather than the app, and reading it as clipped has produced two wrong
    /// conclusions in a row: a karaoke cluster called "wider than the card and cut
    /// by it" when the row did not exist, and deck B phantoms at x≈1041-1153 in a
    /// 1040-pixel-wide window. Named rather than dropped, so a block that really is
    /// cut still reaches the <c>clipped</c> arm below and this exclusion cannot pass
    /// for a mute.</para>
    /// </summary>
    private static string ClipState(FrameworkElement target, Visual? root)
    {
        if (root is not FrameworkElement rootElement) return "unknown";
        if (!LaidOut(target, rootElement)) return "hidden";

        Rect region = new Rect(0, 0, rootElement.ActualWidth, rootElement.ActualHeight);
        Rect bounds;
        try
        {
            Point origin = target.TransformToAncestor(rootElement).Transform(new Point(0, 0));
            bounds = new Rect(origin, new Size(target.ActualWidth, target.ActualHeight));
        }
        catch (Exception)
        {
            return "unknown";
        }

        DependencyObject? current = VisualTreeHelper.GetParent(target);
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

    /// <summary>
    /// Whether <paramref name="element"/> and every ancestor up to the captured root
    /// are <see cref="Visibility.Visible"/>, i.e. whether it was laid out at all.
    /// </summary>
    private static bool LaidOut(DependencyObject element, FrameworkElement root)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) break;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }

        return true;
    }

    /// <summary>
    /// The element a LIB tab is showing, so "the tab exists but is empty" cannot pass as
    /// a capture. Null when the tab is missing or carries no content element.
    /// </summary>
    private static FrameworkElement? RealisedTabContent(Window window, string header)
    {
        if (window is not MainWindow main) return null;
        var library = main.LibraryPane;
        if (library == null) return null;

        foreach (var item in library.LibraryTabs.Items)
        {
            if (item is TabItem tab
                && string.Equals(tab.Header as string, header, StringComparison.Ordinal))
            {
                return tab.Content as FrameworkElement;
            }
        }

        return null;
    }

    /// <summary>
    /// Select a tab by its header text. Header, not index: an inserted tab would
    /// silently change what a number means, and a capture that photographs the wrong
    /// tab is worse than no capture.
    ///
    /// <para>SPOTIFY and YOUTUBE are LIB tabs now, so the strip they live in is the
    /// library pane's, not the centre one - and the pane has to be open before its tabs
    /// exist to select. LYRICS is not a tab any more: it is the LYRIC dock panel, so
    /// "select" means open that panel.</para>
    /// </summary>
    private static bool SelectBrowserTab(Window window, string header)
    {
        if (window is not MainWindow main) return false;

        if (string.Equals(header, "LYRICS", StringComparison.Ordinal))
        {
            main.SetPanelOpen(ShellPanelId.Lyrics, true);
            return main.PanelHost.Current == ShellPanelId.Lyrics;
        }

        main.SetPanelOpen(ShellPanelId.Library, true);
        var library = main.LibraryPane;
        if (library == null) return false;

        foreach (var item in library.LibraryTabs.Items)
        {
            if (item is TabItem tab && string.Equals(tab.Header as string, header, StringComparison.Ordinal))
            {
                library.LibraryTabs.SelectedItem = tab;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>--uishot-window youtubesearch</c>: the Find on YouTube window with its
    /// results already listed.
    ///
    /// <para><b>Why literal rows.</b> A capture must be the same picture on every
    /// run, and this path must never reach the network: a real search would make
    /// the PNG depend on what YouTube returned that minute and would put yt-dlp
    /// inside the capture. The window only searches on open while its list is
    /// empty, so filling the list here is also what keeps the run offline.</para>
    ///
    /// <para><b>What it cannot show.</b> The audition strip at the bottom is bound
    /// to the shared preview transport's real state and only appears while that
    /// transport is streaming this window's own URL. Showing it would mean
    /// starting audio in a headless capture, so it is absent from the picture
    /// rather than faked.</para>
    /// </summary>
    private static YouTubeSearchViewModel StubYouTubeSearch(string? audition)
    {
        var vm = new YouTubeSearchViewModel(
            Settings.AppSettings.Load(),
            StreamPreviewTransport.CreateDefault(),
            "Daft Punk Around The World",
            "Daft Punk \u2014 Around The World",
            pick: _ => { });

        foreach (var entry in StubSearchRows) vm.Results.Add(entry);

        // The state the window is in right after its own search: first row selected.
        vm.SelectedResult = vm.Results[0];

        // --uishot-audition: the strip's own layout, from a stubbed state. Nothing
        // is streamed and no audio starts - the transport is untouched, so this
        // proves the strip lays out, not that a real preview works. Off by default
        // so the ordinary capture never shows a state the app was not in.
        //
        // The strip renders from vm.Strip (a PreviewBarViewModel), not from the
        // window VM, so the fake state goes there. A length and a position about a
        // third of the way in are what make IsSeekable true and the thumb sit off
        // the start; without them the picture is an idle 0:00 bar and proves
        // nothing about the seek UI. PreviewTitle still feeds Strip.Title through
        // OnPreviewTitleChanged, so the header follows the row being auditioned.
        // on purpose is what makes the delta visible: idle captures dim the whole
        // panel, so a dimmed slider inside one proves nothing.
        if (audition != null)
        {
            vm.PreviewTitle = StubSearchRows[0].Title;
            vm.HasPreview = true;
            vm.Strip.IsAvailable = true;

            if (audition == "idle")
            {
                // --uishot-audition idle: available with no known length, so
                // IsSeekable is false and the slider renders its disabled state
                // (Opacity 0.45 from the StripSlider trigger) while its parent
                // panel is NOT dimmed. That is the only arrangement in which the
                // designer's "disabled sliders dim to 45%" can be measured.
                vm.PreviewClock = "0:42 / --:--";
                vm.PlayGlyph = "\u25B6";
                vm.Strip.IsPlaying = false;
                vm.Strip.DurationSeconds = 0;
                vm.Strip.PositionSeconds = 0;
            }
            else if (audition == "buffering")
            {
                // --uishot-audition buffering: the reported state. A known length
                // (3:19), a moving position (0:43) AND a status line, which is the
                // combination that hid the slider.
                vm.PreviewClock = "0:43 / 3:19";
                vm.PlayGlyph = "\u23F8";
                vm.Strip.DurationSeconds = 199;
                vm.Strip.PositionSeconds = 43;
                vm.Strip.IsPlaying = true;
                vm.Strip.StatusText = "Buffering 100%\u2026";
            }
            else
            {
                vm.PreviewClock = "0:42 / 4:09";
                vm.PlayGlyph = "\u23F8";
                vm.Strip.DurationSeconds = StubSearchRows[0].DurationSeconds;
                vm.Strip.PositionSeconds = StubSearchRows[0].DurationSeconds / 3.0;
                vm.Strip.IsPlaying = true;
            }
        }

        return vm;
    }

    /// <summary>Rows the capture lists, in the order a search ranks them.</summary>
    private static readonly YtEntry[] StubSearchRows =
    {
        new("K0HSD_i2DvA", "Daft Punk - Around The World (Official Video)", "Daft Punk", 249, null, 402_331_884, false),
        new("sQ7Z7x1z8xw", "Daft Punk - Around The World (Official Audio)", "Daft Punk", 250, null, 61_204_772, false),
        new("1k3x5wFq9n4", "Around The World (Extended Version)", "Daft Punk Archive", 428, null, 3_918_402, false),
        new("Ht7cZ5xT0nA", "Daft Punk - Around The World (Live at Coachella 2006)", "festival recordings", 312, null, 12_884_019, false),
        new("pQ2sYbT8mRc", "Around The World - Daft Punk (Lyrics)", "LyricVault", 252, null, 8_772_310, false),
        new("Zx9Lm2Kq7Vw", "Daft Punk - Around The World (Funk Cover)", "The Groove Unit", 261, null, 214_905, false),
        new("c4Bd1nR8WyT", "Around The World (Remix) - Daft Punk", "House Tools", 379, null, 1_102_447, false),
        new("M8v0Pd6YsQz", "Daft Punk - Around The World (Bass Boosted)", "Bass Channel", 251, null, 5_338_901, false),
    };

    /// <summary>
    /// <c>--uishot-measure</c>: the laid-out box of each track list, in px, and how
    /// many whole rows that box holds. Row pitch comes from the row template's own
    /// numbers: TrackRowGrid is a fixed 28 px and the item container adds 3 px above
    /// and below, so a row is 34 px and a partly visible row does not count.
    /// </summary>
    /// <summary>
    /// Opt-in layout probe: the numbers a capture cannot show. "The window grew" is a
    /// statement about extents, not about drawn ink; a scrollbar is a few dark pixels
    /// at the edge of a PNG. Viewport against extent says which surface is carrying
    /// the overflow, and each slider's mark strip says whether the marks cost the deck
    /// card any height.
    /// </summary>
    private static void ProbeLayout(Visual root)
    {
        foreach (var panel in Descendants<LyricsPanel>(root))
        {
            ScrollViewer? host = null;
            for (DependencyObject? node = VisualTreeHelper.GetParent(panel); node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ScrollViewer scroller)
                {
                    host = scroller;
                    break;
                }
            }

            var list = Descendants<ListBox>(panel).FirstOrDefault();
            string top = "n/a";
            if (host is not null)
            {
                try
                {
                    top = $"{panel.TransformToAncestor(host).Transform(new Point(0, 0)).Y:0.0}";
                }
                catch (InvalidOperationException)
                {
                    top = "n/a";
                }
            }

            Console.Out.WriteLine(
                $"uishot: lyrics top {top} height {panel.ActualHeight:0.0}"
                + $" viewport {host?.ViewportHeight ?? double.NaN:0.0}"
                + $" list {list?.ActualHeight ?? double.NaN:0.0} max {list?.MaxHeight ?? double.NaN:0.0}");
        }

        foreach (var scroller in Descendants<ScrollViewer>(root))
        {
            Console.Out.WriteLine(
                $"uishot: scroll {Owner(scroller)} viewport {scroller.ViewportHeight:0.0}"
                + $" extent {scroller.ExtentHeight:0.0} scrollable {scroller.ScrollableHeight:0.0}"
                + $" bar {scroller.ComputedVerticalScrollBarVisibility}");
        }

        foreach (var slider in Descendants<Slider>(root))
        {
            var marks = Descendants<SliderTickBar>(slider).FirstOrDefault();
            string owner = Owner(slider);
            Console.Out.WriteLine(
                $"uishot: slider {owner} {slider.ActualWidth:0.0}x{slider.ActualHeight:0.0}"
                + $" range {slider.Minimum:0.###}..{slider.Maximum:0.###}"
                + $" value {slider.Value:0.###} snap {slider.IsSnapToTickEnabled}"
                + $" tf {slider.TickFrequency:0.###}"
                + $" tf-source {DependencyPropertyHelper.GetValueSource(slider, Slider.TickFrequencyProperty).BaseValueSource}"
                + $" marks {(marks is null ? "none" : marks.MarkCount.ToString(CultureInfo.InvariantCulture))}"
                + $" centre {(marks is null || marks.MarkCount == 0 ? "n/a" : marks.CenterValue.ToString("0.###", CultureInfo.InvariantCulture))}"
                + $" strip {(marks is null ? "n/a" : $"{marks.ActualWidth:0.0}x{marks.ActualHeight:0.0}")}");
        }
    }

    private static string Owner(FrameworkElement element)
    {
        if (!string.IsNullOrEmpty(element.Name))
        {
            return element.Name;
        }

        return element.TemplatedParent is FrameworkElement owner && !string.IsNullOrEmpty(owner.Name)
            ? $"{element.GetType().Name}<{owner.Name}>"
            : element.GetType().Name;
    }

    /// <summary>
    /// One line per list: the containers its items panel actually realized against
    /// the number of items it was handed.
    ///
    /// <para><b>Why.</b> "Virtualizing" is a wish until a viewport exists. The
    /// items panel is the thing that materializes rows, so its child count is the
    /// measurement; reading the attached property would only repeat the XAML back.
    /// A 4 000-row table that reports 4 000 containers was never virtualized, and
    /// that is a per-row cost paid inside one layout pass.</para>
    /// </summary>
    private static void ReportRealized(Visual root)
    {
        foreach (var element in Descendants<FrameworkElement>(root))
        {
            if (element is not (ListBox or TreeView)) continue;

            string name = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;
            int items = element is ItemsControl control ? control.Items.Count : -1;
            Panel? panel = Descendants<Panel>(element).FirstOrDefault(p => p.IsItemsHost);
            int realized = panel?.Children.Count ?? -1;

            Console.Out.WriteLine(
                $"uishot: realized {name} items {items} containers {realized}" +
                $" panel {panel?.GetType().Name ?? "none"}" +
                $" height {element.ActualHeight:0.0}");
        }

        Console.Out.Flush();
    }

    private static void MeasureTrackLists(Visual root)    {
        const double rowPitch = 34;

        foreach (var view in Descendants<YouTubeView>(root))
        {
            var list = view.ResultList;
            Console.Out.WriteLine(
                $"uishot: youtube ResultList {list.ActualWidth:0.0}x{list.ActualHeight:0.0} px -> {Math.Floor(list.ActualHeight / rowPitch):0} whole rows");
            DumpRowChain(list);
        }

        // Every list in the tree, named, so two tabs in the same browser card can be
        // compared without a second flag per view. A list that is squeezed has no
        // text of its own, so this is the only place its size shows up. TreeView is
        // here because the Library tab's track table is one, not a ListBox.
        foreach (var element in Descendants<FrameworkElement>(root))
        {
            if (element is not (ListBox or TreeView)) continue;
            var name = string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name;
            Console.Out.WriteLine($"uishot: list {name} {element.ActualWidth:0.0}x{element.ActualHeight:0.0} px");
            // The row chain says which row starved the list. Without it, "the list is
            // 0 px tall" is a symptom with no owner.
            DumpRowChain(element);
            // And the siblings in that row: a row is only ever tall because something
            // in it asked for the height, so the desired sizes name the culprit.
            if (element.Parent is Panel parent)
            {
                foreach (var sibling in parent.Children.OfType<FrameworkElement>())
                {
                    Console.Out.WriteLine(
                        $"uishot:   row {Grid.GetRow(sibling)}" +
                        $" {(sibling.Name.Length > 0 ? sibling.Name : sibling.GetType().Name)}" +
                        $" desired {sibling.DesiredSize.Height:0.0}" +
                        $" actual {sibling.ActualHeight:0.0}" +
                        $" v {sibling.VerticalAlignment}");
                }
            }
        }

        Console.Out.Flush();
    }

    /// <summary>
    /// Walk from an element up to the window, printing every Grid on the way with
    /// the height each of its rows actually took.
    ///
    /// <para><b>Why.</b> A star row that measured zero paints nothing, so the
    /// screenshot shows only the chrome that took the space and the list looks
    /// "missing" rather than "squeezed". The row heights are the only evidence of
    /// which row is the one eating the slot, and of what a fix actually bought.</para>
    /// </summary>
    private static void DumpRowChain(DependencyObject start)
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Grid grid || grid.RowDefinitions.Count < 2) continue;

            var rows = new StringBuilder();
            for (int i = 0; i < grid.RowDefinitions.Count; i++)
            {
                var def = grid.RowDefinitions[i];
                double taken = 0;
                foreach (var child in grid.Children.OfType<FrameworkElement>())
                {
                    if (Grid.GetRow(child) != i) continue;
                    taken = Math.Max(taken, child.ActualHeight);
                }

                // Name the tallest child: "row 0 took 122 px" is only actionable if it
                // also says which element took them.
                string tallest = "";
                double most = -1;
                foreach (var child in grid.Children.OfType<FrameworkElement>())
                {
                    if (Grid.GetRow(child) != i || child.ActualHeight <= most) continue;
                    most = child.ActualHeight;
                    tallest = child.Name.Length > 0
                        ? child.Name
                        : child.GetType().Name;
                }

                rows.Append($"  [{i}] {def.Height} -> {taken:0.0} {tallest}");
            }

            Console.Out.WriteLine($"uishot:   grid rows:{rows}"
                + (double.IsPositiveInfinity(grid.MaxHeight) ? "" : $" max {grid.MaxHeight:0.0}")
                + (grid.Name.Length > 0 ? $" ({grid.Name})" : ""));
        }
    }

    /// <summary>
    /// Render one laid-out element on its own. A Popup's content has its own visual
    /// tree, so the window render can never contain it; this is how the menu becomes
    /// pixels. Null when the element never got a size, so an empty menu is reported
    /// rather than shipped as a transparent rectangle.
    /// </summary>
    private static BitmapSource? RenderElement(FrameworkElement element)
    {
        int w = (int)Math.Ceiling(element.ActualWidth);
        int h = (int)Math.Ceiling(element.ActualHeight);
        if (w <= 0 || h <= 0) return null;

        var shot = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        shot.Render(element);
        return shot;
    }

    /// <summary>Every descendant of one type, in tree order.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// One field of the dump: newlines and tabs escaped, empty as <c>''</c>, 60
    /// chars max unless the caller asks for another cap (<c>0</c> = no cap).
    /// </summary>
    private static string Escaped(string? raw, int cap = 60)
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

        return cap > 0 && sb.Length > cap ? sb.ToString(0, cap) : sb.ToString();
    }

    /// <summary>
    /// <c>laidOut/visible</c> for one block: how many lines the text wrapped into at
    /// the width it was given, and how many of those fit in the height it was given.
    ///
    /// <para><b>Why.</b> A block whose text wraps to four lines but whose height is
    /// capped at two shows two, and the PNG cannot say whether the rest exists. The
    /// line count is measured with the same font metrics the block lays out with, at
    /// the same width, so <c>laidOut</c> is the wrap and <c>visible</c> is the
    /// consequence of the height: equal numbers mean nothing is hidden, and the
    /// remainder is what a cap, a clip or a scroll is holding back.</para>
    /// </summary>
    private static string LineFit(TextBlock textBlock, string text)
    {
        if (text.Length == 0 || textBlock.ActualWidth <= 0) return "0/0";

        var typeface = new Typeface(
            textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(textBlock).PixelsPerDip;

        FormattedText Measure(string value, double maxWidth) => new(
            value, CultureInfo.CurrentUICulture, textBlock.FlowDirection, typeface,
            textBlock.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(textBlock), pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
        };

        // The height of one unwrapped line, and the height the block asked for. An
        // explicit LineHeight wins, the way the text engine treats it.
        double lineHeight = textBlock.LineHeight;
        if (double.IsNaN(lineHeight) || lineHeight <= 0)
            lineHeight = Measure("Ag", 100000).Height;
        if (lineHeight <= 0) return "?/?";

        double wrapWidth = textBlock.ActualWidth - textBlock.Padding.Left - textBlock.Padding.Right;
        if (wrapWidth <= 0) return "0/0";

        int laidOut = Math.Max(1, (int)Math.Round(Measure(text, wrapWidth).Height / lineHeight));
        // Half a pixel of slack: a line-height sum and the block height differ by
        // sub-pixel rounding, and reporting 2 visible for 2.0001 lines of room is right.
        int visible = (int)Math.Floor((textBlock.ActualHeight + 0.5) / lineHeight);
        if (visible > laidOut) visible = laidOut;
        if (visible < 0) visible = 0;

        return $"{laidOut}/{visible}";
    }

    /// <summary>
    /// The laid-out height of the block's parent panel: the row, not the text.
    ///
    /// <para><b>Why.</b> A block's own height cannot show that its row ran out of
    /// room, because a block that is clipped still reports the height it asked
    /// for. The parent can: when the parent is shorter than the block, the text is
    /// laid out and then cut, which is the failure a text dump otherwise describes
    /// as "all lines present".</para>
    /// </summary>
    private static string ParentBox(TextBlock textBlock)
    {
        DependencyObject? parent;
        try
        {
            parent = VisualTreeHelper.GetParent(textBlock);
        }
        catch (Exception)
        {
            // Not connected to a tree yet.
            return "?";
        }

        return parent is FrameworkElement element
            ? $"{element.GetType().Name} {OneDecimal(element.ActualHeight)}"
            : "?";
    }

    private static string OneDecimal(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string BrushName(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        null => "null",
        _ => brush.GetType().Name,
    };
}
