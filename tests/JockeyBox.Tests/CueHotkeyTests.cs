using JockeyBox.Playback;
using JockeyBox.ViewModels;

namespace JockeyBox.Tests;

/// <summary>
/// The cue action map and its binding layer. A global hotkey cannot be pressed
/// in CI, so these tests pin the layer underneath: the ids, the shipped chords,
/// and what happens when a chord is already owned by something else.
/// </summary>
// Drives UiThread: it builds a MainViewModel on the process's one Application
// dispatcher, the static UiThreadCollection serialises. Without this it ran beside
// the other view-model suites on that single thread.
[Collection(UiThreadCollection.Name)]
public sealed class CueHotkeyTests
{
    [Fact]
    public void Eighteen_defaults_one_per_cue_and_no_chord_used_twice()
    {
        var defaults = CueHotkeys.Defaults();

        Assert.Equal(18, defaults.Count);
        Assert.Equal(18, CueHotkeys.Actions.Count);

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in defaults)
        {
            string chord = GlobalHotkeyService.Normalize(pair.Value);
            Assert.False(seen.TryGetValue(chord, out var other),
                $"{pair.Key} and {other} share {chord}");
            seen[chord] = pair.Key;
        }
    }

    [Fact]
    public void Defaults_avoid_every_chord_the_app_and_the_rig_already_use()
    {
        // SoundboardViewModel.NextFreeHotkey hands out F13-F24 first, then
        // Ctrl+Alt+digits; the main window binds Ctrl+Q and Ctrl+Shift+Left/Right;
        // the rig's adv-ss owns F20. None of these may appear as a cue default.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 13; i <= 24; i++) reserved.Add(GlobalHotkeyService.Normalize($"F{i}"));
        for (int i = 1; i <= 9; i++) reserved.Add(GlobalHotkeyService.Normalize($"Ctrl+Alt+{i}"));
        reserved.Add(GlobalHotkeyService.Normalize("Ctrl+Q"));
        reserved.Add(GlobalHotkeyService.Normalize("Ctrl+Shift+Left"));
        reserved.Add(GlobalHotkeyService.Normalize("Ctrl+Shift+Right"));

        foreach (var pair in CueHotkeys.Defaults())
        {
            string chord = GlobalHotkeyService.Normalize(pair.Value);
            Assert.DoesNotContain(chord, reserved);
        }
    }

    [Theory]
    [InlineData("cue.a.1", "cue.a.1", PlaybackDeck.A, 1)]
    [InlineData("cue.b.8", "cue.b.8", PlaybackDeck.B, 8)]
    [InlineData("cue.a.q", "cue.a.q", PlaybackDeck.A, 0)]
    [InlineData("cue.b.quick", "cue.b.q", PlaybackDeck.B, 0)]
    [InlineData("CUE.A.0", "cue.a.q", PlaybackDeck.A, 0)]
    public void Ids_round_trip(string id, string canonical, PlaybackDeck deck, int slot)
    {
        Assert.True(CueHotkeys.TryParseId(id, out var action));
        Assert.Equal(deck, action.Deck);
        Assert.Equal(slot, action.Slot);
        Assert.Equal(canonical, CueHotkeys.Id(action));
    }

    [Theory]
    [InlineData("cue.c.1")]
    [InlineData("cue.a.9")]
    [InlineData("cue.a")]
    [InlineData("pad.guid")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_ids_are_rejected(string? id)
    {
        Assert.False(CueHotkeys.TryParseId(id, out _));
    }

    [Fact]
    public void Resolve_layers_settings_over_defaults_and_honours_an_empty_chord()
    {
        var stored = new Dictionary<string, string>
        {
            ["cue.a.1"] = "Ctrl+Alt+F5",
            ["cue.b.q"] = "",
            ["cue.c.9"] = "Ctrl+Alt+F6", // unknown action: dropped, not registered as a dead key
        };

        var plan = CueHotkeys.Resolve(stored);

        Assert.Equal("Ctrl+Alt+F5", plan["cue.a.1"]);
        Assert.Equal(string.Empty, plan["cue.b.q"]);
        Assert.Equal(CueHotkeys.DefaultChord(new CueAction(PlaybackDeck.A, 2)), plan["cue.a.2"]);
        Assert.Equal(18, plan.Count);
        Assert.False(plan.ContainsKey("cue.c.9"));
    }

    [Fact]
    public void Binder_registers_every_chord_in_the_plan()
    {
        var registrar = new FakeHotkeyRegistrar();
        using var binder = new CueHotkeyBinder(registrar);

        binder.Bind(CueHotkeys.Resolve(null));

        Assert.Equal(18, binder.Count);
        Assert.Empty(binder.Failures);
        Assert.Equal(18, registrar.Live.Count);
    }

    [Fact]
    public void Binder_reports_an_occupied_chord_by_name_and_binds_the_rest()
    {
        var registrar = new FakeHotkeyRegistrar();
        registrar.Taken.Add("Ctrl+Shift+3");

        var warnings = new List<string>();
        using var binder = new CueHotkeyBinder(registrar, warnings.Add);

        binder.Bind(CueHotkeys.Resolve(null));

        Assert.Equal(17, binder.Count);
        var failure = Assert.Single(binder.Failures);
        Assert.Equal("cue.a.3", failure.Key);
        Assert.Contains("Ctrl+Shift+3", failure.Value);

        var warning = Assert.Single(warnings);
        Assert.Contains("Ctrl+Shift+3", warning);
        Assert.Contains("A3", warning);
    }

    [Fact]
    public void Binder_leaves_a_deliberately_unbound_cue_unregistered()
    {
        var registrar = new FakeHotkeyRegistrar();
        using var binder = new CueHotkeyBinder(registrar);

        var plan = CueHotkeys.Resolve(new Dictionary<string, string> { ["cue.b.q"] = "" });
        binder.Bind(plan);

        Assert.Equal(17, binder.Count);
        Assert.Null(binder.BindingOf(new CueAction(PlaybackDeck.B, 0)));
        Assert.False(registrar.Live.ContainsKey("cue.b.q"));
    }

    [Fact]
    public void Teardown_drops_every_registration_and_a_rebind_registers_them_again()
    {
        var registrar = new FakeHotkeyRegistrar();
        var binder = new CueHotkeyBinder(registrar);

        binder.Bind(CueHotkeys.Resolve(null));
        Assert.Equal(18, registrar.Live.Count);

        binder.Dispose();
        Assert.Empty(registrar.Live);
        Assert.Equal(0, binder.Count);

        // The leak this guards against: after a settings change the old chords
        // must not still be registered, and re-registering must work.
        binder.Bind(CueHotkeys.Resolve(null));
        Assert.Equal(18, registrar.Live.Count);

        binder.Dispose();
    }

    [Fact]
    public void Rebind_replaces_the_previous_chord_rather_than_adding_one()
    {
        var registrar = new FakeHotkeyRegistrar();
        using var binder = new CueHotkeyBinder(registrar);

        binder.Bind(CueHotkeys.Resolve(new Dictionary<string, string> { ["cue.a.1"] = "Ctrl+Alt+F7" }));
        binder.Bind(CueHotkeys.Resolve(new Dictionary<string, string> { ["cue.a.1"] = "Ctrl+Alt+F8" }));

        Assert.Equal("Ctrl+Alt+F8", binder.BindingOf(new CueAction(PlaybackDeck.A, 1)));
        Assert.Equal(1, registrar.Live.Keys.Count(k => k == "cue.a.1"));
    }
    [Theory]
    [InlineData("A", false)]
    [InlineData("5", false)]
    [InlineData("NumPad5", false)]
    [InlineData("F13", true)]
    [InlineData("Ctrl+Shift+1", true)]
    [InlineData("Ctrl+Alt+Shift+Q", true)]
    public void A_global_chord_needs_a_modifier_unless_it_is_a_function_key(string chord, bool allowed) =>
        Assert.Equal(allowed, CueHotkeys.HasRequiredModifier(chord));

    [Fact]
    public void Binder_refuses_a_bare_letter_instead_of_swallowing_it_desktop_wide()
    {
        var registrar = new FakeHotkeyRegistrar();
        var warnings = new List<string>();
        using var binder = new CueHotkeyBinder(registrar, warnings.Add);

        binder.Bind(CueHotkeys.Resolve(new Dictionary<string, string> { ["cue.a.1"] = "A" }));

        Assert.False(registrar.Live.ContainsKey("cue.a.1"));
        Assert.Contains("cue.a.1", binder.Failures);
        Assert.Contains(warnings, w => w.Contains("cue.a.1") && w.Contains("A "));
        Assert.Equal(17, binder.Count);
    }

    [Fact]
    public void Binder_still_accepts_a_bare_function_key()
    {
        var registrar = new FakeHotkeyRegistrar();
        using var binder = new CueHotkeyBinder(registrar);

        binder.Bind(CueHotkeys.Resolve(new Dictionary<string, string> { ["cue.a.1"] = "F13" }));

        Assert.Equal("F13", binder.BindingOf(new CueAction(PlaybackDeck.A, 1)));
        Assert.Empty(binder.Failures);
        Assert.Equal(18, binder.Count);
    }

    [Fact]
    public void Cue_input_rebinds_and_fires_again_after_a_detach()
    {
        var registrar = new FakeHotkeyRegistrar();
        var fired = new List<CueAction>();
        using var input = new CueHotkeyInput(registrar, fired.Add);

        input.Attach(CueHotkeys.Resolve(null));
        Assert.Equal(18, input.Bound);

        input.Detach();
        Assert.Equal(0, input.Bound);
        registrar.Raise("cue.a.2");
        Assert.Empty(fired);

        // The regression: after a stop the next attach must bind again, not
        // silently do nothing until the app restarts.
        input.Attach(CueHotkeys.Resolve(null));
        Assert.Equal(18, input.Bound);

        registrar.Raise("cue.a.2");
        registrar.Raise("cue.b.q");
        registrar.Raise("pad.unknown");
        Assert.Equal(2, fired.Count);
        Assert.Equal(new CueAction(PlaybackDeck.B, 0), fired[1]);
    }

    [Fact]
    public void Main_view_model_asks_for_every_chord_again_after_a_stop()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            UiThread.Run(() =>
            {
                vm.StopCueHotkeys();
                return 0;
            });

            // -1 is "no input object at all", which is exactly the silent failure.
            Assert.Equal(-1, vm.CueHotkeyFailureCount);

            UiThread.Run(() =>
            {
                vm.AttachCueHotkeys();
                return 0;
            });

            // Headless: there is no window handle, so every chord is refused as
            // "window not ready" - the point is that all 27 were attempted
            // (eighteen deck cues and the nine pair cues).
            Assert.Equal(27, vm.CueHotkeyFailureCount);
            Assert.Equal(0, vm.CueHotkeyBound);
        }
        finally
        {
            UiThread.Run(() =>
            {
                vm.Dispose();
                return 0;
            });
        }
    }
}
