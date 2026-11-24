using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace JockeyBox.Audio.Virtual;

/// <summary>Width of a virtual-device line: Stereo carries two channels, Mono one.</summary>
public enum VirtualLineKind
{
    Mono = 1,
    Stereo = 2,
}

/// <summary>
/// One ordered line of the virtual device. The line is the unit the operator sees;
/// its width is what the transport and the ASIO driver see.
/// </summary>
public sealed class VirtualLine
{
    /// <summary>Stable identity, never reused for a different line.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public VirtualLineKind Kind { get; set; } = VirtualLineKind.Stereo;

    /// <summary>Source binding, opaque to this layer (the routing lane fills it in).</summary>
    public string Source { get; set; } = "";

    public double Gain { get; set; } = 1.0;

    public bool Mute { get; set; }

    /// <summary>Transport width of this line: 2 for stereo, 1 for mono.</summary>
    public int Channels => (int)Kind;
}

/// <summary>
/// The virtual device's ordered line list and the one on-disk form of it.
///
/// The native ASIO driver reads this file at <c>init()</c> and derives its channel
/// geometry from it, so both sides must agree byte for byte on the file. The writer
/// here is deliberately hand-rolled (fixed key order, no indentation, invariant
/// numbers) and the driver's reader accepts exactly this subset of JSON — no
/// library on either side, no escaping surprises.
///
/// The file lives under <c>%ProgramData%</c> and is world-readable on purpose: an
/// elevated host (OBS in session 0) reads the same file the app wrote.
///
/// Changing the list is applied by restarting the ASIO host, not live.
/// </summary>
public static class VirtualDeviceConfig
{
    public const int Version = 1;
    public const string FileName = "virtual-device.json";
    public const string ConfigPathEnvVar = "JOCKEYBOX_DEVICE_CONFIG";

    /// <summary>The device always exposes at least a stereo pair.</summary>
    public const int MinChannels = 2;

    /// <summary>ASIO hosts are built for far fewer; 32 keeps every path bounded.</summary>
    public const int MaxChannels = 32;

    // The driver's reader keeps these in fixed C buffers (id[64], name[64],
    // source[128]) and counts UTF-8 bytes including the terminator.
    public const int MaxIdBytes = 63;
    public const int MaxNameBytes = 63;
    public const int MaxSourceBytes = 127;

    /// <summary>What the app must tell the operator after a geometry edit.</summary>
    public const string RestartRequiredMessage =
        "Virtual device lines changed. Restart OBS (the ASIO host) to apply.";

    public static string DirectoryPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                var dir = Path.GetDirectoryName(env.Trim());
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JockeyBox");
        }
    }

    public static string DefaultPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim();
            }
            return Path.Combine(DirectoryPath, FileName);
        }
    }

    /// <summary>
    /// Builds the legacy stereo pair lines representing an unconfigured or empty line set.
    /// </summary>
    public static IReadOnlyList<VirtualLine> LegacyLines => new[]
    {
        new VirtualLine { Id = "line-1", Name = "Line 1", Kind = VirtualLineKind.Stereo, Source = "main" }
    };

    /// <summary>
    /// Returns the effective line set: if lines is empty or null, returns <see cref="LegacyLines"/>.
    /// </summary>
    public static IReadOnlyList<VirtualLine> EffectiveLines(IReadOnlyList<VirtualLine>? lines) =>
        lines is { Count: > 0 } ? lines : LegacyLines;

    /// <summary>Sum of the line widths, clamped to the device's bounds.</summary>
    public static int ChannelCount(IReadOnlyList<VirtualLine> lines)
    {
        var total = 0;
        for (var i = 0; i < lines.Count; i++) total += lines[i].Channels;
        return Math.Clamp(total, MinChannels, MaxChannels);
    }

    public static void Save(IReadOnlyList<VirtualLine> lines, string? path = null)
    {
        path ??= DefaultPath;
        var text = Serialize(lines);   // validate before touching the disk at all

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) VirtualConfigDirectory.Ensure(directory);

        // The driver re-reads this file every second while it retries a re-attach, so
        // a plain overwrite can be read half-written (or as zero bytes). Write a
        // sibling, then move it over the real file: a reader sees old or new, never torn.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Saves the effective lines to disk, ensuring a failing write logs once and does not throw into the caller.
    /// Returns true if saved successfully, false otherwise.
    /// </summary>
    public static bool TrySaveEffective(IReadOnlyList<VirtualLine>? lines, string? path = null)
    {
        try
        {
            var effective = EffectiveLines(lines);
            Save(effective, path);
            return true;
        }
        catch (Exception ex)
        {
            JockeyBox.Tools.Log.Warn($"virtual device config could not be written to '{path ?? DefaultPath}': {ex.Message}");
            return false;
        }
    }

    public static IReadOnlyList<VirtualLine> Load(string? path = null)
    {
        path ??= DefaultPath;

        // A zero-length file is a truncated write, not "unconfigured": treating it as
        // absent would silently serve the legacy pair and hide the real fault.
        var info = new FileInfo(path);
        if (info.Exists && info.Length == 0)
            throw new FormatException($"'{path}' is empty; refusing it as a device config");

        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// The canonical bytes. Key order, number form and escaping are part of the
    /// contract with the driver; do not "tidy" this output.
    /// </summary>
    public static string Serialize(IReadOnlyList<VirtualLine> lines)
    {
        Validate(lines);
        var text = new StringBuilder(128 + lines.Count * 96);
        text.Append("{\"version\":").Append(Version).Append(",\"lines\":[");
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (i > 0) text.Append(',');
            text.Append("{\"id\":");
            AppendString(text, line.Id);
            text.Append(",\"name\":");
            AppendString(text, line.Name);
            text.Append(",\"type\":");
            AppendString(text, line.Kind == VirtualLineKind.Stereo ? "stereo" : "mono");
            text.Append(",\"source\":");
            AppendString(text, line.Source);
            text.Append(",\"gain\":")
                .Append(line.Gain.ToString("R", CultureInfo.InvariantCulture));
            text.Append(",\"mute\":").Append(line.Mute ? "true" : "false");
            text.Append('}');
        }
        text.Append("]}");
        return text.ToString();
    }

    /// <summary>
    /// Parses the canonical form. Strict on purpose: any deviation the driver would
    /// reject is rejected here too, so a bad file fails in the app, not as silence.
    /// </summary>
    public static IReadOnlyList<VirtualLine> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("root is not an object");

            var version = 0;
            if (!root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out version) || version != Version)
                throw new FormatException($"version must be {Version}");

            if (!root.TryGetProperty("lines", out var linesElement) ||
                linesElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("lines must be an array");

            var lines = new List<VirtualLine>();
            foreach (var element in linesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new FormatException("line is not an object");

                var line = new VirtualLine
                {
                    Id = RequiredString(element, "id"),
                    Name = RequiredString(element, "name"),
                    Source = RequiredString(element, "source"),
                };

                var type = RequiredString(element, "type");
                line.Kind = type switch
                {
                    "stereo" => VirtualLineKind.Stereo,
                    "mono" => VirtualLineKind.Mono,
                    _ => throw new FormatException($"unknown line type '{type}'"),
                };

                if (!element.TryGetProperty("gain", out var gain) ||
                    gain.ValueKind != JsonValueKind.Number || !gain.TryGetDouble(out var gainValue) ||
                    !double.IsFinite(gainValue))
                    throw new FormatException("gain must be a finite number");
                line.Gain = gainValue;

                if (!element.TryGetProperty("mute", out var mute) ||
                    (mute.ValueKind != JsonValueKind.True && mute.ValueKind != JsonValueKind.False))
                    throw new FormatException("mute must be a boolean");
                line.Mute = mute.GetBoolean();

                lines.Add(line);
            }

            Validate(lines);
            return lines;
        }
        catch (JsonException exception)
        {
            throw new FormatException($"not the virtual-device format: {exception.Message}", exception);
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"'{name}' must be a string");
        return value.GetString() ?? throw new FormatException($"'{name}' must not be null");
    }

    private static void Validate(IReadOnlyList<VirtualLine> lines)
    {
        var channels = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Id))
                throw new InvalidOperationException($"line {i} has no id");
            if (!ids.Add(line.Id))
                throw new InvalidOperationException($"duplicate line id '{line.Id}'");
            if (line.Kind != VirtualLineKind.Mono && line.Kind != VirtualLineKind.Stereo)
                throw new InvalidOperationException($"line '{line.Id}' has an unknown kind");
            if (line.Name is null || line.Source is null)
                throw new InvalidOperationException($"line '{line.Id}' has a null name or source");
            if (!double.IsFinite(line.Gain))
                throw new InvalidOperationException($"line '{line.Id}' gain is not finite");

            // The driver's reader holds these in fixed char arrays (64/64/128 bytes
            // including the terminator) and counts UTF-8 bytes, not characters. A
            // name that fits as characters but not as bytes would make the driver
            // reject the whole file and serve two silent channels, so refuse it here
            // where the operator can still see why.
            RequireBytes(line.Id, MaxIdBytes, "id");
            RequireBytes(line.Name, MaxNameBytes, "name");
            RequireBytes(line.Source, MaxSourceBytes, "source");

            channels += line.Channels;
        }

        if (channels > MaxChannels)
            throw new InvalidOperationException(
                $"{channels} channels exceeds the device maximum of {MaxChannels}");
    }

    private static void RequireBytes(string value, int limit, string field)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > limit)
            throw new InvalidOperationException(
                $"line {field} \"{value}\" is {bytes} bytes, over the device limit of {limit}");
    }

    // Only '"', '\\' and control characters are escaped: System.Text.Json's default
    // encoder would turn every accented name into \uXXXX, which the driver's small
    // reader deliberately does not need to support beyond plain UTF-8.
    private static void AppendString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }
}
