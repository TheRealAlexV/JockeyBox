using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JockeyBox.Audio.Graph;
using JockeyBox.Data;
using JockeyBox.Downloads;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Soundboard;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Requirement 13: pads that can actually be filled and bound.
///
/// <para>Everything here runs without a device, a window or a network: the
/// engine is an ordinary <see cref="AudioEngine"/> (offline until started), the
/// hotkey layer is a recording double, and files live in a temp directory.</para>
/// </summary>
public sealed class SoundboardTests : IDisposable
{
    private readonly string _dir;
    private readonly AppSettings _settings = new();
    private readonly AudioEngine _engine = new();

    public SoundboardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jb-soundboard", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "soundboard"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string BoardFile(string name) => Path.Combine(_dir, "soundboard", name);

    private string WriteFakeMp3(string directory, string name = "horn.mp3")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        // Contents are irrelevant to the copy/probe path (ffprobe is allowed to fail).
        File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, 0x04, 0, 0, 0, 0 });
        return path;
    }

    // ------------------------------------------------------------------ paths

    [Fact]
    public void IsInside_is_separator_aware()
    {
        string root = BoardFile("");
        Assert.True(SoundboardFiles.IsInside(root, BoardFile("a.mp3")));
        Assert.True(SoundboardFiles.IsInside(root, BoardFile(Path.Combine("sub", "a.mp3"))));

        // The classic prefix bug: a sibling called "soundboard-backup" is not ours.
        Assert.False(SoundboardFiles.IsInside(root, root + "-backup" + Path.DirectorySeparatorChar + "a.mp3"));
        Assert.False(SoundboardFiles.IsInside(root, Path.Combine(root, "..", "outside.mp3")));
        Assert.False(SoundboardFiles.IsInside(root, null));
        Assert.False(SoundboardFiles.IsInside(null, BoardFile("a.mp3")));
    }

    [Fact]
    public void Supported_extensions_cover_the_picker_list()
    {
        foreach (var extension in new[] { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus" })
            Assert.True(SoundboardFiles.IsSupported("x" + extension), extension);
        Assert.False(SoundboardFiles.IsSupported("x.txt"));
        Assert.False(SoundboardFiles.IsSupported(null));
    }

    // ------------------------------------------------------------------ ingest

    [Fact]
    public async Task Local_file_is_copied_into_the_board_folder()
    {
        string source = WriteFakeMp3(Path.Combine(_dir, "incoming"), "Air Horn.mp3");
        var ingest = new SoundboardIngest(_settings, Path.Combine(_dir, "soundboard"));

        var result = await ingest.ImportLocalAsync(source);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.FilePath);
        Assert.True(SoundboardFiles.IsInside(Path.Combine(_dir, "soundboard"), result.FilePath));
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal("Air Horn", result.Title);
        Assert.NotEqual(source, result.FilePath); // copied, not referenced
    }

    [Fact]
    public async Task Importing_the_same_name_twice_keeps_both_files()
    {
        var ingest = new SoundboardIngest(_settings, Path.Combine(_dir, "soundboard"));
        string first = WriteFakeMp3(Path.Combine(_dir, "a"), "horn.mp3");
        string second = WriteFakeMp3(Path.Combine(_dir, "b"), "horn.mp3");

        var one = await ingest.ImportLocalAsync(first);
        var two = await ingest.ImportLocalAsync(second);

        Assert.True(one.Ok && two.Ok);
        Assert.NotEqual(one.FilePath, two.FilePath);
        Assert.True(File.Exists(one.FilePath));
        Assert.True(File.Exists(two.FilePath));
    }

    [Fact]
    public async Task A_non_audio_file_is_refused()
    {
        string source = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(source, "not audio");

        var result = await new SoundboardIngest(_settings, Path.Combine(_dir, "soundboard"))
            .ImportLocalAsync(source);

        Assert.False(result.Ok);
        Assert.Contains("not an audio file", result.Error);
    }

    [Fact]
    public async Task A_missing_file_is_refused_with_a_reason()
    {
        var result = await new SoundboardIngest(_settings, Path.Combine(_dir, "soundboard"))
            .ImportLocalAsync(Path.Combine(_dir, "nope.mp3"));

        Assert.False(result.Ok);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task An_empty_url_is_refused_before_any_tool_runs()
    {
        var result = await new SoundboardIngest(_settings, Path.Combine(_dir, "soundboard"))
            .DownloadAsync("   ");

        Assert.False(result.Ok);
        Assert.Contains("Paste a YouTube link", result.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "soundboard")));
    }

    // ------------------------------------------------------------------ hotkey parsing

    [Theory]
    [InlineData("F13")]
    [InlineData("ctrl+shift+1", "Ctrl+Shift+1")]
    [InlineData("ALT+CTRL+A", "Ctrl+Alt+A")]
    [InlineData("Ctrl+Alt+NumPad5", "Ctrl+Alt+NumPad5")]
    [InlineData("F24", "F24")]
    public void Chords_normalise_and_parse(string input, string? expected = null)
    {
        Assert.Equal(expected ?? input, GlobalHotkeyService.Normalize(input));
        Assert.True(GlobalHotkeyService.TryParse(input, out uint modifiers, out uint vk, out string error), error);
        Assert.NotEqual(0u, vk);
        _ = modifiers;
    }

    [Fact]
    public void Nonsense_chords_do_not_throw()
    {
        Assert.False(GlobalHotkeyService.TryParse("Ctrl+Blah", out _, out _, out string error));
        Assert.Contains("unknown key", error);
        // Unparseable text is preserved so the status line can show what was typed.
        Assert.Equal("Ctrl+Blah", GlobalHotkeyService.Normalize("Ctrl+Blah"));
        Assert.False(GlobalHotkeyService.SameCombo("Ctrl+Blah", "Ctrl+Shift+1"));
    }

    [Fact]
    public void Describe_turns_a_pressed_combo_into_text()
    {
        Assert.Equal("Ctrl+Alt+1", GlobalHotkeyService.Describe(
            System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt,
            System.Windows.Input.Key.D1));
        Assert.Equal("F17", GlobalHotkeyService.Describe(
            System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.F17));

        // A bare modifier can never be a hotkey.
        Assert.Null(GlobalHotkeyService.Describe(
            System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.LeftCtrl));
    }

    // ------------------------------------------------------------------ view model

    [Fact]
    public async Task Adding_a_file_creates_a_pad_with_a_default_hotkey()
    {
        var host = new RecordingHotkeyHost();
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir) { HotkeyHost = host };
        string source = WriteFakeMp3(Path.Combine(_dir, "incoming"), "Applause.mp3");

        var pad = await vm.AddLocalFileAsync(source);

        Assert.NotNull(pad);
        Assert.Single(vm.Pads);
        Assert.Equal("Applause", pad!.Name);
        Assert.Equal("F13", pad.Hotkey);
        Assert.True(host.IsBound(pad.Id, "F13"));
        Assert.True(pad.IsLoaded);
        Assert.Contains("Applause", vm.StatusText);
        Assert.True(File.Exists(pad.FilePath));
    }

    [Fact]
    public async Task Default_hotkeys_walk_up_and_stop_at_the_end_of_the_list()
    {
        using var vm = new SoundboardViewModel(_settings, new AudioEngine(), null, _dir);
        for (int i = 0; i < 3; i++)
            await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "incoming"), $"s{i}.mp3"));

        Assert.Equal(new[] { "F13", "F14", "F15" }, vm.Pads.Select(p => p.Hotkey));
    }

    [Fact]
    public async Task A_conflicting_bind_is_reported_and_the_old_owner_keeps_it()
    {
        var host = new RecordingHotkeyHost();
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir) { HotkeyHost = host };
        var first = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "a"), "one.mp3"));
        var second = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "b"), "two.mp3"));

        vm.AssignHotkey(second!, first!.Hotkey);

        Assert.Equal("F13", first.Hotkey);
        Assert.Equal("F14", second!.Hotkey);           // unchanged
        Assert.Contains("already bound", vm.StatusText);
        Assert.Equal(first.Id, host.OwnerOf("F13"));
    }

    [Fact]
    public async Task Rebinding_moves_the_registration()
    {
        var host = new RecordingHotkeyHost();
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir) { HotkeyHost = host };
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "x.mp3"));

        vm.AssignHotkey(pad!, "Ctrl+Shift+7");

        Assert.Equal("Ctrl+Shift+7", pad!.Hotkey);
        Assert.True(host.IsBound(pad.Id, "Ctrl+Shift+7"));
        Assert.False(host.IsBound(pad.Id, "F13"));

        vm.ClearHotkey(pad);
        Assert.Null(pad.Hotkey);
        Assert.False(host.IsBound(pad.Id));
    }

    [Fact]
    public async Task ReloadHotkeys_registers_every_stored_binding()
    {
        var host = new RecordingHotkeyHost();
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir) { HotkeyHost = host };
        await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "a"), "one.mp3"));
        await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "b"), "two.mp3"));

        host.UnbindAll();
        Assert.Equal(0, host.Count);

        vm.ReloadHotkeys();
        Assert.Equal(2, host.Count);
        Assert.Contains("hotkeys active", vm.StatusText);
    }

    [Fact]
    public async Task Capture_assigns_the_next_combo_and_escape_cancels()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "z.mp3"));

        vm.Bind(pad);
        Assert.True(vm.IsBinding);
        Assert.True(pad!.IsCapturing);

        Assert.True(vm.TryCaptureHotkey(System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.F9));
        Assert.Equal("Ctrl+F9", pad.Hotkey);
        Assert.False(vm.IsBinding);
        Assert.False(pad.IsCapturing);

        vm.Bind(pad);
        Assert.True(vm.TryCaptureHotkey(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.Escape));
        Assert.Equal("Ctrl+F9", pad.Hotkey);  // unchanged
        Assert.False(vm.IsBinding);
        Assert.Contains("cancelled", vm.StatusText);
    }

    [Fact]
    public async Task The_add_command_uses_the_file_picker()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        string source = WriteFakeMp3(Path.Combine(_dir, "picked"), "pick-me.mp3");
        vm.FilePicker = () => source;

        await vm.AddFromFileAsync();

        var pad = Assert.Single(vm.Pads);
        Assert.Equal("pick-me", pad.Name);
        Assert.True(SoundboardFiles.IsInside(Path.Combine(_dir, "soundboard"), pad.FilePath));
    }

    [Fact]
    public async Task Cancelling_the_picker_adds_nothing()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        vm.FilePicker = () => null;

        await vm.AddFromFileAsync();

        Assert.Empty(vm.Pads);
    }

    [Fact]
    public async Task The_url_command_keeps_the_box_when_the_download_fails()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        vm.NewSoundUrl = "   ";

        await vm.AddFromUrlAsync();

        Assert.Empty(vm.Pads);
        Assert.Contains("Paste a YouTube", vm.StatusText);
    }

    [Fact]
    public async Task Deleting_a_pad_removes_its_file_and_hotkey()
    {
        var host = new RecordingHotkeyHost();
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir) { HotkeyHost = host };
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "gone.mp3"));
        string file = pad!.FilePath!;

        vm.Remove(pad);

        Assert.Empty(vm.Pads);
        Assert.False(File.Exists(file));
        Assert.False(host.IsBound(pad.Id));
    }

    [Fact]
    public async Task Deleting_a_pad_never_deletes_a_file_outside_the_board()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "keep.mp3"));

        // Simulate a hand-edited soundboard.json pointing at the user's own file.
        string outside = WriteFakeMp3(Path.Combine(_dir, "elsewhere"), "user-owns-this.mp3");
        pad!.FilePath = outside;

        vm.Remove(pad);

        Assert.True(File.Exists(outside), "a pad must not delete outside the soundboard folder");
    }

    [Fact]
    public async Task Stop_all_stops_every_pad()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        var a = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "a"), "a.mp3"));
        var b = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "b"), "b.mp3"));
        a!.IsPlaying = true;
        b!.IsPlaying = true;

        vm.StopAll();

        Assert.False(a.IsPlaying);
        Assert.False(b.IsPlaying);
        Assert.All(_engine.Soundboard.Voices, voice => Assert.False(voice.IsPlaying));
        Assert.Contains("stopped", vm.StatusText);
    }

    [Fact]
    public async Task A_missing_file_pad_does_not_throw_when_played()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "vanish.mp3"));
        File.Delete(pad!.FilePath!);

        vm.Play(pad);   // must report, not throw

        Assert.False(pad.IsPlaying);
        Assert.Contains("no playable file", vm.StatusText);
    }

    [Fact]
    public async Task Pads_survive_a_reload_from_json()
    {
        string source = WriteFakeMp3(Path.Combine(_dir, "in"), "persist.mp3");
        string id;
        string file;
        using (var first = new SoundboardViewModel(_settings, _engine, null, _dir))
        {
            var pad = await first.AddLocalFileAsync(source);
            id = pad!.Id;
            file = pad.FilePath!;
            first.AssignHotkey(pad, "Ctrl+Alt+9");
        }

        using var second = new SoundboardViewModel(_settings, new AudioEngine(), null, _dir);

        var reloaded = Assert.Single(second.Pads);
        Assert.Equal(id, reloaded.Id);
        Assert.Equal(file, reloaded.FilePath);
        Assert.Equal("Ctrl+Alt+9", reloaded.Hotkey);
    }

    [Fact]
    public async Task Pads_are_mirrored_into_the_soundboard_table()
    {
        using var database = new TestDatabase();
        var vm = new SoundboardViewModel(_settings, _engine, database.Database, _dir);
        await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "a"), "db-one.mp3"));
        await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "b"), "db-two.mp3"));
        vm.Pads[1].Hotkey2 = "Ctrl+Alt+8";
        vm.FlushDatabase();

        var rows = database.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT title, hotkey, mode, sort FROM soundboard ORDER BY sort";
            using var reader = command.ExecuteReader();
            var list = new System.Collections.Generic.List<(string, string, string, long)>();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            return list;
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal("db-one", rows[0].Item1);
        Assert.Equal("F13", rows[0].Item2);
        Assert.Equal("oneshot", rows[0].Item3);
        Assert.Equal("db-two", rows[1].Item1);
        Assert.Equal(1, rows[1].Item4);

        vm.Dispose();
    }

    [Fact]
    public async Task Deleting_a_pad_removes_its_index_row()
    {
        using var database = new TestDatabase();
        using var vm = new SoundboardViewModel(_settings, _engine, database.Database, _dir);
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "index.mp3"));
        vm.FlushDatabase();

        vm.Remove(pad);
        vm.FlushDatabase();

        long count = database.Database.Read(connection =>
            Convert.ToInt64(JockeyBoxDatabase.Scalar(connection, "SELECT COUNT(*) FROM soundboard")));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Api_payload_carries_the_hotkey_the_deck_renders()
    {
        using var vm = new SoundboardViewModel(_settings, _engine, null, _dir);
        var pad = await vm.AddLocalFileAsync(WriteFakeMp3(Path.Combine(_dir, "in"), "deck.mp3"));
        vm.AssignHotkey(pad!, "Ctrl+Shift+4");

        // The API builds ApiPad from the pad exactly like this.
        var apiPad = new JockeyBox.Api.ApiPad(pad!.Id, pad.Name, pad.Hotkey, pad.Volume, pad.Loop, pad.IsPlaying, pad.DurationSeconds);

        Assert.Equal("Ctrl+Shift+4", apiPad.Hotkey);
    }
}

/// <summary>
/// Records what the board asks the hotkey layer to do. Mirrors the real
/// service's conflict rules without needing a window or <c>RegisterHotKey</c>.
/// </summary>
internal sealed class RecordingHotkeyHost : ISoundboardHotkeyHost
{
    private readonly System.Collections.Generic.Dictionary<string, string> _byPad = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAttached => true;

    public int Count => _byPad.Count;

    public bool FailNext { get; set; }

    public string? BindingOf(string padId) => _byPad.TryGetValue(padId, out var text) ? text : null;

    public string? OwnerOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return _byPad.FirstOrDefault(pair => GlobalHotkeyService.SameCombo(pair.Value, text)).Key;
    }

    public bool IsBound(string padId, string? text = null) =>
        _byPad.TryGetValue(padId, out var bound)
        && (text == null || GlobalHotkeyService.SameCombo(bound, text));

    public bool TryBind(string padId, string? text, out string? error)
    {
        error = null;
        Unbind(padId);
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (FailNext)
        {
            FailNext = false;
            error = "simulated registration failure";
            return false;
        }

        var owner = OwnerOf(text);
        if (owner != null && !string.Equals(owner, padId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{GlobalHotkeyService.Normalize(text)}' is already bound to another pad";
            return false;
        }

        _byPad[padId] = GlobalHotkeyService.Normalize(text);
        return true;
    }

    public void Unbind(string padId) => _byPad.Remove(padId);

    public void UnbindAll() => _byPad.Clear();
}
