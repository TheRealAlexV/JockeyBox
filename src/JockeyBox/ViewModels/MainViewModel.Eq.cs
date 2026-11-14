using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Audio.Graph;

namespace JockeyBox.ViewModels;

/// <summary>
/// LOW / MID / HIGH: the mix EQ's three bands, on the main window between the two
/// deck cards.
///
/// <para><b>This is a control surface, not the feature.</b> <c>MixEq</c> owns the
/// filters (RBJ low shelf 70 Hz, bell 1 kHz Q 0.6, high shelf 13 kHz), the per-block
/// smoothing and the state; the engine runs it on the deck fan-out. Nothing here
/// changes a coefficient — a band is one number, and the engine reads the target the
/// setter writes.</para>
///
/// <para><b>Where the state lives.</b> The three gains are settings, so a persisted
/// mix is in circuit on the first block after a restart. <see cref="InitializeEq"/>
/// adopts them and pushes them without writing the file back (a launch is not a user
/// change); every later change writes through.</para>
///
/// <para><b>Zero is a bypass.</b> Below <see cref="MixEq.BypassDb"/> the engine takes
/// the band out of circuit entirely, so the readout says BYP next to 0.0 dB rather
/// than leaving the operator to guess whether a flat-looking band is still filtering.
/// </para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>True once the saved bands have been adopted, so start-up does not save.</summary>
    private bool _eqAdopted;

    /// <summary>Low shelf gain in dB, -26…+6 (0 = out of circuit).</summary>
    [ObservableProperty] private double _eqLowDb;

    /// <summary>Mid bell gain in dB, -26…+6 (0 = out of circuit).</summary>
    [ObservableProperty] private double _eqMidDb;

    /// <summary>High shelf gain in dB, -26…+6 (0 = out of circuit).</summary>
    [ObservableProperty] private double _eqHighDb;

    /// <summary>Low band's readout: "+6.0 dB", "0.0 dB", "−26.0 dB".</summary>
    public string EqLowText => DbText(EqLowDb);

    /// <summary>Mid band's readout.</summary>
    public string EqMidText => DbText(EqMidDb);

    /// <summary>High band's readout.</summary>
    public string EqHighText => DbText(EqHighDb);

    /// <summary>Low band is below the engine's bypass threshold, so it filters nothing.</summary>
    public bool IsEqLowBypassed => Math.Abs(EqLowDb) < MixEq.BypassDb;

    /// <summary>Mid band is below the engine's bypass threshold.</summary>
    public bool IsEqMidBypassed => Math.Abs(EqMidDb) < MixEq.BypassDb;

    /// <summary>High band is below the engine's bypass threshold.</summary>
    public bool IsEqHighBypassed => Math.Abs(EqHighDb) < MixEq.BypassDb;

    /// <summary>
    /// Adopt the saved bands. Called from the window next to
    /// <see cref="InitializeMonitor"/>, before the first block is rendered, so a
    /// persisted mix is already in circuit rather than arriving on the first drag.
    /// </summary>
    public void InitializeEq()
    {
        EqLowDb = _settings.EqLowDb;
        EqMidDb = _settings.EqMidDb;
        EqHighDb = _settings.EqHighDb;
        _eqAdopted = true;
        ApplyEq();
    }

    partial void OnEqLowDbChanged(double value)
    {
        Changed(EqBand.Low, value, nameof(EqLowText), nameof(IsEqLowBypassed));
    }

    partial void OnEqMidDbChanged(double value)
    {
        Changed(EqBand.Mid, value, nameof(EqMidText), nameof(IsEqMidBypassed));
    }

    partial void OnEqHighDbChanged(double value)
    {
        Changed(EqBand.High, value, nameof(EqHighText), nameof(IsEqHighBypassed));
    }

    /// <summary>
    /// One band moved: engine first (a drag must be audible on the next block), then
    /// the settings object, then the file. The file write is gated on
    /// <see cref="_eqAdopted"/> so adopting the saved values at start-up is not itself
    /// a save.
    /// </summary>
    private void Changed(EqBand band, double db, string textProperty, string bypassProperty)
    {
        if (_transport?.Engine is { } engine) engine.Eq.SetTargetDb(band, (float)db);

        switch (band)
        {
            case EqBand.Low:
                _settings.EqLowDb = db;
                break;
            case EqBand.Mid:
                _settings.EqMidDb = db;
                break;
            default:
                _settings.EqHighDb = db;
                break;
        }

        OnPropertyChanged(textProperty);
        OnPropertyChanged(bypassProperty);
        if (_eqAdopted) _settings.Save();
    }

    /// <summary>Push all three bands. Also used after a preset or a device change.</summary>
    private void ApplyEq()
    {
        if (_transport?.Engine is not { } engine) return;
        engine.Eq.SetAll(EqLowDb, EqMidDb, EqHighDb);
    }

    /// <summary>
    /// "+6.0 dB" / "0.0 dB" / "−26.0 dB". Rounded before the sign is chosen so a drag
    /// that lands on -0.04 reads "0.0 dB" and not "−0.0 dB"; the minus is U+2212 to
    /// match the rest of the window's readouts.
    /// </summary>
    private static string DbText(double db)
    {
        double rounded = Math.Round(db, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0) return "0.0 dB";

        string magnitude = Math.Abs(rounded).ToString("0.0", CultureInfo.InvariantCulture);
        return rounded > 0 ? $"+{magnitude} dB" : $"−{magnitude} dB";
    }
}
