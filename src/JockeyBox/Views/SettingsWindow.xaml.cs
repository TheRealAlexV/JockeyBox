using System.Windows;
using JockeyBox.Lyrics;
using JockeyBox.Separation;
using JockeyBox.Settings;

namespace JockeyBox.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] FormatPresets = { "FLAC", "MP3", "WAV" };

    private readonly AppSettings _settings;
    private readonly SpotifyOAuthService _oauth;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _oauth = new SpotifyOAuthService(settings);

        MusicDirBox.Text = settings.MusicDirectory;
        KaraokeDirBox.Text = settings.KaraokeDirectory;

        foreach (var f in FormatPresets) FormatBox.Items.Add(f);
        FormatBox.SelectedItem = FormatPresets.Contains(settings.SeparationOutputFormat)
            ? settings.SeparationOutputFormat : "FLAC";
        PythonBox.Text = settings.PythonPath;
        GpuCheck.IsChecked = settings.SeparationUseGpu;
        UpdateEngineState();

        ApiUrlBox.Text = settings.LyricsApiBaseUrl;
        OffsetBox.Text = settings.LyricsOffsetMs.ToString("F0");
        UpdateSpotifyStatus();
    }

    private void UpdateEngineState() =>
        EngineStateText.Text = SeparationEngine.IsInstalled
            ? "\u2713 engine installed" : "engine not installed yet";

    private void UpdateSpotifyStatus()
    {
        if (_oauth.IsLoggedIn)
            SpotifyStatus.Text = "\u2713 Logged in to Spotify";
        else if (!string.IsNullOrWhiteSpace(_settings.SpotifyClientId))
            SpotifyStatus.Text = "\u2713 Using client credentials (legacy)";
        else
            SpotifyStatus.Text = "Not logged in";
    }

    private async void OnSpotifyLoginClick(object sender, RoutedEventArgs e)
    {
        SpotifyLoginButton.IsEnabled = false;
        SpotifyStatus.Text = "Opening browser for login\u2026";

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await _oauth.LoginAsync(cts.Token);
            UpdateSpotifyStatus();
        }
        catch (OperationCanceledException)
        {
            SpotifyStatus.Text = "Login timed out or was cancelled";
        }
        catch (Exception ex)
        {
            SpotifyStatus.Text = $"Login failed: {ex.Message}";
        }
        finally
        {
            SpotifyLoginButton.IsEnabled = true;
        }
    }

    private void OnBrowseMusic(object sender, RoutedEventArgs e) => BrowseInto(MusicDirBox, "Select your Music folder (originals with vocals)");

    private void OnBrowseKaraoke(object sender, RoutedEventArgs e) => BrowseInto(KaraokeDirBox, "Select the Karaoke folder (generated instrumentals)");

    private void BrowseInto(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void OnInstallEngine(object sender, RoutedEventArgs e)
    {
        _settings.PythonPath = PythonBox.Text.Trim();
        _settings.SeparationUseGpu = GpuCheck.IsChecked == true;
        _settings.Save();

        var setup = new SetupWindow(_settings) { Owner = this };
        setup.ShowDialog();
        UpdateEngineState();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var apiUrl = ApiUrlBox.Text.Trim();
        if (apiUrl.Length > 0
            && (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this,
                "Lyrics API base URL must be a full http:// or https:// address (e.g. http://10.0.0.5:8091).",
                "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var music = MusicDirBox.Text.Trim();
        var karaoke = KaraokeDirBox.Text.Trim();
        if (music.Length > 0 && karaoke.Length > 0
            && string.Equals(System.IO.Path.GetFullPath(music), System.IO.Path.GetFullPath(karaoke),
                             StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Music and Karaoke folders must be different directories.",
                "Invalid folders", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MusicDirectory = music;
        _settings.KaraokeDirectory = karaoke;
        _settings.SeparationOutputFormat = (FormatBox.SelectedItem as string) ?? "FLAC";
        _settings.PythonPath = PythonBox.Text.Trim();
        _settings.SeparationUseGpu = GpuCheck.IsChecked == true;
        _settings.LyricsApiBaseUrl = apiUrl;
        if (double.TryParse(OffsetBox.Text, out var offset))
            _settings.LyricsOffsetMs = Math.Clamp(offset, -10000, 10000);
        _settings.Save();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
