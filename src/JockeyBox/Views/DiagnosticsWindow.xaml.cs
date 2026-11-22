using System;
using System.Windows;
using System.Windows.Threading;
using JockeyBox.Audio.Graph;
using JockeyBox.Settings;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The diagnostics surface: reads the engine the app is already running and shows it.
///
/// <para>It owns no audio. There is no start, stop, device open or selftest here; the
/// ticker only re-reads values that the render thread has already produced, and the
/// REFRESH button reads the same values once. Closing it cannot change what an audience
/// hears.</para>
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsViewModel _vm;
    private readonly DispatcherTimer _ticker;

    public DiagnosticsWindow(AudioEngine engine, AppSettings settings)
    {
        InitializeComponent();

        _vm = new DiagnosticsViewModel(engine, settings);
        DataContext = _vm;

        // Peaks and frame counts move continuously; everything else is static. A slow
        // tick keeps the readout live without competing with the render thread, which
        // is never waited on.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _ticker.Tick += (_, _) => _vm.Refresh();
        Loaded += (_, _) => _ticker.Start();
        Closed += (_, _) => _ticker.Stop();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _vm.Refresh();
}
