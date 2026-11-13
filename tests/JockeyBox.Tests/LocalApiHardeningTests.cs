using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using JockeyBox.Api;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// The listener's own refusals, over a real socket: a browser must not reach
/// this surface, a body must not be unbounded, and a WebSocket is not a way
/// around the token. Each test starts a server on a free loopback port.
/// </summary>
public sealed class LocalApiHardeningTests
{
    [Fact]
    public async Task Websocket_without_a_token_is_refused_and_with_one_is_accepted()
    {
        using var server = NewServer(new FakeCueTarget(), token: "s3cret");
        Assert.True(server.Start(FreePort()));
        string url = $"ws://127.0.0.1:{server.Port}/ws";

        using (var unauthorised = new ClientWebSocket())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => unauthorised.ConnectAsync(new Uri(url), timeout.Token));
        }

        using var authorised = new ClientWebSocket();
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await authorised.ConnectAsync(new Uri(url + "?token=s3cret"), window.Token);

        Assert.Equal(WebSocketState.Open, authorised.State);
        await authorised.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", window.Token);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task A_browser_simple_request_content_type_is_refused_and_never_reaches_the_router(string contentType)
    {
        var target = new FakeCueTarget();
        using var server = NewServer(target);
        Assert.True(server.Start(FreePort()));
        using var http = Client(server);

        var response = await http.PostAsync(
            "cue/a/1/capture", new StringContent(string.Empty, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(target.Captures);
    }

    [Fact]
    public async Task A_request_carrying_an_origin_header_is_refused_even_when_it_is_json()
    {
        var target = new FakeCueTarget();
        using var server = NewServer(target);
        Assert.True(server.Start(FreePort()));
        using var http = Client(server);

        var request = new HttpRequestMessage(HttpMethod.Post, "cue/a/1/capture")
        {
            Content = Json("{}"),
        };
        request.Headers.Add("Origin", "http://example.test");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(target.Captures);
    }

    [Fact]
    public async Task A_json_post_without_a_token_still_works_and_has_no_browser_headers()
    {
        var target = new FakeCueTarget();
        using var server = NewServer(target);
        Assert.True(server.Start(FreePort()));
        using var http = Client(server);

        var response = await http.PostAsync("cue/a/1/capture", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(target.Captures);
    }

    [Fact]
    public async Task An_oversized_declared_body_is_a_413_and_the_router_never_sees_it()
    {
        var target = new FakeCueTarget();
        using var server = NewServer(target);
        Assert.True(server.Start(FreePort()));
        using var http = Client(server);

        string big = new('x', (int)LocalApiServer.MaxBodyBytes + 1_000);
        var response = await http.PostAsync("cue/a/1/capture", Json(big));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(target.Captures);
    }

    [Fact]
    public async Task A_chunked_body_that_declares_no_length_is_capped_too()
    {
        var target = new FakeCueTarget();
        using var server = NewServer(target);
        Assert.True(server.Start(FreePort()));
        using var http = Client(server);

        string big = new('y', (int)LocalApiServer.MaxBodyBytes + 1_000);
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "cue/a/1/capture")
        {
            Content = content,
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(target.Captures);
    }

    [Fact]
    public async Task Sounds_add_uses_the_same_ceiling()
    {
        bool added = false;
        var server = new LocalApiServer();
        server.Configure(
            token: string.Empty,
            padSource: () => Array.Empty<ApiPad>(),
            playPad: _ => { },
            stopAll: () => { },
            stateSource: () => new ApiState(),
            addSound: (path, url) =>
            {
                added = true;
                return Task.FromResult(ApiAddResult.Fail("should not be reached"));
            });

        using var _ = server;
        Assert.True(server.Start(FreePort()));
        using var http = Client(server);

        string big = new('z', (int)LocalApiServer.MaxBodyBytes + 1_000);
        var response = await http.PostAsync("sounds/add", Json(big));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(added);
    }

    [Fact]
    public void A_tokenless_start_says_what_that_means()
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Sink(string line) => lines.Enqueue(line);

        Log.LineWritten += Sink;
        try
        {
            using var server = NewServer(new FakeCueTarget());
            Assert.True(server.Start(FreePort()));
        }
        finally
        {
            Log.LineWritten -= Sink;
        }

        Assert.Contains(lines, line => line.Contains("no token configured", StringComparison.OrdinalIgnoreCase));
    }

    private static LocalApiServer NewServer(FakeCueTarget target, string token = "")
    {
        var server = new LocalApiServer();
        server.Configure(
            token: token,
            padSource: () => Array.Empty<ApiPad>(),
            playPad: _ => { },
            stopAll: () => { },
            stateSource: () => new ApiState(),
            cueRouter: new CueApiRouter(target));
        return server;
    }

    private static HttpClient Client(LocalApiServer server) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
