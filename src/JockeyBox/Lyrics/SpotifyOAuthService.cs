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
/// The user (or Spotify) refused the authorisation request: the app asked for
/// scopes the client id does not allow, or the user pressed "cancel" on the
/// consent screen. Distinct from a generic failure so the UI can suggest the
/// right fix instead of "try again".
/// </summary>
public sealed class SpotifyAuthException : Exception
{
    public SpotifyAuthException(string message) : base(message) { }

    public SpotifyAuthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Spotify PKCE OAuth flow for the lyrics and import features.
///
/// Flow:
///   1. Generate PKCE code verifier + SHA-256 challenge
///   2. Open browser to accounts.spotify.com/authorize
///   3. Listen on loopback for the redirect with the auth code
///   4. Exchange code → access + refresh tokens
///   5. Store tokens encrypted (DPAPI) in AppSettings
///   6. Auto-refresh when access token expires
///
/// Uses Spotify's public client ID by default — no app registration needed —
/// unless the user supplied their own in settings (required for the playlist
/// scopes, which the public web-player client does not grant). The token
/// replaces the old client ID/secret model entirely.
/// </summary>
public class SpotifyOAuthService
{
    /// <summary>
    /// Scopes the lyrics feature needs. Always requested.
    /// </summary>
    public const string BaseScopes = "user-read-private";

    /// <summary>
    /// Scopes the import screens need (reading playlists and Liked Songs).
    /// Requested together with <see cref="BaseScopes"/> unless the caller opts
    /// out, and checked afterwards via <see cref="HasPlaylistScopes"/>.
    /// </summary>
    public const string PlaylistScopes =
        "playlist-read-private playlist-read-collaborative user-library-read";

    private const string DefaultClientId = "65b25a7ad33747ff9bde6e2443a6f7f2"; // Spotify public web player client

    /// <summary>
    /// Loopback port for the OAuth callback. It must match one of the Redirect
    /// URIs registered on the user's Spotify app exactly, so it is a setting:
    /// a client id created for another tool usually already has a port.
    /// </summary>
    private int RedirectPort =>
        _settings.SpotifyRedirectPort is > 0 and <= 65535 ? _settings.SpotifyRedirectPort : 8888;

    private string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";

    /// <summary>Settings override wins: playlist scopes need a client id that allows them.</summary>
    private string EffectiveClientId =>
        string.IsNullOrWhiteSpace(_settings.SpotifyClientIdOverride)
            ? DefaultClientId
            : _settings.SpotifyClientIdOverride.Trim();

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

    /// <summary>
    /// True when the last grant included <c>playlist-read-private</c>. Older
    /// logins (and any grant Spotify narrowed) report false, which the import
    /// screen turns into a "re-authorise" prompt. Empty/unknown scopes are false.
    /// </summary>
    public bool HasPlaylistScopes =>
        _settings.SpotifyGrantedScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("playlist-read-private", StringComparer.Ordinal);

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
    /// Run the full interactive OAuth login flow, asking for the base and the
    /// playlist scopes. Returns the access token on success; throws
    /// <see cref="SpotifyAuthException"/> when Spotify refuses the request and
    /// <see cref="OperationCanceledException"/> on cancellation or timeout.
    /// </summary>
    public Task<string> LoginAsync(CancellationToken ct) => LoginAsync(includePlaylists: true, ct);

    /// <summary>
    /// Run the full interactive OAuth login flow. <paramref name="includePlaylists"/>
    /// adds <see cref="PlaylistScopes"/> to the consent screen (the user can
    /// still decline individual scopes, hence <see cref="HasPlaylistScopes"/>).
    /// Returns the access token on success.
    /// </summary>
    public async Task<string> LoginAsync(bool includePlaylists, CancellationToken ct)
    {
        var (verifier, challenge) = GeneratePkcePair();
        var state = Guid.NewGuid().ToString("N")[..16];
        var scopes = includePlaylists ? $"{BaseScopes} {PlaylistScopes}" : BaseScopes;

        var authUrl = "https://accounts.spotify.com/authorize?" +
            $"client_id={Uri.EscapeDataString(EffectiveClientId)}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&state={state}" +
            $"&code_challenge_method=S256&code_challenge={challenge}" +
            $"&scope={Uri.EscapeDataString(scopes)}";

        // Open browser
        OpenBrowser(authUrl);

        // Start loopback listener
        var (code, error) = await ListenForCallbackAsync(state, ct);

        if (!string.IsNullOrEmpty(error))
            throw new SpotifyAuthException(DescribeAuthError(error));

        if (code == null)
            throw new OperationCanceledException("OAuth callback was cancelled or timed out.");

        // Exchange code for tokens
        await ExchangeCodeAsync(code, verifier, ct);
        return _settings.SpotifyAccessToken!;
    }

    /// <summary>Turn Spotify's redirect error code into something actionable.</summary>
    private static string DescribeAuthError(string error) => error switch
    {
        "invalid_scope" =>
            "Spotify refused the requested permissions (invalid_scope). Playlist import needs a client id " +
            "that allows the playlist and library scopes - set one in Settings, or log in for lyrics only.",
        "access_denied" =>
            "Spotify login was cancelled, or access to the requested permissions was denied.",
        "invalid_client" =>
            "Spotify did not recognise the client id. Check the client id override in Settings.",
        "state_mismatch" =>
            "Spotify's login response did not match this request (state mismatch). Please try again.",
        _ => $"Spotify authorisation failed ({error}).",
    };

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

    private async Task<(string? Code, string? Error)> ListenForCallbackAsync(string expectedState, CancellationToken ct)
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

            return state == expectedState ? (code, error) : (null, "state_mismatch");
        }
        catch (OperationCanceledException) { return (null, null); }
        catch (HttpListenerException) { return (null, null); }
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
            ["client_id"] = EffectiveClientId,
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
            ["client_id"] = EffectiveClientId,
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

    /// <summary>
    /// Persist a token response. The granted <c>scope</c> string is recorded
    /// verbatim so the UI can tell whether the playlist scopes were actually
    /// granted; refresh responses usually omit it, in which case the previous
    /// value is kept.
    /// </summary>
    private async Task StoreTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;

        _settings.SpotifyAccessToken = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        _settings.SpotifyTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        if (root.TryGetProperty("refresh_token", out var rt))
            _settings.SpotifyRefreshToken = rt.GetString()!;

        if (root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String)
        {
            var granted = scope.GetString();
            if (!string.IsNullOrWhiteSpace(granted))
                _settings.SpotifyGrantedScopes = granted;
        }

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
