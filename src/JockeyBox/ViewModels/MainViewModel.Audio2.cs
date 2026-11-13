using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Audio.Beat;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Api;
using JockeyBox.Audio.Transition;
using JockeyBox.Library;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// 2.0 additions to <see cref="MainViewModel"/>: master volume, global output
/// delay, transition settings, beat analysis and the soundboard.
///
/// <para>Kept in its own file so the 1.5.0 view model — which is the largest file
/// in the project and carries a lot of unrelated behaviour (lyrics, downloads,
/// karaoke) — stays reviewable. Everything here is additive.</para>
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- state

    /// <summary>Master fader, 0..1.2. Applied on the program bus after the deck sum.</summary>
    [ObservableProperty] private double _masterVolume = 1.0;

    /// <summary>Global output delay in ms (OBS lip-sync); 0..2000.</summary>
    [ObservableProperty] private int _outputDelayMs;

    /// <summary>Short text for the header: how the delay is currently affecting the clock.</summary>
    [ObservableProperty] private string _outputDelayText = "0 ms";

    /// <summary>Transition style used when a queued track takes over.</summary>
    [ObservableProperty] private TransitionMode _transitionMode = TransitionMode.AutoCrossfade;

    /// <summary>Fade length in seconds for automatic transitions.</summary>
    [ObservableProperty] private double _transitionFadeSeconds = 2.0;

    /// <summary>True while a transition is running (drives the header indicator).</summary>
    [ObservableProperty] private bool _isTransitioning;

    /// <summary>Human-readable description of the live transition ("A→B · ratio 1.008 · 2 s").</summary>
    [ObservableProperty] private string _transitionText = "";

    /// <summary>How many tracks have an analysed beat grid (shown in settings).</summary>
    [ObservableProperty] private int _analysedTrackCount;

    /// <summary>Status line for the analysis lane.</summary>
    [ObservableProperty] private string _analysisStatus = "Beat analysis idle";

    /// <summary>Background beat analysis (null until <see cref="InitializeAudio2"/> runs).</summary>
    private BpmAnalysisService? _bpmService;

    /// <summary>Analysed grids: the JSON document that survives a cold start.</summary>
    private BeatGridStore? _bpmStore;

    /// <summary>Last queue depth painted into <see cref="AnalysisStatus"/>.</summary>
    private int _lastAnalysisQueue = -1;

    /// <summary>Last failure count painted into <see cref="AnalysisStatus"/>.</summary>
    private int _lastAnalysisFailed = -1;

    /// <summary>Loopback API for the Stream Deck plugin and browser overlays.</summary>
    private LocalApiServer? _api;

    /// <summary>Global hotkeys for soundboard pads.</summary>
    private GlobalHotkeyService? _hotkeys;

    /// <summary>Wall-clock gate so the API socket is not spammed at 30 Hz.</summary>
    private DateTime _lastApiPublish = DateTime.MinValue;

    /// <summary>The queue row that is loaded on the idle deck and armed to take over.</summary>
    private PlaybackQueueItem? _armedItem;

    /// <summary>Deck the armed row is loaded on.</summary>
    private PlaybackDeck _armedDeck = PlaybackDeck.B;

    /// <summary>Soundboard pads and their commands.</summary>
    public SoundboardViewModel Soundboard { get; private set; } = null!;

    /// <summary>Options for the transition mode picker.</summary>
    public IReadOnlyList<TransitionMode> TransitionModeOptions { get; } =
        new[] { TransitionMode.Off, TransitionMode.AutoCrossfade, TransitionMode.AutoMix };

    /// <summary>Human label for a transition mode (used by the settings UI).</summary>
    public static string DescribeTransitionMode(TransitionMode mode) => mode switch
    {
        TransitionMode.Off => "Hard cut at end of track",
        TransitionMode.AutoCrossfade => "Crossfade at end of track",
        TransitionMode.AutoMix => "Auto-mix (beat & tempo match)",
        _ => mode.ToString(),
    };

    // ---------------------------------------------------------------- init

    /// <summary>
    /// Wire the 2.0 audio features. Called from the constructor once the transport,
    /// decks and settings exist.
    /// </summary>
    private void InitializeAudio2()
    {
        MasterVolume = _settings.MasterVolume;
        OutputDelayMs = _settings.OutputDelayMs;
        TransitionMode = _settings.TransitionMode;
        TransitionFadeSeconds = _settings.TransitionFadeSeconds;
        DeckA.SetTempoRange(_settings.ManualTempoRangePercent);
        DeckB.SetTempoRange(_settings.ManualTempoRangePercent);

        Soundboard = new SoundboardViewModel(_settings, _transport.Engine, JockeyBox.Data.LibraryDatabase.Current);
        Soundboard.PadTriggered += pad => StatusText = $"Soundboard: {pad.Name}";

        ApplyMasterVolume();
        ApplyOutputDelay();

        if (_transport.Engine is { } engine)
        {
            engine.OutputFault += name =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    StatusText = $"Output device lost: {name}");
        }

        if (_transport.Transitions is { } transitions)
        {
            transitions.Completed += plan =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnTransitionCompleted(plan));
        }

        StartBeatAnalysis();
        StartLocalApi();
        StartHotkeys();
        StartCueHotkeys();
    }

    // ---------------------------------------------------------------- beat analysis

    /// <summary>
    /// Build the analyser once and hand it to everything that needs BPM data:
    /// the transition scheduler (through <see cref="DualDeckTransport.BeatGridProvider"/>)
    /// and the decks (through <see cref="AttachBeatGrid"/>).
    ///
    /// <para>Construction is cheap — the store reads one JSON document and the
    /// service starts an idle worker — so this runs before the first track can be
    /// loaded, which is what makes a freshly loaded deck analyse immediately
    /// instead of falling back to "BPM —" for the whole first mix.</para>
    /// </summary>
    private void StartBeatAnalysis()
    {
        try
        {
            _bpmStore = new BeatGridStore();
            // AnyPlaying, not IsPlaying: the latter follows the active deck only, and
            // PlayDeck never activates, so "deck A stopped, B playing" read as silent
            // and let the pass run mid-mix.
            _bpmService = new BpmAnalysisService(_bpmStore, audioPlaying: () => _transport.AnyPlaying);
            _bpmService.Analysed += OnBeatAnalysed;
            _transport.BeatGridProvider = _bpmService;
            Log.Info($"beat analysis: {_bpmStore.Count} cached grid(s) at {_bpmStore.FilePath}");
        }
        catch (Exception ex)
        {
            // Analysis is an enhancement; a broken store must not stop playback.
            Log.Error("beat analysis: could not start", ex);
            _bpmStore = null;
            _bpmService = null;
        }
    }

    /// <summary>
    /// Point a deck at the analysed grid for what is loaded on it (null when the
    /// track has not been analysed yet, which shows "BPM —"). Called on every
    /// load and again when analysis finishes for a track that is still loaded.
    /// </summary>
    private void AttachBeatGrid(DeckViewModel deck, string? path)
    {
        if (_bpmService == null || string.IsNullOrEmpty(path)) return;
        deck.SetBeatGrid(_bpmService.For(path));
        if (!_bpmStore!.Has(path)) _bpmService.Enqueue(path);
    }

    /// <summary>
    /// The worker finished a track. It may be loaded on a deck right now (the
    /// common case: the user dropped a track and the readout filled in a second
    /// later), so re-attach to both decks. The worker runs off the UI thread.
    /// </summary>
    private void OnBeatAnalysed(string path, BeatGrid grid)
    {
        // Persist before repainting. A row that shows a tempo the file does not
        // state is a value that dies with the process: library.json is the durable
        // copy the next scan reads (the store already mirrored the grid into the
        // tracks columns when it saved it). Off the UI thread, so the write is not
        // in the paint path; SetBpm is a no-op for a tag tempo.
        if (grid.Bpm > 0 && _tagCache.SetBpm(path, grid.Bpm)) _tagCache.Save();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(() =>
        {
            // A load that happened between Enqueue and completion has the right
            // path on the deck already; compare against that, not against a
            // remembered "last loaded" field.
            if (PathEquals(DeckA.TrackPath, path)) DeckA.SetBeatGrid(grid);
            if (PathEquals(DeckB.TrackPath, path)) DeckB.SetBeatGrid(grid);

            // The row was already on screen when this finished: fill its key cell
            // the same way the deck readout just filled. Blank means "no tag key",
            // and the scan (which runs before this worker) has already written any
            // tag it found, so analysis cannot overwrite authored metadata.
            if (grid.Key != null)
                foreach (var track in Tracks)
                    if (PathEquals(track.MusicPath, path) && string.IsNullOrEmpty(track.Key))
                        track.Key = TrackKeyResolver.Resolve(tagKey: null, grid);

            // Tempo follows the identical rule: the scan (which runs before this
            // worker) already wrote any TBPM tag, so a non-zero Bpm is authored
            // metadata and must not be replaced by an estimate. Zero means the
            // file states no tempo, which is the only case analysis may fill.
            if (grid.Bpm > 0)
                foreach (var track in Tracks)
                    if (PathEquals(track.MusicPath, path) && track.Bpm <= 0)
                        track.Bpm = TrackBpmResolver.Resolve(tagBpm: 0, grid);
        });
    }

    private static bool PathEquals(string? a, string? b) =>
        a != null && b != null && string.Equals(
            System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// First-run pass: walk the library in the background so the beat data fills
    /// in once instead of track by track. Runs after the folder scan (the scan
    /// must not be fought for disk) and skips everything already cached.
    /// </summary>
    private void StartLibraryAnalysisPass()
    {
        if (_bpmService == null) return;

        // Read the collection on the UI thread (it is bound to the browser), then
        // queue off it: 771 paths is a millisecond, but EnqueueAll takes the
        // service lock and the store's keys, which should not sit in a frame.
        List<string> paths;
        try
        {
            paths = Tracks
                .Select(t => t.MusicPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"beat analysis: library pass could not read the library ({ex.Message})");
            return;
        }

        if (paths.Count == 0) return;
        var service = _bpmService;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { service.EnqueueAll(paths); }
            catch (Exception ex) { Log.Warn($"beat analysis: library pass failed ({ex.Message})"); }
        });
    }

    // ---------------------------------------------------------------- queue hand-off

    /// <summary>
    /// Prepare the next queued row on the idle deck and arm a transition that
    /// finishes exactly at the end of the track now playing (requirements 4, 10
    /// and 11).
    ///
    /// <para>The old behaviour started the next track the moment the current one
    /// ended and then faded — a gap followed by a fade from silence. Here the
    /// incoming deck is already loaded, tempo-matched and gain-scheduled against
    /// the render clock, so the fade *ends* with the outgoing track and there is
    /// no audible seam.</para>
    ///
    /// <para>Called after a queue row starts. Safe to call repeatedly: an earlier
    /// armed plan for the same pair is reused, a different one is cancelled and
    /// replaced.</para>
    /// </summary>
    private async Task ArmNextQueueItemAsync(PlaybackDeck currentDeck)
    {
        var transitions = _transport.Transitions;
        if (transitions == null) return;

        var next = FindNextUnplayed(CurrentIndex + 1);
        if (next == null && RepeatQueue && Queue.Count > 1)
            next = FindNextUnplayed(0);

        if (next == null || next == _armedItem) return;

        if (_armedItem != null)
        {
            // A different row is now next: drop the stale plan before loading.
            transitions.Cancel();
            _armedItem = null;
        }

        var target = currentDeck.Opposite();

        // Load only — the transition owns when it becomes audible.
        if (next.Track != null)
        {
            _armedDeck = target;
            next.DeckTarget = target;
            LoadTrackForTransition(next.Track, target);
        }
        else if (next.Source != null)
        {
            _armedDeck = target;
            next.DeckTarget = target;
            if (!await PlaySourceTrackAsync(next.Source, fromQueue: true, targetDeck: target,
                                            startPlayback: false, activate: false))
                return;
        }
        else
        {
            return;
        }

        var from = currentDeck == PlaybackDeck.A ? _transport.Engine.SlotA : _transport.Engine.SlotB;
        var to = target == PlaybackDeck.A ? _transport.Engine.SlotA : _transport.Engine.SlotB;

        var plan = transitions.Begin(from, to, atTrackEnd: true);
        if (plan == null)
        {
            StatusText = "Next track is loaded but the transition was refused (see log).";
            return;
        }

        _armedItem = next;
        StatusText = $"Next: {next.Title} — {plan.Reason}";
    }

    /// <summary>The render thread finished the armed fade: make the new row current and arm the one after it.</summary>
    private void OnTransitionCompleted(TransitionPlan plan)
    {
        try
        {
            var arrived = plan.To;
            var item = _armedItem;

            if (CurrentIndex >= 0 && CurrentIndex < Queue.Count)
                Queue[CurrentIndex].IsPlayed = true;

            if (item != null)
            {
                int index = Queue.IndexOf(item);
                if (index >= 0) SetCurrentQueueIndex(index);
            }

            _armedItem = null;
            MarkQueueState();
            SaveQueue();

            _transport.ActivateDeck(arrived);
            SyncDeckActivation();
            SyncCrossfaderFromTransport();

            if (item != null) _ = ArmNextQueueItemAsync(arrived);
            else if (CurrentIndex < 0) StatusText = "Track finished.";
        }
        catch (Exception ex)
        {
            Log.Error("transition completion handling failed", ex);
        }
    }

    /// <summary>
    /// Load a track onto a specific deck without touching the current-row state.
    /// <see cref="LoadTrack"/> carries the library/karaoke pairing and the deck
    /// bookkeeping; this wrapper exists so the call site reads as what it is.
    ///
    /// <para><c>activate: false</c> is the arming contract: the incoming deck gets
    /// its media, tempo and gain plan ready while the outgoing deck keeps ACTIVE
    /// and the transport strip. Promotion happens in
    /// <see cref="OnTransitionCompleted"/>, when the fade actually arrives.</para>
    /// </summary>
    private void LoadTrackForTransition(TrackEntry track, PlaybackDeck deck)
    {
        LoadTrack(track, fromQueue: true, targetDeck: deck, activate: false);
    }

    /// <summary>
    /// Start the loopback API. A port clash is not fatal — the server walks up a
    /// few ports and the Stream Deck plugin is told the real one through its own
    /// settings.
    /// </summary>
    private void StartLocalApi()
    {
        _api = new LocalApiServer();
        _api.Configure(
            _settings.ApiToken,
            padSource: () => Soundboard.Pads
                .Select(p => new ApiPad(p.Id, p.Name, p.Hotkey, p.Volume, p.Loop, p.IsPlaying, p.DurationSeconds))
                .ToList(),
            playPad: id =>
            {
                var pad = Soundboard.Pads.FirstOrDefault(p => p.Id == id);
                if (pad == null) return;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Soundboard.Play(pad));
            },
            stopAll: () =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Soundboard.StopAll()),
            stateSource: () => new ApiState
            {
                Playing = Soundboard.Pads.Where(p => p.IsPlaying).Select(p => p.Id).ToArray(),
                Master = MasterVolume,
                AnyDeckPlaying = IsPlaying,
                Transition = TransitionText,
            },
            addSound: async (path, url) => await AddSoundFromApiAsync(path, url),
            cueRouter: CreateCueApiRouter());

        // ONE port: the Stream Deck plugin defaults to 17890 (LocalApiServer.DefaultPort).
        // AppSettings.ApiPort predates the plugin; if it disagrees we log it and use
        // the plugin's port, so "install the plugin and it works" stays true. An
        // explicit override sits ahead of that, so a sandbox can bind its own port
        // instead of answering the operator's plugin in place of their app.
        var requested = ApiPortOption.Resolve();
        int port = requested.Port;
        if (requested.Origin != ApiPortOption.Origin.Default)
            Log.Info($"local api: port {port} from {requested.Origin} (default {LocalApiServer.DefaultPort})");
        else if (_settings.ApiPort != port)
            Log.Info($"local api: ignoring AppSettings.ApiPort={_settings.ApiPort}; the Stream Deck plugin uses {port}");

        if (!_api.Start(port) && _api.LastError != null)
            StatusText = $"Soundboard API unavailable: {_api.LastError}";
        else if (_api.Port != port)
            StatusText = $"Stream Deck API on port {_api.Port} (port {port} was busy)";

        if (_api.IsRunning)
        {
            // Say the bound port, not the requested one: the walk-up can move it,
            // and only the bound port tells the truth about who can reach us.
            Log.Info(_api.Port == LocalApiServer.DefaultPort
                ? $"local api: bound port {_api.Port}"
                : $"local api: bound port {_api.Port}, not the Stream Deck default {LocalApiServer.DefaultPort}; " +
                  "the plugin will not reach this instance");
        }
    }

    /// <summary>Register every pad hotkey; failures are surfaced in the pad tooltip, not silent.</summary>
    private void StartHotkeys()
    {
        _hotkeys = new GlobalHotkeyService();
        _hotkeys.PadTriggered += id =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var pad = Soundboard.Pads.FirstOrDefault(p => p.Id == id);
                if (pad == null) return;
                Log.Info($"soundboard: hotkey fired pad '{pad.Name}' ({id})");
                Soundboard.Play(pad);
            });
    }

    /// <summary>
    /// Give the hotkey layer a window handle once the window exists (the view
    /// model is built before it), then register every stored binding.
    /// </summary>
    public void AttachHotkeyWindow(System.Windows.Window window)
    {
        if (_hotkeys == null) return;
        if (!_hotkeys.Attach(window))
        {
            Log.Warn("soundboard: could not attach hotkeys to the window");
            return;
        }

        // The board owns the pad→chord map; handing it the host registers every
        // stored binding and keeps bind/unbind in one place.
        Soundboard.HotkeyHost = _hotkeys;

        // Cue chords need the same handle; registering before Attach would fail
        // with "the app window is not ready yet" for all eighteen.
        AttachCueHotkeys();
    }

    /// <summary>Re-register every pad binding (after a bind/unbind or a settings reload).</summary>
    public void RebindHotkeys()
    {
        Soundboard.ReloadHotkeys();
        AttachCueHotkeys();
    }

    /// <summary>API <c>POST /sounds/add</c>: local path or URL, off the UI thread, off the request thread.</summary>
    private async Task<ApiAddResult> AddSoundFromApiAsync(string? path, string? url)
    {
        var panel = Soundboard;

        // The board owns an ObservableCollection: start the work on the UI
        // dispatcher or the change notification throws in the panel.
        Func<Task<SoundboardPad?>> start = !string.IsNullOrWhiteSpace(path)
            ? () => panel.AddLocalFileAsync(path!)
            : () => panel.AddUrlAsync(url!);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var pad = dispatcher != null && !dispatcher.CheckAccess()
            ? await await dispatcher.InvokeAsync(start)
            : await start();
        return pad == null
            ? ApiAddResult.Fail(panel.StatusText)
            : new ApiAddResult(true, pad.Id, pad.Name, pad.Hotkey, null);
    }

    /// <summary>
    /// Mirror playback state to the API at ~5 Hz. The deck playheads run at 30 Hz;
    /// a Stream Deck key does not need that, and every publish is a socket write.
    /// </summary>
    private void PublishApiState()
    {
        if (_api == null || !_api.IsRunning) return;
        var now = DateTime.UtcNow;
        if ((now - _lastApiPublish).TotalMilliseconds < 200) return;
        _lastApiPublish = now;

        _api.PublishState(new ApiState
        {
            Playing = Soundboard.Pads.Where(p => p.IsPlaying).Select(p => p.Id).ToArray(),
            Master = MasterVolume,
            AnyDeckPlaying = IsPlaying,
            Transition = TransitionText,
        });
    }

    /// <summary>Tear down the API and hotkeys (called from Dispose).</summary>
    private void ShutdownAudio2()
    {
        _bpmService?.Dispose();
        _bpmService = null;
        _bpmStore?.Dispose();
        _bpmStore = null;
        StopCueHotkeys();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _api?.Dispose();
        _api = null;
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void ResetMasterVolume() => MasterVolume = 1.0;

    partial void OnMasterVolumeChanged(double value)
    {
        double clamped = Math.Clamp(value, 0, 1.2);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            MasterVolume = clamped;
            return;
        }
        ApplyMasterVolume();
    }

    private void ApplyMasterVolume()
    {
        if (_transport?.Engine == null) return;
        _transport.Engine.MasterVolume = (float)MasterVolume;
        _settings.MasterVolume = (float)MasterVolume;
    }

    partial void OnOutputDelayMsChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 2000);
        if (clamped != value)
        {
            OutputDelayMs = clamped;
            return;
        }
        ApplyOutputDelay();
    }

    private void ApplyOutputDelay()
    {
        if (_transport?.Engine == null) return;
        _transport.OutputDelayMs = OutputDelayMs;
        _settings.OutputDelayMs = OutputDelayMs;
        OutputDelayText = OutputDelayMs == 0 ? "off" : $"{OutputDelayMs} ms";
    }

    partial void OnTransitionFadeSecondsChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.2, 30);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            TransitionFadeSeconds = clamped;
            return;
        }
        _settings.TransitionFadeSeconds = clamped;
        if (_transport?.Transitions != null) _transport.Transitions.FadeSeconds = clamped;
    }

    partial void OnTransitionModeChanged(TransitionMode value)
    {
        _settings.TransitionMode = value;
        if (_transport?.Transitions != null) _transport.Transitions.Mode = value;
    }

    /// <summary>Analyse the whole library in the background (idempotent, cached).</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task AnalyseLibrary()
    {
        if (_bpmService == null)
        {
            AnalysisStatus = "Beat analysis unavailable (see log)";
            return;
        }

        var paths = Tracks.Where(t => t.MusicPath != null).Select(t => t.MusicPath!).ToList();
        AnalysisStatus = $"Beat analysis: queued {paths.Count} track(s)";
        await System.Threading.Tasks.Task.Run(() => _bpmService?.EnqueueAll(paths));
        AnalysisStatus = paths.Count == 0 ? "Beat analysis: library is empty" : "Beat analysis running in background";
    }

    /// <summary>
    /// Refresh the analysed-count readout (called by the UI tick, cheap).
    /// Writes only when a number the user can see changed — the tick runs at
    /// 30 Hz and an unconditional set would re-notify the settings text forever.
    /// </summary>
    private void RefreshAnalysisCounters()
    {
        var service = _bpmService;
        if (service == null) return;

        int analysed = service.AnalysedCount;
        int queued = service.QueuedCount;
        int failed = service.FailedCount;

        if (analysed == AnalysedTrackCount && queued == _lastAnalysisQueue &&
            failed == _lastAnalysisFailed)
            return;

        AnalysedTrackCount = analysed;
        _lastAnalysisQueue = queued;
        _lastAnalysisFailed = failed;

        if (service.DisabledReason != null)
        {
            AnalysisStatus = $"Beat analysis disabled: {service.DisabledReason}";
            return;
        }

        AnalysisStatus = $"Beat analysis: {analysed} analysed" +
                         (queued > 0 ? $", {queued} queued" : "") +
                         (failed > 0 ? $", {failed} failed" : "");
    }

    // ---------------------------------------------------------------- transition status

    /// <summary>Mirror the scheduler's live plan into the UI (called from the UI tick).</summary>
    private void RefreshTransitionStatus()
    {
        var plan = _transport?.Transitions?.Current;
        if (plan == null)
        {
            if (!IsTransitioning) return;
            IsTransitioning = false;
            TransitionText = "";
            return;
        }

        IsTransitioning = true;
        string ratio = plan.TempoMatched ? $"{plan.Ratio:0.####}x" : "1x";
        TransitionText = $"{plan.From} \u2192 {plan.To} \u00b7 {(plan.BeatLocked ? "beat-locked" : "crossfade")} \u00b7 {ratio}";
    }

    // ---------------------------------------------------------------- six-bus route

    /// <summary>
    /// Adopt a Settings save without a restart: the transport re-reads the document,
    /// rebuilds the six-bus route (it reopens outputs only when something actually
    /// changed) and re-applies the recording-device strips.
    ///
    /// <para>Called from the main window after the dialog returns true, next to
    /// <see cref="OnSettingsSaved"/> and <see cref="OnSettingsSavedMic"/>: those two
    /// cover the library/loudness side, this one the routing side.</para>
    /// </summary>
    public void ApplyAudioSettings()
    {
        // Reads AppSettings from disk, so it must run after the dialog wrote it.
        _transport.ApplyDeckSettingsChange(PlaybackDeck.A);

        RefreshMicDeviceCount();
    }

}
