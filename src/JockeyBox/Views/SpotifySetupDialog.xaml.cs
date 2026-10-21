using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Views;

/// <summary>
/// Three-step setup for a user-owned Spotify app:
///   1. create an app at developer.spotify.com/dashboard,
///   2. add the loopback redirect URI in the app's settings,
///   3. paste the app's Client ID and run the OAuth login.
///
/// Spotify's built-in web-player client id cannot be granted the playlist
/// scopes (<c>invalid_scope</c>), so this dialog is the only path to playlist
/// import. It takes the settings and the OAuth service from the caller, so both
/// Settings and the Spotify tab can open it without a service locator.
/// </summary>
public partial class SpotifySetupDialog : Window
{
    public const string DashboardUrl = "https://developer.spotify.com/dashboard";
    /// <summary>The Redirect URI to register; follows the configured loopback port.</summary>
    public static string RedirectUriFor(AppSettings settings) =>
        $"http://127.0.0.1:{(settings.SpotifyRedirectPort is > 0 and <= 65535 ? settings.SpotifyRedirectPort : 8888)}/callback";

    private readonly AppSettings _settings;
    private readonly SpotifyOAuthService _oauth;

    public SpotifySetupDialog(AppSettings settings, SpotifyOAuthService oauth)
    {
        _settings = settings;
        _oauth = oauth;
        ClientId = settings.SpotifyClientIdOverride;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            ClientIdBox.Focus();
            ClientIdBox.SelectAll();
        };
    }

    /// <summary>Two-way bound to the Client ID box; committed to settings on Connect.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Shown in step 2; the port comes from settings so it always matches the login.</summary>
    public string RedirectUri => RedirectUriFor(_settings);

    /// <summary>True when the login that just ran actually granted the playlist scopes.</summary>
    public bool PlaylistAccessGranted { get; private set; }

    /// <summary>Open developer.spotify.com/dashboard in the default browser.</summary>
    public static void OpenDeveloperDashboard()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true });
            else
                Process.Start("xdg-open", DashboardUrl);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {DashboardUrl}: {ex.Message}");
        }
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e) => OpenDeveloperDashboard();

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var clientId = (ClientId ?? "").Trim();
        if (clientId.Length == 0)
        {
            ShowStatus("Paste the Client ID from your Spotify app first.", isError: true);
            ClientIdBox.Focus();
            return;
        }

        ClientId = clientId;
        _settings.SpotifyClientIdOverride = clientId;
        _settings.Save();

        SetBusy(true);
        ShowStatus("Opening Spotify in your browser. Approve the playlist permissions, then return here.",
            isError: false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await _oauth.LoginAsync(includePlaylists: true, cts.Token);

            PlaylistAccessGranted = _oauth.HasPlaylistScopes;
            if (PlaylistAccessGranted)
            {
                CloseWithResult(true);
                return;
            }

            ShowStatus("Connected, but Spotify did not grant playlist access. Check the redirect URI " +
                       "in the app's settings, then press Connect again.", isError: true);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Login timed out or was cancelled.", isError: true);
        }
        catch (Exception ex)
        {
            // Verbatim error (invalid_scope, invalid_client, HTTP status) so the user can act on it.
            Log.Warn($"Spotify setup login failed: {ex.Message}");
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusLine.Text = message;
        StatusLine.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "AccentBBrush" : "TextDimBrush");
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        OpenDashboardButton.IsEnabled = !busy;
        ClientIdBox.IsEnabled = !busy;
    }

    private void CloseWithResult(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}
