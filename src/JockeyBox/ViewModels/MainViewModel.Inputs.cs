using System;
using System.Collections.Generic;
using System.Linq;
using JockeyBox.Api;
using JockeyBox.Playback;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// Input surfaces for the cue system: system-wide hotkeys and the loopback HTTP
/// endpoints a Stream Deck key or plugin can call.
///
/// <para><b>One path per action.</b> A hotkey and an HTTP request end in the same
/// <see cref="CueBankViewModel"/> methods the pads use (<c>TryRecall</c>,
/// <c>TryCapture</c>) and in the deck's own <c>PlayPauseCommand</c>. There is no
/// second play/pause and no second seek to keep in step.</para>
///
/// <para><b>Why the registration lives on the existing registrar.</b> Only one
/// <c>WM_HOTKEY</c> hook per window is sane; <see cref="GlobalHotkeyService"/>
/// already owns it for soundboard pads and keys every registration by string, so
/// cue ids ("cue.a.1") ride the same hook instead of opening a second one.</para>
/// </summary>
public partial class MainViewModel
{
    private CueHotkeyBinder? _cueHotkeys;

    private DeckViewModel DeckFor(PlaybackDeck deck) => deck == PlaybackDeck.A ? DeckA : DeckB;

    /// <summary>Create the binder; registration waits for the window handle.</summary>
    internal void StartCueHotkeys()
    {
        if (_hotkeys == null) return;

        _cueHotkeys = new CueHotkeyBinder(_hotkeys);
        _hotkeys.PadTriggered += OnHotkeyPressed;
    }

    /// <summary>Register every cue chord. Safe to call again after a settings change.</summary>
    internal void AttachCueHotkeys() =>
        _cueHotkeys?.Bind(CueHotkeys.Resolve(_settings.CueHotkeys));

    /// <summary>Drop the registrations and the subscription (shutdown and settings reload).</summary>
    internal void StopCueHotkeys()
    {
        if (_hotkeys != null) _hotkeys.PadTriggered -= OnHotkeyPressed;
        _cueHotkeys?.Dispose();
        _cueHotkeys = null;
    }

    /// <summary>The HTTP face of the cue surface, wired to this view model.</summary>
    internal CueApiRouter CreateCueApiRouter() => new(new CueApiTarget(this));

    private void OnHotkeyPressed(string id)
    {
        if (!CueHotkeys.TryParseId(id, out var action)) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            FireCue(action);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => FireCue(action)));
    }

    /// <summary>
    /// A hotkey is the pad, not a separate verb: an occupied slot jumps, an empty
    /// one stores the current media position. Clear mode is not consulted, so a
    /// key cannot wipe a cue because a toggle was left armed.
    /// </summary>
    private void FireCue(CueAction action)
    {
        var bank = DeckFor(action.Deck).Cues;
        if (bank.TryRecall(action.Slot, out _)) return;

        if (!bank.TryCapture(action.Slot, out string error))
            Log.Warn($"cue hotkey {action.Label}: {error}");
    }

    /// <summary>Marshal to the UI thread, because cues and transport are UI state.</summary>
    private T OnUi<T>(Func<T> work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return work();
        return dispatcher.Invoke(work);
    }

    /// <summary>
    /// <see cref="ICueActionTarget"/> over the live view models. Every method is
    /// synchronous on the UI thread so the HTTP caller gets the real outcome
    /// (409 "slot is empty") instead of an accepted-then-dropped request.
    /// </summary>
    private sealed class CueApiTarget : ICueActionTarget
    {
        private readonly MainViewModel _vm;

        public CueApiTarget(MainViewModel vm) => _vm = vm;

        public CueSurface Snapshot() => _vm.OnUi(() =>
        {
            var decks = new List<CueDeckState>(2);
            foreach (var deck in new[] { PlaybackDeck.A, PlaybackDeck.B })
            {
                var bank = _vm.DeckFor(deck).Cues;
                var slots = bank.Pads
                    .Select(p => new CueSlotState(p.Slot, p.PositionMs, p.HasPosition))
                    .ToList();

                decks.Add(new CueDeckState(deck == PlaybackDeck.A ? "a" : "b", bank.TrackId, slots));
            }

            return new CueSurface(decks, _vm.IsABMode, _vm.IsDeckSync, _vm.IsVolumeSync, _vm.IsTempoSync);
        });

        public bool Recall(PlaybackDeck deck, int slot, out long positionMs, out string error)
        {
            long position = 0;
            string message = string.Empty;

            bool ok = _vm.OnUi(() =>
            {
                var bank = _vm.DeckFor(deck).Cues;
                bool success = bank.TryRecall(slot, out message);
                position = bank.PositionOf(slot) ?? 0;
                return success;
            });

            positionMs = position;
            error = message;
            return ok;
        }

        public bool Capture(PlaybackDeck deck, int slot, out long positionMs, out string error)
        {
            long position = 0;
            string message = string.Empty;

            bool ok = _vm.OnUi(() =>
            {
                var bank = _vm.DeckFor(deck).Cues;
                bool success = bank.TryCapture(slot, out message);
                position = bank.PositionOf(slot) ?? 0;
                return success;
            });

            positionMs = position;
            error = message;
            return ok;
        }

        public void PlayPause(PlaybackDeck deck) =>
            _vm.OnUi(() =>
            {
                _vm.DeckFor(deck).PlayPauseCommand.Execute(null);
                return true;
            });

        public bool SetABMode(bool enabled, out string error)
        {
            _vm.OnUi(() =>
            {
                _vm.IsABMode = enabled;
                return true;
            });

            error = string.Empty;
            return true;
        }

        public bool SetSync(string name, bool enabled, out string error)
        {
            bool available = _vm.OnUi(() => _vm.IsSyncAvailable);
            if (!available)
            {
                error = "A-B mode is off; enable /transport/ab first";
                return false;
            }

            _vm.OnUi(() =>
            {
                switch (name)
                {
                    case "deck": _vm.IsDeckSync = enabled; break;
                    case "volume": _vm.IsVolumeSync = enabled; break;
                    case "tempo": _vm.IsTempoSync = enabled; break;
                }

                return true;
            });

            error = string.Empty;
            return true;
        }
    }
}
