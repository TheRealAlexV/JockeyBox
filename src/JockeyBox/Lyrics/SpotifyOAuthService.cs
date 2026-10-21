using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JockeyBox.Settings;

namespace JockeyBox.Lyrics;

/// <summary>
/// Spotify PKCE OAuth flow for lyrics integration.
///
/// Flow:
///   1. Generate PKCE code verifier + SHA-256 challenge
///   2. Open browser to accounts.spotify.com/authorize
///   3. Listen on loopback for the redirect with the auth code
///   4. Exchange code → access + refresh tokens
///   5. Store tokens encrypted (DPAPI) in AppSettings
///   6. Auto-refresh when access token expires
///
/// Uses Spotify's public client ID — no app registration needed. The token
/// replaces the old client ID/secret model entirely.
/// </summary>
public class SpotifyOAuthService
{
    private const string ClientId = "65b25a7ad33747ff9bde6e2443a6f7f2"; // Spotify public web player client
    private const string RedirectUri = "http://127.0.0.1:5543/callback";
    private const int RedirectPort = 5543;

    private readonly AppSettings _settings;
    private readonly HttpClient _http;

    public SpotifyOAuthService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>True when we have a valid (or refreshable) OAuth token.</summary>
    public bool IsLoggedIn =>
        !string.IsNullOrWhiteSpace(_settings.SpotifyAccessToken)
        || !string.IsNullOrWhiteSpace(_settings.SpotifyRefreshToken);

    /// <summary>Get a valid access token, refreshing if needed.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.SpotifyAccessToken)
            && string.IsNullOrWhiteSpace(_settings.SpotifyRefreshToken))
            return null;

        // Try the current token first
        if (!string.IsNullOrWhiteSpace(_settings.SpotifyAccessToken)
            && DateTime.UtcNow < _settings.SpotifyTokenExpiry - TimeSpan.FromSeconds(60))
            return _settings.SpotifyAccessToken;

        // Refresh
        if (!string.IsNullOrWhiteSpace(_settings.SpotifyRefreshToken))
        {
            try
            {
                await RefreshTokenAsync(ct);
                return _settings.SpotifyAccessToken;
            }
            catch
            {
                _settings.SpotifyRefreshToken = "";
                _settings.Save();
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Run the full interactive OAuth login flow. Returns the access token on
    /// success; throws on user cancellation or network failure.
    /// </summary>
    public async Task<string> LoginAsync(CancellationToken ct)
    {
        var (verifier, challenge) = GeneratePkcePair();
        var state = Guid.NewGuid().ToString("N")[..16];

        var authUrl = "https://accounts.spotify.com/authorize?" +
            $"client_id={ClientId}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&state={state}" +
            $"&code_challenge_method=S256&code_challenge={challenge}" +
            "&scope=user-read-private";

        // Open browser
        OpenBrowser(authUrl);

        // Start loopback listener
        string? code = await ListenForCallbackAsync(state, ct);
        if (code == null)
            throw new OperationCanceledException("OAuth callback was cancelled or timed out.");

        // Exchange code for tokens
        await ExchangeCodeAsync(code, verifier, ct);
        return _settings.SpotifyAccessToken!;
    }

    /// <summary>Log out — forget stored tokens.</summary>
    public void Logout()
    {
        _settings.SpotifyAccessToken = "";
        _settings.SpotifyRefreshToken = "";
        _settings.SpotifyTokenExpiry = DateTime.MinValue;
        _settings.Save();
    }

    // ---- PKCE utilities -------------------------------------------------------

    private static (string verifier, string challenge) GeneratePkcePair()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64UrlEncode(bytes);
        if (verifier.Length > 128) verifier = verifier[..128]; // RFC 7636: 43-128 chars
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ---- loopback HTTP server -------------------------------------------------

    private static async Task<string?> ListenForCallbackAsync(string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{RedirectPort}/callback/");
        listener.Prefixes.Add($"http://127.0.0.1:{RedirectPort}/"); // Spotify appends params
        listener.Start();

        // Link cancellation to abort the pending GetContextAsync
        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linked.Token.Register(() => { try { listener.Stop(); } catch { } });

            var context = await listener.GetContextAsync().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();

            var query = context.Request.Url!.Query;
            var code = ExtractParam(query, "code");
            var state = ExtractParam(query, "state");
            var error = ExtractParam(query, "error");

            // Write a response so the browser tab shows something and closes
            var html = error != null
                ? $"<html><body style='background:#0E1013;color:#E8EAED;font:16px sans-serif;padding:40px'><p>Login failed: {error}</p><p>You can close this tab.</p></body></html>"
                : state == expectedState
                    ? "<html><body style='background:#0E1013;color:#E8EAED;font:16px sans-serif;padding:40px'><p>Logged in! You can close this tab.</p><script>window.close()</script></body></html>"
                    : "<html><body style='background:#0E1013;color:#E8EAED;font:16px sans-serif;padding:40px'><p>State mismatch. Please try again.</p></body></html>";

            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, ct);
            context.Response.OutputStream.Close();

            return state == expectedState ? code : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (HttpListenerException) { return null; }
    }

    private static string? ExtractParam(string query, string name)
    {
        var prefix = $"{name}=";
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[prefix.Length..]);
        }
        return null;
    }

    // ---- token exchange & refresh ---------------------------------------------

    private async Task ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = content,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await StoreTokensAsync(response, ct);
    }

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _settings.SpotifyRefreshToken!,
            ["client_id"] = ClientId,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = content,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await StoreTokensAsync(response, ct);
    }

    private async Task StoreTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        _settings.SpotifyAccessToken = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        _settings.SpotifyTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        if (root.TryGetProperty("refresh_token", out var rt))
            _settings.SpotifyRefreshToken = rt.GetString()!;

        _settings.Save();
    }

    // ---- browser --------------------------------------------------------------

    private static void OpenBrowser(string url)
    {
        try
        {
            // OpenUrl on Linux/WSL, ShellExecute on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start("xdg-open", url);
        }
        catch { /* browser might not be available */ }
    }
}
