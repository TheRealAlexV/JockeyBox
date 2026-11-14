using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using NAudio.CoreAudioApi;
using JockeyBox.Tools;

namespace JockeyBox.Audio;

/// <summary>
/// Read-only CoreAudio probe: every render endpoint, the endpoint's own volume and
/// mute, the sessions living on it and each session's mute and master volume.
///
/// <para><b>Why it exists.</b> "Buffer reaches a running session at full level and
/// the operator still hears nothing" has one candidate that outranks the endpoint:
/// the Windows per-app volume/mute for JockeyBox.exe. It persists across rebuilds,
/// survives a new exe at the same path, and produces exactly a healthy
/// <c>Playing</c> session with non-zero peaks and no error anywhere. This probe
/// reads that state instead of guessing at it.</para>
///
/// <para>Nothing here writes: no volume is set, no mute is toggled, no session is
/// created. It is diagnosis.</para>
/// </summary>
public static class AudioProbe
{
    /// <summary>One line per endpoint plus one per session, for the log and stdout.</summary>
    public static IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        string? defaultId = null;
        using (var enumerator = new MMDeviceEnumerator())
        {
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception ex)
            {
                lines.Add($"probe: no default render endpoint ({ex.Message})");
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                try
                {
                    lines.Add(DescribeDevice(device, defaultId));
                }
                catch (Exception ex)
                {
                    // A disabled or unplugged endpoint throws on its own property
                    // reads (0xE000020B); one bad device must not end the probe.
                    lines.Add($"endpoint <unreadable: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}>");
                }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// The endpoint facts the router needs, without opening anything: every render
    /// endpoint's id, name and whether it is active, plus the current default.
    /// Cheap enough to call once per route change.
    /// </summary>
    public static (string? DefaultId, string? DefaultName, IReadOnlyList<RouteEndpoint> Endpoints) Snapshot()
    {
        var endpoints = new List<RouteEndpoint>();
        string? defaultId = null;
        string? defaultName = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
                defaultName = def.FriendlyName;
            }
            catch (Exception ex)
            {
                Log.Warn($"audio route: Windows reports no default render endpoint ({ex.Message})");
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                try
                {
                    string id = device.ID;
                    endpoints.Add(new RouteEndpoint(
                        id,
                        device.FriendlyName,
                        device.State == DeviceState.Active,
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                catch
                {
                    // A disabled endpoint can throw on its own property reads; the
                    // router treats it as absent, which is what it is.
                }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"audio route: enumerating render endpoints failed ({ex.Message})");
        }

        return (defaultId, defaultName, endpoints);
    }

    private static string DescribeDevice(MMDevice device, string? defaultId)    {
        var sb = new StringBuilder(240);
        string name = "<unnamed>";
        DeviceState state = DeviceState.Unplugged;
        try { name = device.FriendlyName; } catch { }
        try { state = device.State; } catch { }

        string kind = Classify(name);
        sb.Append($"endpoint '{name}' [{kind}] state={state}");
        try
        {
            if (string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase))
                sb.Append(" DEFAULT");
            sb.Append($" id={device.ID}");
        }
        catch { }

        try
        {
            var volume = device.AudioEndpointVolume;
            sb.Append($" endpoint-volume={volume.MasterVolumeLevelScalar:0.000} " +
                      $"endpoint-mute={volume.Mute}");
        }
        catch (Exception ex)
        {
            sb.Append($" endpoint-volume=<unreadable: {ex.Message}>");
        }

        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            sb.Append($" sessions={sessions.Count}");
            for (int i = 0; i < sessions.Count; i++)
                sb.Append("\n  ").Append(DescribeSession(sessions[i]));
        }
        catch (Exception ex)
        {
            sb.Append($" sessions=<unreadable: {ex.Message}>");
        }

        return sb.ToString();
    }

    private static string DescribeSession(AudioSessionControl session)
    {
        uint pid = 0;
        string proc = "?";
        try
        {
            pid = session.GetProcessID;
            proc = pid == 0 ? "<system>" : Process.GetProcessById((int)pid).ProcessName + ".exe";
        }
        catch
        {
            // A session can outlive its process; the pid is still worth reporting.
        }

        string mute = "?", volume = "?";
        try
        {
            mute = session.SimpleAudioVolume.Mute.ToString();
            volume = session.SimpleAudioVolume.Volume.ToString("0.000");
        }
        catch
        {
            // Some sessions have no ISimpleAudioVolume; leave both unknown.
        }

        return $"session pid={pid} proc={proc} state={session.State} " +
               $"mute={mute} volume={volume} system-sounds={session.IsSystemSoundsSession}";
    }

    /// <summary>
    /// "physical" or the DSP family the name belongs to. The distinction is the
    /// whole point of the second half of the investigation: a virtual enhancer in
    /// front of the speakers can sit between a healthy session and silence.
    /// </summary>
    public static string Classify(string friendlyName)
    {
        string n = friendlyName.ToLowerInvariant();
        if (n.Contains("fxsound")) return "virtual/DSP FxSound enhancer";
        if (n.Contains("vb-audio") || n.Contains("vbmatrix") || n.Contains("voicemeeter"))
            return "virtual VB-Audio";
        if (n.Contains("cable output") || n.Contains("cable input")) return "virtual VB-Cable";
        if (n.Contains("virtual") || n.Contains("loopback")) return "virtual";
        if (n.Contains("hdmi") || n.Contains("display audio")) return "physical (HDMI)";
        return "physical";
    }
}
