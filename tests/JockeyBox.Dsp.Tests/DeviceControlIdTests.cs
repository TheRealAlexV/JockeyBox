namespace JockeyBox.Dsp.Tests;

/// <summary>
/// <see cref="DeviceControlId"/> is an API contract: control ids end up in saved settings, URLs
/// and remote clients, so the slug shape and one known mapping are pinned here.
/// </summary>
public class DeviceControlIdTests
{
    private const string RealisticEndpointId =
        "{0.0.1.00000000}.{6b2f8e9c-1a2b-4c3d-8e4f-5a6b7c8d9e0f}";

    [Fact]
    public void Slug_is_pinned_for_a_known_endpoint_id()
    {
        Assert.Equal("f22f2d874051f93a", DeviceControlId.Slug(RealisticEndpointId));
    }

    [Fact]
    public void Slug_is_url_safe_and_lowercase()
    {
        string[] endpoints =
        {
            RealisticEndpointId,
            "capture:usb-mic-1",
            "Mic (USB Audio)",
            "micro à accents/ü",
        };

        foreach (string endpoint in endpoints)
        {
            string slug = DeviceControlId.Slug(endpoint);

            Assert.Equal(DeviceControlId.SlugLength, slug.Length);
            foreach (char c in slug)
            {
                Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f', $"slug '{slug}' has a non-hex character '{c}'");
                Assert.DoesNotContain(c.ToString(), new[] { "{", "}", ".", " " });
                Assert.False(char.IsUpper(c));
            }

            Assert.Equal(slug, Uri.EscapeDataString(slug));
        }
    }

    [Fact]
    public void Slug_is_stable_across_calls()
    {
        Assert.Equal(DeviceControlId.Slug(RealisticEndpointId), DeviceControlId.Slug(RealisticEndpointId));
        Assert.Equal(DeviceControlId.Slug("capture:usb-mic-1"), DeviceControlId.Slug("capture:usb-mic-1"));
    }

    [Fact]
    public void Different_endpoints_get_different_slugs()
    {
        Assert.NotEqual(DeviceControlId.Slug("capture:usb-mic-1"), DeviceControlId.Slug("capture:usb-mic-2"));
    }

    [Fact]
    public void Control_id_shape_is_mic_slug_parameter()
    {
        string id = DeviceControlId.For(RealisticEndpointId, "gate.threshold");

        Assert.Equal($"mic.{DeviceControlId.Slug(RealisticEndpointId)}.gate.threshold", id);
        Assert.Equal("mic.f22f2d874051f93a.gate.threshold", id);
        Assert.DoesNotContain('{', id);
        Assert.DoesNotContain('}', id);
        Assert.DoesNotContain(' ', id);
        Assert.Equal(id, Uri.EscapeDataString(id));
    }

    [Fact]
    public void Missing_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => DeviceControlId.Slug(" "));
        Assert.Throws<ArgumentException>(() => DeviceControlId.For(RealisticEndpointId, ""));
    }
}
