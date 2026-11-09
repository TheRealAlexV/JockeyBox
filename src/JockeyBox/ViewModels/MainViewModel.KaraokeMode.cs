using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using JockeyBox.Audio;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Separation;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>What a deck load should do about the track's karaoke stem.</summary>
public enum KaraokeStemAction
{
    /// <summary>Karaoke mode is off: load the pair exactly as 1.x did.</summary>
    PlayOriginal,

    /// <summary>The stem exists on disk: hand it to the deck.</summary>
    PlayKaraoke,

    /// <summary>No stem, and one can be made: queue the separation, play the mix.</summary>
    QueueSeparation,

    /// <summary>No stem, but one is already being made: play the mix until it lands.</summary>
    WaitForSeparation,

    /// <summary>No stem and none can be made: play the mix, say why in one line.</summary>
    PlayOriginalNoStem,
}

/// <summary>The decision for one load, plus the one-line status that explains it.</summary>
public readonly record struct KaraokeStemPlan(KaraokeStemAction Action, string Status)
{
    public bool WantsKaraoke => Action == KaraokeStemAction.PlayKaraoke;
    public bool Enqueues => Action == KaraokeStemAction.QueueSeparation;
}

/// <summary>
/// The Karaoke mode decision as a pure function, so the rules can be asserted
/// without a library, an engine or a separater: given what a deck is about to
/// play, whether its karaoke partner is on disk, whether the mode is on and what
/// the separation queue already says about that track, what should happen?
///
/// <para>No disk access here on purpose: <c>musicPath</c> is the file the deck
/// is loading and <paramref name="karaokePath"/> is passed only when the caller
/// has already established that the partner file is present.</para>
/// </summary>
public static class KaraokeStemPlanner
{
    public static KaraokeStemPlan Decide(string? musicPath, string? karaokePath,
                                         bool karaokeModeEnabled, SeparationState separation)
        => Decide(musicPath, karaokePath, karaokeModeEnabled, separation, canSeparate: true);

    public static KaraokeStemPlan Decide(string? musicPath, string? karaokePath,
                                         bool karaokeModeEnabled, SeparationState separation,
                                         bool canSeparate)
    {
        // Off is the 1.x behaviour: whatever pair sits on disk loads as usual and
        // nothing is ever generated behind the operator's back.
        if (!karaokeModeEnabled) return new KaraokeStemPlan(KaraokeStemAction.PlayOriginal, "");

        // A karaoke-only library row has no original stem to separate from.
        if (string.IsNullOrWhiteSpace(musicPath))
            return new KaraokeStemPlan(KaraokeStemAction.PlayOriginal, "");

        var name = Label(musicPath);

        if (!string.IsNullOrWhiteSpace(karaokePath))
            return new KaraokeStemPlan(KaraokeStemAction.PlayKaraoke, "");

        switch (separation)
        {
            // The queue already owns this track (another deck, or a "Make karaoke
            // track" click): wait for it instead of queueing it twice.
            case SeparationState.Queued:
            case SeparationState.Running:
                return new KaraokeStemPlan(KaraokeStemAction.WaitForSeparation,
                    $"Karaoke mode: the karaoke stem for \u201c{name}\u201d is still being made \u2014 playing the original mix");

            // A failure is not retried automatically: it would spin on the same
            // missing model or disk error every time the deck loads.
            case SeparationState.Failed:
                return new KaraokeStemPlan(KaraokeStemAction.PlayOriginalNoStem,
                    $"Karaoke mode: no karaoke stem for \u201c{name}\u201d \u2014 playing the original mix");

            default:
                if (!canSeparate)
                {
                    // The separater is not installed, or the file has no library
                    // home to write a stem beside.
                    return new KaraokeStemPlan(KaraokeStemAction.PlayOriginalNoStem,
                        $"Karaoke mode: no karaoke stem for \u201c{name}\u201d \u2014 playing the original mix");
                }

                return new KaraokeStemPlan(KaraokeStemAction.QueueSeparation,
                    $"Karaoke mode: making a karaoke stem for \u201c{name}\u201d \u2014 playing the original mix until it lands");
        }
    }

    private static string Label(string musicPath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(musicPath);
            return string.IsNullOrWhiteSpace(name) ? musicPath : name;
        }
        catch
        {
            return musicPath;
        }
    }
}

/// <summary>
/// Karaoke mode, ViewModel side: while the mode is on, a deck plays the karaoke
/// stem on Main and the original on the Karaoke Aid bus, so the operator hears
/// the vocal the room does not. The audio itself is the engine's
/// (<see cref="AudioEngine.KaraokeMode"/> plus the deck's split render); this
/// part only decides where the stem comes from:
///
/// <list type="bullet">
/// <item>stem on disk - the deck loads it, no work queued;</item>
/// <item>no stem - one separation is queued for the track that is about to play
/// (never a library sweep) and the deck plays the mix until it lands;</item>
/// <item>it lands - the deck that asked gets the stem attached at the same
/// position, mid-playback, without a re-load from zero;</item>
/// <item>it fails - the mix keeps playing and the status line says why.</item>
/// </list>
///
/// With the mode off nothing here queues anything.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// The track each deck is waiting for ("one separation per deck"): the row's
    /// key is compared on completion, so a stem that lands after the deck moved
    /// on is left for the next load instead of being stapled to the wrong track.
    /// </summary>
    private readonly Dictionary<PlaybackDeck, string> _karaokeWaitingByDeck = new();

    private bool KaraokeModeOn => KaraokeModeEnabled;

    /// <summary>
    /// Wire the automatic separation into the existing queue. Called from the
    /// constructor right after the <see cref="SeparationService"/> exists: the
    /// service already owns progress, the status line and the per-row glyph, so
    /// Karaoke mode reuses it rather than running a second pipeline.
    /// </summary>
    private void InitializeKaraokeMode()
    {
        _separation.Completed += OnKaraokeStemReady;

        // The KARAOKE toggle (transport bar) and the Settings checkbox both write
        // the one observable property, so watching it is enough to react to a
        // flip mid-playback - no wiring in the other lanes' files.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KaraokeModeEnabled)) ApplyKaraokeMode();
        };

        // Adopt the saved mode before the first load, so a deck picked from the
        // library at start-up already knows to ask for the stem.
        _transport.Engine.KaraokeMode = KaraokeModeOn;
    }

    /// <summary>
    /// Re-read Karaoke mode from settings (the Settings window's Save path and
    /// the start-up pass). Mid-playback this is only a flag: the engine swaps to
    /// (or away from) the split render on the next block, so the deck keeps its
    /// clock and the position never jumps. Decks loaded without a stem get one
    /// queued - but only the deck that is loaded, never the library.
    /// </summary>
    public void ApplyKaraokeMode()
    {
        bool on = KaraokeModeOn;
        _transport.Engine.KaraokeMode = on;

        if (!on)
        {
            // Nothing is cancelled: a separation already running is harmless and
            // its file is there for the next time the mode comes on.
            _karaokeWaitingByDeck.Clear();
            Log.Info("karaoke: mode off - one mixed render per deck");
            return;
        }

        foreach (var deck in new[] { PlaybackDeck.A, PlaybackDeck.B })
        {
            var channel = _transport.Channel(deck);
            if (!channel.IsLoaded) continue;

            if (channel.HasKaraoke)
            {
                // Already split: flipping the flag is all this deck needs.
                _karaokeWaitingByDeck.Remove(deck);
                Log.Info($"karaoke: deck {DeckName(deck)} already carries a stem - split render only");
                continue;
            }

            var primary = channel.PrimaryPath;
            if (string.IsNullOrWhiteSpace(primary))
            {
                _karaokeWaitingByDeck.Remove(deck);
                continue;
            }

            var track = FindTrackByMusicPath(primary);
            var stem = StemBeside(primary) ?? StemOnDisk(track);
            var plan = KaraokeStemPlanner.Decide(primary, stem, true,
                track?.Separation ?? SeparationState.None, CanAutoSeparate(primary));

            switch (plan.Action)
            {
                case KaraokeStemAction.PlayKaraoke when stem != null:
                    Log.Info($"karaoke: stem already on disk for deck {DeckName(deck)} - attaching in place");
                    _ = AttachKaraokeStemAsync(deck, track, primary!, stem);
                    break;

                case KaraokeStemAction.QueueSeparation:
                case KaraokeStemAction.WaitForSeparation:
                    // Without a library row there is nobody to report progress
                    // on, so only a real row is queued.
                    if (track != null && plan.Enqueues && _separation.Enqueue(track))
                        Log.Info($"karaoke: queued a separation for \"{track.Title}\" (deck {DeckName(deck)}) - the mix plays until it lands");
                    _karaokeWaitingByDeck[deck] = track?.RelativeKey ?? "";
                    // Progress for this job is the console's row; StatusText stays
                    // for messages only.
                    break;

                default:
                    _karaokeWaitingByDeck.Remove(deck);
                    if (plan.Status.Length > 0) StatusText = plan.Status;
                    Log.Info($"karaoke: deck {DeckName(deck)} has no stem - playing the original mix");
                    break;
            }
        }
    }

    /// <summary>
    /// The load-time hook, called by <c>LoadTrack</c> with what the deck is about
    /// to play. Returns the karaoke path the deck should get now: the stem when
    /// it is on disk, otherwise null (the mix) plus a queued or awaited
    /// separation that <see cref="OnKaraokeStemReady"/> turns into an in-place
    /// attach when it lands.
    /// </summary>
    private string? ApplyKaraokePlan(PlaybackDeck deck, TrackEntry track, string? musicPath,
                                     string? karaokePath)
    {
        var stem = StemOnDisk(track);
        if (stem == null && !string.IsNullOrWhiteSpace(karaokePath))
        {
            try { if (File.Exists(karaokePath)) stem = karaokePath; }
            catch { /* unreadable path: treat as no stem */ }
        }
        var canSeparate = CanAutoSeparate(musicPath);
        var plan = KaraokeStemPlanner.Decide(musicPath, stem, KaraokeModeOn, track.Separation, canSeparate);

        switch (plan.Action)
        {
            case KaraokeStemAction.PlayKaraoke:
                _karaokeWaitingByDeck.Remove(deck);
                Log.Info($"karaoke: stem already present for \"{track.Title}\" - deck {DeckName(deck)} loads it now");
                return stem;

            case KaraokeStemAction.QueueSeparation:
                // The service owns the job from here: it flips the row to
                // Queued/Running and reports progress; the deck keeps the mix.
                if (!_separation.Enqueue(track))
                    Log.Info($"karaoke: separation for \"{track.Title}\" is already outstanding - waiting for it");
                else
                    Log.Info($"karaoke: stem queued for \"{track.Title}\" - deck {DeckName(deck)} plays the original mix until it lands");

                _karaokeWaitingByDeck[deck] = track.RelativeKey;
                // The queued job is a console row; no plan line in StatusText.
                return null;

            case KaraokeStemAction.WaitForSeparation:
                _karaokeWaitingByDeck[deck] = track.RelativeKey;
                // Same: the console already shows this job is still running.
                Log.Info($"karaoke: waiting for the stem of \"{track.Title}\" - deck {DeckName(deck)} plays the original mix");
                return null;

            case KaraokeStemAction.PlayOriginalNoStem:
                _karaokeWaitingByDeck.Remove(deck);
                StatusText = plan.Status;
                Log.Info($"karaoke: stem failed for \"{track.Title}\" - deck {DeckName(deck)} plays the original mix"
                    + (canSeparate ? "" : " (no separator available)"));
                return null;

            default:
                // Mode off: the pair on disk loads exactly as before.
                _karaokeWaitingByDeck.Remove(deck);
                return karaokePath;
        }
    }

    /// <summary>
    /// A separation finished. Attach the stem to every deck that is loaded with
    /// that same original and had no partner - at the position it is playing
    /// from, keeping play/pause and the karaoke offset, so Karaoke mode lands
    /// mid-track with no click and no jump. A deck that moved on in the meantime
    /// is left alone.
    /// </summary>
    private void OnKaraokeStemReady(TrackEntry track, string karaokePath)
    {
        if (string.IsNullOrWhiteSpace(track.MusicPath)) return;

        // With the mode off nothing is attached: the file simply sits beside the
        // original, which is exactly the 1.x "Make karaoke track" behaviour.
        if (!KaraokeModeOn)
        {
            _karaokeWaitingByDeck.Clear();
            Log.Info($"karaoke: stem finished for \"{track.Title}\" (mode off - kept for the next load)");
            return;
        }

        bool attached = false;
        foreach (var deck in new[] { PlaybackDeck.A, PlaybackDeck.B })
        {
            var channel = _transport.Channel(deck);
            if (channel.HasKaraoke) continue;
            if (!SamePath(channel.PrimaryPath, track.MusicPath)) continue;

            _karaokeWaitingByDeck.Remove(deck);
            Log.Info($"karaoke: stem finished for \"{track.Title}\" - attaching it to deck {DeckName(deck)} "
                + $"at {channel.DisplayPosition:mm':'ss}");
            _ = AttachKaraokeStemAsync(deck, track, track.MusicPath, karaokePath);
            attached = true;
        }

        if (!attached)
            Log.Info($"karaoke: stem finished for \"{track.Title}\" - no deck is waiting for it, it loads next time");
    }

    /// <summary>
    /// Swap a deck from "original on both outputs" to "karaoke on Main, original
    /// on Karaoke Aid" without losing the clock: the deck re-opens the same pair
    /// on a worker and resumes/re-seeks where it was. Every failure keeps the
    /// mix audible and reports one line - never a spinner, never a stuck glyph.
    /// </summary>
    private async Task AttachKaraokeStemAsync(PlaybackDeck deck, TrackEntry? track,
                                              string musicPath, string karaokePath)
    {
        var channel = _transport.Channel(deck);
        var vm = deck == PlaybackDeck.A ? DeckA : DeckB;

        bool ok;
        try
        {
            ok = await channel.AttachKaraokeStemAsync(karaokePath);
        }
        catch (Exception ex)
        {
            ok = false;
            Log.Warn($"karaoke: attaching the stem to deck {DeckName(deck)} failed: {ex.Message}");
        }

        vm.HasKaraoke = channel.HasKaraoke;
        if (!ok || !channel.HasKaraoke)
        {
            StatusText = $"Karaoke mode: could not attach the stem for \"{track?.Title ?? Path.GetFileNameWithoutExtension(musicPath)}\" \u2014 playing the original mix";
            Log.Warn($"karaoke: stem not attached on deck {DeckName(deck)} - the original mix keeps playing");
            return;
        }

        RefreshKaraokeWaveform(vm, karaokePath);
        StatusText = $"Karaoke mode: \"{track?.Title ?? Path.GetFileNameWithoutExtension(musicPath)}\" is now split \u2014 karaoke on Main, original on Karaoke Aid";
        Log.Info($"karaoke: deck {DeckName(deck)} split at {channel.DisplayPosition:mm':'ss} - Main = karaoke stem, Karaoke Aid = original");
    }

    /// <summary>Draw the karaoke stem's waveform into a deck that only had a primary.</summary>
    private async void RefreshKaraokeWaveform(DeckViewModel deck, string? karaokePath)
    {
        if (string.IsNullOrWhiteSpace(karaokePath)) return;

        var generation = deck.WaveformGeneration;
        var primary = deck.Waveform;
        try
        {
            var karaoke = await Task.Run(() => WaveformGenerator.Generate(karaokePath));
            deck.SetWaveforms(generation, primary, karaoke);
        }
        catch (Exception ex)
        {
            // A waveform is decoration: never fail a load (or the mode) over it.
            Log.Warn($"karaoke: waveform for {Path.GetFileName(karaokePath)} failed: {ex.Message}");
        }
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>The karaoke partner of a row, but only when the file is really there.</summary>
    private static string? StemOnDisk(TrackEntry? track)
    {
        var path = track?.KaraokePath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return File.Exists(path) ? path : null; }
        catch { return null; }
    }

    /// <summary>Probe the naming rule for a stem beside a file the library has no row for.</summary>
    private static string? StemBeside(string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return null;

        var exact = KaraokeNaming.KaraokePathFor(musicPath);
        try
        {
            if (!string.IsNullOrEmpty(exact) && File.Exists(exact)) return exact;

            var folder = Path.GetDirectoryName(musicPath);
            if (string.IsNullOrEmpty(folder)) return null;

            // A generated stem may use another format beside a downloaded original.
            var baseName = KaraokeNaming.BaseFileName(musicPath);
            foreach (var ext in new[] { ".flac", ".wav", ".mp3", ".m4a", ".ogg", ".opus" })
            {
                var candidate = Path.Combine(folder, baseName + KaraokeNaming.Marker + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* unreadable path: no stem */ }

        return null;
    }

    /// <summary>
    /// Whether this file may be auto-separated: the engine has to be installed,
    /// and the stem is written beside the original, so a stream-cache copy (an
    /// LRU temp file) or a removable drive the operator barely has is not a
    /// place to put one. Library files under the music/karaoke roots qualify.
    /// </summary>
    private bool CanAutoSeparate(string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return false;
        if (!SeparationEngine.IsInstalled) return false;

        foreach (var root in new[] { _settings.MusicDirectory, _settings.KaraokeDirectory })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (IsUnder(musicPath, root)) return true;
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(root);
            if (!dir.EndsWith(Path.DirectorySeparatorChar))
                dir += Path.DirectorySeparatorChar;
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The library row that owns a file, or null for something we did not scan.</summary>
    private TrackEntry? FindTrackByMusicPath(string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return null;

        foreach (var track in Tracks)
        {
            if (SamePath(track.MusicPath, musicPath)) return track;
        }

        return null;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string DeckName(PlaybackDeck deck) => deck == PlaybackDeck.A ? "A" : "B";
}
