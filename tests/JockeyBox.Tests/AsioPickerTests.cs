using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JockeyBox.Dsp;
using JockeyBox.Settings;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The ASIO source picker, driven through the real settings dialog with a fixed driver
/// list standing in for hardware.
///
/// <para><b>The bug this pins.</b> <c>CollectMicDevices</c> rebuilt every
/// <see cref="AppSettings.MicDeviceSettings"/> from the rows and left
/// <c>Mode</c>/<c>Channel</c> off, so every save reset a stereo pair to the Mono
/// default. These tests compose a pair through the picker, read back exactly what Save
/// would write, then load that list into a second window and read it back again: the
/// mode has to survive both directions.</para>
///
/// <para>The driver answers with the six inputs a Focusrite 4i4 reports, including the
/// two loopbacks, so the labels come from the driver and not from an index.</para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class AsioPickerTests
{
    private const string Focusrite = "Focusrite USB ASIO";

    private static AsioDriverInfo SixInputFocusrite() => new(
        Focusrite,
        6,
        new[] { "Input 1", "Input 2", "Input 3", "Input 4", "Loopback 1", "Loopback 2" },
        AsioDriverState.Ready,
        null);

    [Fact]
    public void StereoPairModeSurvivesSaveAndReload()
    {
        var first = new AppSettings();
        List<AppSettings.MicDeviceSettings> saved = null!;

        WithWindow(first, new[] { SixInputFocusrite() }, (window, _) =>
        {
            window.AddAsioSourceForTest(Focusrite, 2, AsioChannelMode.StereoPair);
            saved = window.CollectMicDevicesForTest();
        });

        var written = Assert.Single(saved);
        Assert.Equal("asio:Focusrite USB ASIO#pair2", written.DeviceId);
        Assert.Equal(2, written.Channel);
        Assert.Equal(AsioChannelMode.StereoPair, written.Mode);

        // Reload: what Save wrote goes back in as a saved device and comes out again.
        var reloaded = new AppSettings();
        reloaded.MicDevices.AddRange(saved);
        List<AppSettings.MicDeviceSettings> again = null!;

        WithWindow(reloaded, new[] { SixInputFocusrite() }, (window, _) =>
            again = window.CollectMicDevicesForTest());

        var roundTripped = Assert.Single(again);
        Assert.Equal(AsioChannelMode.StereoPair, roundTripped.Mode);
        Assert.Equal(2, roundTripped.Channel);
    }

    [Fact]
    public void MonoTapAtALoopbackUsesTheDriversOwnName()
    {
        var settings = new AppSettings();

        WithWindow(settings, new[] { SixInputFocusrite() }, (window, panel) =>
        {
            // Offset 4 is the driver's "Loopback 1": a real source, offered as a mono tap.
            window.AddAsioSourceForTest(Focusrite, 4, AsioChannelMode.Mono);

            var texts = Texts(panel);
            Assert.Contains(texts, t => t.Contains("Loopback 1"));
            Assert.DoesNotContain(texts, t => t.Contains("Input 5"));

            var saved = Assert.Single(window.CollectMicDevicesForTest());
            Assert.Equal("asio:Focusrite USB ASIO#ch4", saved.DeviceId);
            Assert.Equal(AsioChannelMode.Mono, saved.Mode);
            Assert.Equal(4, saved.Channel);
        });
    }

    [Fact]
    public void TheDriverLineReportsItsRealChannelCount()
    {
        WithWindow(new AppSettings(), new[] { SixInputFocusrite() }, (_, panel) =>
            Assert.Contains(Texts(panel), t => t.Contains("Focusrite USB ASIO") && t.Contains("6 inputs")));
    }

    [Fact]
    public void NoDriversReadsInWordsNotABlankBox()
    {
        WithWindow(new AppSettings(), Array.Empty<AsioDriverInfo>(), (_, panel) =>
        {
            Assert.Contains(Texts(panel), t => t.Contains("No ASIO drivers installed"));
        });
    }

    [Fact]
    public void ADriverHeldByAnotherAppIsAStateNotAnError()
    {
        var held = new AsioDriverInfo(Focusrite, 0, Array.Empty<string>(), AsioDriverState.InUse,
            "already open");

        WithWindow(new AppSettings(), new[] { held }, (_, panel) =>
        {
            Assert.Contains(Texts(panel), t => t.Contains("in use by another application"));
            // No composer for a driver it could not read: no dead controls.
            Assert.Empty(Descendants(panel).OfType<ComboBox>());
        });
    }

    private static void WithWindow(AppSettings settings, IReadOnlyList<AsioDriverInfo> drivers,
                                   Action<SettingsWindow, StackPanel> body)
    {
        UiThread.Run(() =>
        {
            // The dialog's XAML names brushes from Themes/Dark.xaml, which the test
            // host's bare Application does not merge the way App.xaml does.
            var app = Application.Current!;
            if (app.Resources.MergedDictionaries.Count == 0)
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/JockeyBox;component/Themes/Dark.xaml", UriKind.Relative),
                });

            var window = new SettingsWindow(settings, asioDrivers: () => drivers)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
            };
            window.Show();
            window.UpdateLayout();
            try
            {
                var panel = (StackPanel)window.FindName("MicListPanel")!;
                body(window, panel);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<string> Texts(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grand in Descendants(child)) yield return grand;
        }
    }
}
