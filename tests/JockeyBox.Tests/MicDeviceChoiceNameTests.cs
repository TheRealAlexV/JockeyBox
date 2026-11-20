using System.Collections.Generic;
using JockeyBox.Audio;
using JockeyBox.ViewModels;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The microphone strip's dropdown text. In the headless sandbox the mic chooser
/// rendered two rows with no text at all while the monitor chooser beside it showed
/// "Speakers (FxSound Audio Enhancer) (Default)".
///
/// <para><b>Why the mic rows were blank.</b> Their text came from
/// <c>MicStrip.DeviceName</c>, which <c>MicCapture</c> only fills in once the
/// endpoint actually opens, so a saved microphone that is not connected (or not
/// open yet) yielded "". The strip is in the mixer list, the capture never started,
/// and nothing in the row builder asked the endpoint itself - the monitor combo was
/// right only because it is fed by the live enumeration.</para>
///
/// <para>The chain these tests lock down: the live endpoint's name, then the open
/// strip's, then the id, so a row can never render as blank.</para>
/// </summary>
public sealed class MicDeviceChoiceNameTests
{
    private const string Mic = "{0.0.1.00000000}.{1568944e-e3eb-4519-9903-7aa57b93efdc}";
    private const string Other = "{0.0.1.00000000}.{86b24b7f-9810-4668-9db4-98748806089e}";

    [Fact]
    public void A_saved_mic_that_is_not_connected_still_reads_as_something()
    {
        // The sandbox case: the device is enabled in settings so it has a strip,
        // the capture never opened so that strip has no name, and the endpoint is
        // not in the live list either.
        string name = MainViewModel.MicChoiceName(Mic, new List<AudioDeviceInfo>(), "");

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(Mic, name);
    }

    [Fact]
    public void The_live_endpoint_name_wins_over_the_open_strip()
    {
        var live = new List<AudioDeviceInfo> { new(Mic, "Microphone (NVIDIA Broadcast)") };

        string name = MainViewModel.MicChoiceName(Mic, live, "an older name");

        Assert.Equal("Microphone (NVIDIA Broadcast)", name);
    }

    [Fact]
    public void A_live_endpoint_that_reports_no_name_does_not_blank_the_row()
    {
        // The other candidate cause: a host whose endpoints report a blank
        // FriendlyName. The row must still say something.
        var live = new List<AudioDeviceInfo> { new(Mic, "  ") };

        string name = MainViewModel.MicChoiceName(Mic, live, "Microphone (USB Audio)");

        Assert.Equal("Microphone (USB Audio)", name);
    }

    [Fact]
    public void The_open_strip_name_is_used_when_the_endpoint_is_not_live()
    {
        string name = MainViewModel.MicChoiceName(Mic, new List<AudioDeviceInfo>(), "Analogue 1 + 2");

        Assert.Equal("Analogue 1 + 2", name);
    }

    [Fact]
    public void Another_devices_name_is_never_borrowed()
    {
        var live = new List<AudioDeviceInfo> { new(Other, "Line In (SB Omni Surround 5.1)") };

        string name = MainViewModel.MicChoiceName(Mic, live, "Analogue 1 + 2");

        Assert.Equal("Analogue 1 + 2", name);
    }
}
