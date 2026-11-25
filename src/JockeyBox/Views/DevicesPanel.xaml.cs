namespace JockeyBox.Views;

/// <summary>
/// The microphone input and the monitor output: the two device choices that are
/// not per-deck. Both lists come from the view model, which is the same source the
/// Settings dialog writes, so the panel needs no handlers of its own.
/// </summary>
public partial class DevicesPanel
{
    public DevicesPanel() => InitializeComponent();
}
