using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using JockeyBox.Api;

namespace JockeyBox.Tests;

/// <summary>
/// The listener around the cue router: it walks off a busy port instead of
/// crashing, frees the port on stop so a restart works, and carries a real HTTP
/// request through to the router. Loopback only — nothing here binds anything
/// but 127.0.0.1.
/// </summary>
public sealed class CueApiLifecycleTests
{
    [Fact]
    public void Port_in_use_walks_up_and_reports_the_port_it_actually_took()
    {
        int port = FreePort();
        using var blocker = new HttpListener();
        blocker.Prefixes.Add($"http://127.0.0.1:{port}/");
        blocker.Start();

        try
        {
            using var server = NewServer(new FakeCueTarget());
            Assert.True(server.Start(port));

            Assert.True(server.IsRunning);
            Assert.NotEqual(port, server.Port);
            Assert.InRange(server.Port, port + 1, port + 11);
        }
        finally
        {
            blocker.Stop();
            blocker.Close();
        }
    }

    [Fact]
    public void Clean_stop_frees_the_port_for_a_restart()
    {
        int port = FreePort();

        var first = NewServer(new FakeCueTarget());
        Assert.True(first.Start(port));
        int bound = first.Port;
        first.Dispose();

        Assert.False(first.IsRunning);

        using var second = NewServer(new FakeCueTarget());
        Assert.True(second.Start(bound));
        Assert.Equal(bound, second.Port);
    }

    [Fact]
    public async Task A_real_request_reaches_the_router_and_a_bad_one_gets_a_4xx()
    {
        var target = new FakeCueTarget();
        using var server = NewServer(target);
        Assert.True(server.Start(FreePort()));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };

        var ok = await http.PostAsync("cue/a/2/recall", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"ok\":true", await ok.Content.ReadAsStringAsync());
        Assert.Single(target.Recalls);

        var bad = await http.PostAsync("cue/c/2/recall", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("unknown deck 'c'", await bad.Content.ReadAsStringAsync());
        Assert.Single(target.Recalls);

        var snapshot = await http.GetAsync("cues");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        Assert.Contains("\"decks\"", await snapshot.Content.ReadAsStringAsync());
    }

    private static LocalApiServer NewServer(FakeCueTarget target)
    {
        var server = new LocalApiServer();
        server.Configure(
            token: string.Empty,
            padSource: () => Array.Empty<ApiPad>(),
            playPad: _ => { },
            stopAll: () => { },
            stateSource: () => new ApiState(),
            cueRouter: new CueApiRouter(target));
        return server;
    }

    /// <summary>A port the OS just handed out and we released, so it is almost certainly free.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
