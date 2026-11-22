using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using JockeyBox.Settings;
using JockeyBox.Views;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The settings dialog's microphone list needs no hardware: saved device ids render
/// as rows even when nothing is plugged in, which is the seam this uses to reach a
/// list longer than the dialog is tall.
///
/// <para><b>What this cannot prove.</b> All twelve rows are "Saved device (not
/// connected)"; no capture endpoint is opened. What is proven is that every row
/// present keeps its full height, that the panel is not clamped, and that the page
/// scroll brings the last row into view - the three things the old 201 px cap and
/// its dead "more" chevron broke.</para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class MicListTests
{
    [Fact]
    public void TwelveSavedDevicesAllGetAFullRowAndThePageReachesTheLastOne()
    {
        var settings = new AppSettings();
        for (int i = 1; i <= 12; i++)
            settings.MicDevices.Add(new AppSettings.MicDeviceSettings { DeviceId = $"saved-mic-{i:00}", Volume = 1f });

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

            var window = new SettingsWindow(settings);
            try
            {
                // A Window only lays its content out once it has an HWND, so it is
                // shown off-screen rather than measured by hand.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Show();
                window.UpdateLayout();

                var panel = (StackPanel)window.FindName("MicListPanel")!;
                // Device rows are tagged by BuildMicRow. The panel's other children are
                // captions and the ASIO section's gap, and that gap is a Border as well,
                // so "every Border child" would count 6 px of padding as a device row.
                var rows = panel.Children.OfType<Border>()
                    .Where(b => Equals(b.Tag, SettingsWindow.MicRowTag)).ToList();

                Assert.True(rows.Count >= 12, $"expected at least 12 device rows, got {rows.Count}");
                Assert.All(rows, row => Assert.True(row.ActualHeight > 18,
                    $"a device row collapsed to {row.ActualHeight:0.#} px"));

                // The panel is exactly as tall as its rows, so no cap is cutting one.
                // DesiredSize already carries each child's margin; adding it again
                // counted every row three pixels short.
                double content = panel.Children.Cast<FrameworkElement>()
                    .Sum(c => c.DesiredSize.Height);
                Assert.True(Math.Abs(panel.ActualHeight - content) <= 1,
                    $"panel {panel.ActualHeight:0.#} vs children {content:0.#} (rows {rows.Count})");

                // The old affordance is gone, not restyled: no decorative overlay and
                // no second scroll region competing with the page's own bar.
                Assert.Null(window.FindName("MicListFade"));
                Assert.Null(window.FindName("MicListScroll"));

                // The last row is reachable, which is what a Tab to it does: focus
                // brings it into view inside the page scroll.
                var page = (ScrollViewer)window.FindName("PageScroll")!;
                var last = rows[^1];
                last.BringIntoView();
                window.UpdateLayout();

                Assert.True(page.VerticalOffset > 0, "the page did not scroll to the last device");
                double top = last.TransformToAncestor(page).Transform(new Point(0, 0)).Y;
                Assert.InRange(top, 0, Math.Max(0, page.ViewportHeight - 1));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
