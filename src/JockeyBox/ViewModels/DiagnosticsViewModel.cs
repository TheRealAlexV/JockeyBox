using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JockeyBox.Audio.Graph;
using JockeyBox.Dsp;
using JockeyBox.Selftest;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// Read-only facts about the running audio engine, its buses and the microphone mixer,
/// for the diagnostics window.
///
/// <para><b>What it may do.</b> Only read: it never starts or stops the engine, never
/// opens a device and never runs a selftest. <see cref="Refresh"/> re-reads values that
/// already exist and is safe to call while audio is rendering.</para>
///
/// <para><b>Where the mixer numbers come from.</b> The strips the mixer already holds, not
/// a fresh enumeration of capture endpoints: a route change replaces that collection and
/// the rows follow it. Per-device parameters are read once from the same
/// <c>mixer-settings.json</c> the mixer loads, and only re-read when the file's timestamp
/// moves.</para>
///
/// <para><b>What it cannot say.</b> No <see cref="ControlRegistry"/> instance is constructed
/// in this process, so the window reports that as current state rather than as zeros.</para>
/// </summary>
public sealed class DiagnosticsViewModel : NotifyBase
{
    private readonly AudioEngine _engine;
    private readonly AppSettings _settings;

    /// <summary>
    /// Channel strips are expensive to build per tick, so one is kept to answer "how many
    /// processors does this device's strip have?" from its stored parameters. It never
    /// touches an audio device.
    /// </summary>
    private readonly InterleavedMixerStrip _probe =
        new(AudioEngine.Rate, AudioEngine.Channels, AudioEngine.BlockFrames);

    private readonly Dictionary<string, StripParameters> _stored = new(StringComparer.Ordinal);
    private DateTime _storedStamp = DateTime.MinValue;
    private string _micSignature = "uninitialised";

    public DiagnosticsViewModel(AudioEngine engine, AppSettings settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        Assembly asm = Assembly.GetEntryAssembly() ?? typeof(DiagnosticsViewModel).Assembly;
        string informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                               ?? "unknown";

        // Windows' own "Product version" property carries the source revision
        // ("2.2.0-m0+<sha>"). Showing the milestone part on its own, and the file version
        // beside it, keeps "which build is deployed" answerable without the +sha tail.
        int plus = informational.IndexOf('+');
        ProductVersion = plus > 0 ? informational[..plus] : informational;
        InformationalVersion = informational;
        FileVersion = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                      ?? asm.GetName().Version?.ToString()
                      ?? "unknown";

        foreach (BusKind kind in Enum.GetValues<BusKind>())
        {
            Buses.Add(new BusRow(kind, _engine.Bus(kind)));
        }

        foreach (string name in SelftestRunner.CaseNames)
        {
            Cases.Add(new CaseRow(name));
        }

        Refresh();
    }

    // ---- build identity (fixed for the life of the process) ------------------

    public string ProductVersion { get; }
    public string InformationalVersion { get; }
    public string FileVersion { get; }

    // ---- engine constants (fixed for the life of the process) ---------------

    public int SampleRate => AudioEngine.Rate;
    public int Channels => AudioEngine.Channels;
    public int BlockFrames => AudioEngine.BlockFrames;
    public string BlockDurationMs =>
        (AudioEngine.BlockFrames * 1000.0 / AudioEngine.Rate).ToString("0.00", CultureInfo.InvariantCulture) + " ms";

    // ---- live engine state ---------------------------------------------------

    private string _engineState = "";
    public string EngineState { get => _engineState; private set => Set(ref _engineState, value); }

    private string _frameIndex = "";
    public string FrameIndex { get => _frameIndex; private set => Set(ref _frameIndex, value); }

    public ObservableCollection<BusRow> Buses { get; } = new();
    public ObservableCollection<CaseRow> Cases { get; } = new();

    /// <summary>
    /// One row per attached capture device, in mixer order. Rebuilt only when the mixer's
    /// strip collection changes, so the ticker never enumerates devices.
    /// </summary>
    public ObservableCollection<MicRow> Mics { get; } = new();

    private string _latencyEntries = "";
    public string LatencyEntries { get => _latencyEntries; private set => Set(ref _latencyEntries, value); }

    private string _mixLatency = "";
    public string MixLatency { get => _mixLatency; private set => Set(ref _mixLatency, value); }

    /// <summary>Current state, not a promise about the future: nothing builds one yet.</summary>
    public string ControlRegistry => "no instance";

    private string _mixerEnabled = "";
    public string MixerEnabled { get => _mixerEnabled; private set => Set(ref _mixerEnabled, value); }

    /// <summary>Re-read every live value. Nothing here changes engine state.</summary>
    public void Refresh()
    {
        EngineState = _engine.IsRunning ? "running" : "stopped";
        FrameIndex = _engine.FrameIndex.ToString("N0", CultureInfo.InvariantCulture);
        MixerEnabled = _settings.MixerEnabled ? "on" : "off";

        foreach (BusRow bus in Buses) bus.Update();
        RefreshMics();

        RefreshCount++;
    }

    /// <summary>
    /// Re-reads the mixer's strips. The row list is rebuilt only when the device set changes;
    /// each row is then refreshed in place, so a renamed device or a moved pad shows up
    /// without allocating on every tick.
    /// </summary>
    private void RefreshMics()
    {
        var strips = _engine.Mics.Strips;
        ReloadStoredParameters();

        string signature = string.Join("|", strips.Select(strip => strip.DeviceId));
        if (signature != _micSignature)
        {
            _micSignature = signature;
            Mics.Clear();
            foreach (var strip in strips) Mics.Add(new MicRow(strip.DeviceId));
        }

        int maxChain = 0;
        foreach (var strip in strips) maxChain = Math.Max(maxChain, strip.ChainLatencyFrames);

        // The mixer's ledger holds one entry per strip and pads each to the slowest chain;
        // with device and user offsets at zero, those are exactly these numbers.
        LatencyEntries = strips.Count.ToString(CultureInfo.InvariantCulture)
                         + (strips.Count == 1 ? " device" : " devices");
        MixLatency = maxChain.ToString(CultureInfo.InvariantCulture) + " fr / "
                     + (maxChain * 1000.0 / AudioEngine.Rate).ToString("0.00", CultureInfo.InvariantCulture) + " ms";

        for (int i = 0; i < Mics.Count && i < strips.Count; i++)
        {
            var strip = strips[i];
            string id = strip.DeviceId ?? "";
            bool stored = _stored.TryGetValue(id, out var parameters);
            Mics[i].Update(strip, stored ? parameters : StripParameters.Neutral, stored,
                maxChain - strip.ChainLatencyFrames, _probe);
        }
    }

    /// <summary>
    /// The same file the mixer loads, re-read only when its timestamp moves. One stat per
    /// tick, no device enumeration. An unreadable file means every device is neutral, which
    /// is what the mixer itself does with it.
    /// </summary>
    private void ReloadStoredParameters()
    {
        string path = Path.Combine(AppPaths.Home, MixerSettingsStore.FileName);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _storedStamp) return;

        _storedStamp = stamp;
        _stored.Clear();
        try
        {
            var loaded = new MixerSettingsStore(AppPaths.Home).Load();
            foreach (var pair in loaded.Devices) _stored[pair.Key] = pair.Value;
        }
        catch (Exception)
        {
            _stored.Clear();
        }
    }

    /// <summary>
    /// How many times <see cref="Refresh"/> has run. One after construction; the
    /// diagnostics window's ticker makes it two on its first tick, so a capture can
    /// wait for the window's own live read rather than for construction.
    /// </summary>
    public int RefreshCount { get; private set; }

    // ---- rows ---------------------------------------------------------------

    /// <summary>
    /// One capture device as the mixer holds it: the friendly name, the control slug, the
    /// bypass state, how many processors its chain carries, its chain latency and the pad the
    /// mix-wide pass gave it. A device with no stored settings reads "neutral", never blank.
    /// </summary>
    public sealed class MicRow : NotifyBase
    {
        public MicRow(string deviceId) => Slug = deviceId;

        /// <summary>The control slug: the capture endpoint id this strip is bound to.</summary>
        public string Slug { get; }

        private string _name = "";
        public string Name { get => _name; private set => Set(ref _name, value); }

        private string _bypass = "";
        public string Bypass { get => _bypass; private set => Set(ref _bypass, value); }

        private string _processors = "";
        public string Processors { get => _processors; private set => Set(ref _processors, value); }

        private string _chain = "";
        public string Chain { get => _chain; private set => Set(ref _chain, value); }

        private string _pad = "";
        public string Pad { get => _pad; private set => Set(ref _pad, value); }

        public void Update(MicStrip strip, StripParameters parameters, bool stored, int padFrames,
            InterleavedMixerStrip probe)
        {
            Name = string.IsNullOrWhiteSpace(strip.DeviceName) ? "(unnamed)" : strip.DeviceName;
            Bypass = !stored ? "neutral" : parameters.MasterBypass ? "yes" : "no";

            probe.Apply(parameters);
            Processors = probe.ProcessorCount.ToString(CultureInfo.InvariantCulture);
            Chain = strip.ChainLatencyFrames.ToString(CultureInfo.InvariantCulture);
            Pad = padFrames.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One render bus: where it goes and how hot it is this block.</summary>
    public sealed class BusRow : NotifyBase
    {
        private const int BarTrack = 150;

        private readonly AudioBus _bus;

        public BusRow(BusKind kind, AudioBus bus)
        {
            Kind = kind;
            Name = kind switch
            {
                BusKind.DeckA => "DECK A",
                BusKind.DeckB => "DECK B",
                BusKind.Recording => "RECORDING",
                BusKind.Soundboard => "SOUNDBOARD",
                BusKind.Monitor => "MONITOR",
                _ => kind.ToString().ToUpperInvariant(),
            };
            _bus = bus;
            Update();
        }

        public BusKind Kind { get; }
        public string Name { get; }

        private string _device = "";
        public string Device { get => _device; private set => Set(ref _device, value); }

        private string _delay = "";
        public string Delay { get => _delay; private set => Set(ref _delay, value); }

        private string _peakDb = "";
        public string PeakDb { get => _peakDb; private set => Set(ref _peakDb, value); }

        private double _barWidth;
        public double BarWidth { get => _barWidth; private set => Set(ref _barWidth, value); }

        private bool _hasSignal;
        /// <summary>True when the bus is above silence, so the bar can be shown at all.</summary>
        public bool HasSignal { get => _hasSignal; private set => Set(ref _hasSignal, value); }

        public void Update()
        {
            var sink = _bus.Sink;
            Device = sink == null ? "not routed" : sink.DeviceName;
            Delay = _bus.DelayMs.ToString(CultureInfo.InvariantCulture) + " ms";

            float peak = Samples.Peak(_bus.Input, 0, AudioEngine.BlockFrames * AudioEngine.Channels);
            HasSignal = peak > 0f;
            PeakDb = HasSignal
                ? Samples.PeakDb(_bus.Input, 0, AudioEngine.BlockFrames * AudioEngine.Channels)
                    .ToString("0.0", CultureInfo.InvariantCulture) + " dB"
                : "-inf dB";
            BarWidth = Math.Clamp(peak, 0f, 1f) * BarWidth;
        }
    }

    /// <summary>A selftest case this build knows about. The runner keeps no results, so
    /// the only honest state after start-up is "not run this session".</summary>
    public sealed class CaseRow
    {
        public CaseRow(string name) => Name = name;

        public string Name { get; }
        public string State => "not run this session";
    }
}

/// <summary>Minimal change notification; no toolkit dependency for six rows.</summary>
public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
