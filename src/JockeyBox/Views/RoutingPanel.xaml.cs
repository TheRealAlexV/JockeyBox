namespace JockeyBox.Views;

/// <summary>
/// The routing matrix: which device each deck, the soundboard and the monitor play
/// through. Four bound combos and nothing else, so there is no code here - the
/// view model owns both the device lists and the selection, and the same values
/// are what the deck cards and the Settings dialog read.
/// </summary>
public partial class RoutingPanel
{
    public RoutingPanel() => InitializeComponent();
}
