# JockeyBox 2.0 verification harness

End-to-end proof that the **deployed** app loads, plays, tempo-shifts and hands a
real library track off through the 2.0 audio graph, and that the remote Spotify /
YouTube rows carry the library's row menu, driven only through UI Automation (no
product hooks, no product changes).

## Run it

```bash
# from WSL, on the box that hosts the Windows guest:
/home/alex/OBS/JockeyBox/scripts/verify/run-e2e.sh          # restart + full run (~7 min)
/home/alex/OBS/JockeyBox/scripts/verify/run-e2e.sh -KeepRunning   # reuse the running app
```

`run-e2e.sh` deploys `jb-e2e.ps1` to `C:\Dev\jb-e2e.ps1` **as UTF-8 with BOM**
(mandatory: PowerShell reads a BOM-less .ps1 as ANSI and mangles `▶`/`⏹`, which
the automation matches on), then runs it through a Session-1 scheduled task
(`/ru Alex /it /rl highest`; `win`/`winps` ssh land in Session 0 and cannot see
the app's windows), waits for `e2e.json`, and prints the table.

Artifacts land in `C:\Dev\shots\`:

| file | contents |
| --- | --- |
| `e2e.json` | machine-readable verdict, per-assertion PASS/FAIL, parameters, reads, log digest |
| `e2e-run.log` | the harness console transcript |
| `e2e-1-loaded.png` … `e2e-4-handoff.png` | screenshot per step |
| `e2e-5-*.png` | Spotify tab, a remote row's menu, the add, the state after the restart |

## What it asserts

| step | assertions |
| --- | --- |
| preflight | window visible to UIA, both 2.0 deck cards, LIBRARY/QUEUED tabs |
| 0 reset | nothing playing, ALL TRACKS selected, queue cleared, tempo 0 %, crossfader on A |
| 1 load + play | deck A takes a real library track, readout passes 0:03, title/BPM/ratio printed |
| 2 tempo | deck A fader to +6 %, ratio `1.060`, `EffectiveBpm` follows, track keeps advancing, deck B untouched |
| 3 queue hand-off | `+ Queue` ×2, queued row ▶, armed `transition A->B … fade=…`, deck A plays into the fade, the fade promotes deck B, deck B advances, the queued row reads `▶ playing`, the outgoing row reads `played` |
| 4 crossfader | slider `RangeValue` before/after plus the whole fade trace, loopback `/state` cross-check |
| 5 remote rows | `R1` a remote list with >= 20 rows (paged - the list is virtualised), `R2` the row menu carries an enabled `Add to playlist`, `R3` the submenu lists the internal playlists and choosing one writes the row (status line `Added "…" to …` **and** the playlist's own row count +1), `R4` rows that resolve to a library file carry the karaoke state (`Karaoke available` / `Karaoke needed` / `No karaoke version`), `R5` the second remote tab, `R6` the menu keeps the other local actions, `R7` the write survives a restart (store on disk + rail count after relaunch) |

`R3` writes into an app-owned playlist named **E2E Harness**. The harness deletes
and recreates it at the start of step 5 (that is what makes "count +1"
repeatable), so the only playlists it ever touches are its own. `R7` ends with a
second WM_CLOSE + relaunch; `P8` is asserted before it, so "the same instance ran
steps 1-4" still holds.

`e2e.json.verdict` is `PASS` only when every assertion passed.

### Spotify

`R1`-`R6` run on `-RemotePrimaryTab`, default **YOUTUBE**, because the Spotify tab
on this host is not connected (`Connect Spotify to load your playlists.`, 0 rows)
and there is nothing to right-click. The Spotify tab is still measured (`S1`) and
`R5` asserts it degrades to that prompt instead of an empty list. Once an account
is connected, run

```bash
run-e2e.sh            # jb-e2e.ps1 -RemotePrimaryTab SPOTIFY
```

and the identical checks move to the Spotify rows automatically.

## Host facts baked into the script

* **Restart first.** An armed transition plan is scheduled on the engine frame
  clock and is not cancelled when its outgoing deck never starts, so a plan left
  behind by an earlier run fires minutes later and makes "deck B advanced"
  meaningless. The run therefore WM_CLOSEs the app (graceful, so state flushes),
  relaunches it, and proves deck B was idle before the hand-off (`H0`).
* **State persists across restarts**: the library filter text, the queue and the
  deck media. Every run clears the filter, empties the queue, stops both decks.
* `AutomationElement.FindAll(Descendants, …)` is unreliable on this host (returns
  0 rows for Button/Text/Slider while the tree is populated) — everything walks
  with `TreeWalker.ControlViewWalker` and filters in managed code.
* UIA calls fail transiently (`GetFirstChild` → `E_UNEXPECTED`) against a window
  repainting at 30 Hz — every child read is retried.
* The window sits at negative X (multi-monitor) — nothing is filtered by
  coordinates, only by name/control type/document order.
* The transport strip acts on the ViewModel's *active* deck, and pre-loading the
  armed track reassigns that — deck work therefore uses each card's own ▶ button.
* Tempo/stretch cost is real: at +6 % the readout advances ~6 % faster than wall
  clock, so waits are generous.
* `/state` on `127.0.0.1:17890` exposes `playing`, `anyDeckPlaying`, `master`,
  `transition` — **no** deck position, title or BPM, so deck readouts stay the
  primary evidence.
* A WPF context menu is not a separate top-level window here: its `MenuItem`s
  appear inside the app window's own automation tree, so the menu is found by
  control type + text, never by class or position. It is opened with
  `Shift+F10` on the selected row — a synthetic right-click needs
  `SetCursorPos`/`Add-Type`, which the antivirus blocks in `%TEMP%`.
* The remote rows are virtualised: only the visible page is realised, so "how
  many rows" means paging the list with `ScrollPattern` and collecting distinct
  names.
* The karaoke cell is the library column rendered into the remote row and is
  **collapsed when the row has no library file** (`SourceTrack.LocalEntry` is
  null), so only rows that resolve locally show a state — on the `@Kavinsky`
  playlist that is 1 of 23.
* `Add to playlist` submenu rows are built in code-behind on every menu open
  (the store is re-read), which is why the app log shows
  `remote row menu: 'Add to playlist' filled with N row(s)`.
* After a relaunch the window exists before the shell is built: selecting a tab
  too early is lost when the shell is rebuilt, so `R7` waits for the deck cards
  first.

## Library fixtures

Defaults are chosen for a ~2 minute run: `Luke Muzzic — We Don't Talk Anymore -
Instrumental, Edit` (102 s, 161.5 BPM, has a beat grid) as the outgoing track and
`… - but it's slowed and muffled` (103 s, 107.7 BPM) as the queued one. Both
have analysed beat grids, so the arm line carries a real `ratio`/`beatLocked`
instead of `auto-mix: no BPM data`. Override with `-Artist`, `-TrackAName`,
`-TrackBName`.
