using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using JockeyBox.Selftest;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.Asio;

namespace JockeyBox.Diagnostics;

/// <summary>
/// <c>--asio-probe [driver name]</c>: list the installed ASIO drivers from the
/// registry, then open one of them (or each in turn) through <see cref="AsioOut"/>
/// and capture two seconds, printing per-channel peak and RMS.
///
/// <para><b>Why the registry and not just NAudio.</b> NAudio's
/// <c>GetDriverNames()</c> returns subkey names under <c>HKLM\SOFTWARE\ASIO</c>,
/// and on this machine not one of those subkeys carries a <c>Name</c> value - the
/// name <i>is</i> the subkey. Anything that reads a <c>Name</c> value sees an
/// empty ASIO list. Enumerating both registry views prints what is actually
/// installed, including a driver registered only for 32-bit hosts.</para>
///
/// <para><b>Why RMS and peak.</b> "ASIO opened" says nothing about whether a
/// channel carries signal: a muted input, a dead preamp and a wrong channel
/// index all look healthy from the API. Two seconds of numbers tells them
/// apart, and the same numbers say whether a mono tap and a stereo pair will
/// move audio.</para>
///
/// <para>Every driver is probed in isolation: a driver that throws (already open
/// elsewhere, bad CLSID, missing DLL) prints its message and the probe moves on.
/// Zero installed drivers is a valid state and exits 0.</para>
/// </summary>
internal static class AsioProbe
{
    /// <summary>The capture length. ASIO needs a moment to settle; two seconds is enough to see a quiet channel.</summary>
    private const int CaptureMs = 2000;

    /// <summary>The rate asked of every driver, matching <c>AudioEngine.Rate</c>.</summary>
    private const int RequestedRate = 48000;

    /// <summary>Both machine views: a 64-bit process sees the first, a 32-bit-only install lives in the second.</summary>
    private static readonly (string Label, string Key)[] Hives =
    {
        (@"HKLM\SOFTWARE\ASIO", @"SOFTWARE\ASIO"),
        (@"HKLM\SOFTWARE\WOW6432Node\ASIO", @"SOFTWARE\WOW6432Node\ASIO"),
    };

    public static bool Requested(string[] args) => Harness.Flag(args, "--asio-probe") != null;

    public static int Run(string[] args)
    {
        string? wanted = Harness.Flag(args, "--asio-probe");
        if (wanted != null) wanted = wanted.Trim();

        Console.WriteLine($"asio-probe: {Environment.Is64BitProcess switch { true => "64-bit", false => "32-bit" }} process, NAudio {NaudioVersion()}");

        List<DriverEntry> installed = ReadRegistry();
        if (installed.Count == 0)
        {
            Console.WriteLine("asio-probe: no ASIO drivers are registered (HKLM\\SOFTWARE\\ASIO and WOW6432Node\\ASIO are empty); nothing to probe.");
            return 0;
        }

        Console.WriteLine($"asio-probe: {installed.Count} registry entr{(installed.Count == 1 ? "y" : "ies")}");
        int index = 0;
        foreach (DriverEntry driver in installed)
        {
            index++;
            Console.WriteLine();
            Console.WriteLine($"  {index}. {driver.Name}   [{driver.Hive}]");
            Console.WriteLine($"     CLSID      {driver.Clsid ?? "(no CLSID value)"}");
            if (!string.IsNullOrWhiteSpace(driver.Description) && !string.Equals(driver.Description, driver.Name, StringComparison.Ordinal))
                Console.WriteLine($"     described  {driver.Description}");
            Console.WriteLine(driver.Dll == null
                ? "     dll        (InprocServer32 not found)"
                : $"     dll        {driver.Dll}  [{(driver.DllExists ? "present" : "MISSING")}]");
        }

        string[] naudioNames;
        try
        {
            naudioNames = AsioOut.GetDriverNames();
        }
        catch (Exception ex)
        {
            naudioNames = Array.Empty<string>();
            Console.WriteLine($"asio-probe: AsioOut.GetDriverNames() threw {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine($"asio-probe: AsioOut.GetDriverNames() -> {naudioNames.Length}: {string.Join(", ", naudioNames)}");

        // The same names from the registry, de-duplicated across the two views, so
        // "NAudio sees fewer drivers than the registry lists" is visible as a fact.
        var registryNames = new List<string>();
        foreach (DriverEntry driver in installed)
        {
            if (!registryNames.Contains(driver.Name, StringComparer.OrdinalIgnoreCase)) registryNames.Add(driver.Name);
        }
        var missingFromNaudio = registryNames.FindAll(n => Array.FindIndex(naudioNames, x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)) < 0);
        if (missingFromNaudio.Count > 0)
            Console.WriteLine($"asio-probe: registered but not returned by NAudio: {string.Join(", ", missingFromNaudio)}");

        var targets = new List<string>();
        if (wanted is { Length: > 0 })
        {
            string? match = Array.Find(naudioNames, n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                ?? registryNames.Find(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Console.WriteLine();
                Console.WriteLine($"asio-probe: no installed driver is named '{wanted}'. Nothing probed.");
                return 2;
            }
            targets.Add(match);
        }
        else
        {
            targets.AddRange(naudioNames);
            if (targets.Count == 0)
            {
                Console.WriteLine("asio-probe: the registry lists drivers but NAudio returns none, so none can be opened; nothing probed.");
                return 0;
            }
        }

        int opened = 0;
        foreach (string target in targets)
        {
            if (ProbeDriver(target)) opened++;
        }

        Console.WriteLine();
        Console.WriteLine(wanted is { Length: > 0 }
            ? $"asio-probe: '{targets[0]}' {(opened > 0 ? "opened and captured" : "could not be opened")}."
            : $"asio-probe: {opened} of {targets.Count} driver(s) opened and captured.");

        return opened > 0 ? 0 : 1;
    }

    /// <summary>Opens one driver, prints what it negotiated and every channel's peak and RMS. Returns false when it could not be opened or captured.</summary>
    private static bool ProbeDriver(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {name} ===");

        AsioOut? device = null;
        try
        {
            // The ctor initialises the driver: this is the call that fails while
            // another application already holds the single ASIO client.
            device = new AsioOut(name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  open failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        try
        {
            int channels = device.DriverInputChannelCount;
            Console.WriteLine($"  driver input channels: {channels}");

            if (channels <= 0)
            {
                Console.WriteLine("  the driver exposes no input channels; nothing to capture.");
                return false;
            }

            var meter = new ChannelMeter(channels);
            device.AudioAvailable += meter.OnAudio;

            // Capture only: no playback provider, every input channel, the engine's rate.
            device.InitRecordAndPlayback(null, channels, RequestedRate);

            Console.WriteLine($"  frames per buffer:     {device.FramesPerBuffer}");
            Console.WriteLine($"  sample rate:           driver reports {DriverReportedRate(device)}, requested {RequestedRate}");
            for (int c = 0; c < channels; c++)
            {
                string channelName;
                try { channelName = device.AsioInputChannelName(c); }
                catch (Exception ex) { channelName = $"({ex.GetType().Name})"; }
                Console.WriteLine($"  input {c}: {channelName}");
            }

            device.Play();
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < CaptureMs) Thread.Sleep(20);
            device.Stop();

            long frames = Interlocked.Read(ref meter.Frames);
            double seconds = clock.Elapsed.TotalSeconds;
            Console.WriteLine(frames > 0
                ? $"  captured {clock.ElapsedMilliseconds} ms: {meter.Buffers} buffers, {frames} frames/channel -> {(frames / seconds):0} Hz measured"
                : $"  captured {clock.ElapsedMilliseconds} ms: NO AUDIO CALLBACKS - the driver never delivered a buffer");

            for (int c = 0; c < channels; c++)
            {
                double peak = meter.Peak[c];
                double rms = frames > 0 ? Math.Sqrt(meter.SumSquares[c] / frames) : 0;
                Console.WriteLine($"  input {c}: peak {peak,10:0.000000} ({Dbfs(peak),7})   rms {rms,10:0.000000} ({Dbfs(rms),7})");
            }

            return frames > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { device.Stop(); } catch { }
            try { device.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// The rate the driver says it runs at. <see cref="AsioOut"/> exposes no input
    /// format and is not an <see cref="ISampleProvider"/>, so its private driver object
    /// is walked by reflection (fields and properties, because NAudio mixes both) and
    /// asked for ASIO's own rate. "unknown" is honest; the measured rate after the
    /// capture is always printed alongside.
    /// </summary>
    private static string DriverReportedRate(AsioOut device)
    {
        foreach (string fieldName in new[] { "driver", "asioDriver" })
        {
            object? driver = null;
            try { driver = typeof(AsioOut).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device); }
            catch { }
            if (driver == null) continue;

            try
            {
                if (driver.GetType().GetMethod("GetSampleRate", Type.EmptyTypes)?.Invoke(driver, null) is double rate && rate > 0)
                    return rate.ToString("0.##") + " Hz";
            }
            catch { }

            try
            {
                Type type = driver.GetType();
                object? capabilities = type.GetProperty("Capabilities")?.GetValue(driver)
                    ?? type.GetField("Capabilities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(driver);
                if (capabilities == null) continue;
                Type caps = capabilities.GetType();
                object? value = caps.GetProperty("SampleRate")?.GetValue(capabilities)
                    ?? caps.GetField("SampleRate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(capabilities);
                if (value is double sampleRate && sampleRate > 0) return sampleRate.ToString("0.##") + " Hz";
            }
            catch { }
        }

        return "unknown";
    }

    private static string Dbfs(double linear)
    {
        if (linear <= 0) return "-inf dBFS";
        return (20 * Math.Log10(linear)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dBFS";
    }

    private static string NaudioVersion()
    {
        Version? version = typeof(AsioOut).Assembly.GetName().Version;
        return version?.ToString(3) ?? "unknown";
    }

    private sealed record DriverEntry(string Hive, string Name, string? Clsid, string? Description, string? Dll, bool DllExists);

    private static List<DriverEntry> ReadRegistry()
    {
        var entries = new List<DriverEntry>();
        foreach ((string label, string key) in Hives)
        {
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(key);
                if (root == null) continue;

                foreach (string name in root.GetSubKeyNames())
                {
                    using RegistryKey? sub = root.OpenSubKey(name);
                    string? clsid = (sub?.GetValue("CLSID") as string)?.Trim();
                    string? description = (sub?.GetValue("Description") as string)?.Trim();
                    string? dll = string.IsNullOrWhiteSpace(clsid) ? null : ReadInprocServer(clsid);
                    bool exists = dll != null && SafeFileExists(dll);
                    entries.Add(new DriverEntry(label, name, clsid, description, dll, exists));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"asio-probe: {label} unreadable ({ex.GetType().Name}: {ex.Message})");
            }
        }

        return entries;
    }

    /// <summary>The default value of the CLSID's <c>InprocServer32</c>, from the 64-bit view and then the 32-bit one.</summary>
    private static string? ReadInprocServer(string clsid)
    {
        foreach (string prefix in new[] { "CLSID", @"WOW6432Node\CLSID" })
        {
            try
            {
                using RegistryKey? server = Registry.ClassesRoot.OpenSubKey($@"{prefix}\{clsid}\InprocServer32");
                if (server?.GetValue(null) is string path && path.Trim().Length > 0)
                    return path.Trim().Trim('"');
            }
            catch
            {
                // A missing or unreadable CLSID is reported as a missing DLL.
            }
        }

        return null;
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    /// <summary>Accumulates peak and sum-of-squares inside the ASIO buffer callback: no output, no locks, no allocation once sized.</summary>
    private sealed class ChannelMeter
    {
        private readonly int _channels;
        private float[] _buffer = Array.Empty<float>();

        public ChannelMeter(int channels)
        {
            _channels = channels;
            Peak = new double[channels];
            SumSquares = new double[channels];
        }

        public long Frames;
        public long Buffers;
        public double[] Peak { get; }
        public double[] SumSquares { get; }

        public void OnAudio(object? sender, AsioAudioAvailableEventArgs e)
        {
            int channels = Math.Min(_channels, e.InputBuffers.Length);
            int frames = e.SamplesPerBuffer;
            int required = frames * e.InputBuffers.Length;
            if (required <= 0 || channels <= 0) return;

            // GetAsInterleavedSamples demands exactly frames x channels floats.
            if (_buffer.Length != required) _buffer = new float[required];
            e.GetAsInterleavedSamples(_buffer);

            float[] buffer = _buffer;
            for (int frame = 0; frame < frames; frame++)
            {
                int baseIndex = frame * e.InputBuffers.Length;
                for (int c = 0; c < channels; c++)
                {
                    double sample = buffer[baseIndex + c];
                    double magnitude = sample < 0 ? -sample : sample;
                    if (magnitude > Peak[c]) Peak[c] = magnitude;
                    SumSquares[c] += sample * sample;
                }
            }

            Interlocked.Add(ref Frames, frames);
            Buffers++;
        }
    }
}
