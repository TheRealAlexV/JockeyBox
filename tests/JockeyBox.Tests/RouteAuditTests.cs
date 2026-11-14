using System.Collections.Generic;
using System.Linq;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The router has to say two things out loud, because both failures are silent
/// otherwise: a stored device that no longer exists, and an output that is not the
/// Windows default. The second matters most when the output is a virtual DSP
/// driver (FxSound, VB-Audio), because that is the shape of "healthy peaks, no
/// sound".
///
/// <para>These run against endpoint facts, not devices, so they are stable on a
/// machine with no audio hardware at all.</para>
/// </summary>
public class RouteAuditTests
{
    private const string Realtek = "{0.0.0.00000000}.{b120335b-db56-4c51-9757-f11e6f84819f}";
    private const string FxSound = "{0.0.0.00000000}.{f62063e4-a554-4227-93fd-7f42109ee5ed}";
    private const string Focusrite = "{0.0.0.00000000}.{08f5d71c-b5f6-4694-973a-db0d2a44c614}";

    private static RouteEndpoint Endpoint(string id, string name, bool present, bool isDefault) =>
        new(id, name, present, isDefault);

    private static readonly RouteEndpoint RealtekDefault =
        Endpoint(Realtek, "Speakers (Realtek(R) Audio)", present: true, isDefault: true);

    private static readonly RouteEndpoint FxSoundEndpoint =
        Endpoint(FxSound, "Speakers (FxSound Audio Enhancer)", present: true, isDefault: false);

    private static readonly RouteEndpoint FocusriteEndpoint =
        Endpoint(Focusrite, "Playback 5 + 6 (3- Focusrite USB Audio)", present: true, isDefault: false);

    [Fact]
    public void Absent_configured_device_falls_back_to_the_default_and_says_so()
    {
        var route = new AudioRoute(DeckADeviceId: "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}");

        var check = RouteAudit.Check(route, new[] { RealtekDefault, FxSoundEndpoint }, Realtek);

        Assert.Equal(Realtek, check.Route.DeckADeviceId);
        var notice = Assert.Single(check.Notices);
        Assert.True(notice.IsWarning);
        Assert.Contains("audio route: deckA fell back to default 'Speakers (Realtek(R) Audio)'", notice.Message);
        Assert.Contains("is not on this machine", notice.Message);
        // The fallback must not be mistaken for the mismatch line.
        Assert.DoesNotContain("is not the Windows default", notice.Message);
    }

    [Fact]
    public void Unplugged_configured_device_falls_back_to_the_default_and_says_so()
    {
        var gone = Endpoint(FxSound, "Speakers (FxSound Audio Enhancer)", present: false, isDefault: false);
        var route = new AudioRoute(DeckADeviceId: FxSound);

        var check = RouteAudit.Check(route, new[] { RealtekDefault, gone }, Realtek);

        Assert.Equal(Realtek, check.Route.DeckADeviceId);
        var notice = Assert.Single(check.Notices);
        Assert.True(notice.IsWarning);
        Assert.Contains("fell back to default 'Speakers (Realtek(R) Audio)'", notice.Message);
        Assert.Contains("but not present", notice.Message);
    }

    [Fact]
    public void Configured_device_that_is_the_default_is_left_alone_and_logs_nothing()
    {
        var route = new AudioRoute(DeckADeviceId: Realtek, MonitorDeviceId: Realtek);

        var check = RouteAudit.Check(route, new[] { RealtekDefault }, Realtek);

        Assert.Equal(Realtek, check.Route.DeckADeviceId);
        Assert.Equal(Realtek, check.Route.MonitorDeviceId);
        Assert.Empty(check.Notices);
        Assert.False(check.AnyWarning);
    }

    [Fact]
    public void Virtual_output_that_is_not_the_default_warns_once_for_the_whole_route()
    {
        var route = new AudioRoute(
            DeckADeviceId: FxSound,
            DeckBDeviceId: FxSound,
            MonitorDeviceId: Realtek);

        var check = RouteAudit.Check(route, new[] { RealtekDefault, FxSoundEndpoint }, Realtek);

        var notice = Assert.Single(check.Notices);
        Assert.True(notice.IsWarning);
        Assert.Contains("[virtual/DSP", notice.Message);
        Assert.Contains("deckA+deckB on 'Speakers (FxSound Audio Enhancer)'", notice.Message);
        Assert.Contains("is not the Windows default 'Speakers (Realtek(R) Audio)'", notice.Message);
        // Two buses on one device must name that device once, not twice.
        Assert.Equal(1, notice.Message.Split("FxSound Audio Enhancer").Length - 1);
    }

    [Fact]
    public void Several_outputs_off_the_default_are_named_in_one_line()
    {
        var route = new AudioRoute(DeckADeviceId: FxSound, MonitorDeviceId: Focusrite);

        var check = RouteAudit.Check(
            route, new[] { RealtekDefault, FxSoundEndpoint, FocusriteEndpoint }, Realtek);

        var notice = Assert.Single(check.Notices);
        Assert.True(notice.IsWarning, "one virtual output anywhere makes the line a warning");
        Assert.Contains("2 outputs are not the Windows default", notice.Message);
        Assert.Contains("deckA on 'Speakers (FxSound Audio Enhancer)'", notice.Message);
        Assert.Contains("monitor on 'Playback 5 + 6", notice.Message);
    }

    [Fact]
    public void Physical_output_that_is_not_the_default_is_information_not_a_warning()
    {
        var route = new AudioRoute(DeckADeviceId: Focusrite);

        var check = RouteAudit.Check(route, new[] { RealtekDefault, FocusriteEndpoint }, Realtek);

        var notice = Assert.Single(check.Notices);
        Assert.False(notice.IsWarning);
        Assert.Contains("[physical]", notice.Message);
        Assert.Contains("is not the Windows default", notice.Message);
    }

    [Fact]
    public void Bus_with_no_device_is_untouched()
    {
        var route = new AudioRoute(DeckADeviceId: Realtek, DeckBDeviceId: null);

        var check = RouteAudit.Check(route, new[] { RealtekDefault }, Realtek);

        Assert.Null(check.Route.DeckBDeviceId);
        Assert.Empty(check.Notices);
    }

    [Fact]
    public void Absent_device_with_no_default_still_reports_and_leaves_the_route_alone()
    {
        var route = new AudioRoute(DeckADeviceId: FxSound);

        var check = RouteAudit.Check(route, new[] { FxSoundEndpoint }, defaultId: null);

        Assert.Equal(FxSound, check.Route.DeckADeviceId);
        Assert.NotEmpty(check.Notices);
        Assert.All(check.Notices, n => Assert.True(n.IsWarning));
    }

    [Fact]
    public void Classifier_separates_dsp_drivers_from_physical_devices()
    {
        Assert.True(Endpoint(FxSound, "Speakers (FxSound Audio Enhancer)", true, false).IsVirtual);
        Assert.True(Endpoint("x", "VBMatrix In 3 (VB-Audio Matrix VAIO)", true, false).IsVirtual);
        Assert.False(Endpoint(Realtek, "Speakers (Realtek(R) Audio)", true, false).IsVirtual);
        Assert.False(Endpoint(Focusrite, "Playback 5 + 6 (3- Focusrite USB Audio)", true, false).IsVirtual);
    }
}
