namespace JockeyBox.Dsp;

/// <summary>
/// The key that identifies one mixing strip's source. A whole capture endpoint keeps the bare
/// endpoint id, byte for byte, so an existing <c>mixer-settings.json</c> and every slug derived
/// from it stay valid. One channel of a multi-channel source is a composite key:
/// <c>&lt;kind&gt;:&lt;endpoint&gt;#ch&lt;offset&gt;</c> for a mono tap or
/// <c>&lt;kind&gt;:&lt;endpoint&gt;#pair&lt;offset&gt;</c> for a stereo pair at
/// <c>offset</c> and <c>offset + 1</c>, for example
/// <c>asio:Focusrite USB ASIO#ch2</c>, <c>asio:Focusrite USB ASIO#pair2</c> or
/// <c>wasapi:{0.0.1…}.{guid}#ch0</c>.
/// <para>
/// <see cref="DeviceControlId.Slug"/> hashes the whole key, so each channel (and each mode of a
/// channel) gets its own slug and therefore its own <c>mic.&lt;slug&gt;.&lt;parameter&gt;</c> ids
/// with no change downstream. Nothing in this type is Windows-specific.
/// </para>
/// <para>
/// Parsing splits on the last <c>#</c> and the first <c>:</c>; endpoint ids contain neither
/// (WASAPI ids are <c>{0.0.1.00000000}.{guid}</c>), so a bare endpoint id always parses as
/// channel <c>-1</c>.
/// </para>
/// </summary>
public static class DeviceSourceKey
{
    /// <summary>Separator before the channel marker.</summary>
    public const char ChannelSeparator = '#';

    /// <summary>Prefix of a mono channel offset, between <see cref="ChannelSeparator"/> and the number.</summary>
    public const string ChannelMarker = "ch";

    /// <summary>Prefix of a stereo pair offset, between <see cref="ChannelSeparator"/> and the number.</summary>
    public const string PairMarker = "pair";

    /// <summary>The channel value meaning "the whole device", not one channel.</summary>
    public const int WholeDevice = -1;

    /// <summary>
    /// The strip key for <paramref name="kind"/>'s <paramref name="endpointId"/> channel
    /// <paramref name="channel"/> as a mono tap. A negative channel returns the endpoint id
    /// unchanged. Use the <see cref="AsioChannelMode"/> overload for a stereo pair.
    /// </summary>
    public static string Compose(string kind, string endpointId, int channel) =>
        Compose(kind, endpointId, channel, AsioChannelMode.Mono);

    /// <summary>
    /// The strip key for <paramref name="kind"/>'s <paramref name="endpointId"/> channel
    /// <paramref name="channel"/> read as <paramref name="mode"/>: one source channel duplicated
    /// to both sides, or <paramref name="channel"/> and <c>channel + 1</c> as a pair. A negative
    /// channel returns the endpoint id unchanged whatever the mode: the whole device has no
    /// channel and no pair.
    /// </summary>
    public static string Compose(string kind, string endpointId, int channel, AsioChannelMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        if (channel < 0) return endpointId;

        if (kind.Contains(':') || kind.Contains(ChannelSeparator))
        {
            throw new ArgumentException(
                $"a source kind must not contain ':' or '{ChannelSeparator}': '{kind}'", nameof(kind));
        }

        if (endpointId.Contains(ChannelSeparator))
        {
            throw new ArgumentException(
                $"an endpoint id must not contain '{ChannelSeparator}': '{endpointId}'", nameof(endpointId));
        }

        string marker = mode == AsioChannelMode.StereoPair ? PairMarker : ChannelMarker;
        return $"{kind}:{endpointId}{ChannelSeparator}{marker}{channel}";
    }

    /// <summary>
    /// Splits a strip key back into its parts, discarding the mode. See the
    /// <see cref="AsioChannelMode"/> overload.
    /// </summary>
    public static bool TryParse(string key, out string kind, out string endpointId, out int channel) =>
        TryParse(key, out kind, out endpointId, out channel, out _);

    /// <summary>
    /// Splits a strip key back into its parts. A bare endpoint id yields channel <c>-1</c>, an
    /// empty kind and <see cref="AsioChannelMode.Mono"/>; <c>#pair&lt;N&gt;</c> yields
    /// <see cref="AsioChannelMode.StereoPair"/>. Returns false for a key that has a channel
    /// suffix but no valid marker and offset.
    /// </summary>
    public static bool TryParse(
        string key,
        out string kind,
        out string endpointId,
        out int channel,
        out AsioChannelMode mode)
    {
        kind = "";
        endpointId = "";
        channel = WholeDevice;
        mode = AsioChannelMode.Mono;

        if (string.IsNullOrWhiteSpace(key)) return false;

        int hash = key.LastIndexOf(ChannelSeparator);
        if (hash < 0)
        {
            endpointId = key;
            return true;
        }

        ReadOnlySpan<char> suffix = key.AsSpan(hash + 1);
        int parsed;
        if (suffix.StartsWith(PairMarker, StringComparison.Ordinal))
        {
            if (!int.TryParse(suffix[PairMarker.Length..], out parsed) || parsed < 0) return false;
            mode = AsioChannelMode.StereoPair;
        }
        else if (suffix.StartsWith(ChannelMarker, StringComparison.Ordinal))
        {
            if (!int.TryParse(suffix[ChannelMarker.Length..], out parsed) || parsed < 0) return false;
        }
        else
        {
            return false;
        }

        int colon = key.IndexOf(':');
        if (colon <= 0 || colon > hash) return false;

        kind = key[..colon];
        endpointId = key[(colon + 1)..hash];
        channel = parsed;
        return endpointId.Length > 0;
    }
}
