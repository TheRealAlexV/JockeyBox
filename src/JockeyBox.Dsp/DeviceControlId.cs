using System.Text;

namespace JockeyBox.Dsp;

/// <summary>
/// Derives the stable, URL-safe control ids for a capture endpoint.
/// <para>
/// The slug is the 16-character lowercase hex of a 64-bit FNV-1a hash over the endpoint id's
/// UTF-8 bytes. Capture endpoint ids contain braces, dots and spaces (for example
/// <c>{0.0.1.00000000}.{6b2f…}</c>) and cannot appear in a control id, a URL or a config key
/// unchanged; the hash makes a fixed-width token with none of those characters. It is stable
/// across runs and machines because it depends on nothing but the endpoint id string.
/// </para>
/// <para>Id shape: <c>mic.&lt;slug&gt;.&lt;parameter&gt;</c>, e.g. <c>mic.f22f2d874051f93a.gate.threshold</c>.</para>
/// </summary>
public static class DeviceControlId
{
    /// <summary>Prefix for per-device capture controls.</summary>
    public const string Prefix = "mic";

    /// <summary>Length of <see cref="Slug"/>, in characters.</summary>
    public const int SlugLength = 16;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Stable 16-character lowercase hex slug for a capture endpoint id. Deterministic: the
    /// same id always yields the same slug, on every run and platform.
    /// </summary>
    public static string Slug(string captureEndpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureEndpointId);

        ulong hash = FnvOffsetBasis;
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(captureEndpointId);
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }

        return ToHex(hash);
    }

    /// <summary>Control id for one parameter of one device: <c>mic.&lt;slug&gt;.&lt;parameter&gt;</c>.</summary>
    public static string For(string captureEndpointId, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        return string.Create(Prefix.Length + SlugLength + parameter.Length + 2,
            (captureEndpointId, parameter), static (span, state) =>
            {
                int written = 0;
                Prefix.AsSpan().CopyTo(span);
                written += Prefix.Length;
                span[written++] = '.';
                Slug(state.captureEndpointId).AsSpan().CopyTo(span[written..]);
                written += SlugLength;
                span[written++] = '.';
                state.parameter.AsSpan().CopyTo(span[written..]);
            });
    }

    private static string ToHex(ulong value)
    {
        const string digits = "0123456789abcdef";
        var chars = new char[SlugLength];
        for (int i = SlugLength - 1; i >= 0; i--)
        {
            chars[i] = digits[(int)(value & 0xF)];
            value >>= 4;
        }

        return new string(chars);
    }
}
