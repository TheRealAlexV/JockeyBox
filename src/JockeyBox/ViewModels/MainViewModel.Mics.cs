using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.ViewModels;

/// <summary>
/// The recording-device side of the MIC strip: how many microphones the operator
/// enabled in Settings, and the meter they share.
///
/// <para>1.5 had one microphone, so the strip could hard-code "MIC" and read a
/// single peak. 2.0 has a list of capture devices (<c>AppSettings.MicDevices</c>),
/// so the label reports how many are open and the meter has to follow the loudest
/// of them. <see cref="Audio.AudioEngine.MicPeaks"/> is that per-device array;
/// <see cref="Audio.AudioEngine.MicPeak"/> — which <see cref="MicPeak"/> already
/// tracks in <c>MainViewModel.Mic.cs</c> — is its maximum, so the existing meter
/// binding keeps working untouched while this file supplies the count.</para>
///
/// <para>Kept additive and in its own file, like <c>MainViewModel.Mic.cs</c> and
/// <c>MainViewModel.Audio2.cs</c>: the transport bar is the only consumer.</para>
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- state

    /// <summary>Recording devices the operator enabled in Settings (0..N).</summary>
    [ObservableProperty] private int _micDeviceCount;

    /// <summary>Strip label: "MIC" with none enabled, "MIC 2" with two.</summary>
    [ObservableProperty] private string _micStripLabel = "MIC";

    // ---------------------------------------------------------------- wiring

    /// <summary>
    /// Keep the count/label in step with the meter instead of adding a second timer:
    /// the strip's peak is polled at 20 Hz already, and the count only changes when
    /// Settings saves, so both paths call <see cref="RefreshMicDeviceCount"/>.
    /// </summary>
    partial void OnMicPeakChanged(double value) => RefreshMicDeviceCount();

    /// <summary>
    /// Recount the enabled recording devices and repaint the label. Cheap (a list
    /// walk over a handful of rows) but it only writes when the number changed, so
    /// the 20 Hz meter tick does not re-notify the strip forever.
    /// </summary>
    public void RefreshMicDeviceCount()
    {
        var devices = _settings?.MicDevices;
        int count = devices == null
            ? 0
            : devices.Count(m => !string.IsNullOrWhiteSpace(m.DeviceId));

        if (MicDeviceCount != count) MicDeviceCount = count;

        string label = count switch
        {
            0 => "MIC",
            1 => "MIC 1",
            _ => $"MIC {count}",
        };
        if (MicStripLabel != label) MicStripLabel = label;
    }
}
