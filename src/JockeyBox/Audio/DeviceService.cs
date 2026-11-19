using JockeyBox.Tools;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JockeyBox.Audio;

/// <summary>Lightweight descriptor for a render (output) endpoint.</summary>
public record AudioDeviceInfo(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}

/// <summary>
/// Enumerates active WASAPI endpoints and resolves device IDs back to
/// MMDevice instances. A fresh MMDevice is resolved per playback session so a
/// stale COM object is never reused after unplug/replug.
///
/// <para><b>Render and capture are separate lists on purpose.</b> The program,
/// monitor and deck-send pickers must never offer a microphone, and the mic
/// picker must never offer a speaker (that would send the voice to a device that
/// cannot record). Every "None" contract is the same on both sides: a null id
/// means off, and a stored id that no longer resolves is reported as missing
/// instead of silently falling back to another device.</para>
/// </summary>
public class DeviceService
{
    /// <summary>All currently active output devices, default endpoint first.</summary>
    public List<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        // Enumeration runs on a background thread during start-up, so a broken
        // audio service must cost an empty picker, not the whole launch.
        try
        {
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
        }
        catch (Exception ex)
        {
            Log.Warn("Output device enumeration failed: " + ex.Message);
        }
        return list;
    }

    /// <summary>
    /// All currently active capture (microphone) devices, default first.
    ///
    /// <para>The default is the <i>communications</i> endpoint when Windows has
    /// one, then the console endpoint, then multimedia. That ordering is what the
    /// OS itself uses for calls, so a headset mic is preferred over a webcam mic
    /// that happens to be the multimedia default.</para>
    /// </summary>
    public List<AudioDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        // Same contract as the render side: a failure is logged, never thrown.
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = DefaultCaptureId(enumerator);
            if (defaultId != null)
            {
                try
                {
                    using var def = enumerator.GetDevice(defaultId);
                    list.Add(new AudioDeviceInfo(def.ID, $"{def.FriendlyName} (Default)"));
                }
                catch
                {
                    // The endpoint vanished between the two calls; the loop below
                    // still lists whatever is active.
                }
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (device.ID != defaultId)
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Input device enumeration failed: " + ex.Message);
        }
        return list;
    }

    /// <summary>The default communications/console capture endpoint, or null when the machine has no microphone.</summary>
    public string? GetDefaultCaptureDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return DefaultCaptureId(enumerator);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The native mix format of a capture endpoint, so the mixer can label each
    /// device with the rate the OS actually runs it at (devices differ, and the
    /// mixer resamples per device). Null when the device is gone or the format
    /// cannot be read.
    /// </summary>
    public WaveFormat? GetInputMixFormat(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) return null;
            return device.AudioClient?.MixFormat;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// Resolve a stored <i>capture</i> device ID. Unlike <see cref="ResolveDevice"/>
    /// there is no fallback: null (an empty id) means "None", and an id whose
    /// device is unplugged resolves to null so a saved routing choice is never
    /// silently replaced by a different microphone. Caller owns the result.
    /// </summary>
    public MMDevice? ResolveCaptureDevice(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        var enumerator = new MMDeviceEnumerator();
        try
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch
            {
                // Unknown id (unplugged, other user's endpoint): report missing.
            }
            return null;
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>Communications → console → multimedia, the order Windows uses for calls.</summary>
    private static string? DefaultCaptureId(MMDeviceEnumerator enumerator)
    {
        foreach (var role in new[] { Role.Communications, Role.Console, Role.Multimedia })
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                if (!string.IsNullOrEmpty(device.ID)) return device.ID;
            }
            catch
            {
                // No default for this role; try the next one.
            }
        }
        return null;
    }
}
