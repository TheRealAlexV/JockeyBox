using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Data;
using JockeyBox.Downloads;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Soundboard;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// One soundboard pad: a short audio file (or a URL that was downloaded into one)
/// plus the key that fires it.
///
/// <para>A pad is deliberately independent of the library: a horn stab, a rimshot
/// or an airhorn is not a track, must not appear in the browser and must not be
/// scanned. The file lives under <c>%APPDATA%\JockeyBox\soundboard</c>.</para>
/// </summary>
public partial class SoundboardPad : ObservableObject
{
    /// <summary>Stable id used by the settings file, the hotkey map and the Stream Deck plugin.</summary>
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N")[..8];

    [ObservableProperty] private string _name = "New sound";

    [ObservableProperty] private string? _filePath;

    /// <summary>Where the file came from when it was downloaded (YouTube URL).</summary>
    [ObservableProperty] private string? _sourceUrl;

    /// <summary>Virtual-key name for the global hotkey, e.g. "F13" or "Ctrl+Shift+1".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay))]
    private string? _hotkey;

    /// <summary>Second binding, so one pad can sit on both a Stream Deck key and a keyboard key.</summary>
    [ObservableProperty] private string? _hotkey2;

    [ObservableProperty] private double _volume = 1.0;

    [ObservableProperty] private bool _loop;

    [ObservableProperty] private bool _isPlaying;

    [ObservableProperty] private double _durationSeconds;

    /// <summary>When the pad was added; mirrored into the database index.</summary>
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Backing voice in the audio engine (null until the file is loaded).</summary>
    [JsonIgnore]
    public SoundboardVoice? Voice { get; set; }

    /// <summary>True while a download is running for this pad (progress shown on the tile).</summary>
    [JsonIgnore]
    [ObservableProperty] private bool _isBusy;

    /// <summary>Download progress 0..1, -1 when indeterminate.</summary>
    [JsonIgnore]
    [ObservableProperty] private double _progress;

    /// <summary>Per-pad status line ("Downloading 42%" / "yt-dlp missing").</summary>
    [JsonIgnore]
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True while this pad is waiting for the next key combination.</summary>
    [JsonIgnore]
    [ObservableProperty] private bool _isCapturing;

    /// <summary>True when a playable file is attached.</summary>
    [JsonIgnore]
    public bool IsLoaded => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    /// <summary>Hotkey as shown on the tile face.</summary>
    public string HotkeyDisplay => string.IsNullOrWhiteSpace(Hotkey) ? string.Empty : Hotkey!;

    /// <summary>Playback mode as the database index spells it.</summary>
    [JsonIgnore]
    public string Mode => Loop ? "loop" : "oneshot";

    /// <summary>Initials shown on the pad face when no artwork exists.</summary>
    [JsonIgnore]
    public string Initials => string.IsNullOrWhiteSpace(Name)
        ? "?"
        : new string(Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(IsLoaded));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Initials));

    partial void OnLoopChanged(bool value) => OnPropertyChanged(nameof(Mode));
}

/// <summary>
/// The soundboard: pads, their triggers, their files and their persistence
/// (plan item 13).
///
/// <para><b>Where the sound goes.</b> Pads are summed into the same program block
/// as the decks, before the limiter and master fader, so a soundboard hit is part
/// of what OBS captures and what the audience hears — not a side channel that
/// only the operator can hear.</para>
///
/// <para><b>Adding sounds.</b> A pad is created from a local audio file or from a
/// URL. Both go through <see cref="SoundboardIngest"/>, which copies into the
/// board folder, downloads with the app's own yt-dlp/ffmpeg and reports progress
/// without touching the UI thread.</para>
///
/// <para><b>Persistence.</b> <c>soundboard.json</c> stays the source of truth; the
/// <c>soundboard</c> table is mirrored on every mutation through the same
/// <see cref="WriteQueue"/> the playlist store uses, so the index is cheap and
/// never blocks a click.</para>
/// </summary>
public partial class SoundboardViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioEngine _engine;
    private readonly string _root;
    private readonly string _storePath;
    private readonly SoundboardIngest _ingest;
    private readonly SoundboardRepository? _repository;
    private readonly WriteQueue _dbQueue = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _stateTimer;
    private ISoundboardHotkeyHost? _hotkeyHost;
    private bool _disposed;

    public SoundboardViewModel(
        AppSettings settings,
        AudioEngine engine,
        JockeyBoxDatabase? database = null,
        string? dataDirectory = null)
    {
        _settings = settings;
        _engine = engine;

        // Tests pass a temp directory; the app leaves it null for %APPDATA%.
        string appData = dataDirectory ?? ToolBootstrap.AppDataDir;
        _root = Path.Combine(appData, "soundboard");
        _storePath = Path.Combine(appData, "soundboard.json");
        _ingest = new SoundboardIngest(settings, _root);
        _repository = database == null ? null : new SoundboardRepository(database);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        // A pad's "playing" flag has to come back down when the voice ends by
        // itself; the voice owns that state and the tile cannot poll a render
        // thread property from a binding.
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _stateTimer.Tick += (_, _) => SyncPlayingState();
        _stateTimer.Start();

        Directory.CreateDirectory(_root);
        Load();
    }

    /// <summary>All pads, in board order.</summary>
    public ObservableCollection<SoundboardPad> Pads { get; } = new();

    /// <summary>Raised when a pad is triggered (status line / Stream Deck mirror).</summary>
    public event Action<SoundboardPad>? PadTriggered;

    /// <summary>Directory the sound files live in.</summary>
    public string RootDirectory => _root;

    /// <summary>Master gain for the whole board.</summary>
    [ObservableProperty] private double _volume = 1.0;

    /// <summary>Panel summary ("3 sounds · 1 playing").</summary>
    [ObservableProperty] private string _summary = "No sounds";

    /// <summary>Result of the last add / bind / delete — including conflicts.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Text of the "add from URL" box.</summary>
    [ObservableProperty] private string _newSoundUrl = string.Empty;

    /// <summary>True while waiting for a key combination.</summary>
    [ObservableProperty] private bool _isBinding;

    /// <summary>The pad that is waiting for a key combination.</summary>
    [ObservableProperty] private SoundboardPad? _bindingPad;

    /// <summary>
    /// File picker hook. Tests replace it; the app leaves it null and the command
    /// opens the standard open-file dialog.
    /// </summary>
    public Func<string?>? FilePicker { get; set; }

    /// <summary>The hotkey layer; set by the window once it has a handle.</summary>
    public ISoundboardHotkeyHost? HotkeyHost
    {
        get => _hotkeyHost;
        set
        {
            _hotkeyHost = value;
            ReloadHotkeys();
        }
    }

    /// <summary>True when the board has at least one pad that can play.</summary>
    public bool HasSounds => Pads.Any(p => p.IsLoaded);

    partial void OnVolumeChanged(double value)
    {
        _engine.SoundboardVolume = (float)Math.Clamp(value, 0, 1.2);
        _settings.SoundboardVolume = (float)Volume;
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Fire a pad. Restarts it from the top when it is already playing.</summary>
    [RelayCommand]
    public void Play(SoundboardPad? pad)
    {
        if (pad == null) return;

        if (pad.IsBusy)
        {
            StatusText = $"'{pad.Name}' is still downloading";
            return;
        }

        var voice = EnsureVoice(pad);
        if (voice == null)
        {
            Log.Warn($"soundboard: pad '{pad.Name}' has no playable file");
            StatusText = $"'{pad.Name}' has no playable file";
            return;
        }

        voice.Gain = (float)pad.Volume;
        voice.Loop = pad.Loop;
        voice.Play();
        pad.IsPlaying = true;
        PadTriggered?.Invoke(pad);
        RefreshSummary();
    }

    /// <summary>Stop one pad.</summary>
    [RelayCommand]
    public void Stop(SoundboardPad? pad)
    {
        if (pad == null) return;
        pad.Voice?.Stop();
        pad.IsPlaying = false;
        RefreshSummary();
    }

    /// <summary>Panic: stop every pad (also what the API's /stop-all and the deck use).</summary>
    [RelayCommand]
    public void StopAll()
    {
        _engine.Soundboard.StopAll();
        foreach (var pad in Pads)
        {
            pad.IsPlaying = false;
        }

        StatusText = "All sounds stopped";
        RefreshSummary();
    }

    /// <summary>Remove a pad, unregister its hotkey and delete its managed file.</summary>
    [RelayCommand]
    public void Remove(SoundboardPad? pad)
    {
        if (pad == null) return;

        _hotkeyHost?.Unbind(pad.Id);
        Pads.Remove(pad);
        pad.Voice?.Stop();
        pad.Voice?.Dispose();
        pad.Voice = null;

        if (TryDeleteManagedFile(pad.FilePath))
            StatusText = $"Removed '{pad.Name}'";
        else if (!string.IsNullOrEmpty(pad.FilePath) && !SoundboardFiles.IsInside(_root, pad.FilePath))
            StatusText = $"Removed '{pad.Name}' (file left in place: outside the soundboard folder)";

        Save();
        RefreshSummary();
    }

    /// <summary>Add an empty slot the user can then point at a file or URL.</summary>
    [RelayCommand]
    public void AddSlot()
    {
        var pad = new SoundboardPad { Name = $"Sound {Pads.Count + 1}", AddedUtc = DateTime.UtcNow };
        Hook(pad);
        Pads.Add(pad);
        AssignDefaultHotkey(pad);
        Save();
        RefreshSummary();
    }

    /// <summary>Pick a local audio file and add it as a pad.</summary>
    [RelayCommand]
    public async Task AddFromFileAsync()
    {
        // A hook (tests) owns the choice; only the real app opens the dialog.
        string? path = FilePicker != null ? FilePicker() : PickAudioFile();
        if (string.IsNullOrWhiteSpace(path)) return;

        await AddLocalFileAsync(path).ConfigureAwait(true);
    }

    /// <summary>Add the URL currently typed in the box.</summary>
    [RelayCommand]
    public async Task AddFromUrlAsync()
    {
        string url = (NewSoundUrl ?? "").Trim();
        if (url.Length == 0)
        {
            StatusText = "Paste a YouTube (or direct audio) link first";
            return;
        }

        NewSoundUrl = string.Empty;
        await AddUrlAsync(url).ConfigureAwait(true);
    }

    /// <summary>Start capturing the next key combination for <paramref name="pad"/>.</summary>
    [RelayCommand]
    public void Bind(SoundboardPad? pad)
    {
        if (pad == null) return;

        foreach (var other in Pads) other.IsCapturing = false;
        pad.IsCapturing = true;
        BindingPad = pad;
        IsBinding = true;
        StatusText = $"Press a key combination for '{pad.Name}' (Esc cancels)";
    }

    /// <summary>Forget a pad's hotkey.</summary>
    [RelayCommand]
    public void ClearHotkey(SoundboardPad? pad)
    {
        if (pad == null) return;
        AssignHotkey(pad, null);
    }

    /// <summary>Copy the soundboard folder path for the user.</summary>
    [RelayCommand]
    public void RevealFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"soundboard: could not open {_root}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- adding

    /// <summary>Add a file that already exists on disk (copied into the board folder).</summary>
    public async Task<SoundboardPad?> AddLocalFileAsync(string path)
    {
        StatusText = $"Adding {Path.GetFileName(path)}…";
        var result = await _ingest.ImportLocalAsync(path).ConfigureAwait(true);
        if (!result.Ok)
        {
            StatusText = result.Error ?? $"Could not add {Path.GetFileName(path)}";
            Log.Warn($"soundboard: {StatusText}");
            return null;
        }

        return Adopt(result);
    }

    /// <summary>Download a URL as audio and add it as a pad.</summary>
    public async Task<SoundboardPad?> AddUrlAsync(string url)
    {
        var pad = new SoundboardPad
        {
            Name = "Downloading…",
            SourceUrl = url,
            IsBusy = true,
            Progress = -1,
            AddedUtc = DateTime.UtcNow,
        };
        Hook(pad);
        Pads.Add(pad);
        RefreshSummary();

        var progress = new Progress<SoundboardIngestProgress>(p =>
        {
            if (p.Fraction >= 0) pad.Progress = p.Fraction;
            pad.Status = p.Message;
            StatusText = p.Message;
        });

        SoundboardIngestResult result;
        try
        {
            result = await _ingest.DownloadAsync(url, progress).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error($"soundboard: download failed: {url}", ex);
            result = SoundboardIngestResult.Fail("Download failed (see log)");
        }

        if (!result.Ok)
        {
            Pads.Remove(pad);
            StatusText = result.Error ?? "Download failed";
            RefreshSummary();
            return null;
        }

        pad.Name = string.IsNullOrWhiteSpace(result.Title) ? $"Sound {Pads.Count + 1}" : result.Title!;
        pad.FilePath = result.FilePath;
        pad.DurationSeconds = result.DurationSeconds;
        pad.IsBusy = false;
        pad.Progress = 1;
        pad.Status = string.Empty;
        OnPropertyChanged(nameof(HasSounds));

        RegisterHotkey(pad, NextFreeHotkey());
        Save();
        RefreshSummary();

        StatusText = pad.Hotkey is null
            ? $"Added '{pad.Name}'"
            : $"Added '{pad.Name}' — hotkey {pad.Hotkey}";
        Log.Info($"soundboard: added '{pad.Name}' from {url} ({pad.DurationSeconds:0.0}s)");
        return pad;
    }

    private void Hook(SoundboardPad pad) => pad.PropertyChanged += OnPadChanged;

    private void OnPadChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SoundboardPad.Name):
            case nameof(SoundboardPad.Volume):
            case nameof(SoundboardPad.Loop):
            case nameof(SoundboardPad.Hotkey2):
                SaveDeferred();
                break;
        }
    }

    private SoundboardPad Adopt(SoundboardIngestResult result)
    {
        var pad = new SoundboardPad
        {
            Name = string.IsNullOrWhiteSpace(result.Title) ? Path.GetFileNameWithoutExtension(result.FilePath!) : result.Title!,
            FilePath = result.FilePath,
            SourceUrl = result.SourceUrl,
            DurationSeconds = result.DurationSeconds,
            AddedUtc = DateTime.UtcNow,
        };

        Hook(pad);
        Pads.Add(pad);
        OnPropertyChanged(nameof(HasSounds));
        RegisterHotkey(pad, NextFreeHotkey());
        Save();
        RefreshSummary();

        StatusText = pad.Hotkey is null
            ? $"Added '{pad.Name}'"
            : $"Added '{pad.Name}' — hotkey {pad.Hotkey}";
        Log.Info($"soundboard: added '{pad.Name}' from {result.FilePath} ({pad.DurationSeconds:0.0}s)");
        return pad;
    }

    /// <summary>
    /// Point an existing (empty) pad at a file. Files outside the board folder are
    /// copied in, so the board keeps working when the user moves the original.
    /// </summary>
    public async Task<bool> SetFileAsync(SoundboardPad pad, string sourcePath)
    {
        if (pad == null || !File.Exists(sourcePath)) return false;

        var imported = await _ingest.ImportLocalAsync(sourcePath).ConfigureAwait(true);
        if (!imported.Ok || imported.FilePath == null)
        {
            StatusText = imported.Error ?? "Could not attach the file";
            return false;
        }

        string target = imported.FilePath;
        // Two pads must never own the same file, or deleting one would silence the other.
        if (Pads.Any(p => !ReferenceEquals(p, pad) && SoundboardFiles.SamePath(p.FilePath, target)))
        {
            string copy = SoundboardFiles.UniquePath(target);
            File.Copy(target, copy);
            target = copy;
        }

        pad.FilePath = target;
        pad.DurationSeconds = imported.DurationSeconds;
        if (string.IsNullOrWhiteSpace(pad.Name) || pad.Name.StartsWith("Sound ", StringComparison.Ordinal))
            pad.Name = imported.Title ?? Path.GetFileNameWithoutExtension(target);

        if (pad.Hotkey is null) RegisterHotkey(pad, NextFreeHotkey());
        Save();
        RefreshSummary();
        OnPropertyChanged(nameof(HasSounds));
        return true;
    }

    /// <summary>Legacy entry point kept for the download pipeline's "send to soundboard".</summary>
    public Task ImportFromSourceAsync(SoundboardPad pad, string source) => AddUrlAsync(source);

    // ---------------------------------------------------------------- hotkeys

    /// <summary>
    /// Turn a captured chord into a binding. Conflicts are reported in
    /// <see cref="StatusText"/> and never thrown.
    /// </summary>
    public void AssignHotkey(SoundboardPad pad, string? text)
    {
        if (pad == null) return;

        var normalised = GlobalHotkeyService.Normalize(text);
        if (normalised.Length == 0)
        {
            _hotkeyHost?.Unbind(pad.Id);
            pad.Hotkey = null;
            StatusText = $"'{pad.Name}' is no longer bound";
            Save();
            RefreshSummary();
            return;
        }

        var owner = _hotkeyHost?.OwnerOf(normalised);
        if (owner != null && !string.Equals(owner, pad.Id, StringComparison.OrdinalIgnoreCase))
        {
            var other = Pads.FirstOrDefault(p => string.Equals(p.Id, owner, StringComparison.OrdinalIgnoreCase));
            StatusText = $"{normalised} is already bound to '{other?.Name ?? owner}' — '{pad.Name}' left unchanged";
            return;
        }

        pad.Hotkey = normalised;

        if (_hotkeyHost == null)
        {
            StatusText = $"'{pad.Name}' → {normalised} (hotkey host not ready; will register on load)";
        }
        else if (_hotkeyHost.TryBind(pad.Id, normalised, out string? error))
        {
            StatusText = $"'{pad.Name}' now fires on {normalised}";
        }
        else
        {
            StatusText = $"Hotkey {normalised} for '{pad.Name}': {error}";
        }

        Save();
        RefreshSummary();
    }

    /// <summary>Called from the window's key handler while a bind is active.</summary>
    public bool TryCaptureHotkey(ModifierKeys modifiers, Key key)
    {
        if (!IsBinding || BindingPad == null) return false;

        var pad = BindingPad;
        if (key == Key.Escape)
        {
            EndCapture();
            StatusText = "Hotkey capture cancelled";
            return true;
        }

        // Alt arrives as Key.System with the real key in SystemKey; the caller
        // resolves that, but be forgiving if it does not.
        if (key == Key.System) return true;

        var text = GlobalHotkeyService.Describe(modifiers, key);
        if (text == null)
        {
            StatusText = "That key cannot be a global hotkey — add a modifier (Ctrl+Alt+…) or use an F-key";
            return true;
        }

        EndCapture();
        AssignHotkey(pad, text);
        return true;
    }

    /// <summary>Register every stored binding with the host (load, attach, reload).</summary>
    public void ReloadHotkeys()
    {
        if (_hotkeyHost == null) return;

        _hotkeyHost.UnbindAll();
        int bound = 0, failed = 0;
        var firstError = (string?)null;

        foreach (var pad in Pads)
        {
            if (string.IsNullOrWhiteSpace(pad.Hotkey)) continue;
            if (_hotkeyHost.TryBind(pad.Id, pad.Hotkey, out string? error)) bound++;
            else
            {
                failed++;
                firstError ??= $"{pad.Hotkey} for '{pad.Name}': {error}";
            }
        }

        if (failed > 0)
            StatusText = $"{bound} hotkey{(bound == 1 ? "" : "s")} active, {failed} failed — {firstError}";
        else if (bound > 0)
            StatusText = $"{bound} hotkey{(bound == 1 ? "" : "s")} active";

        Log.Info($"soundboard: hotkeys registered={bound} failed={failed}");
        RefreshSummary();
    }

    private void RegisterHotkey(SoundboardPad pad, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AssignHotkey(pad, text);
    }

    private void AssignDefaultHotkey(SoundboardPad pad)
    {
        var text = NextFreeHotkey();
        if (text != null) RegisterHotkey(pad, text);
    }

    /// <summary>First unbound chord from a Stream-Deck-friendly list (F13–F24, then Ctrl+Alt+digits).</summary>
    public string? NextFreeHotkey()
    {
        for (int i = 13; i <= 24; i++)
        {
            string candidate = $"F{i}";
            if (!IsBoundElsewhere(candidate)) return candidate;
        }

        for (int i = 1; i <= 9; i++)
        {
            string candidate = $"Ctrl+Alt+{i}";
            if (!IsBoundElsewhere(candidate)) return candidate;
        }

        return null;
    }

    private bool IsBoundElsewhere(string text) =>
        Pads.Any(p => GlobalHotkeyService.SameCombo(p.Hotkey, text))
        || _hotkeyHost?.OwnerOf(text) != null;

    private void EndCapture()
    {
        foreach (var pad in Pads) pad.IsCapturing = false;
        BindingPad = null;
        IsBinding = false;
    }

    // ---------------------------------------------------------------- engine

    /// <summary>Rebuild the engine voices after a settings/route change.</summary>
    public void ReloadVoices()
    {
        foreach (var pad in Pads)
        {
            pad.Voice?.Dispose();
            pad.Voice = null;
        }

        foreach (var pad in Pads.Where(p => p.IsLoaded)) EnsureVoice(pad);
        RefreshSummary();
    }

    /// <summary>
    /// Mirror the engine's voice state onto the pads (end of sound, panic stop
    /// from the API, a track change stopping the bus). Cheap: no work when
    /// nothing is playing and no voices exist.
    /// </summary>
    private void SyncPlayingState()
    {
        bool changed = false;
        foreach (var pad in Pads)
        {
            bool playing = pad.Voice?.IsPlaying ?? false;
            if (pad.IsPlaying != playing)
            {
                pad.IsPlaying = playing;
                changed = true;
            }
        }

        if (changed) RefreshSummary();
    }

    private SoundboardVoice? EnsureVoice(SoundboardPad pad)
    {
        if (string.IsNullOrEmpty(pad.FilePath) || !File.Exists(pad.FilePath)) return null;

        if (pad.Voice == null)
        {
            pad.Voice = _engine.Soundboard.GetOrAdd(pad.Id, AudioEngine.Channels);
            pad.Voice.Name = pad.Name;
            _ = pad.Voice.LoadAsync(pad.FilePath);
        }

        return pad.Voice;
    }

    private bool TryDeleteManagedFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!SoundboardFiles.IsInside(_root, path)) return false; // never delete a user's own file

        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"soundboard: delete failed for {path}: {ex.Message}");
            return false;
        }
    }

    private string? PickAudioFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a sound",
            Filter = SoundboardFiles.OpenFileFilter,
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>Load pads from disk. A corrupt file is logged and ignored, never fatal.</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var loaded = JsonSerializer.Deserialize<List<SoundboardPad>>(File.ReadAllText(_storePath));
            if (loaded == null) return;

            Pads.Clear();
            foreach (var pad in loaded)
            {
                pad.IsPlaying = false;
                pad.IsBusy = false;
                pad.IsCapturing = false;
                pad.Voice = null;
                if (pad.AddedUtc == default) pad.AddedUtc = DateTime.UtcNow;
                Hook(pad);
                Pads.Add(pad);
            }

            RefreshSummary();
        }
        catch (Exception ex)
        {
            Log.Error("soundboard: could not load soundboard.json", ex);
        }
    }

    /// <summary>Write pads to disk (atomic replace) and mirror them into the database.</summary>
    public void Save()
    {
        try
        {
            string temp = _storePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Pads, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("soundboard: could not save soundboard.json", ex);
        }

        MirrorToDatabase();
    }

    /// <summary>An edit that should not write on every keystroke/slider tick.</summary>
    private void SaveDeferred()
    {
        // No message loop (unit tests, headless tools): write straight away.
        if (System.Windows.Application.Current?.Dispatcher == null)
        {
            Save();
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void MirrorToDatabase()
    {
        var repository = _repository;
        if (repository == null) return;

        var rows = Pads.Select((pad, index) => new SoundboardRow
        {
            Id = SoundboardRepository.RowIdFor(pad.Id),
            Title = pad.Name ?? "",
            Path = pad.FilePath ?? "",
            SourceUrl = pad.SourceUrl,
            DurationMs = pad.DurationSeconds * 1000.0,
            Gain = pad.Volume,
            Mode = pad.Mode,
            Hotkey = pad.Hotkey,
            Hotkey2 = pad.Hotkey2,
            Sort = index,
            AddedUtc = pad.AddedUtc,
        }).ToList();

        _dbQueue.Enqueue("soundboard", () =>
        {
            try
            {
                repository.SaveAll(rows);
            }
            catch (Exception ex)
            {
                Log.Warn($"soundboard: database mirror failed: {ex.Message}");
            }
        });
    }

    /// <summary>Wait for queued mirror writes (shutdown and tests).</summary>
    public void FlushDatabase() => _dbQueue.Flush();

    /// <summary>Recompute the panel summary ("4 sounds · 1 playing").</summary>
    public void RefreshSummary()
    {
        int total = Pads.Count;
        int playing = Pads.Count(p => p.IsPlaying);
        int bound = Pads.Count(p => !string.IsNullOrWhiteSpace(p.Hotkey));
        Summary = total == 0
            ? "No sounds"
            : $"{total} sound{(total == 1 ? "" : "s")} \u00b7 {bound} bound \u00b7 {playing} playing";
        OnPropertyChanged(nameof(HasSounds));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _saveTimer.Stop();
        _stateTimer.Stop();
        Save();
        _dbQueue.Flush();
        _dbQueue.Dispose();
        _hotkeyHost?.UnbindAll();
    }
}
