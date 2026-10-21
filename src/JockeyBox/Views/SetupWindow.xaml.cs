using System.Windows;
using JockeyBox.Separation;
using JockeyBox.Settings;

namespace JockeyBox.Views;

/// <summary>
/// Modal window that installs the vocal removal engine (Python venv +
/// audio-separator) while streaming the installer output. DialogResult is
/// true when the engine installed successfully.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;
    private bool _succeeded;

    public SetupWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += async (_, _) => await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        _running = true;
        IProgress<string> log = new Progress<string>(line =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogScroll.ScrollToBottom();
        });

        try
        {
            // Step 0: make sure a usable Python exists; offer to install one.
            var python = await SeparationEngine.DetectPythonAsync(
                _settings.PythonPath, log, _cts.Token);
            if (python == null)
            {
                PhaseText.Text = "Python 3.10-3.13 was not found on this PC.";
                var answer = MessageBox.Show(this,
                    "The vocal removal engine needs Python 3.10-3.13, which was not " +
                    "found on this PC.\n\nInstall Python 3.12 automatically now?\n\n" +
                    "(Uses winget when available, otherwise the official python.org " +
                    "installer - per-user, no admin rights needed.)",
                    "Install Python?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    _running = false;
                    PhaseText.Text = "Setup cancelled: Python is required. Install it from " +
                                     "python.org (3.10-3.13) or set a python.exe path in Settings.";
                    Spinner.IsIndeterminate = false;
                    CloseButton.Content = "Close";
                    return;
                }

                PhaseText.Text = "Installing Python 3.12\u2026";
                python = await SeparationEngine.InstallPythonAsync(log, _cts.Token);
                log.Report($"Python ready: {python}");

                // Remember the concrete path - our process PATH is stale, so
                // future engine runs must not depend on PATH resolution.
                _settings.PythonPath = python;
                _settings.Save();
            }

            PhaseText.Text = "Installing the separation engine\u2026";
            await SeparationEngine.InstallAsync(
                python, _settings.SeparationUseGpu, log, _cts.Token);

            _running = false;
            _succeeded = true;
            PhaseText.Text = "Engine installed. You can now generate karaoke tracks.";
            Spinner.IsIndeterminate = false;
            Spinner.Value = 100;
            CloseButton.Content = "Done";
        }
        catch (OperationCanceledException)
        {
            _running = false;
            if (IsVisible) Close(); // may already be gone if the user hit X
        }
        catch (Exception ex)
        {
            _running = false;
            PhaseText.Text = $"Install failed: {ex.Message}";
            Spinner.IsIndeterminate = false;
            CloseButton.Content = "Close";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts.Cancel(); // install task closes the window when it unwinds
            return;
        }
        DialogResult = _succeeded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }
}
