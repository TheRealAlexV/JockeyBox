using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Dsp;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// The channel-strip rack: one strip per attached capture device, the strip selected for
/// editing, and the master gate.
///
/// <para><b>Where it lives.</b> The microphone row in the crossfader card already picks the
/// device and owns its gain, monitor send and mute. The rack grows that same card instead of
/// opening a second surface: the row selects, the rack edits.</para>
///
/// <para><b>Reads.</b> <see cref="RefreshChannelStripsFromTick"/> rides the existing 20 Hz mic
/// meter tick. Meters and latency refresh every tick; the parameter set is re-read from
/// <see cref="Audio.Graph.MicMixer.SnapshotStrips"/> at 2 Hz, which is fast enough to show a
/// change made by the HTTP API and slow enough not to allocate at meter rate.</para>
///
/// <para><b>Writes.</b> <see cref="WriteStrip"/> posts one <see cref="Audio.Graph.RenderCommands"/>
/// action, exactly like <c>MixerApiRouter</c>; persistence is debounced 500 ms and written off
/// the UI thread. The UI thread never touches a filter, envelope or gain stage.</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>20 Hz tick / 10 = 2 Hz parameter resync.</summary>
    private const int StripParamRefreshTicks = 10;

    private const int MixerPersistDebounceMs = 500;

    private int _stripParamTicks;
    private long _mixerPersistDueAt;
    private bool _adoptingMixerState;

    /// <summary>One per attached device, in engine (settings) order.</summary>
    public ObservableCollection<MicChannelStripViewModel> MicStrips { get; } = new();

    /// <summary>The strip whose controls are showing; always the device the mic row names.</summary>
    [ObservableProperty] private MicChannelStripViewModel? _selectedMicStrip;

    /// <summary>Master gate, mirrored from the engine so the HTTP API and the UI agree.</summary>
    [ObservableProperty] private bool _isMixerOn;

    /// <summary>
    /// Controls band open/closed. Closed by default and out of the layout flow: the rack in the
    /// mic card is the always-visible part, the band is the editor the operator asks for.
    /// </summary>
    [ObservableProperty] private bool _areStripControlsOpen;

    /// <summary>
    /// The band exists only when there is something to edit and the operator asked for it.
    /// <para>The control this drives is an <see cref="System.Windows.Controls.ItemsControl"/>
    /// fed by <see cref="BandStrip"/>, not a ContentControl: a ContentControl applies its
    /// ContentTemplate even with null content, which drew a band of empty value boxes. An
    /// ItemsControl with no items instantiates nothing, so with no strip there are no nodes at
    /// all - not invisible ones, none.</para>
    /// </summary>
    public bool ShowStripControls => AreStripControlsOpen && SelectedMicStrip != null;

    /// <summary>The band's only item: the selected strip, or none at all.</summary>
    public ObservableCollection<MicChannelStripViewModel> BandStrip { get; } = new();

    partial void OnAreStripControlsOpenChanged(bool value) => OnPropertyChanged(nameof(ShowStripControls));

    /// <summary>"2 devices" / "1 device" for the rack header.</summary>
    [ObservableProperty] private string _micStripCountText = "0 devices";

    /// <summary>Highest chain latency across strips, in frames: the limiter's 240 when one is on.</summary>
    [ObservableProperty] private int _mixerChainFrames;

    /// <summary>Highest pad across strips: the frames the mixer adds to keep devices aligned.</summary>
    [ObservableProperty] private int _mixerPadFrames;

    public bool HasMicStrips => MicStrips.Count > 0;

    public bool HasSelectedMicStrip => SelectedMicStrip != null;

    // ---------------------------------------------------------------- selection

    partial void OnSelectedMicStripChanged(MicChannelStripViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedMicStrip));
        OnPropertyChanged(nameof(ShowStripControls));
        if (value == null) return;

        // Selection is the mic row's selection: choosing a strip retargets the gain,
        // monitor and mute controls above it rather than opening a second concept.
        if (!SameId(value.DeviceId, SelectedMicDeviceId)) SelectedMicDeviceId = value.DeviceId;
    }

    // ---------------------------------------------------------------- master gate

    partial void OnIsMixerOnChanged(bool value)
    {
        if (_adoptingMixerState) return;

        var mics = _transport?.Engine?.Mics;
        if (mics == null) return;

        mics.Commands.Post(() => mics.MixerEnabled = value);
        _settings.MixerEnabled = value;
        _settings.Save();
    }

    // ---------------------------------------------------------------- writes

    /// <summary>
    /// One device, one render-thread action - the path <c>MixerApiRouter</c> uses. Returns at
    /// once; the change lands at the top of the next block.
    /// </summary>
    internal void WriteStrip(string deviceId, StripParameters parameters)
    {
        var mics = _transport?.Engine?.Mics;
        if (mics == null) return;

        var update = new KeyValuePair<string, StripParameters>(deviceId, parameters);
        mics.Commands.Post(() => mics.ApplyAll(new[] { update }));
        _mixerPersistDueAt = Environment.TickCount64 + MixerPersistDebounceMs;
    }

    /// <summary>
    /// A slider drag is many values, not many files: write at most twice a second, and off the
    /// UI thread. Same store and same directory the HTTP router persists to.
    /// </summary>
    private void PersistMixerIfDue()
    {
        if (_mixerPersistDueAt == 0 || Environment.TickCount64 < _mixerPersistDueAt) return;
        _mixerPersistDueAt = 0;

        var mics = _transport?.Engine?.Mics;
        if (mics == null) return;

        Dictionary<string, StripParameters> snapshot = mics.SnapshotStrips()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        string directory = AppPaths.Home;

        _ = Task.Run(() =>
        {
            try
            {
                new MixerSettingsStore(directory).Save(snapshot);
            }
            catch (Exception ex)
            {
                Log.Warn($"mixer settings not saved: {ex.Message}");
            }
        });
    }

    // ---------------------------------------------------------------- the tick

    /// <summary>
    /// Called from <see cref="RefreshMicDeviceCount"/> so the rack shares the mic meter's timer:
    /// no second timer, no second read of the strip list.
    /// </summary>
    internal void RefreshChannelStripsFromTick()
    {
        var mics = _transport?.Engine?.Mics;
        if (mics == null) return;

        var strips = mics.Strips;
        if (NeedsRebuild(strips))
        {
            MicStrips.Clear();
            for (int i = 0; i < strips.Count; i++)
                MicStrips.Add(new MicChannelStripViewModel(strips[i].DeviceId ?? "", strips[i].DeviceName, WriteStrip));

            HasMicStripsChanged();
        }

        int maxChain = 0;
        for (int i = 0; i < strips.Count; i++)
            maxChain = Math.Max(maxChain, strips[i].ChainLatencyFrames);

        // Re-padding is the engine's rule (every device up to the slowest chain), so the pad
        // column shows the same arithmetic rather than a stored second copy.
        var parameters = _stripParamTicks++ % StripParamRefreshTicks == 0
            ? mics.SnapshotStrips()
            : null;

        for (int i = 0; i < MicStrips.Count && i < strips.Count; i++)
        {
            MicChannelStripViewModel strip = MicStrips[i];

            double peak = Math.Clamp(strips[i].Peak, 0, 1);
            strip.Peak = Math.Max(peak, strip.Peak * 0.80);
            strip.IsOpen = strips[i].IsOpen;
            strip.ChainFrames = strips[i].ChainLatencyFrames;
            strip.PadFrames = maxChain - strips[i].ChainLatencyFrames;

            if (parameters != null && parameters.TryGetValue(strips[i].DeviceId ?? "", out StripParameters applied))
                strip.Apply(applied);
        }

        MixerChainFrames = maxChain;
        MixerPadFrames = MaxPad();

        _adoptingMixerState = true;
        try
        {
            if (IsMixerOn != mics.MixerEnabled) IsMixerOn = mics.MixerEnabled;
        }
        finally
        {
            _adoptingMixerState = false;
        }

        SyncStripSelection();
        PersistMixerIfDue();
    }

    private int MaxPad()
    {
        int pad = 0;
        foreach (MicChannelStripViewModel strip in MicStrips) pad = Math.Max(pad, strip.PadFrames);
        return pad;
    }

    private bool NeedsRebuild(IReadOnlyList<Audio.Graph.MicStrip> strips)
    {
        if (MicStrips.Count != strips.Count) return true;

        for (int i = 0; i < strips.Count; i++)
        {
            if (!SameId(MicStrips[i].DeviceId, strips[i].DeviceId)) return true;
            if (!string.Equals(MicStrips[i].Name, strips[i].DeviceName, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private void HasMicStripsChanged()
    {
        OnPropertyChanged(nameof(HasMicStrips));
        MicStripCountText = MicStrips.Count == 1
            ? "1 device"
            : MicStrips.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " devices";
    }

    private void SyncStripSelection()
    {
        MicChannelStripViewModel? match = MicStrips.FirstOrDefault(s => SameId(s.DeviceId, SelectedMicDeviceId));
        for (int i = 0; i < MicStrips.Count; i++)
        {
            bool selected = ReferenceEquals(MicStrips[i], match);
            if (MicStrips[i].IsSelected != selected) MicStrips[i].IsSelected = selected;
        }

        if (!ReferenceEquals(SelectedMicStrip, match)) SelectedMicStrip = match;

        // The band's items: exactly the selected strip, or none. Empty is the state that keeps
        // the whole controls tree out of the visual tree instead of collapsing it.
        if (BandStrip.Count == 1 && ReferenceEquals(BandStrip[0], match)) return;
        if (BandStrip.Count == 0 && match == null) return;

        BandStrip.Clear();
        if (match != null) BandStrip.Add(match);
    }
}
