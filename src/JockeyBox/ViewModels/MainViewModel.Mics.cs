using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Audio;
using JockeyBox.Settings;

namespace JockeyBox.ViewModels;

/// <summary>
/// The recording-device side of the MIC strip: which of the enabled microphones
/// the strip edits (<see cref="SelectedMicDeviceId"/>), the dropdown that picks
/// it, and the label reporting how many are open.
///
/// <para>2.0 has a list of capture devices (<c>AppSettings.MicDevices</c>), so a
/// single strip has to name the device it is driving. The selection is a saved
/// routing field: a device that is unplugged or disabled later stays selected,
/// the strip keeps its controls bound to it, and the status line says it is
/// missing instead of silently moving to another microphone.</para>
///
/// <para>Kept additive and in its own file, like <c>MainViewModel.Mic.cs</c>: the
/// transport bar is the only consumer.</para>
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- state

    /// <summary>Recording devices the operator enabled in Settings (0..N).</summary>
    [ObservableProperty] private int _micDeviceCount;

    /// <summary>Strip label: "MIC" with none enabled, "MIC 2" with two.</summary>
    [ObservableProperty] private string _micStripLabel = "MIC";

    /// <summary>The enabled microphones, plus the saved-but-unplugged selection.</summary>
    public ObservableCollection<AudioDeviceInfo> MicDeviceChoices { get; } = new();

    /// <summary>
    /// The device the strip edits; null = none selected. Persisted, so a
    /// restart comes back on the same microphone.
    /// </summary>
    [ObservableProperty] private string? _selectedMicDeviceId;

    /// <summary>Set while adopting a value from the strip, so the write-back handlers stay quiet.</summary>
    private bool _adoptingMicSelection;

    // ---------------------------------------------------------------- wiring

    partial void OnSelectedMicDeviceIdChanged(string? value)
    {
        string? id = string.IsNullOrWhiteSpace(value) ? null : value;

        // AdoptMicSelection already holds the guard and applies the values itself.
        if (_adoptingMicSelection) return;

        if (!string.Equals(_settings.SelectedMicDeviceId, id, StringComparison.Ordinal))
        {
            _settings.SelectedMicDeviceId = id;
            _settings.Save();
        }

        AdoptSelectedMicValues();
    }

    /// <summary>
    /// Bind the selection on start-up and after the settings dialog: the saved id
    /// wins while it is one of the enabled devices, otherwise the first enabled
    /// device is used. A saved id that is not enabled is kept by
    /// <see cref="OnSelectedMicDeviceIdChanged"/>'s caller (<see cref="AdoptMicSelection"/>).
    /// </summary>
    private void AdoptMicSelection()
    {
        // A saved id always wins, even when it is no longer enabled or plugged
        // in: the strip stays bound to it and says it is missing. Only a
        // profile with no choice yet falls back to the first enabled device.
        string? saved = _settings.SelectedMicDeviceId;
        string? id = string.IsNullOrEmpty(saved) ? EnabledMicIds().FirstOrDefault() : saved;

        _adoptingMicSelection = true;
        try
        {
            SelectedMicDeviceId = id;
            AdoptSelectedMicValues();
        }
        finally { _adoptingMicSelection = false; }

        RebuildMicDeviceChoices();
        UpdateMicStatus();
    }

    /// <summary>Read the selected strip (or its saved row) into the strip's scalars.</summary>
    private void AdoptSelectedMicValues()
    {
        var id = SelectedMicDeviceId;
        bool was = _adoptingMicSelection;
        _adoptingMicSelection = true;
        try
        {
            var engine = _transport?.Engine;
            MicLevel = engine?.MicVolumeFor(id) ?? MicSettingsFor(id)?.Volume ?? 1.0f;
            MicMuted = engine?.MicMutedFor(id) ?? MicSettingsFor(id)?.Muted ?? false;
            MicMonitorEnabled = engine?.MicMonitorFor(id) ?? MicSettingsFor(id)?.Monitor ?? false;
            MicPeak = 0;
        }
        finally { _adoptingMicSelection = was; }
    }

    /// <summary>Rebuild the dropdown: enabled devices, plus the saved id when it is gone.</summary>
    private void RebuildMicDeviceChoices()
    {
        MicDeviceChoices.Clear();
        // One enumeration for the whole list: the names come from the endpoints
        // themselves, so a device whose capture is not open still names itself.
        var live = _deviceService.GetInputDevices();
        foreach (var id in EnabledMicIds())
            MicDeviceChoices.Add(new AudioDeviceInfo(id, MicDeviceName(id, live)));

        var selected = SelectedMicDeviceId;
        if (!string.IsNullOrEmpty(selected) &&
            !MicDeviceChoices.Any(c => SameId(c.Id, selected)))
        {
            MicDeviceChoices.Add(new AudioDeviceInfo(selected!, "Saved microphone (not connected)"));
        }
    }

    /// <summary>Index of the selected strip in the mixer, or -1 when it is not open.</summary>
    private int SelectedMicIndex()
    {
        var strips = _transport?.Engine?.Mics.Strips;
        var id = SelectedMicDeviceId;
        if (strips == null || string.IsNullOrEmpty(id)) return -1;

        for (int i = 0; i < strips.Count; i++)
            if (SameId(strips[i].DeviceId, id)) return i;
        return -1;
    }

    private System.Collections.Generic.IEnumerable<string> EnabledMicIds() =>
        _settings.MicDevices
            .Select(m => m.DeviceId)
            .Where(id => !string.IsNullOrWhiteSpace(id));

    private bool IsEnabledMic(string? id) =>
        !string.IsNullOrEmpty(id) && _settings.MicDevices.Any(m => SameId(m.DeviceId, id));

    private AppSettings.MicDeviceSettings? MicSettingsFor(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : _settings.MicDevices.FirstOrDefault(m => SameId(m.DeviceId, id));

    private string MicDeviceName(string id, IReadOnlyList<AudioDeviceInfo> live)
    {
        string? stripName = null;
        var strips = _transport?.Engine?.Mics.Strips;
        if (strips != null)
            foreach (var strip in strips)
                if (SameId(strip.DeviceId, id)) stripName = strip.DeviceName;

        return MicChoiceName(id, live, stripName);
    }

    /// <summary>
    /// The row's text. The live endpoint is the authority: it names itself whether or
    /// not this strip's capture is open, and a capture that never opened keeps
    /// <see cref="Endpoints.MicCapture.DeviceName"/> empty (that is what made every
    /// row of the headless mic chooser blank). The open strip's name is the second
    /// source, and the id is the last resort so a saved-but-unplugged mic still reads
    /// as something rather than as an empty row.
    /// </summary>
    internal static string MicChoiceName(string id, IReadOnlyList<AudioDeviceInfo> live, string? stripName)
    {
        foreach (var device in live)
            if (SameId(device.Id, id) && !string.IsNullOrWhiteSpace(device.FriendlyName))
                return device.FriendlyName;

        if (!string.IsNullOrWhiteSpace(stripName)) return stripName!;
        return id;
    }

    private static bool SameId(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recount the enabled recording devices and repaint the label. Cheap (a list
    /// walk over a handful of rows) but it only writes when the number changed, so
    /// the 20 Hz meter tick does not re-notify the strip forever.
    /// </summary>
    partial void OnMicPeakChanged(double value) => RefreshMicDeviceCount();

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
