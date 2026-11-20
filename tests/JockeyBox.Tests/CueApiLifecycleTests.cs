using System.Net;
using System.Net.Http;
using System.Text;
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
    /// <summary>Candidates <see cref="LocalApiServer.Start"/> tries before it gives up.</summary>
    private const int WalkUpCandidates = 12;

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
            Assert.True(server.Start(port),
                $"Start({port}) returned false with only that one port held. {StartEvidence(port, server)}");

            Assert.True(server.IsRunning,
                $"Start({port}) returned true but IsRunning is false. {StartEvidence(port, server)}");
            Assert.True(server.Port != port,
                $"the walk-up stayed on the held port {port}. {StartEvidence(port, server)}");
            Assert.True(server.Port >= port + 1 && server.Port <= port + WalkUpCandidates - 1,
                $"the walk-up bound {server.Port}, outside the {WalkUpCandidates} candidates it searches. "
                + StartEvidence(port, server));
        }
        finally
        {
            blocker.Stop();
            blocker.Close();
        }
    }

    /// <summary>
    /// The walk-up against a range that is deliberately fragmented, instead of whatever
    /// <see cref="FreePort"/> happened to leave behind: three consecutive loopback ports
    /// are held by real listeners for the life of the test, so the first free candidate is
    /// a known number and the server must land exactly on it, not merely somewhere in range.
    /// </summary>
    [Fact]
    public void Walk_up_crosses_a_held_block_and_takes_the_first_port_after_it()
    {
        const int held = 3;
        var (first, listeners) = ReserveConsecutiveFreePorts(held);

        try
        {
            int expected = first + held;
            using var server = NewServer(new FakeCueTarget());

            Assert.True(server.Start(first),
                $"Start({first}) returned false with {first}..{expected - 1} held by this test. "
                + $"LastError: {server.LastError ?? "<none>"}");

            Assert.True(server.IsRunning,
                $"Start({first}) returned true but IsRunning is false; bound port {server.Port}.");

            // The assertion that makes this a walk-up test and not a range test.
            Assert.True(server.Port == expected,
                $"expected the first port after the held block ({expected}), got {server.Port}. "
                + $"held {first}..{expected - 1}; "
                + $"LastError (last refusal): {server.LastError ?? "<none>"}");

            Console.WriteLine(
                $"[walk-up] held {first}..{expected - 1}; server bound {server.Port}; expected {expected}");
        }
        finally
        {
            ReleaseListeners(listeners);
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

        var ok = await http.PostAsync(
            "cue/a/2/recall", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"ok\":true", await ok.Content.ReadAsStringAsync());
        Assert.Single(target.Recalls);

        var bad = await http.PostAsync(
            "cue/c/2/recall", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
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

    /// <summary>
    /// The facts a failed <see cref="LocalApiServer.Start"/> needs to be diagnosable: the port
    /// asked for, every candidate it could have tried, the port it actually bound, and the last
    /// refusal http.sys reported. Without these a bare false return cannot be falsified.
    /// </summary>
    private static string StartEvidence(int requested, LocalApiServer server) =>
        $"requested {requested}; tried {requested}..{requested + WalkUpCandidates - 1} "
        + $"({WalkUpCandidates} candidates); "
        + $"bound: {(server.IsRunning ? server.Port.ToString() : "<none>")}; "
        + $"LastError: {server.LastError ?? "<none>"}";

    /// <summary>
    /// Hold <paramref name="count"/> consecutive loopback ports with real listeners, so a test
    /// can know exactly which port is the first free one. Bases are drawn from 20000-38999:
    /// below the ephemeral range <see cref="FreePort"/> hands out and away from the default port.
    /// </summary>
    private static (int First, List<HttpListener> Listeners) ReserveConsecutiveFreePorts(int count)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            int first = 20000 + Random.Shared.Next(0, 19000);
            var held = new List<HttpListener>(count);
            bool complete = true;

            for (int i = 0; i < count; i++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{first + i}/");
                try
                {
                    listener.Start();
                    held.Add(listener);
                }
                catch
                {
                    try { listener.Close(); } catch { }
                    complete = false;
                    break;
                }
            }

            if (complete) return (first, held);
            ReleaseListeners(held);
        }

        throw new InvalidOperationException(
            $"could not hold {count} consecutive loopback ports in 20000..38999 after 40 bases");
    }

    /// <summary>Stop and close every listener; safe to call on a partial block.</summary>
    private static void ReleaseListeners(IEnumerable<HttpListener> listeners)
    {
        foreach (var listener in listeners)
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
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
