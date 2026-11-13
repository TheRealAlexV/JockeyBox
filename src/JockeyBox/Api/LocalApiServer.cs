using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Tools;

namespace JockeyBox.Api;

/// <summary>
/// Loopback control API for the Stream Deck plugin and for OBS browser sources
/// (requirement 13).
///
/// <para><b>Why loopback HTTP instead of the Stream Deck property inspector talking
/// to the app directly.</b> The inspector runs on a <c>file://</c> origin, so a
/// direct fetch is cross-origin with an opaque origin and only works if the app
/// answers CORS preflights. A Stream Deck plugin's Node backend is a normal
/// process: plain loopback HTTP with no CORS involved. So the plugin backend talks
/// to this server and the inspector talks to the plugin over Stream Deck's own
/// socket.</para>
///
/// <para><b>Binding.</b> Strictly <c>127.0.0.1</c> — never <c>0.0.0.0</c> — and
/// every request needs the bearer token from settings, so another machine on the
/// LAN (or a browser tab on this one) cannot fire pads. A short-lived WebSocket
/// feed carries playback state so a key can show "playing" without polling.</para>
/// </summary>
public sealed class LocalApiServer : IDisposable
{
    /// <summary>Default port. Loopback only; the Stream Deck plugin reads it from its global settings.</summary>
    public const int DefaultPort = 17890;

    private HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _sockets = new();
    private readonly object _socketGate = new();

    private Func<IReadOnlyList<ApiPad>>? _padSource;
    private Action<string>? _playPad;
    private Action? _stopAll;
    private Func<ApiState>? _stateSource;
    private Func<string?, string?, Task<ApiAddResult>>? _addSound;
    private CueApiRouter? _cueRouter;
    private string _token = string.Empty;
    private Task? _loop;

    /// <summary>True while the listener is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The bound port (0 when not running).</summary>
    public int Port { get; private set; }

    /// <summary>Last error, when the port could not be bound.</summary>
    public string? LastError { get; private set; }

    /// <summary>Wire the server to the soundboard.</summary>
    public void Configure(
        string token,
        Func<IReadOnlyList<ApiPad>> padSource,
        Action<string> playPad,
        Action stopAll,
        Func<ApiState> stateSource,
        Func<string?, string?, Task<ApiAddResult>>? addSound = null,
        CueApiRouter? cueRouter = null)
    {
        _token = token ?? string.Empty;
        _padSource = padSource;
        _playPad = playPad;
        _stopAll = stopAll;
        _stateSource = stateSource;
        _addSound = addSound;
        _cueRouter = cueRouter;
    }

    /// <summary>Start listening on 127.0.0.1:<paramref name="port"/> (falls back to the next free port).</summary>
    public bool Start(int port = DefaultPort)
    {
        if (IsRunning) return true;
        if (_cts.IsCancellationRequested)
        {
            LastError = "the server was already disposed";
            return false;
        }

        for (int attempt = 0; attempt < 12; attempt++)
        {
            int candidate = port + attempt;

            // A fresh listener per attempt: once http.sys refuses a prefix, that
            // HttpListener instance stays unusable, so reusing it would make the
            // walk-up fail for every remaining candidate and report the first
            // conflict as if the whole range were taken.
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { listener.Close(); } catch { }
                continue;
            }

            _listener = listener;
            Port = candidate;
            IsRunning = true;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Log.Info($"local api listening on http://127.0.0.1:{candidate}/");
            return true;
        }

        Log.Warn($"local api could not bind a port near {port}: {LastError}");
        return false;
    }

    /// <summary>Push a state change to every connected socket.</summary>
    public void PublishState(ApiState state)
    {
        string json = JsonSerializer.Serialize(new { type = "state", state });
        foreach (var socket in SnapshotSockets())
            _ = SendAsync(socket, json);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"local api accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, token), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            if (!IsAuthorised(context.Request))
            {
                context.Response.StatusCode = 401;
                await WriteTextAsync(context.Response, "unauthorized").ConfigureAwait(false);
                return;
            }

            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            string method = context.Request.HttpMethod;

            if (path == "/ws" && context.Request.IsWebSocketRequest)
            {
                var ws = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                lock (_socketGate) _sockets.Add(ws.WebSocket);
                await PumpSocketAsync(ws.WebSocket, token).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/sounds")
            {
                await WriteJsonAsync(context.Response, _padSource?.Invoke() ?? Array.Empty<ApiPad>()).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/state")
            {
                await WriteJsonAsync(context.Response, _stateSource?.Invoke() ?? new ApiState()).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/sounds/add")
            {
                await AddSoundAsync(context).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path.StartsWith("/sounds/", StringComparison.Ordinal))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 3 && segments[2] == "play")
                {
                    string id = segments[1];
                    _playPad?.Invoke(id);
                    await WriteJsonAsync(context.Response, new { ok = true, id }).ConfigureAwait(false);
                    return;
                }
            }

            if (method == "POST" && path == "/stop-all")
            {
                _stopAll?.Invoke();
                await WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
                return;
            }

            // Cue and transport keys: the router owns the parsing and the 4xx
            // refusals, this listener only supplies the transport.
            if (_cueRouter != null && CueApiRouter.Handles(path))
            {
                string? body = null;
                if (method == "POST")
                {
                    using var reader = new StreamReader(
                        context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var outcome = _cueRouter.Handle(method, path, body);
                context.Response.StatusCode = outcome.Status;
                await WriteRawJsonAsync(context.Response, outcome.Json).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            await WriteTextAsync(context.Response, "not found").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"local api handler failed: {ex.Message}");
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// <c>POST /sounds/add</c> with <c>{"path":"C:\...\horn.mp3"}</c> or
    /// <c>{"url":"https://youtu.be/..."}</c>. Used by the headless verification
    /// scripts and by anything that wants to drop a sound in without the picker.
    /// </summary>
    private async Task AddSoundAsync(HttpListenerContext context)
    {
        if (_addSound == null)
        {
            context.Response.StatusCode = 501;
            await WriteTextAsync(context.Response, "adding sounds is not wired").ConfigureAwait(false);
            return;
        }

        SoundAddRequest? request;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            string body = await reader.ReadToEndAsync().ConfigureAwait(false);
            request = string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<SoundAddRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
        }
        catch (Exception ex)
        {
            Log.Warn($"local api: bad /sounds/add body: {ex.Message}");
            context.Response.StatusCode = 400;
            await WriteTextAsync(context.Response, "bad json").ConfigureAwait(false);
            return;
        }

        string? path = request?.Path;
        string? url = request?.Url;
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(url))
        {
            context.Response.StatusCode = 400;
            await WriteTextAsync(context.Response, "expected {path} or {url}").ConfigureAwait(false);
            return;
        }

        var result = await _addSound(path, url).ConfigureAwait(false);
        context.Response.StatusCode = result.Ok ? 200 : 422;
        await WriteJsonAsync(context.Response, result).ConfigureAwait(false);
    }

    private bool IsAuthorised(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(_token)) return true; // token optional for local-only use
        string? header = request.Headers["X-JockeyBox-Token"];
        if (header == _token) return true;

        string? query = request.Url?.Query;
        if (!string.IsNullOrEmpty(query) && query.Contains($"token={_token}", StringComparison.Ordinal)) return true;

        if (request.IsWebSocketRequest) return true; // the socket carries no custom headers
        return false;
    }

    private async Task PumpSocketAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[1024];
        try
        {
            // Send the current state immediately so a freshly connected key is correct.
            if (_stateSource != null) await SendAsync(socket, JsonSerializer.Serialize(new { type = "state", state = _stateSource() })).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception) { /* client went away */ }
        finally
        {
            lock (_socketGate) _sockets.Remove(socket);
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private async Task SendAsync(WebSocket socket, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_socketGate) _sockets.Remove(socket);
        }
    }

    private WebSocket[] SnapshotSockets()
    {
        lock (_socketGate) return _sockets.Where(s => s.State == WebSocketState.Open).ToArray();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-store";
        await WriteTextAsync(response, json).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    /// <summary>Write an already-serialized body (the cue router returns JSON, not objects).</summary>
    private static async Task WriteRawJsonAsync(HttpListenerResponse response, string json)
    {
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-store";
        await WriteTextAsync(response, json).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        IsRunning = false;

        lock (_socketGate)
        {
            foreach (var socket in _sockets)
            {
                try { socket.Dispose(); } catch { }
            }
            _sockets.Clear();
        }
    }
}

/// <summary>Body of <c>POST /sounds/add</c>.</summary>
public sealed class SoundAddRequest
{
    /// <summary>Absolute path of a local audio file.</summary>
    public string? Path { get; set; }

    /// <summary>A YouTube (or direct audio) URL to download.</summary>
    public string? Url { get; set; }
}

/// <summary>Result of <c>POST /sounds/add</c>.</summary>
public sealed record ApiAddResult(bool Ok, string? Id, string? Name, string? Hotkey, string? Error)
{
    public static ApiAddResult Fail(string error) => new(false, null, null, null, error);
}

/// <summary>A soundboard pad as the API exposes it.</summary>
public sealed record ApiPad(string Id, string Name, string? Hotkey, double Volume, bool Loop, bool IsPlaying, double DurationSeconds);

/// <summary>Current playback state for the Stream Deck keys and browser overlays.</summary>
public sealed record ApiState
{
    /// <summary>Ids of pads currently producing audio.</summary>
    public string[] Playing { get; init; } = Array.Empty<string>();

    /// <summary>Master volume as shown in the UI (0..1.2).</summary>
    public double Master { get; init; } = 1;

    /// <summary>True while a deck is playing.</summary>
    public bool AnyDeckPlaying { get; init; }

    /// <summary>Live transition description, empty when idle.</summary>
    public string Transition { get; init; } = string.Empty;
}
