using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Tools;

namespace JockeyBox.YouTube;

/// <summary>The persisted half of the Google connection. One file, one user.</summary>
public sealed class YouTubeOAuthTokens
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string Scope { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChannelTitle { get; set; } = "";
}

/// <summary>Google said no; <see cref="Exception.Message"/> is Google's own wording where possible.</summary>
public sealed class GoogleAuthException : Exception
{
    public GoogleAuthException(string message) : base(message) { }
}

/// <summary>
/// Google OAuth 2.0 for an installed app, using the loopback redirect:
///
///   1. bind an <see cref="HttpListener"/> to a free 127.0.0.1 port,
///   2. open the system browser on accounts.google.com/o/oauth2/v2/auth with
///      response_type=code, access_type=offline, prompt=consent and the chosen
///      127.0.0.1 port as redirect_uri,
///   3. capture ?code= on the loopback request, exchange it at
///      oauth2.googleapis.com/token, and keep the refresh token + expiry,
///   4. refresh transparently when the access token is (nearly) expired.
///
/// Scope is <c>youtube.force-ssl</c>: playlist insert/delete is impossible with
/// the read-only <c>youtube.readonly</c>, and force-ssl is Google's narrower,
/// recommended replacement for the legacy full <c>youtube</c> scope. It still
/// covers every write this app makes (playlists + playlistItems) and is the
/// scope Google's current consent screen shows as "manage your YouTube account".
///
/// Tokens and the (user's own) app credentials live in
/// %APPDATA%\JockeyBox\youtube-oauth.json - deliberately outside AppSettings, so
/// the Settings window and its file stay untouched. Note: that file is plain
/// JSON; the client secret is the user's own app secret. DPAPI encryption is the
/// obvious follow-up (see Protect/Unprotect), but it would make the file
/// unreadable across machines, which is why it is not on by default.
///
/// Tokens are never logged and never embedded: only the client id (public) is
/// echoed into log lines, and only as a prefix.
/// </summary>
public sealed class GoogleOAuthService
{
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string Scope = "https://www.googleapis.com/auth/youtube.force-ssl";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private YouTubeOAuthTokens _tokens;

    public GoogleOAuthService(string? path = null)
    {
        _path = path ?? DefaultPath;
        _tokens = Load(_path);
    }

    /// <summary>The fixed location: %APPDATA%\JockeyBox\youtube-oauth.json.</summary>
    public static string DefaultPath
    {
        get
        {
            string appData = AppPaths.Home;
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Path.GetTempPath(), "JockeyBox");
            return Path.Combine(appData, "JockeyBox", "youtube-oauth.json");
        }
    }

    /// <summary>Raised after any state change (credentials saved, tokens refreshed, disconnected).</summary>
    public event Action? Changed;

    public string ClientId => _tokens.ClientId;
    public string ClientSecret => _tokens.ClientSecret;
    public string ChannelId => _tokens.ChannelId;
    public string ChannelTitle => _tokens.ChannelTitle;
    public string ScopeGranted => _tokens.Scope;

    /// <summary>A refresh token is the durable proof of connection; an access token alone expires.</summary>
    public bool IsConnected => !string.IsNullOrWhiteSpace(_tokens.RefreshToken);

    public bool HasAccessToken => !string.IsNullOrWhiteSpace(_tokens.AccessToken)
                                  && _tokens.ExpiresAtUtc > DateTime.UtcNow.AddSeconds(60);

    // ---- credentials ------------------------------------------------------

    /// <summary>Store the user's own OAuth client (Desktop app) credentials.</summary>
    public void SaveCredentials(string clientId, string clientSecret)
    {
        clientId = (clientId ?? "").Trim();
        clientSecret = (clientSecret ?? "").Trim();
        if (clientId == _tokens.ClientId && clientSecret == _tokens.ClientSecret) return;

        _tokens.ClientId = clientId;
        _tokens.ClientSecret = clientSecret;
        Save();
        Changed?.Invoke();
    }

    public void SetChannel(string channelId, string channelTitle)
    {
        if (_tokens.ChannelId == channelId && _tokens.ChannelTitle == channelTitle) return;
        _tokens.ChannelId = channelId ?? "";
        _tokens.ChannelTitle = channelTitle ?? "";
        Save();
        Changed?.Invoke();
    }

    /// <summary>Drop the cached access token so the next call refreshes instead of trusting it.</summary>
    public void InvalidateAccessToken()
    {
        _tokens.AccessToken = "";
        _tokens.ExpiresAtUtc = DateTime.MinValue;
    }

    /// <summary>Forget the refresh token (and cached access token); keep the client id/secret.</summary>
    public void Disconnect()
    {
        var refresh = _tokens.RefreshToken;
        _tokens.RefreshToken = "";
        _tokens.AccessToken = "";
        _tokens.ExpiresAtUtc = DateTime.MinValue;
        _tokens.Scope = "";
        _tokens.ChannelId = "";
        _tokens.ChannelTitle = "";
        Save();
        Changed?.Invoke();

        // Best effort: tell Google the grant is gone too, so the app disappears
        // from the account's third-party list. Failure is not the user's problem.
        if (!string.IsNullOrWhiteSpace(refresh)) _ = RevokeAsync(refresh);
    }

    private static async Task RevokeAsync(string token)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await Http.PostAsync(RevokeEndpoint, content);
            Log.Info($"YouTube: revoke returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube: could not revoke the token: {ex.Message}");
        }
    }

    // ---- access token -----------------------------------------------------

    /// <summary>
    /// A valid access token, refreshed first when needed. Throws
    /// <see cref="GoogleAuthException"/> when there is no connection or Google refuses.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (HasAccessToken) return _tokens.AccessToken;
            if (!IsConnected)
                throw new GoogleAuthException("Not connected to YouTube. Open Connect YouTube on the YouTube tab.");

            await RefreshLockedAsync(ct);
            return _tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshLockedAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _tokens.ClientId,
            ["client_secret"] = _tokens.ClientSecret,
            ["refresh_token"] = _tokens.RefreshToken,
            ["grant_type"] = "refresh_token",
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ReadOAuthError(body);
            // invalid_grant means the refresh token is dead (revoked, password
            // changed, testing-mode consent expired after 7 days).
            if (error == "invalid_grant")
            {
                _tokens.RefreshToken = "";
                _tokens.AccessToken = "";
                _tokens.ExpiresAtUtc = DateTime.MinValue;
                Save();
                Changed?.Invoke();
                throw new GoogleAuthException(
                    "Google no longer accepts the saved YouTube login (invalid_grant). Press Reconnect " +
                    "on the YouTube tab to sign in again. Google expires refresh tokens after 7 days while " +
                    "the Cloud project is still in \"Testing\" - publish the consent screen to stop that.");
            }

            throw new GoogleAuthException(string.IsNullOrWhiteSpace(description)
                ? $"Google refused the token refresh ({error})."
                : $"Google refused the token refresh: {description}");
        }

        ApplyTokenResponse(body);
        Save();
        Changed?.Invoke();
        Log.Info($"YouTube: access token refreshed, valid until {_tokens.ExpiresAtUtc:u}");
    }

    // ---- interactive authorize -------------------------------------------

    /// <summary>
    /// Run the loopback flow. <paramref name="progress"/> receives human-readable
    /// steps (never a token). Cancelling the token or the timeout aborts the wait
    /// and shuts the listener down.
    /// </summary>
    public async Task AuthorizeAsync(Action<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_tokens.ClientId) || string.IsNullOrWhiteSpace(_tokens.ClientSecret))
            throw new GoogleAuthException(
                "No Google OAuth client configured. Paste a Desktop app client id and secret first.");

        var listener = StartListener(out var port, out var redirectUri);
        try
        {
            var state = RandomState();
            // prompt=consent on every run: Google only returns a refresh token on
            // the authorization_code grant when the user sees the consent screen,
            // and asking again is what lets a reconnect switch Google accounts
            // instead of silently reusing the previous grant.
            var url = BuildAuthorizeUrl(redirectUri, state, prompt: "consent");

            progress?.Invoke($"Waiting for Google in your browser (redirect {redirectUri})\u2026");
            Log.Info($"YouTube: opening Google consent on loopback port {port}");

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new GoogleAuthException(
                    $"Could not open the browser: {ex.Message}. Open this address yourself: {url}");
            }

            var code = await WaitForCodeAsync(listener, state, ct);
            progress?.Invoke("Exchanging the authorization code\u2026");
            await ExchangeCodeLockedAsync(code, redirectUri, ct);

            Changed?.Invoke();
            Log.Info($"YouTube: connected (channel id '{_tokens.ChannelId}')");
        }
        finally
        {
            try { listener.Stop(); listener.Close(); } catch { /* already down */ }
        }
    }

    private static HttpListener StartListener(out int port, out string redirectUri)
    {
        // Ask the OS for a free loopback port, then bind the listener to exactly
        // that port. A loopback prefix needs no URL ACL and no elevation.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var candidate = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                redirectUri = $"http://127.0.0.1:{candidate}/";
                return listener;
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (attempt == 4)
                    throw new GoogleAuthException(
                        $"Could not open the loopback listener for the Google redirect: {ex.Message}");
            }
        }

        throw new GoogleAuthException("Could not open a loopback port for the Google redirect.");
    }

    private string BuildAuthorizeUrl(string redirectUri, string state, string prompt)
    {
        var query = new StringBuilder(AuthorizeEndpoint)
            .Append("?response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(_tokens.ClientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&access_type=offline")
            .Append("&include_granted_scopes=true")
            .Append("&prompt=").Append(Uri.EscapeDataString(prompt))
            .Append("&state=").Append(Uri.EscapeDataString(state));
        return query.ToString();
    }

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            try { listener.Stop(); } catch { /* already stopped */ }
        });

        HttpListenerContext context;
        while (true)
        {
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                           or HttpListenerException or TaskCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                throw new GoogleAuthException("The loopback listener closed before Google answered.");
            }

            // Browsers probe /favicon.ico (and sometimes other paths) on the
            // redirect origin. Those carries no code; answer and keep waiting.
            if (context.Request.QueryString["code"] == null
                && context.Request.QueryString["error"] == null)
            {
                try
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch { /* browser hung up */ }
                continue;
            }

            break;
        }

        var query = context.Request.QueryString;
        var returnedState = query["state"] ?? "";
        var code = query["code"] ?? "";
        var error = query["error"] ?? "";
        var description = query["error_description"] ?? "";

        await WriteBrowserPageAsync(context, error.Length == 0,
            error.Length == 0
                ? "JockeyBox is connected to YouTube. You can close this tab."
                : $"Google refused access: {WebUtility.HtmlEncode(error)}");

        if (error.Length > 0)
            throw new GoogleAuthException(string.IsNullOrWhiteSpace(description)
                ? $"Google refused access ({error})."
                : $"Google refused access: {description}");

        // A mismatched state is either a stale tab or a forged callback.
        if (!string.Equals(returnedState, state, StringComparison.Ordinal))
            throw new GoogleAuthException("Google returned a mismatched OAuth state; the sign-in was cancelled for safety.");

        if (string.IsNullOrWhiteSpace(code))
            throw new GoogleAuthException("Google returned no authorization code.");

        return code;
    }

    private static async Task WriteBrowserPageAsync(HttpListenerContext context, bool ok, string message)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>JockeyBox</title></head>" +
                   "<body style=\"background:#12141a;color:#e8eaf0;font-family:Segoe UI,sans-serif;" +
                   "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
                   $"<p style=\"font-size:15px;color:{(ok ? "#7ad9a0" : "#e06c7a")}\">{message}</p>" +
                   "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);

        try
        {
            context.Response.StatusCode = ok ? 200 : 400;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            Log.Warn($"YouTube: could not write the loopback response: {ex.Message}");
        }
    }

    private async Task ExchangeCodeLockedAsync(string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _tokens.ClientId,
            ["client_secret"] = _tokens.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ReadOAuthError(body);
            throw new GoogleAuthException(string.IsNullOrWhiteSpace(description)
                ? $"Google token exchange failed ({error}). Check that the OAuth client is of type \"Desktop app\" and that the client secret is current."
                : $"Google token exchange failed: {description}");
        }

        var previousRefresh = _tokens.RefreshToken;
        var refreshed = ApplyTokenResponse(body);
        if (!refreshed)
        {
            // Do not keep a refresh token (or an access token) that may belong to
            // a different account than the previous grant.
            _tokens.RefreshToken = previousRefresh;
            _tokens.AccessToken = "";
            _tokens.ExpiresAtUtc = DateTime.MinValue;
            throw new GoogleAuthException(
                "Google returned no refresh token, so background refreshing is impossible. Remove the app's " +
                "access at myaccount.google.com/permissions, then press Connect again - the consent screen " +
                "is what returns a refresh token.");
        }

        Save();
    }

    /// <summary>
    /// Parse a token response. The refresh token is only replaced when Google
    /// actually returns one: a refresh grant normally does not, and the old one
    /// must survive.
    /// </summary>
    private bool ApplyTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("access_token", out var access) && access.ValueKind == JsonValueKind.String)
            _tokens.AccessToken = access.GetString() ?? "";

        if (root.TryGetProperty("expires_in", out var expires)
            && expires.TryGetInt32(out var seconds) && seconds > 0)
            _tokens.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(seconds - 30);

        var gotRefreshToken = false;
        if (root.TryGetProperty("refresh_token", out var refresh)
            && refresh.ValueKind == JsonValueKind.String
            && refresh.GetString() is { Length: > 0 } token)
        {
            _tokens.RefreshToken = token;
            gotRefreshToken = true;
        }

        if (root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String)
            _tokens.Scope = scope.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(_tokens.AccessToken))
            throw new GoogleAuthException("Google returned no access token.");

        return gotRefreshToken;
    }

    private static (string Error, string Description) ReadOAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
            return (error, description);
        }
        catch (JsonException)
        {
            return ("", body);
        }
    }

    private static string RandomState()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ---- persistence ------------------------------------------------------

    private static YouTubeOAuthTokens Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new YouTubeOAuthTokens();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<YouTubeOAuthTokens>(json, JsonOpts) ?? new YouTubeOAuthTokens();
        }
        catch (Exception ex)
        {
            // A corrupt file must not make the tab unusable; start clean.
            Log.Warn($"YouTube: could not read {path}: {ex.Message}");
            return new YouTubeOAuthTokens();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write-then-replace so a crash never leaves a truncated token file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_tokens, JsonOpts));
            File.Move(temp, _path, overwrite: true);
            Log.Info($"YouTube: credentials saved to {_path}");
        }
        catch (Exception ex)
        {
            Log.Error($"YouTube: could not write {_path}", ex);
        }
    }
}
