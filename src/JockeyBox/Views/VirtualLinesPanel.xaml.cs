using System.Windows;
using System.Windows.Controls;
using JockeyBox.Settings;
using JockeyBox.ViewModels;

namespace JockeyBox.Views;

/// <summary>
/// The five lines the native ASIO driver publishes, and the ring's name.
///
/// <para><b>Why the handlers live here.</b> Every one of them reads a named check
/// box or the name box, so none of them can stay on <c>MainWindow</c>: a named
/// element inside a detached root is not reachable from the window's code-behind,
/// and a pop-out would have thrown <c>NameScope</c> errors on first show. They moved
/// with the checkboxes, the same way the library tree's handlers moved with the
/// tree.</para>
///
/// <para><b>Why one line at a time.</b> The ring has one section, so ticking a second
/// line releases the first instead of refusing the change in a dialog, which is not
/// something to be doing mid-show.</para>
/// </summary>
public partial class VirtualLinesPanel
{
    private bool _loading;

    public VirtualLinesPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e) => LoadLines();

    private void LoadLines()
    {
        if (Vm is not { } vm) return;

        var settings = vm.SettingsRef;
        _loading = true;
        try
        {
            VirtualLineDeckA.IsChecked = settings.VirtualAsioDeckA;
            VirtualLineDeckB.IsChecked = settings.VirtualAsioDeckB;
            VirtualLineRecording.IsChecked = settings.VirtualAsioRecording;
            VirtualLineSoundboard.IsChecked = settings.VirtualAsioSoundboard;
            VirtualLineMonitor.IsChecked = settings.VirtualAsioMonitor;
            VirtualLineBusName.Text = settings.VirtualAsioBusName;
        }
        finally { _loading = false; }
    }

    private void OnVirtualLineChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Vm is not { } vm) return;

        var box = (CheckBox)sender;
        _loading = true;
        try
        {
            if (box.IsChecked == true)
            {
                foreach (var other in new[]
                         {
                             VirtualLineDeckA, VirtualLineDeckB, VirtualLineRecording,
                             VirtualLineSoundboard, VirtualLineMonitor,
                         })
                {
                    if (!ReferenceEquals(other, box)) other.IsChecked = false;
                }
            }

            var settings = vm.SettingsRef;
            settings.VirtualAsioDeckA = VirtualLineDeckA.IsChecked == true;
            settings.VirtualAsioDeckB = VirtualLineDeckB.IsChecked == true;
            settings.VirtualAsioRecording = VirtualLineRecording.IsChecked == true;
            settings.VirtualAsioSoundboard = VirtualLineSoundboard.IsChecked == true;
            settings.VirtualAsioMonitor = VirtualLineMonitor.IsChecked == true;
            settings.Save();
            // The ring's bus set is part of the six-bus route: saving the flag alone
            // leaves the audio closed until an unrelated Settings save rebuilds it
            // (a124). Adopt the file the way the dialog does.
            vm.ApplyVirtualLineSettings();
        }
        finally { _loading = false; }
    }

    /// <summary>
    /// The ring name is validated here with the same rule the save path uses, so a
    /// typo cannot make the native writer fail at route time, where the operator
    /// would only hear silence.
    /// </summary>
    private void OnVirtualLineNameChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Vm is not { } vm) return;

        var settings = vm.SettingsRef;
        string name = VirtualLineBusName.Text.Trim();
        if (name.Length == 0) name = "main";
        if (AppSettings.ValidateVirtualAsioBusName(name) is not null)
        {
            VirtualLineBusName.Text = settings.VirtualAsioBusName;
            return;
        }

        if (string.Equals(name, settings.VirtualAsioBusName, StringComparison.Ordinal)) return;
        settings.VirtualAsioBusName = name;
        VirtualLineBusName.Text = name;
        settings.Save();
    }
}
