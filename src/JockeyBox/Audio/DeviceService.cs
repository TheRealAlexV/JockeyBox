using NAudio.CoreAudioApi;

namespace JockeyBox.Audio;

/// <summary>Lightweight descriptor for a render (output) endpoint.</summary>
public record AudioDeviceInfo(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}

/// <summary>
/// Enumerates active WASAPI render endpoints and resolves device IDs back to
/// MMDevice instances. A fresh MMDevice is resolved per playback session so a
/// stale COM object is never reused after unplug/replug.
/// </summary>
public class DeviceService
{
    /// <summary>All currently active output devices, default endpoint first.</summary>
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = def.ID;
            list.Add(new AudioDeviceInfo(def.ID, $"{def.FriendlyName} (Default)"));
        }
        catch
        {
            // No default endpoint (no audio devices at all) - list stays empty.
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (device.ID != defaultId)
                list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            device.Dispose();
        }
        return list;
    }

    /// <summary>
    /// Resolve a stored device ID to an MMDevice. Falls back to the system
    /// default endpoint when the ID is unknown (device unplugged, etc).
    /// Caller owns the returned device and must dispose it.
    /// </summary>
    public MMDevice ResolveDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    var device = enumerator.GetDevice(deviceId);
                    if (device.State == DeviceState.Active) return device;
                    device.Dispose();
                }
                catch
                {
                    // fall through to default
                }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}
