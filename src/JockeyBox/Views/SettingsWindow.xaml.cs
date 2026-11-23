using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JockeyBox.Audio;
using JockeyBox.Audio.Endpoints;
using JockeyBox.Audio.Graph;
using JockeyBox.Dsp;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    /// <summary>
    /// The state the controls were stamped from, detached at construction. Save
    /// compares against it to tell an operator edit from a change made elsewhere
    /// while this dialog was open (the engine setup writes PythonPath, a download
    /// refreshes tool paths, the transport persists karaoke mode).
    /// </summary>
    private readonly AppSettings _snapshot;
    private readonly SpotifyOAuthService _oauth;

    /// <summary>
    /// Library rows come from the main view model (this window never owns them).
    /// Optional so the dialog still works when opened without a library.
    /// </summary>
    private readonly Func<IReadOnlyList<TrackEntry>>? _tracks;
    private readonly PlaylistStore? _playlists;
    private readonly DeviceWatcher? _deviceWatcher;
    /// <summary>Render endpoints for the routing boxes; a test hands in a fixed list.</summary>
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _listOutputs;
    /// <summary>
    /// The installed ASIO drivers. Reading a driver's channel count opens it, and ASIO is
    /// single-client, so the probe runs once and the answer is kept for the life of the
    /// dialog; a test hands in a fixed list instead.
    /// </summary>
    private readonly Func<IReadOnlyList<AsioDriverInfo>> _asioDrivers;
    private IReadOnlyList<AsioDriverInfo>? _asioDriversLoaded;
    /// <summary>The per-driver picker selection (mode + offset) not yet added as a source.</summary>
    private readonly Dictionary<string, AsioDraft> _asioDrafts = new(StringComparer.Ordinal);
    private bool _libraryToolsBusy;

    public SettingsWindow(AppSettings settings, Func<IReadOnlyList<TrackEntry>>? tracks = null,
                          PlaylistStore? playlists = null, DeviceWatcher? deviceWatcher = null,
                          Func<IReadOnlyList<AudioDeviceInfo>>? outputDevices = null,
                          Func<IReadOnlyList<AsioDriverInfo>>? asioDrivers = null)
    {
        InitializeComponent();
        _settings = settings;
        _tracks = tracks;
        _playlists = playlists;
        _listOutputs = outputDevices ?? ListOutputDevices;
        _asioDrivers = asioDrivers ?? AsioSourceCatalog.Probe;
        _oauth = new SpotifyOAuthService(settings);
        // The main window owns the process's one watcher; a dialog that
        // registered its own would be a second COM callback nobody unregisters.
        if (deviceWatcher != null)
        {
            _deviceWatcher = deviceWatcher;
            _deviceWatcher.DevicesChanged += OnDeviceChange;
            Closed += (_, _) => _deviceWatcher.DevicesChanged -= OnDeviceChange;
        }

        MusicDirBox.Text = settings.MusicDirectory;

        PythonBox.Text = settings.PythonPath;
        GpuCheck.IsChecked = settings.SeparationUseGpu;
        UpdateEngineState();

        ApiUrlBox.Text = settings.LyricsApiBaseUrl;
        OffsetBox.Text = settings.LyricsOffsetMs.ToString("F0");
        SpotifyClientIdBox.Text = settings.SpotifyClientIdOverride;
        UpdateSpotifyStatus();

        LoadDownloadSettings(settings);
        LoadAudioSettings(settings);

        // After every control is stamped: this is the baseline Save merges against.
        _snapshot = _settings.Clone();
    }

    /// <summary>
    /// Fill the six-bus audio routing, the karaoke mode flag, the recording
    /// devices, delay, tempo and transition controls.
    ///
    /// <para>Device lists come from the same <see cref="DeviceService"/> the decks
    /// use (default endpoint first), and "None" is always the first entry. The
    /// legacy Program/Second outputs and the single-mic fields are gone from this
    /// window on purpose: they are migration inputs now, and showing them would
    /// let a save undo the migration.</para>
    /// </summary>
    private void LoadAudioSettings(AppSettings settings)
    {
        var devices = _listOutputs();

        FillDeviceBox(MainOutputBox, devices, settings.MainOutputDeviceId);
        FillDeviceBox(KaraokeAidOutputBox, devices, settings.KaraokeAidOutputDeviceId);
        FillDeviceBox(RecordingOutputBox, devices, settings.RecordingOutputDeviceId);
        FillDeviceBox(SoundboardOutputBox, devices, settings.SoundboardOutputDeviceId);
        FillDeviceBox(MonitorOutputBox, devices, settings.MonitorOutputDeviceId);

        SoundboardFollowsMainCheck.IsChecked = settings.SoundboardFollowsMain;

        VirtualAsioDeckACheck.IsChecked = settings.VirtualAsioDeckA;
        VirtualAsioDeckBCheck.IsChecked = settings.VirtualAsioDeckB;
        VirtualAsioRecordingCheck.IsChecked = settings.VirtualAsioRecording;
        VirtualAsioSoundboardCheck.IsChecked = settings.VirtualAsioSoundboard;
        VirtualAsioMonitorCheck.IsChecked = settings.VirtualAsioMonitor;
        VirtualAsioBusNameBox.Text = settings.VirtualAsioBusName;

        LoadMicDevices(settings);

        OutputDelayBox.Text = settings.OutputDelayMs.ToString(CultureInfo.CurrentCulture);
        MainOutputDelayBox.Text = DeviceDelayText(settings, settings.MainOutputDeviceId);
        KaraokeAidOutputDelayBox.Text = DeviceDelayText(settings, settings.KaraokeAidOutputDeviceId);
        RecordingOutputDelayBox.Text = DeviceDelayText(settings, settings.RecordingOutputDeviceId);
        SoundboardOutputDelayBox.Text = DeviceDelayText(settings, settings.SoundboardOutputDeviceId);
        MonitorOutputDelayBox.Text = DeviceDelayText(settings, settings.MonitorOutputDeviceId);
        TempoRangeBox.Text = settings.ManualTempoRangePercent.ToString("0.#", CultureInfo.CurrentCulture);
        TransitionFadeBox.Text = settings.TransitionFadeSeconds.ToString("0.#", CultureInfo.CurrentCulture);

        foreach (var mode in Enum.GetValues<TransitionMode>())
            TransitionModeBox.Items.Add(new ComboBoxItem
            {
                Content = DescribeTransitionMode(mode),
                Tag = mode.ToString(),
            });
        SelectByTag(TransitionModeBox, settings.TransitionMode.ToString(), "AutoCrossfade");
    }

    /// <summary>Active render endpoints, default first; empty when there is no audio device.</summary>
    private static List<AudioDeviceInfo> ListOutputDevices()
    {
        try
        {
            return new DeviceService().GetOutputDevices();
        }
        catch (Exception)
        {
            // No audio service (headless session, driver trouble): the combos
            // still open with None so the rest of the window stays usable.
            return new List<AudioDeviceInfo>();
        }
    }

    /// <summary>Active capture (microphone) endpoints, default communications device first.</summary>
    private static List<AudioDeviceInfo> ListInputDevices()
    {
        try
        {
            return new DeviceService().GetInputDevices();
        }
        catch (Exception)
        {
            return new List<AudioDeviceInfo>();
        }
    }

    /// <summary>
    /// Fill one routing combo: "None" (empty tag, saved back as null), then every
    /// output endpoint. A saved id whose device is unplugged stays in the list so
    /// saving the window does not quietly change the routing.
    /// </summary>
    private static void FillDeviceBox(ComboBox box, IReadOnlyList<AudioDeviceInfo> devices, string? selectedId)
    {
        box.Items.Clear();

        // The same rows the deck combos offer: None, the live endpoints, then the
        // saved one when it is not connected.
        var rows = OutputDeviceChoices.Build(devices, selectedId);
        foreach (var choice in rows)
            box.Items.Add(new ComboBoxItem
            {
                Content = choice.FriendlyName,
                Tag = choice.Id,
                ToolTip = choice.FriendlyName,
            });

        var wanted = OutputDeviceChoices.Select(rows, selectedId);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(
            i => string.Equals((string?)i.Tag, wanted.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Selected endpoint id, or null for the None entry.</summary>
    private static string? SelectedDeviceId(ComboBox box)
    {
        var id = (box.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>This endpoint's stored delay, or 0 when it has none.</summary>
    private static string DeviceDelayText(AppSettings settings, string? deviceId)

        => deviceId != null && settings.DeviceOutputDelays.TryGetValue(deviceId, out var ms)
            ? Math.Round(ms).ToString("F0", CultureInfo.CurrentCulture)
            : "0";

    /// <summary>
    /// Store the typed value for the row's endpoint. Unparseable text leaves the
    /// stored value alone rather than resetting a working delay to zero.
    /// </summary>
    private static void RememberDeviceDelay(AppSettings settings, string? deviceId, TextBox box)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var ms)) return;
        settings.DeviceOutputDelays[deviceId] = Math.Clamp(ms, 0, DelayBuffer.MaxDelayMs);
    }

    /// <summary>Human label for a transition style.</summary>
    private static string DescribeTransitionMode(TransitionMode mode) => mode.ToString() switch
    {
        // The end-of-track style was called Off before the 2.0 rewrite and is
        // being renamed to CrossfadeAtEnd; label whichever name this build has.
        "Off" => "Off (hard cut at end of track)",
        "CrossfadeAtEnd" => "Crossfade at end of track",
        "AutoCrossfade" => "Automatic crossfade",
        "AutoMix" => "Auto-mix (beat & tempo match)",
        _ => mode.ToString(),
    };

    // ================================================== recording devices (mics)

    /// <summary>
    /// One capture endpoint as the list row sees it. The row controls are rebuilt
    /// whenever the grouping changes, so the state lives here and not in the
    /// controls: ticking, sliding and muting all edit this object, and Save writes
    /// exactly the enabled rows out.
    /// </summary>
    private sealed class MicRow
    {
        public string DeviceId { get; init; } = "";
        public string DeviceName { get; init; } = "";
        public bool Enabled { get; set; }
        public float Volume { get; set; } = 1f;
        public bool Muted { get; set; }
        public bool Monitor { get; set; }
        /// <summary>First source channel this strip reads, or -1 for a whole capture endpoint.</summary>
        public int Channel { get; init; } = -1;
        /// <summary>Mono tap or stereo pair; Mono for every non-ASIO source.</summary>
        public AsioChannelMode Mode { get; init; } = AsioChannelMode.Mono;

        public bool IsAsio => Channel >= 0;
    }

    /// <summary>The picker's selection for one driver: mode and offset, before "Add".</summary>
    private sealed class AsioDraft
    {
        public AsioChannelMode Mode { get; set; } = AsioChannelMode.Mono;
        public int Offset { get; set; }
    }

    private readonly List<MicRow> _micRows = new();

    /// <summary>
    /// Every capture endpoint becomes a row: enabled ones first (the devices the
    /// operator actually records with), then the rest. Devices stored in settings
    /// but not plugged in stay listed as "Saved device (not connected)" so they can
    /// be unticked instead of silently vanishing on the next save.
    /// </summary>
    private void LoadMicDevices(AppSettings settings)
    {
        _micRows.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in ListInputDevices())
        {
            var saved = settings.MicDevices.FirstOrDefault(
                m => string.Equals(m.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase));
            _micRows.Add(new MicRow
            {
                DeviceId = device.Id,
                DeviceName = device.FriendlyName,
                Enabled = saved != null,
                Volume = Math.Clamp(saved?.Volume ?? 1f, 0f, 2f),
                Muted = saved?.Muted ?? false,
                Monitor = saved?.Monitor ?? false,
            });
            seen.Add(device.Id);
        }

        foreach (var saved in settings.MicDevices)
        {
            if (string.IsNullOrWhiteSpace(saved.DeviceId) || seen.Contains(saved.DeviceId))
                continue;
            _micRows.Add(new MicRow
            {
                DeviceId = saved.DeviceId,
                DeviceName = SavedDeviceName(saved),
                Enabled = true,
                Volume = Math.Clamp(saved.Volume, 0f, 2f),
                Muted = saved.Muted,
                Monitor = saved.Monitor,
                Channel = SourceChannel(saved),
                Mode = SourceMode(saved),
            });
        }

        RenderMicRows();
    }

    /// <summary>
    /// Repopulate the routing boxes and the mic rows after a device change.
    /// The operator's unticked rows and sliders are carried over by id; a route
    /// that is no longer connected keeps its "not connected" row, so an open
    /// dialog never quietly moves a route.
    /// </summary>
    private void OnDeviceChange(DeviceChange change)
    {
        var devices = _listOutputs();
        foreach (var box in new[]
                 {
                     MainOutputBox, KaraokeAidOutputBox, RecordingOutputBox,
                     SoundboardOutputBox, MonitorOutputBox,
                 })
            FillDeviceBox(box, devices, SelectedDeviceId(box));

        var liveMics = ListInputDevices();
        var previous = _micRows.ToDictionary(r => r.DeviceId, r => r, StringComparer.OrdinalIgnoreCase);
        _micRows.Clear();
        foreach (var device in liveMics)
        {
            var was = previous.GetValueOrDefault(device.Id);
            _micRows.Add(new MicRow
            {
                DeviceId = device.Id,
                DeviceName = device.FriendlyName,
                Enabled = was?.Enabled ?? false,
                Volume = was?.Volume ?? 1f,
                Muted = was?.Muted ?? false,
                Monitor = was?.Monitor ?? false,
                Channel = was?.Channel ?? -1,
                Mode = was?.Mode ?? AsioChannelMode.Mono,
            });
        }
        foreach (var was in previous.Values)
        {
            if (liveMics.Any(d => string.Equals(d.Id, was.DeviceId, StringComparison.OrdinalIgnoreCase)))
                continue;
            _micRows.Add(new MicRow
            {
                DeviceId = was.DeviceId,
                DeviceName = was.DeviceName,
                Enabled = was.Enabled,
                Volume = was.Volume,
                Muted = was.Muted,
                Monitor = was.Monitor,
                Channel = was.Channel,
                Mode = was.Mode,
            });
        }
        RenderMicRows();

        Log.Info($"Device change {change.Kind}: settings pickers rebuilt, " +
                 $"{devices.Count} output, {liveMics.Count} mic device(s).");
    }

    /// <summary>The list as it will be saved: one record per enabled row.</summary>
    private List<AppSettings.MicDeviceSettings> CollectMicDevices()
    {
        return _micRows
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.DeviceId))
            .Select(r => new AppSettings.MicDeviceSettings
            {
                DeviceId = r.DeviceId,
                Volume = Math.Clamp(r.Volume, 0f, 2f),
                Muted = r.Muted,
                Monitor = r.Monitor,
                // The mode and offset are the operator's choice, not the default: writing
                // them back from the row is what keeps a stereo pair a stereo pair across
                // a save. Leaving them off silently reset every ASIO source to Mono.
                Channel = r.Channel,
                Mode = r.Mode,
            })
            .ToList();
    }

    /// <summary>
    /// Rebuild the list: enabled rows under ENABLED, the rest under AVAILABLE.
    /// The panel re-renders on an enable tick so the grouping is always honest.
    /// </summary>
    private void RenderMicRows()
    {
        MicListPanel.Children.Clear();

        // ASIO first: it is where the operator composes a source that the WASAPI list
        // cannot show at all. An empty driver list is rendered in words below, never as
        // a blank space.
        AddAsioSection();

        var enabled = _micRows.Where(r => r.Enabled).ToList();
        var available = _micRows.Where(r => !r.Enabled).ToList();

        if (_micRows.Count == 0)
        {
            MicListPanel.Children.Add(new TextBlock
            {
                Text = "No recording devices found.",
                Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
                Margin = new Thickness(0, 2, 0, 6),
            });
            Dispatcher.BeginInvoke(() => MicListRendered = true, DispatcherPriority.Loaded);
            return;
        }

        MicListPanel.Children.Add(GroupCaption($"ENABLED ({enabled.Count})"));
        if (enabled.Count == 0)
            MicListPanel.Children.Add(new TextBlock
            {
                Text = "None. Tick a device below to record from it.",
                Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
            });
        foreach (var row in enabled)
            MicListPanel.Children.Add(BuildMicRow(row));

        if (available.Count > 0)
        {
            MicListPanel.Children.Add(GroupCaption("AVAILABLE"));
            foreach (var row in available)
                MicListPanel.Children.Add(BuildMicRow(row));
        }

        // The rows are in the tree but unmeasured. Queue the flag the capture waits
        // on, so a screenshot of this dialog is taken after one layout pass.
        Dispatcher.BeginInvoke(() => MicListRendered = true, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// True once the post-layout pass has run at least once. That pass is queued at
    /// <see cref="DispatcherPriority.Loaded"/> by <c>RenderMicRows</c>, so it cannot run
    /// before the window is shown and the dispatcher pumps. A capture waits on this
    /// instead of sleeping for a fixed time and calling it settled.
    /// </summary>
    internal bool MicListRendered { get; private set; }

    /// <summary>
    /// Marks the Border that <see cref="BuildMicRow"/> makes for one device. The panel
    /// also holds captions and an ASIO section gap, and that gap is a Border too, so
    /// "every Border child" is not the same set as "every device row".
    /// </summary>
    internal const string MicRowTag = "MicRow";


    private TextBlock GroupCaption(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        // Section cyan, not deck-B pink: pink means "deck B" in the main window.
        Foreground = LookupBrush("AccentABrush", Brushes.Cyan),
        Margin = new Thickness(0, 2, 0, 0),
    };

    // ================================================== ASIO source picker

    /// <summary>
    /// The installed ASIO drivers, probed once per dialog. Reading a driver's channel
    /// count opens it, and ASIO is single-client, so the answer is kept rather than the
    /// interface re-opened on every render.
    /// </summary>
    private IReadOnlyList<AsioDriverInfo> Drivers()
    {
        if (_asioDriversLoaded != null) return _asioDriversLoaded;
        try
        {
            _asioDriversLoaded = _asioDrivers() ?? Array.Empty<AsioDriverInfo>();
        }
        catch
        {
            _asioDriversLoaded = Array.Empty<AsioDriverInfo>();
        }

        return _asioDriversLoaded;
    }

    private AsioDriverInfo? DriverByName(string name) =>
        Drivers().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The ASIO block at the top of the mic panel: a caption, then for each installed
    /// driver its live state and (when readable) the source composer. No drivers at all
    /// is a real state and says so in words; it is never an empty box.
    /// </summary>
    private void AddAsioSection()
    {
        var drivers = Drivers();
        MicListPanel.Children.Add(GroupCaption("ASIO INPUTS"));

        if (drivers.Count == 0)
        {
            MicListPanel.Children.Add(new TextBlock
            {
                Text = "No ASIO drivers installed. Install the interface's ASIO driver, then reopen Settings.",
                Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            return;
        }

        foreach (var driver in drivers)
        {
            MicListPanel.Children.Add(AsioDriverHeader(driver));
            if (driver.Ready) MicListPanel.Children.Add(BuildAsioComposer(driver));
        }

        MicListPanel.Children.Add(new Border { Height = 6 });
    }

    /// <summary>The driver line: its name and what it answered, in words.</summary>
    private TextBlock AsioDriverHeader(AsioDriverInfo driver)
    {
        string state = driver.State switch
        {
            AsioDriverState.Ready => driver.Detail is { Length: > 0 } detail
                ? $"{driver.Channels} inputs · {detail}"
                : $"{driver.Channels} input{(driver.Channels == 1 ? "" : "s")}",
            AsioDriverState.InUse =>
                "in use by another application — ASIO is single-client, so close it there to read its channels",
            _ => driver.Detail is { Length: > 0 } why ? why : "reports no usable inputs",
        };

        return new TextBlock
        {
            Text = $"{driver.Name} — {state}",
            FontSize = 11,
            Foreground = driver.Ready
                ? LookupBrush("TextBrush", Brushes.White)
                : LookupBrush("TextDimBrush", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        };
    }

    /// <summary>
    /// Mode + offset + Add for one readable driver. Both modes are reachable without
    /// listing overlapping rows: the mode switch redraws the offset list, so a 4-channel
    /// 4i4 offers "Input 1"…"Input 4" as mono taps and "Input 1 + Input 2"… as pairs,
    /// and the operator adds as many as they want.
    /// </summary>
    private FrameworkElement BuildAsioComposer(AsioDriverInfo driver)
    {
        if (!_asioDrafts.TryGetValue(driver.Name, out var draft))
        {
            draft = new AsioDraft();
            _asioDrafts[driver.Name] = draft;
        }

        var mono = new RadioButton
        {
            Content = "Mono tap",
            GroupName = "asio-mode:" + driver.Name,
            Style = (Style)FindResource("AsioPill"),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Read one channel and send it to both sides.",
        };
        var pair = new RadioButton
        {
            Content = "Stereo pair",
            GroupName = "asio-mode:" + driver.Name,
            Style = (Style)FindResource("AsioPill"),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Read two adjacent channels as left and right.",
        };
        var offsets = new ComboBox
        {
            Width = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "The channel, or the first channel of the pair, this source reads.",
        };
        var add = new Button
        {
            Content = "Add source",
            MinWidth = 104,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };

        bool building = true;

        void FillOffsets()
        {
            offsets.Items.Clear();
            if (draft.Mode == AsioChannelMode.Mono)
            {
                for (int i = 0; i < driver.Channels; i++)
                    offsets.Items.Add(new ComboBoxItem { Content = driver.ChannelLabel(i), Tag = i });
            }
            else
            {
                for (int i = 0; i + 1 < driver.Channels; i += 2)
                    offsets.Items.Add(new ComboBoxItem
                    {
                        Content = $"{driver.ChannelLabel(i)} + {driver.ChannelLabel(i + 1)}",
                        Tag = i,
                    });
            }

            int index = 0;
            for (int i = 0; i < offsets.Items.Count; i++)
                if (offsets.Items[i] is ComboBoxItem item && item.Tag is int tag && tag == draft.Offset) index = i;
            offsets.SelectedIndex = offsets.Items.Count > 0 ? index : -1;
            if (offsets.SelectedItem is ComboBoxItem chosen && chosen.Tag is int value) draft.Offset = value;
        }

        void RefreshAdd()
        {
            string key = DeviceSourceKey.Compose(AsioDriverRegistry.Kind, driver.Name, draft.Offset, draft.Mode);
            bool exists = draft.Offset >= 0
                && _micRows.Any(r => string.Equals(r.DeviceId, key, StringComparison.OrdinalIgnoreCase));
            add.IsEnabled = !exists && offsets.SelectedIndex >= 0;
            add.Content = exists ? "Added" : "Add source";
        }

        FillOffsets();
        mono.IsChecked = draft.Mode == AsioChannelMode.Mono;
        pair.IsChecked = draft.Mode == AsioChannelMode.StereoPair;
        RefreshAdd();
        building = false;

        mono.Checked += (_, _) =>
        {
            if (building) return;
            draft.Mode = AsioChannelMode.Mono;
            RenderMicRows();
        };
        pair.Checked += (_, _) =>
        {
            if (building) return;
            draft.Mode = AsioChannelMode.StereoPair;
            draft.Offset -= draft.Offset % 2; // pairs start on an even channel
            RenderMicRows();
        };
        offsets.SelectionChanged += (_, _) =>
        {
            if (building) return;
            if (offsets.SelectedItem is ComboBoxItem item && item.Tag is int value) draft.Offset = value;
            RefreshAdd();
        };
        add.Click += (_, _) => AddAsioSource(driver, draft.Offset, draft.Mode);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10),
        };
        row.Children.Add(mono);
        row.Children.Add(pair);
        row.Children.Add(offsets);
        row.Children.Add(add);
        return row;
    }

    /// <summary>Adds one ASIO source as an enabled row, the same shape a WASAPI row takes.</summary>
    private void AddAsioSource(AsioDriverInfo driver, int offset, AsioChannelMode mode)
    {
        string key = DeviceSourceKey.Compose(AsioDriverRegistry.Kind, driver.Name, offset, mode);
        if (_micRows.Any(r => string.Equals(r.DeviceId, key, StringComparison.OrdinalIgnoreCase))) return;

        _micRows.Add(new MicRow
        {
            DeviceId = key,
            DeviceName = SourceLabel(driver.Name, offset, mode, notConnected: false),
            Enabled = true,
            Volume = 1f,
            Channel = offset,
            Mode = mode,
        });
        RenderMicRows();
    }

    /// <summary>
    /// "Focusrite USB ASIO · Input 3 (mono)" — the driver's own channel name when it gave
    /// one, index-only when it did not, never a bare number.
    /// </summary>
    private string SourceLabel(string driverName, int offset, AsioChannelMode mode, bool notConnected)
    {
        var driver = DriverByName(driverName);
        string channels = mode == AsioChannelMode.StereoPair
            ? $"{driver?.ChannelLabel(offset) ?? $"Input {offset + 1}"} + " +
              $"{driver?.ChannelLabel(offset + 1) ?? $"Input {offset + 2}"}"
            : driver?.ChannelLabel(offset) ?? $"Input {offset + 1}";
        string kind = mode == AsioChannelMode.StereoPair ? "stereo pair" : "mono";
        return $"{driverName} · {channels} ({kind}){(notConnected ? " — not connected" : "")}";
    }

    /// <summary>
    /// The channel and mode a saved device carries: the strip key is the authority (it
    /// is what the mixer routes by), and the settings fields are the fallback for a key
    /// that does not parse.
    /// </summary>
    private static bool TryReadAsioSource(string deviceId, out string driver, out int channel, out AsioChannelMode mode)
    {
        driver = deviceId;
        channel = -1;
        mode = AsioChannelMode.Mono;
        if (!DeviceSourceKey.TryParse(deviceId, out string kind, out string endpoint, out int ch, out AsioChannelMode md))
            return false;
        if (!string.Equals(kind, AsioDriverRegistry.Kind, StringComparison.Ordinal) || ch < 0) return false;
        driver = endpoint;
        channel = ch;
        mode = md;
        return true;
    }

    private static int SourceChannel(AppSettings.MicDeviceSettings saved) =>
        TryReadAsioSource(saved.DeviceId, out _, out int channel, out _) ? channel : saved.Channel;

    private static AsioChannelMode SourceMode(AppSettings.MicDeviceSettings saved) =>
        TryReadAsioSource(saved.DeviceId, out _, out _, out AsioChannelMode mode) ? mode : saved.Mode;

    private string SavedDeviceName(AppSettings.MicDeviceSettings saved)
    {
        if (TryReadAsioSource(saved.DeviceId, out string driver, out int channel, out AsioChannelMode mode))
        {
            var info = DriverByName(driver);
            bool live = info is { Ready: true };
            return SourceLabel(driver, channel, mode, notConnected: !live);
        }

        return "Saved device (not connected)";
    }

    /// <summary>Test seam: the list Save writes, without writing the file.</summary>
    internal List<AppSettings.MicDeviceSettings> CollectMicDevicesForTest() => CollectMicDevices();

    /// <summary>Test seam: add a source the way the picker's Add button does.</summary>
    internal void AddAsioSourceForTest(string driverName, int offset, AsioChannelMode mode)
    {
        var driver = DriverByName(driverName);
        if (driver != null) AddAsioSource(driver, offset, mode);
    }

    /// <summary>One device row: enable box, name, 0-200 % volume, Mute, Monitor.</summary>
    private Border BuildMicRow(MicRow row)
    {
        var border = new Border
        {
            Tag = MicRowTag,
            Background = row.Enabled ? LookupBrush("PanelBrush", Brushes.Black) : Brushes.Transparent,
            BorderBrush = row.Enabled ? LookupBrush("AccentABrush", Brushes.Cyan) : LookupBrush("BorderBrush", Brushes.DimGray),
            BorderThickness = new Thickness(row.Enabled ? 1 : 0, 0, 0, 0),
            Padding = new Thickness(6, 3, 4, 3),
            Margin = new Thickness(0, 0, 0, 3),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enable = new CheckBox
        {
            IsChecked = row.Enabled,
            Style = (Style)FindResource("MicCheck"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Record from this device.",
        };
        System.Windows.Automation.AutomationProperties.SetName(enable, $"Enable {row.DeviceName}");

        var name = new TextBlock
        {
            Text = row.DeviceName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = row.Enabled ? LookupBrush("TextBrush", Brushes.White) : LookupBrush("TextDimBrush", Brushes.Gray),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = row.DeviceId,
        };
        Grid.SetColumn(name, 1);

        var percent = new TextBlock
        {
            Text = $"{row.Volume * 100:0}%",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(percent, 3);

        // 0-200 % is an app-domain gain: the WASAPI endpoint level is never touched.
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 200,
            SmallChange = 1,
            LargeChange = 10,
            IsEnabled = row.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Volume inside JockeyBox (0-200 %). The Windows recording level is never changed.",
        };
        System.Windows.Automation.AutomationProperties.SetName(volume, $"{row.DeviceName} volume");
        Grid.SetColumn(volume, 2);

        // MicPill is defined in SettingsWindow.xaml. IsChecked is both the state and
        // the only visual cue (accent fill), so it must stay bound to the row.
        var mute = new ToggleButton
        {
            Content = "Mute",
            Style = (Style)FindResource("MicPill"),
            Width = 44,
            IsChecked = row.Muted,
            IsEnabled = row.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Silence this device inside JockeyBox.",
        };
        System.Windows.Automation.AutomationProperties.SetName(mute, $"{row.DeviceName} mute");
        Grid.SetColumn(mute, 4);

        var monitor = new ToggleButton
        {
            Content = "Monitor",
            Style = (Style)FindResource("MicPill"),
            Width = 62,
            IsChecked = row.Monitor,
            IsEnabled = row.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Send this device to the Monitoring device output (operator only).",
        };
        System.Windows.Automation.AutomationProperties.SetName(monitor, $"{row.DeviceName} monitor");
        Grid.SetColumn(monitor, 5);

        volume.Value = row.Volume * 100;
        percent.Text = $"{volume.Value:0}%";

        volume.ValueChanged += (_, e) =>
        {
            row.Volume = (float)(e.NewValue / 100.0);
            percent.Text = $"{e.NewValue:0}%";
        };

        mute.Checked += (_, _) => row.Muted = true;
        mute.Unchecked += (_, _) => row.Muted = false;
        monitor.Checked += (_, _) => row.Monitor = true;
        monitor.Unchecked += (_, _) => row.Monitor = false;

        enable.Checked += (_, _) =>
        {
            // A newly enabled device always starts at unity, unmuted, monitor off.
            row.Enabled = true;
            row.Volume = 1f;
            row.Muted = false;
            row.Monitor = false;
            // The row moves to the ENABLED group; re-render out of band so the
            // checkbox that raised this is not torn down inside its own event.
            Dispatcher.BeginInvoke(new Action(RenderMicRows));
        };
        enable.Unchecked += (_, _) =>
        {
            row.Enabled = false;
            Dispatcher.BeginInvoke(new Action(RenderMicRows));
        };

        grid.Children.Add(enable);
        grid.Children.Add(name);
        grid.Children.Add(volume);
        grid.Children.Add(percent);
        grid.Children.Add(mute);
        grid.Children.Add(monitor);
        border.Child = grid;
        return border;
    }

    /// <summary>Parse a decimal in the current or the invariant culture ("2.5" and "2,5").</summary>
    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Fill the download and tool boxes from settings (values live in item tags).</summary>
    private void LoadDownloadSettings(AppSettings settings)
    {
        SelectByTag(LayoutPresetBox, settings.DownloadLayoutPreset, "artist-album");
        LayoutTemplateBox.Text = settings.DownloadLayoutTemplate;
        SelectByTag(BitrateBox, settings.DownloadBitrateKbps.ToString(), "320");

        for (var i = 1; i <= 4; i++)
            ConcurrencyBox.Items.Add(new ComboBoxItem { Content = i.ToString(), Tag = i.ToString() });
        SelectByTag(ConcurrencyBox, settings.DownloadConcurrency.ToString(), "2");

        SelectByTag(CookiesBox, settings.DownloadCookiesBrowser, "");
        SkipExistingCheck.IsChecked = settings.DownloadSkipExisting;
        AutoKaraokeCheck.IsChecked = settings.DownloadAutoKaraoke;

        foreach (var mb in new[] { 512, 1024, 2048, 4096 })
            CacheBox.Items.Add(new ComboBoxItem { Content = mb.ToString(), Tag = mb.ToString() });
        SelectByTag(CacheBox, settings.StreamCacheLimitMb.ToString(), "2048");

        foreach (var (label, minutes) in new[] { ("Off", 0), ("Every 15 min", 15), ("Every 60 min", 60), ("Every 6 hours", 360) })
            AutoSyncBox.Items.Add(new ComboBoxItem { Content = label, Tag = minutes.ToString() });
        SelectByTag(AutoSyncBox, settings.YouTubeAutoSyncMinutes.ToString(), "0");

        YtDlpPathBox.Text = settings.YtDlpPath;
        FfmpegPathBox.Text = settings.FfmpegPath;
    }

    private static void SelectByTag(ComboBox box, string? tag, string fallback)
    {
        var wanted = tag ?? "";
        var match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, wanted, StringComparison.OrdinalIgnoreCase))
            ?? box.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string?)i.Tag, fallback, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void UpdateEngineState() =>
        EngineStateText.Text = SeparationEngine.IsInstalled
            ? "\u2713 engine installed" : "engine not installed yet";

    private void UpdateSpotifyStatus()
    {
        if (_oauth.IsLoggedIn)
        {
            if (_oauth.HasPlaylistScopes)
                SpotifyStatus.Text = "Connected - playlist access granted";
            else if (string.IsNullOrWhiteSpace(_settings.SpotifyClientIdOverride))
                SpotifyStatus.Text = "Connected - needs your own Spotify app";
            else
                SpotifyStatus.Text = "Connected - playlist access not granted";
        }
        else if (!string.IsNullOrWhiteSpace(_settings.SpotifyClientId))
            SpotifyStatus.Text = "\u2713 Using client credentials (legacy)";
        else
            SpotifyStatus.Text = "Not connected";
    }

    /// <summary>
    /// Open the shared Spotify setup dialog. The dialog writes the client id
    /// straight to settings, so mirror it back into the text box afterwards -
    /// otherwise this window's Save would restore the old value.
    /// </summary>
    private void OnSpotifySetupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SpotifySetupDialog(_settings, _oauth) { Owner = this };
        dialog.ShowDialog();
        SpotifyClientIdBox.Text = _settings.SpotifyClientIdOverride;
        UpdateSpotifyStatus();
    }

    private async void OnSpotifyLoginClick(object sender, RoutedEventArgs e)
    {
        SpotifyLoginButton.IsEnabled = false;
        SpotifyStatus.Text = "Opening browser for login\u2026";

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await _oauth.LoginAsync(cts.Token);
            UpdateSpotifyStatus();
        }
        catch (OperationCanceledException)
        {
            SpotifyStatus.Text = "Login timed out or was cancelled";
        }
        catch (Exception ex)
        {
            SpotifyStatus.Text = $"Login failed: {ex.Message}";
        }
        finally
        {
            SpotifyLoginButton.IsEnabled = true;
        }
    }

    private void OnBrowseMusic(object sender, RoutedEventArgs e) => BrowseInto(MusicDirBox, "Select your Music folder (originals with vocals)");



    private void BrowseInto(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void OnInstallEngine(object sender, RoutedEventArgs e)
    {
        _settings.PythonPath = PythonBox.Text.Trim();
        _settings.SeparationUseGpu = GpuCheck.IsChecked == true;
        SaveMerged();

        var setup = new SetupWindow(_settings) { Owner = this };
        setup.ShowDialog();
        UpdateEngineState();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var apiUrl = ApiUrlBox.Text.Trim();
        if (apiUrl.Length > 0
            && (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this,
                "Lyrics API base URL must be a full http:// or https:// address (e.g. http://10.0.0.5:8091).",
                "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var music = MusicDirBox.Text.Trim();
        if (music.Length > 0 && !System.IO.Directory.Exists(music))
        {
            MessageBox.Show(this, "The library folder does not exist.",
                "Invalid folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MusicDirectory = music;
        // One root: karaoke stems live beside their originals (Library/KaraokeNaming).
        _settings.KaraokeDirectory = music;
        _settings.PythonPath = PythonBox.Text.Trim();
        _settings.SeparationUseGpu = GpuCheck.IsChecked == true;
        _settings.LyricsApiBaseUrl = apiUrl;
        if (double.TryParse(OffsetBox.Text, out var offset))
            _settings.LyricsOffsetMs = Math.Clamp(offset, -10000, 10000);
        _settings.SpotifyClientIdOverride = SpotifyClientIdBox.Text.Trim();

        // Breadcrumb: the save merges against disk state and writes the file on the
        // UI thread. If that write wedges (a locked or slow profile dir), the
        // watchdog names this operation instead of a generic stall.
        using var crumb = UiBreadcrumb.Enter("settings save: merge and write settings");

        // 2.0 audio routing: a None entry writes null, never "" - the settings
        // doc comments and the engine both read null as "this output is off".
        // Storing a route - including "None" - is what makes it the user's
        // decision; until then the app falls back to the system default output.
        _settings.AudioRoutingConfigured = true;
        _settings.MainOutputDeviceId = SelectedDeviceId(MainOutputBox);
        _settings.KaraokeAidOutputDeviceId = SelectedDeviceId(KaraokeAidOutputBox);
        _settings.RecordingOutputDeviceId = SelectedDeviceId(RecordingOutputBox);
        _settings.SoundboardOutputDeviceId = SelectedDeviceId(SoundboardOutputBox);
        _settings.MonitorOutputDeviceId = SelectedDeviceId(MonitorOutputBox);
        RememberDeviceDelay(_settings, _settings.MainOutputDeviceId, MainOutputDelayBox);
        RememberDeviceDelay(_settings, _settings.KaraokeAidOutputDeviceId, KaraokeAidOutputDelayBox);
        RememberDeviceDelay(_settings, _settings.RecordingOutputDeviceId, RecordingOutputDelayBox);
        RememberDeviceDelay(_settings, _settings.SoundboardOutputDeviceId, SoundboardOutputDelayBox);
        RememberDeviceDelay(_settings, _settings.MonitorOutputDeviceId, MonitorOutputDelayBox);
        _settings.SoundboardFollowsMain = SoundboardFollowsMainCheck.IsChecked == true;

        // Virtual ASIO ring: additive keys, default off. The name is validated
        // against the bus-name rules here so a typo cannot make the writer fail at
        // route time (where the operator would only see silence).
        int ringBuses = (VirtualAsioDeckACheck.IsChecked == true ? 1 : 0) +
                        (VirtualAsioDeckBCheck.IsChecked == true ? 1 : 0) +
                        (VirtualAsioRecordingCheck.IsChecked == true ? 1 : 0) +
                        (VirtualAsioSoundboardCheck.IsChecked == true ? 1 : 0) +
                        (VirtualAsioMonitorCheck.IsChecked == true ? 1 : 0);
        if (ringBuses > 1)
        {
            MessageBox.Show(this,
                "Only one bus can publish to the virtual ASIO ring. Untick all but one " +
                "(the ring has a single section, so a second bus could only be dropped).",
                "One ring bus", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ringName = VirtualAsioBusNameBox.Text.Trim();
        if (ringName.Length == 0) ringName = "main";
        var ringNameError = AppSettings.ValidateVirtualAsioBusName(ringName);
        if (ringNameError != null)
        {
            MessageBox.Show(this,
                $"The virtual ASIO ring name is not valid: {ringNameError}.",
                "Invalid ring name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.VirtualAsioDeckA = VirtualAsioDeckACheck.IsChecked == true;
        _settings.VirtualAsioDeckB = VirtualAsioDeckBCheck.IsChecked == true;
        _settings.VirtualAsioRecording = VirtualAsioRecordingCheck.IsChecked == true;
        _settings.VirtualAsioSoundboard = VirtualAsioSoundboardCheck.IsChecked == true;
        _settings.VirtualAsioMonitor = VirtualAsioMonitorCheck.IsChecked == true;
        _settings.VirtualAsioBusName = ringName;

        // Recording devices: the rows ARE the list. An enabled row is the whole
        // MicDeviceSettings record, so a save can never resurrect a device the
        // operator unticked. Devices that are not plugged in keep their saved row
        // (rendered as "Saved device (not connected)").
        _settings.MicDevices = CollectMicDevices();
        // An explicit choice (including "None") is final: the one-time default
        // adoption in MainViewModel.InitializeMic must never second-guess it.
        _settings.MicAutoSelected = true;

        if (int.TryParse(OutputDelayBox.Text, out var outputDelay))
            _settings.OutputDelayMs = Math.Clamp(outputDelay, 0, 2000);
        if (TryParseNumber(TempoRangeBox.Text, out var tempoRange))
            _settings.ManualTempoRangePercent = Math.Clamp(tempoRange, 1, 50);
        if (TryParseNumber(TransitionFadeBox.Text, out var fadeSeconds))
            _settings.TransitionFadeSeconds = Math.Clamp(fadeSeconds, 0.2, 30);
        if ((TransitionModeBox.SelectedItem as ComboBoxItem)?.Tag is string modeName
            && Enum.TryParse<TransitionMode>(modeName, out var transitionMode))
            _settings.TransitionMode = transitionMode;

        _settings.DownloadLayoutPreset = TagOf(LayoutPresetBox);
        _settings.DownloadLayoutTemplate = LayoutTemplateBox.Text.Trim();
        if (int.TryParse(TagOf(BitrateBox), out var bitrate)) _settings.DownloadBitrateKbps = bitrate;
        if (int.TryParse(TagOf(ConcurrencyBox), out var concurrency))
            _settings.DownloadConcurrency = Math.Clamp(concurrency, 1, 4);
        _settings.DownloadCookiesBrowser = TagOf(CookiesBox);
        _settings.DownloadSkipExisting = SkipExistingCheck.IsChecked == true;
        _settings.DownloadAutoKaraoke = AutoKaraokeCheck.IsChecked == true;
        if (int.TryParse(TagOf(CacheBox), out var cacheMb)) _settings.StreamCacheLimitMb = cacheMb;
        if (int.TryParse(TagOf(AutoSyncBox), out var autoSync)) _settings.YouTubeAutoSyncMinutes = autoSync;
        _settings.YtDlpPath = YtDlpPathBox.Text.Trim();
        _settings.FfmpegPath = FfmpegPathBox.Text.Trim();

        // Controls above now hold the operator's intent; the file may also have
        // changed while this dialog was open. Every write this window makes goes
        // through the same merge, so none of them can carry stale control state.
        SaveMerged();
        DialogResult = true;
    }

    /// <summary>
    /// Persist this window's state through <see cref="SettingsMerge"/>.
    ///
    /// <para>Called by all three write paths in this window: the Save button, the
    /// pre-save before the engine installer opens (so the typed Python path
    /// survives the installer writing its own), and the pre-save before a tool
    /// task. A field whose control differs from the constructor snapshot is an
    /// operator edit and wins; every other field keeps the value on disk, so a
    /// change made elsewhere while the dialog was open survives.</para>
    /// </summary>
    private void SaveMerged()
    {
        // The baseline stays the CONSTRUCTOR snapshot and is deliberately never
        // refreshed. `_settings` is re-stamped from the controls before every save,
        // and the controls' own baseline is that same stamp: refreshing it after a
        // save would make a control the operator never touched differ from the
        // baseline, and the next save would mistake the stale control value for an
        // edit and write it over a newer value from disk.
        var merged = SettingsMerge.Merge(_snapshot, AppSettings.TryLoad(), _settings);
        _settings.CopyFrom(merged);
        _settings.Save();
    }

    // ---- tools ------------------------------------------------------------

    private async void OnInstallTools(object sender, RoutedEventArgs e) =>
        await RunToolTaskAsync(ToolBootstrap.EnsureAllAsync, "Checking yt-dlp and ffmpeg\u2026",
            "Tools are ready.", "yt-dlp is not available \u2014 see the log.");

    private async void OnUpdateYtDlp(object sender, RoutedEventArgs e) =>
        await RunToolTaskAsync(ToolBootstrap.UpdateYtDlpAsync, "Updating yt-dlp\u2026",
            "yt-dlp updated.", "yt-dlp update failed \u2014 see the log.");

    /// <summary>
    /// The queue window owns the live tool state; this is a convenience copy for
    /// someone already in Settings. Path overrides typed here are used as-is.
    /// </summary>
    private async Task RunToolTaskAsync(
        Func<AppSettings, IProgress<string>, CancellationToken, Task<bool>> work,
        string starting, string success, string failure)
    {
        _settings.YtDlpPath = YtDlpPathBox.Text.Trim();
        _settings.FfmpegPath = FfmpegPathBox.Text.Trim();
        SaveMerged();

        InstallToolsButton.IsEnabled = false;
        UpdateYtDlpButton.IsEnabled = false;
        ToolsStatusText.Text = starting;

        try
        {
            var progress = new Progress<string>(message => ToolsStatusText.Text = message);
            var ok = await work(_settings, progress, CancellationToken.None);
            ToolsStatusText.Text = ok ? success : failure;
        }
        catch (Exception ex)
        {
            ToolsStatusText.Text = $"Tool setup failed: {ex.Message}";
        }
        finally
        {
            InstallToolsButton.IsEnabled = true;
            UpdateYtDlpButton.IsEnabled = true;
        }
    }

    // ---- library tools ----------------------------------------------------

    private async void OnFindDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (_libraryToolsBusy) return;
        var tracks = SnapshotLibrary();
        if (tracks == null) return;

        DuplicateGroups? result = null;
        await RunLibraryToolAsync($"Scanning {tracks.Count} track(s) for duplicates\u2026",
            async ct =>
            {
                result = await Task.Run(() => LibraryTools.FindDuplicates(tracks, ct), ct);
            });

        if (result == null) return;

        if (result.IsEmpty)
        {
            LibraryToolsStatusText.Text = $"No duplicates across {result.MusicFiles} music file(s).";
            return;
        }

        LibraryToolsStatusText.Text =
            $"{result.Groups.Count} group(s) \u00b7 {result.DuplicateFileCount} of {result.MusicFiles} " +
            $"file(s) \u00b7 {FormatSize(result.ReclaimableBytes)} reclaimable.";
        ShowDuplicateGroups(result);
    }

    private async void OnImportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_libraryToolsBusy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import an Exportify playlist (CSV)",
            Filter = "Exportify CSV (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) name = "Imported playlist";

        List<SourceTrack>? imported = null;
        var skipped = 0;
        await RunLibraryToolAsync($"Reading {Path.GetFileName(path)}\u2026", async ct =>
        {
            var parsed = await Task.Run(() =>
            {
                var rows = LibraryTools.ImportExportifyCsv(path, out var skip);
                return (Rows: rows, Skipped: skip);
            }, ct);

            imported = parsed.Rows;
            skipped = parsed.Skipped;
        });

        if (imported == null) return;

        if (imported.Count == 0)
        {
            LibraryToolsStatusText.Text =
                $"No usable rows in {Path.GetFileName(path)} ({skipped} skipped).";
            return;
        }

        // Share the app's live store when it is available: a second instance
        // would write the same file and be clobbered by the next tab save.
        var store = _playlists ?? new PlaylistStore();
        var playlist = store.FindByName(name) ?? store.Create(name, MediaSource.Spotify);
        var added = store.AddTracks(playlist, imported);
        store.Save();

        Log.Info($"Library tools: imported {imported.Count} track(s) from {path} " +
                 $"into \"{playlist.Name}\" ({added} new, {skipped} skipped).");

        LibraryToolsStatusText.Text =
            $"Imported {imported.Count} track(s) into \"{playlist.Name}\" " +
            $"({added} new, {skipped} skipped). Restart to see it in the source tabs.";
    }

    private async void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        if (_libraryToolsBusy) return;
        var tracks = SnapshotLibrary();
        if (tracks == null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the library as JSON",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            FileName = $"jockeybox-library-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var written = -1;
        await RunLibraryToolAsync("Writing library export\u2026", async ct =>
        {
            // The store reads the playlists file synchronously; keep that on the
            // UI thread and only the document build/write goes async.
            var store = new PlaylistStore();
            written = await LibraryTools.ExportLibraryJsonAsync(path, tracks, store, ct);
        });

        if (written < 0) return;
        LibraryToolsStatusText.Text = $"Exported {written} track(s) to {path}";
        Log.Info($"Library tools: exported {written} track(s) to {path}.");
    }

    /// <summary>Rows for the tools, or null after reporting that there is nothing to work on.</summary>
    private List<TrackEntry>? SnapshotLibrary()
    {
        var tracks = _tracks?.Invoke();
        if (tracks is { Count: > 0 }) return tracks.ToList();

        LibraryToolsStatusText.Text =
            "No library loaded yet \u2014 set the folders above and let the scan finish.";
        return null;
    }

    private async Task RunLibraryToolAsync(string starting, Func<CancellationToken, Task> work)
    {
        if (_libraryToolsBusy) return;

        _libraryToolsBusy = true;
        SetLibraryToolsBusy(true);
        LibraryToolsStatusText.Text = starting;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            LibraryToolsStatusText.Text = "Library tool cancelled or timed out.";
        }
        catch (Exception ex)
        {
            Log.Error("Library tool failed.", ex);
            LibraryToolsStatusText.Text = $"Library tool failed: {ex.Message}";
        }
        finally
        {
            _libraryToolsBusy = false;
            SetLibraryToolsBusy(false);
        }
    }

    private void SetLibraryToolsBusy(bool busy)
    {
        FindDuplicatesButton.IsEnabled = !busy;
        ImportCsvButton.IsEnabled = !busy;
        ExportJsonButton.IsEnabled = !busy;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        var mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0.0} MB";
    }

    private Brush LookupBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? fallback;

    /// <summary>
    /// Read-only duplicate report in the app's dark card style. It lists the
    /// sets and their files; it offers no action, because this tool never
    /// touches the audio files.
    /// </summary>
    private void ShowDuplicateGroups(DuplicateGroups result)
    {
        var window = new Window
        {
            Title = "Duplicate tracks",
            Owner = this,
            Width = 640,
            Height = 560,
            MinWidth = 460,
            MinHeight = 320,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = LookupBrush("WindowBrush", Brushes.Black),
        };

        var root = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(2, 0, 2, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "DUPLICATES",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = LookupBrush("AccentABrush", Brushes.Cyan),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{result.Groups.Count} group(s) \u00b7 {result.DuplicateFileCount} of " +
                   $"{result.MusicFiles} file(s) \u00b7 {FormatSize(result.ReclaimableBytes)} reclaimable",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildDuplicateList(result),
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(2, 10, 2, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "Reporting only \u2014 nothing was moved, renamed or deleted.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
        };
        Grid.SetColumn(hint, 0);
        footer.Children.Add(hint);

        var close = new Button { Content = "Close", Width = 90, IsCancel = true };
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        window.Content = root;
        window.ShowDialog();
    }

    private StackPanel BuildDuplicateList(DuplicateGroups result)
    {
        var panel = new StackPanel();
        var panelBrush = LookupBrush("PanelBrush", Brushes.Black);
        var borderBrush = LookupBrush("BorderBrush", Brushes.DimGray);
        var textBrush = LookupBrush("TextBrush", Brushes.White);
        var dimBrush = LookupBrush("TextDimBrush", Brushes.Gray);
        var warnBrush = LookupBrush("AccentBBrush", Brushes.IndianRed);

        foreach (var group in result.Groups)
        {
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock
            {
                Text = group.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = group.Key,
            });
            inner.Children.Add(new TextBlock
            {
                Text = $"{group.Files.Count} files \u00b7 matched on {group.MatchedBy} \u00b7 " +
                       $"{FormatSize(group.TotalBytes)}",
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 4),
                Foreground = dimBrush,
            });

            foreach (var file in group.Files)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
                var size = new TextBlock
                {
                    Text = file.Exists ? FormatSize(file.SizeBytes) : "missing",
                    FontSize = 10,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = file.Exists ? dimBrush : warnBrush,
                };
                DockPanel.SetDock(size, Dock.Right);
                row.Children.Add(size);

                row.Children.Add(new TextBlock
                {
                    Text = file.Path,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = file.Path,
                    Foreground = file.Exists ? textBrush : warnBrush,
                });
                inner.Children.Add(row);
            }

            panel.Children.Add(new Border
            {
                Background = panelBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = inner,
            });
        }

        return panel;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
