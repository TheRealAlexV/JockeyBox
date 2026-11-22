using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JockeyBox.Audio.Graph;
using JockeyBox.Selftest;
using JockeyBox.Settings;

namespace JockeyBox.ViewModels;

/// <summary>
/// Read-only facts about the running audio engine and the M0 rails, for the
/// diagnostics window.
///
/// <para><b>What it may do.</b> Only read: it never starts or stops the engine, never
/// opens a device and never runs a selftest. <see cref="Refresh"/> re-reads values that
/// already exist and is safe to call while audio is rendering.</para>
///
/// <para><b>What it cannot say.</b> The latency ledger and the control registry have no
/// instance anywhere in this build, so they are reported as such rather than as zeros.</para>
/// </summary>
public sealed class DiagnosticsViewModel : NotifyBase
{
    private readonly AudioEngine _engine;
    private readonly AppSettings _settings;

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

    private string _mixerEnabled = "";
    public string MixerEnabled { get => _mixerEnabled; private set => Set(ref _mixerEnabled, value); }

    /// <summary>Re-read every live value. Nothing here changes engine state.</summary>
    public void Refresh()
    {
        EngineState = _engine.IsRunning ? "running" : "stopped";
        FrameIndex = _engine.FrameIndex.ToString("N0", CultureInfo.InvariantCulture);
        MixerEnabled = _settings.MixerEnabled ? "on" : "off";

        foreach (BusRow bus in Buses) bus.Update();

        RefreshCount++;
    }

    /// <summary>
    /// How many times <see cref="Refresh"/> has run. One after construction; the
    /// diagnostics window's ticker makes it two on its first tick, so a capture can
    /// wait for the window's own live read rather than for construction.
    /// </summary>
    public int RefreshCount { get; private set; }

    // ---- rows ---------------------------------------------------------------

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
