using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using JockeyBox.Tools;
using JockeyBox.YouTube;

namespace JockeyBox.Views;

/// <summary>
/// Setup and status for YouTube playlist writes:
///   1. enable the YouTube Data API v3 in a Google Cloud project,
///   2. create an OAuth client of type "Desktop app",
///   3. paste its client id + secret and run the loopback sign-in.
///
/// Google's installed-app flow needs no registered redirect URI: the app opens
/// a listener on a free 127.0.0.1 port and hands Google that exact address, and
/// Desktop clients accept loopback on any port. Credentials and tokens live in
/// %APPDATA%\JockeyBox\youtube-oauth.json, never in AppSettings.
/// </summary>
public partial class YouTubeAuthDialog : Window
{
    public const string ConsoleUrl =
        "https://console.cloud.google.com/apis/library/youtube.googleapis.com";
    public const string CredentialsUrl = "https://console.cloud.google.com/apis/credentials";

    private readonly GoogleOAuthService _auth;
    private readonly YouTubeDataApi _api;
    private bool _busy;

    public YouTubeAuthDialog(GoogleOAuthService auth, YouTubeDataApi api)
    {
        _auth = auth;
        _api = api;
        ClientId = auth.ClientId;
        ClientSecret = auth.ClientSecret;
        InitializeComponent();
        DataContext = this;

        _auth.Changed += OnAuthChanged;
        Loaded += (_, _) =>
        {
            RefreshStatus();
            if (ClientId.Length == 0) ClientIdBox.Focus();
            else if (ClientSecret.Length == 0) ClientSecretBox.Focus();
        };
        Closed += (_, _) => _auth.Changed -= OnAuthChanged;
    }

    /// <summary>Two-way bound to the client id box; committed on Connect (or Disconnect).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Two-way bound to the client secret box. Plain text: it is the user's own app secret.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>True when the dialog closes with a working, channel-resolved connection.</summary>
    public bool IsConnected { get; private set; }

    public static void OpenConsole()
    {
        OpenUrl(ConsoleUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start("xdg-open", url);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {url}: {ex.Message}");
        }
    }

    private void OnOpenConsole(object sender, RoutedEventArgs e) => OpenConsole();

    private void OnAuthChanged() => Dispatcher.BeginInvoke(RefreshStatus);

    private void RefreshStatus()
    {
        var channel = _auth.ChannelTitle;
        if (_auth.IsConnected)
        {
            var name = string.IsNullOrWhiteSpace(channel) ? "(channel not named yet)" : channel;
            SetStatus($"Connected to {name}. Playlist writes are available.", isError: false);
            ConnectButton.Content = "Reconnect";
        }
        else
        {
            SetStatus(ClientId.Trim().Length == 0
                ? "Not connected. Paste your Desktop OAuth client id and secret."
                : "Not connected. Press Connect to sign in with Google.", isError: false);
            ConnectButton.Content = "Connect";
        }

        DisconnectButton.IsEnabled = !_busy && _auth.IsConnected;
        IsConnected = _auth.IsConnected;
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var clientId = (ClientId ?? "").Trim();
        var clientSecret = (ClientSecret ?? "").Trim();

        if (clientId.Length == 0)
        {
            SetStatus("Paste the OAuth client id from your Google Cloud project.", isError: true);
            ClientIdBox.Focus();
            return;
        }

        if (clientSecret.Length == 0)
        {
            SetStatus("Paste the OAuth client secret from your Google Cloud project.", isError: true);
            ClientSecretBox.Focus();
            return;
        }

        ClientId = clientId;
        ClientSecret = clientSecret;
        _auth.SaveCredentials(clientId, clientSecret);

        SetBusy(true);
        SetStatus("Opening Google in your browser. Approve access, then come back here.", isError: false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _auth.AuthorizeAsync(msg => Dispatcher.BeginInvoke(() => SetStatus(msg, isError: false)), cts.Token);

            // Proving the token works and naming the channel also tells the tab
            // which account writes will land on.
            SetStatus("Checking the connected channel\u2026", isError: false);
            var channel = await _api.GetMyChannelAsync(cts.Token);
            _auth.SetChannel(channel.Id, channel.Title);

            SetStatus($"Connected to {channel.Title}. Playlist writes are available.", isError: false);
            IsConnected = true;
            CloseWithResult(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Sign-in timed out or was cancelled.", isError: true);
        }
        catch (Exception ex)
        {
            // Google's own wording, verbatim, so the user can act on it.
            Log.Warn($"YouTube sign-in failed: {ex.Message}");
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
            RefreshStatusButtons();
        }
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        _auth.Disconnect();
        SetStatus("Disconnected. The saved client id and secret are kept, so Connect only needs a click.",
            isError: false);
        RefreshStatusButtons();
    }

    private void RefreshStatusButtons()
    {
        DisconnectButton.IsEnabled = !_busy && _auth.IsConnected;
        ConnectButton.Content = _auth.IsConnected ? "Reconnect" : "Connect";
    }

    private void SetStatus(string message, bool isError)
    {
        StatusLine.Text = message;
        StatusLine.Foreground = (Brush)FindResource(isError ? "AccentBBrush" : "TextDimBrush");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConnectButton.IsEnabled = !busy;
        DisconnectButton.IsEnabled = !busy && _auth.IsConnected;
        OpenConsoleButton.IsEnabled = !busy;
        ClientIdBox.IsEnabled = !busy;
        ClientSecretBox.IsEnabled = !busy;
    }

    private void CloseWithResult(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}
