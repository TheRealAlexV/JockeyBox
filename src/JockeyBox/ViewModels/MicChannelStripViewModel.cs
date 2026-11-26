using System;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Dsp;
using JockeyBox.Dsp.Reverb;

namespace JockeyBox.ViewModels;

/// <summary>
/// One capture device's channel strip, as the rack in the main window sees it: ten EQ bands,
/// the two band limits, gate, compressor, limiter, the reverb send and the per-strip bypass.
///
/// <para><b>Values.</b> Every property reads the last <see cref="StripParameters"/> the engine
/// reported through <see cref="MainViewModel.RefreshChannelStripsFromTick"/>. Nothing here is
/// invented: a strip that has never been touched holds <see cref="StripParameters.Neutral"/>,
/// which is what the engine applies for a device with no stored entry.</para>
///
/// <para><b>Writes.</b> A setter updates the local copy and hands the whole value to
/// <see cref="MainViewModel.WriteStrip"/>, which posts one action on the engine's render queue -
/// the same queue the HTTP API uses. This class never touches a filter.</para>
/// </summary>
public sealed partial class MicChannelStripViewModel : ObservableObject
{
    private readonly Action<string, StripParameters> _write;
    private StripParameters _p = StripParameters.Neutral;

    public MicChannelStripViewModel(string deviceId, string name, Action<string, StripParameters> write)
    {
        DeviceId = deviceId;
        _write = write;
        _name = name;
    }

    public string DeviceId { get; }

    // ---------------------------------------------------------------- strip row

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isSelected;

    /// <summary>True while the capture is running; false reads as "not connected" in the row.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Peak, 0..1, decayed by the 20 Hz tick.</summary>
    [ObservableProperty] private double _peak;

    /// <summary>Frames this strip's own chain costs: 0 with every stage off, 240 with a limiter.</summary>
    [ObservableProperty] private int _chainFrames;

    /// <summary>Frames the mixer adds so this strip lines up with the slowest one.</summary>
    [ObservableProperty] private int _padFrames;

    /// <summary>Peak bar width in pixels; the 56 px track never moves, so the row never reflows.</summary>
    public double MeterWidth => Math.Clamp(Peak, 0, 1) * 56.0;

    /// <summary>"240 f · pad 0 f" is drawn as two reserved columns in XAML, not as this string.</summary>
    public bool HasLatency => ChainFrames > 0 || PadFrames > 0;

    partial void OnPeakChanged(double value) => OnPropertyChanged(nameof(MeterWidth));

    /// <summary>Take the engine's applied value. No-op when it is what we already show, so a
    /// slider drag does not get its own value written back over it mid-gesture.</summary>
    public void Apply(StripParameters parameters)
    {
        if (parameters == _p) return;
        _p = parameters;
        OnPropertyChanged(string.Empty);
    }

    // ---------------------------------------------------------------- writes

    private void Write(Func<StripParameters, StripParameters> edit, [CallerMemberName] string? name = null)
    {
        _p = edit(_p);
        _write(DeviceId, _p);
        OnPropertyChanged(name);
    }

    // ---------------------------------------------------------------- bypass

    public bool Bypass
    {
        get => _p.MasterBypass;
        set => Write(p => p with { MasterBypass = value });
    }

    // ---------------------------------------------------------------- EQ (10 ISO octave bands)

    public double Eq31Db { get => _p.Eq.Gain31Db; set => Write(p => p with { Eq = p.Eq with { Gain31Db = value } }); }
    public double Eq63Db { get => _p.Eq.Gain63Db; set => Write(p => p with { Eq = p.Eq with { Gain63Db = value } }); }
    public double Eq125Db { get => _p.Eq.Gain125Db; set => Write(p => p with { Eq = p.Eq with { Gain125Db = value } }); }
    public double Eq250Db { get => _p.Eq.Gain250Db; set => Write(p => p with { Eq = p.Eq with { Gain250Db = value } }); }
    public double Eq500Db { get => _p.Eq.Gain500Db; set => Write(p => p with { Eq = p.Eq with { Gain500Db = value } }); }
    public double Eq1kDb { get => _p.Eq.Gain1kDb; set => Write(p => p with { Eq = p.Eq with { Gain1kDb = value } }); }
    public double Eq2kDb { get => _p.Eq.Gain2kDb; set => Write(p => p with { Eq = p.Eq with { Gain2kDb = value } }); }
    public double Eq4kDb { get => _p.Eq.Gain4kDb; set => Write(p => p with { Eq = p.Eq with { Gain4kDb = value } }); }
    public double Eq8kDb { get => _p.Eq.Gain8kDb; set => Write(p => p with { Eq = p.Eq with { Gain8kDb = value } }); }
    public double Eq16kDb { get => _p.Eq.Gain16kDb; set => Write(p => p with { Eq = p.Eq with { Gain16kDb = value } }); }

    // ---------------------------------------------------------------- band limits

    public bool HighPassOn { get => _p.HighPass.Enabled; set => Write(p => p with { HighPass = p.HighPass with { Enabled = value } }); }
    public double HighPassHz { get => _p.HighPass.FrequencyHz; set => Write(p => p with { HighPass = p.HighPass with { FrequencyHz = value } }); }
    public bool LowPassOn { get => _p.LowPass.Enabled; set => Write(p => p with { LowPass = p.LowPass with { Enabled = value } }); }
    public double LowPassHz { get => _p.LowPass.FrequencyHz; set => Write(p => p with { LowPass = p.LowPass with { FrequencyHz = value } }); }

    /// <summary>
    /// Fader positions for the corners and the millisecond times. These are the only controls
    /// whose useful range spans more than two decades (20..24000 Hz, 0.01..5000 ms); on a
    /// linear throw the whole useful band of a voice filter sits in the first two pixels. The
    /// fader is logarithmic, the readout still shows the value the engine holds.
    /// </summary>
    public double HighPassSlider
    {
        get => PositionOf(_p.HighPass.FrequencyHz, FilterParameters.MinFrequencyHz, FilterParameters.MaxFrequencyHz);
        set => HighPassHz = ValueOf(value, FilterParameters.MinFrequencyHz, FilterParameters.MaxFrequencyHz);
    }

    public double LowPassSlider
    {
        get => PositionOf(_p.LowPass.FrequencyHz, FilterParameters.MinFrequencyHz, FilterParameters.MaxFrequencyHz);
        set => LowPassHz = ValueOf(value, FilterParameters.MinFrequencyHz, FilterParameters.MaxFrequencyHz);
    }

    public double GateAttackSlider
    {
        get => PositionOf(_p.Gate.AttackMs, 0.01, 1000);
        set => GateAttackMs = ValueOf(value, 0.01, 1000);
    }

    public double GateReleaseSlider
    {
        get => PositionOf(_p.Gate.ReleaseMs, 0.01, 5000);
        set => GateReleaseMs = ValueOf(value, 0.01, 5000);
    }

    public double CompAttackSlider
    {
        get => PositionOf(_p.Compressor.AttackMs, 0.01, 1000);
        set => CompAttackMs = ValueOf(value, 0.01, 1000);
    }

    public double CompReleaseSlider
    {
        get => PositionOf(_p.Compressor.ReleaseMs, 0.01, 5000);
        set => CompReleaseMs = ValueOf(value, 0.01, 5000);
    }

    public double LimiterReleaseSlider
    {
        get => PositionOf(_p.Limiter.ReleaseMs, 1, 1000);
        set => LimiterReleaseMs = ValueOf(value, 1, 1000);
    }

    public double DeEsserSplitSlider
    {
        get => PositionOf(_p.DeEsser.FrequencyHz, 1000, 20000);
        set => DeEsserFrequencyHz = ValueOf(value, 1000, 20000);
    }

    public double DeEsserAttackSlider
    {
        get => PositionOf(_p.DeEsser.AttackMs, 0.01, 1000);
        set => DeEsserAttackMs = ValueOf(value, 0.01, 1000);
    }

    public double DeEsserReleaseSlider
    {
        get => PositionOf(_p.DeEsser.ReleaseMs, 0.01, 5000);
        set => DeEsserReleaseMs = ValueOf(value, 0.01, 5000);
    }

    /// <summary>
    /// The sidechain band, as two sliders that cannot cross. Each slider's bound is derived
    /// from the other's current value, so WPF's own coercion stops an overlap before it is
    /// written; the setter clamps once more for the drag that lands exactly on the bound.
    /// The panel has no dual-handle slider, and adding one would mean a new control template,
    /// so the pair is two ordinary RackRows with a moving bound.
    /// </summary>
    public double DeEsserSidechainLowSlider
    {
        get => PositionOf(_p.DeEsser.SidechainLowHz, 20, 20000);
        set
        {
            DeEsserSidechainLowHz = Math.Min(ValueOf(value, 20, 20000), _p.DeEsser.SidechainHighHz);
            OnPropertyChanged(nameof(DeEsserSidechainHighSliderMin));
        }
    }

    public double DeEsserSidechainLowSliderMax => PositionOf(_p.DeEsser.SidechainHighHz, 20, 20000);

    public double DeEsserSidechainHighSlider
    {
        get => PositionOf(_p.DeEsser.SidechainHighHz, 20, 24000);
        set
        {
            DeEsserSidechainHighHz = Math.Max(ValueOf(value, 20, 24000), _p.DeEsser.SidechainLowHz);
            OnPropertyChanged(nameof(DeEsserSidechainLowSliderMax));
        }
    }

    public double DeEsserSidechainHighSliderMin => PositionOf(_p.DeEsser.SidechainLowHz, 20, 24000);

    private static double PositionOf(double value, double min, double max) =>
        Math.Log(Math.Clamp(value, min, max) / min) / Math.Log(max / min);

    private static double ValueOf(double position, double min, double max) =>
        min * Math.Pow(max / min, Math.Clamp(position, 0, 1));

    // ---------------------------------------------------------------- gate

    public bool GateOn { get => _p.Gate.Enabled; set => Write(p => p with { Gate = p.Gate with { Enabled = value } }); }
    public double GateThresholdDb { get => _p.Gate.ThresholdDb; set => Write(p => p with { Gate = p.Gate with { ThresholdDb = value } }); }
    public double GateHysteresisDb { get => _p.Gate.HysteresisDb; set => Write(p => p with { Gate = p.Gate with { HysteresisDb = value } }); }
    public double GateAttackMs { get => _p.Gate.AttackMs; set => Write(p => p with { Gate = p.Gate with { AttackMs = value } }); }
    public double GateReleaseMs { get => _p.Gate.ReleaseMs; set => Write(p => p with { Gate = p.Gate with { ReleaseMs = value } }); }
    public double GateHoldMs { get => _p.Gate.HoldMs; set => Write(p => p with { Gate = p.Gate with { HoldMs = value } }); }
    public double GateFloorDb { get => _p.Gate.FloorDb; set => Write(p => p with { Gate = p.Gate with { FloorDb = value } }); }

    // ---------------------------------------------------------------- compressor

    public bool CompressorOn { get => _p.Compressor.Enabled; set => Write(p => p with { Compressor = p.Compressor with { Enabled = value } }); }
    public double CompThresholdDb { get => _p.Compressor.ThresholdDb; set => Write(p => p with { Compressor = p.Compressor with { ThresholdDb = value } }); }
    public double CompRatio { get => _p.Compressor.Ratio; set => Write(p => p with { Compressor = p.Compressor with { Ratio = value } }); }
    public double CompAttackMs { get => _p.Compressor.AttackMs; set => Write(p => p with { Compressor = p.Compressor with { AttackMs = value } }); }
    public double CompReleaseMs { get => _p.Compressor.ReleaseMs; set => Write(p => p with { Compressor = p.Compressor with { ReleaseMs = value } }); }
    public double CompKneeDb { get => _p.Compressor.KneeDb; set => Write(p => p with { Compressor = p.Compressor with { KneeDb = value } }); }
    public double CompMakeupDb { get => _p.Compressor.MakeupDb; set => Write(p => p with { Compressor = p.Compressor with { MakeupDb = value } }); }

    // ---------------------------------------------------------------- de-esser

    public bool DeEsserOn { get => _p.DeEsser.Enabled; set => Write(p => p with { DeEsser = p.DeEsser with { Enabled = value } }); }
    public double DeEsserFrequencyHz { get => _p.DeEsser.FrequencyHz; set => Write(p => p with { DeEsser = p.DeEsser with { FrequencyHz = value } }); }
    public double DeEsserThresholdDb { get => _p.DeEsser.ThresholdDb; set => Write(p => p with { DeEsser = p.DeEsser with { ThresholdDb = value } }); }
    public double DeEsserRatio { get => _p.DeEsser.Ratio; set => Write(p => p with { DeEsser = p.DeEsser with { Ratio = value } }); }
    public double DeEsserAttackMs { get => _p.DeEsser.AttackMs; set => Write(p => p with { DeEsser = p.DeEsser with { AttackMs = value } }); }
    public double DeEsserReleaseMs { get => _p.DeEsser.ReleaseMs; set => Write(p => p with { DeEsser = p.DeEsser with { ReleaseMs = value } }); }
    public double DeEsserMaxReductionDb { get => _p.DeEsser.MaxReductionDb; set => Write(p => p with { DeEsser = p.DeEsser with { MaxReductionDb = value } }); }
    public double DeEsserSidechainLowHz { get => _p.DeEsser.SidechainLowHz; set => Write(p => p with { DeEsser = p.DeEsser with { SidechainLowHz = value } }); }
    public double DeEsserSidechainHighHz { get => _p.DeEsser.SidechainHighHz; set => Write(p => p with { DeEsser = p.DeEsser with { SidechainHighHz = value } }); }

    // ---------------------------------------------------------------- limiter

    public bool LimiterOn { get => _p.Limiter.Enabled; set => Write(p => p with { Limiter = p.Limiter with { Enabled = value } }); }
    public double LimiterCeilingDb { get => _p.Limiter.CeilingDb; set => Write(p => p with { Limiter = p.Limiter with { CeilingDb = value } }); }
    public double LimiterReleaseMs { get => _p.Limiter.ReleaseMs; set => Write(p => p with { Limiter = p.Limiter with { ReleaseMs = value } }); }

    // ---------------------------------------------------------------- reverb send

    public bool ReverbOn { get => _p.Reverb.On; set => Write(p => p with { Reverb = p.Reverb with { On = value } }); }
    public double ReverbSize { get => _p.Reverb.Size; set => Write(p => p with { Reverb = p.Reverb with { Size = value } }); }
    public double ReverbWet { get => _p.Reverb.Wet; set => Write(p => p with { Reverb = p.Reverb with { Wet = value } }); }

    // ---------------------------------------------------------------- reset to neutral

    /// <summary>
    /// Double-click on a value: return that one parameter to the value it has in the
    /// transparent strip. <paramref name="key"/> is the control's Tag, so the XAML stays
    /// declarative and the mapping lives in one switch.
    /// </summary>
    public void ResetParameter(string? key)
    {
        switch (key)
        {
            case "eq.0": Eq31Db = 0; break;
            case "eq.1": Eq63Db = 0; break;
            case "eq.2": Eq125Db = 0; break;
            case "eq.3": Eq250Db = 0; break;
            case "eq.4": Eq500Db = 0; break;
            case "eq.5": Eq1kDb = 0; break;
            case "eq.6": Eq2kDb = 0; break;
            case "eq.7": Eq4kDb = 0; break;
            case "eq.8": Eq8kDb = 0; break;
            case "eq.9": Eq16kDb = 0; break;

            // The band limits are neutral wide open, and their neutral corners are the
            // frequencies StripParameters.Neutral parks them at.
            case "hp.freq": HighPassHz = HighPassFilter.TransparentFrequencyHz; break;
            case "lp.freq": LowPassHz = LowPassFilter.TransparentFrequencyHz; break;

            // Gate and compressor values reset to their shipped defaults; the on/off is a
            // switch, not a value, and double-clicking it does nothing.
            case "gate.threshold": GateThresholdDb = new GateParameters(Enabled: false).ThresholdDb; break;
            case "gate.hysteresis": GateHysteresisDb = new GateParameters(Enabled: false).HysteresisDb; break;
            case "gate.attack": GateAttackMs = new GateParameters(Enabled: false).AttackMs; break;
            case "gate.release": GateReleaseMs = new GateParameters(Enabled: false).ReleaseMs; break;
            case "gate.hold": GateHoldMs = new GateParameters(Enabled: false).HoldMs; break;
            case "gate.floor": GateFloorDb = new GateParameters(Enabled: false).FloorDb; break;

            case "comp.threshold": CompThresholdDb = new CompressorParameters(Enabled: false).ThresholdDb; break;
            case "comp.ratio": CompRatio = new CompressorParameters(Enabled: false).Ratio; break;
            case "comp.attack": CompAttackMs = new CompressorParameters(Enabled: false).AttackMs; break;
            case "comp.release": CompReleaseMs = new CompressorParameters(Enabled: false).ReleaseMs; break;
            case "comp.knee": CompKneeDb = new CompressorParameters(Enabled: false).KneeDb; break;
            case "comp.makeup": CompMakeupDb = new CompressorParameters(Enabled: false).MakeupDb; break;

            // De-essing ships off, splitting at 6 kHz and holding 4:1, so a double-click
            // returns a stage someone has pulled back to where the strip ships.
            case "deess.on": DeEsserOn = DeEsserParameters.Neutral.Enabled; break;
            case "deess.threshold": DeEsserThresholdDb = DeEsserParameters.Neutral.ThresholdDb; break;
            case "deess.amount": DeEsserRatio = DeEsserParameters.Neutral.Ratio; break;
            case "deess.split": DeEsserFrequencyHz = DeEsserParameters.Neutral.FrequencyHz; break;
            case "deess.attack": DeEsserAttackMs = DeEsserParameters.Neutral.AttackMs; break;
            case "deess.release": DeEsserReleaseMs = DeEsserParameters.Neutral.ReleaseMs; break;
            case "deess.maxred": DeEsserMaxReductionDb = DeEsserParameters.Neutral.MaxReductionDb; break;
            case "deess.sidechain.low": DeEsserSidechainLowHz = DeEsserParameters.Neutral.SidechainLowHz; break;
            case "deess.sidechain.high": DeEsserSidechainHighHz = DeEsserParameters.Neutral.SidechainHighHz; break;

            case "limiter.ceiling": LimiterCeilingDb = new LimiterParameters(Enabled: false).CeilingDb; break;
            case "limiter.release": LimiterReleaseMs = new LimiterParameters(Enabled: false).ReleaseMs; break;

            // The reverb send's neutral state is off at size 0.5 and wet 0, so a double-click
            // returns a stage someone has pulled back to where the strip ships.
            case "rev.on": ReverbOn = ReverbParameters.Neutral.On; break;
            case "rev.size": ReverbSize = ReverbParameters.Neutral.Size; break;
            case "rev.wet": ReverbWet = ReverbParameters.Neutral.Wet; break;
        }
    }
}
