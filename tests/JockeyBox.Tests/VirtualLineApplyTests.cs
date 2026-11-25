using System;
using System.Linq;
using System.Threading;
using System.Windows;
using JockeyBox.Audio.Graph;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// a124: ticking a line in the virtual-lines panel has to open the ring there and
/// then. The handler used to write the flag and call Save, and nothing rebuilt the
/// route, so the audio only started at the next unrelated Settings save - a
/// checkbox that changed a file and nothing else.
///
/// <para>The panel is the thing under test, not the view model method: the flag
/// write is the half that always worked, and a test of <c>ApplyVirtualLineSettings</c>
/// alone would pass without the call the checkbox makes.</para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class VirtualLineApplyTests
{
    [Fact]
    public void Ticking_deck_A_on_the_panel_rebuilds_the_route_without_a_settings_save()
    {
        var vm = UiThread.Run(() => new MainViewModel());
        try
        {
            var panel = UiThread.Run(() =>
            {
                ThemeResources.Ensure();
                return new VirtualLinesPanel { DataContext = vm };
            });

            Assert.DoesNotContain(BusKind.DeckA, vm.Transport.Engine.Route.VirtualBuses);

            UiThread.Run(() => panel.VirtualLineDeckA.IsChecked = true);

            // The transport applies the rebuilt route on a pool thread and the call
            // is fire-and-forget, so the assertion waits rather than races it.
            Assert.True(WaitFor(() => vm.Transport.Engine.Route.VirtualBuses.Contains(BusKind.DeckA)),
                        "the checkbox saved the flag and nothing rebuilt the route: " +
                        $"route buses = [{string.Join(", ", vm.Transport.Engine.Route.VirtualBuses)}]");
        }
        finally
        {
            UiThread.Run(() => vm.Dispose());
        }
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds = 10_000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (UiThread.Run(condition)) return true;
            Thread.Sleep(10);
        }

        return UiThread.Run(condition);
    }

    /// <summary>
    /// The panel's XAML names Theme brushes by key, so the test thread needs the
    /// app-scope dictionary the real <c>App</c> loads. <see cref="UiThread"/> keeps a
    /// bare Application, which has none.
    /// </summary>
    private static class ThemeResources
    {
        private static bool _loaded;

        public static void Ensure()
        {
            if (_loaded) return;
            _loaded = true;

            var app = Application.Current;
            if (app == null) return;

            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/JockeyBox;component/Themes/Dark.xaml"),
            });
        }
    }
}
