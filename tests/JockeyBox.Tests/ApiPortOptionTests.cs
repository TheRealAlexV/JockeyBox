using System;
using System.Net;
using JockeyBox.Api;

namespace JockeyBox.Tests;

/// <summary>
/// The API port: 17890 stays the default the Stream Deck plugin expects, and an
/// explicit override lets a sandbox bind elsewhere so it cannot answer the
/// operator's plugin in place of their app.
///
/// <para><b>Why serialised with the UI thread.</b> A view model starts this same
/// listener, so a view-model test running in parallel would already hold 17890 and
/// this class's default-port test would see the walk-up instead of the default.</para>
/// </summary>
[Collection(UiThreadCollection.Name)]
public sealed class ApiPortOptionTests
{
    private static Func<string, string?> NoEnv => _ => null;

    [Fact]
    public void Defaults_to_the_plugin_port()
    {
        var resolution = ApiPortOption.Resolve(Array.Empty<string>(), NoEnv);

        Assert.Equal(17890, resolution.Port);
        Assert.Equal(ApiPortOption.Origin.Default, resolution.Origin);
    }

    [Fact]
    public void Argument_wins()
    {
        var resolution = ApiPortOption.Resolve(new[] { "--api-port", "18791" }, NoEnv);

        Assert.Equal(18791, resolution.Port);
        Assert.Equal(ApiPortOption.Origin.Argument, resolution.Origin);
    }

    [Fact]
    public void Argument_accepts_the_equals_form()
    {
        Assert.Equal(18792, ApiPortOption.Resolve(new[] { "--api-port=18792" }, NoEnv).Port);
    }

    [Fact]
    public void Argument_beats_the_environment()
    {
        var resolution = ApiPortOption.Resolve(
            new[] { "--api-port", "18791" },
            name => name == ApiPortOption.Variable ? "18793" : null);

        Assert.Equal(18791, resolution.Port);
        Assert.Equal(ApiPortOption.Origin.Argument, resolution.Origin);
    }

    [Fact]
    public void Environment_is_honoured_when_no_argument_is_given()
    {
        var resolution = ApiPortOption.Resolve(
            Array.Empty<string>(),
            name => name == ApiPortOption.Variable ? "18794" : null);

        Assert.Equal(18794, resolution.Port);
        Assert.Equal(ApiPortOption.Origin.Environment, resolution.Origin);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("nonsense")]
    [InlineData("")]
    public void A_bad_override_falls_back_to_the_default(string value)
    {
        var resolution = ApiPortOption.Resolve(new[] { "--api-port", value }, NoEnv);

        Assert.Equal(LocalApiServer.DefaultPort, resolution.Port);
        Assert.Equal(ApiPortOption.Origin.Default, resolution.Origin);
    }

    [Fact]
    public void Start_binds_the_requested_override_port()
    {
        // Verified through http.sys, the mechanism the server itself uses: a port
        // that a bare TCP probe finds free can still carry a urlacl reservation.
        int requested = PortsThisTestControls().Occupied;

        using var server = NewServer();
        Assert.True(server.Start(requested), server.LastError);
        Assert.Equal(requested, server.Port);
    }

    [Fact]
    public void Default_port_is_the_port_the_plugin_expects()
    {
        Assert.Equal(17890, LocalApiServer.DefaultPort);
        Assert.Equal(17890, ApiPortOption.Resolve(Array.Empty<string>(), NoEnv).Port);
    }

    [Fact]
    public void Start_walks_up_from_the_port_it_was_given_and_reports_where_it_landed()
    {
        // The behaviour under test is the walk-up itself, so the conflict is created
        // here rather than borrowed from whatever this machine happens to own: two
        // ports this test verified against http.sys, the lower one held by our own
        // listener. That makes the walk deterministic on every host, and it removes
        // the old dependence on 17890 being free (a machine running the app, or any
        // stray listener in that range, used to starve all twelve candidates and
        // surface as an opaque http.sys conflict naming the last one, 17901).
        var (occupied, expected) = PortsThisTestControls();

        using var blocker = new HttpListener();
        blocker.Prefixes.Add(Prefix(occupied));
        blocker.Start();

        using var server = NewServer();
        Assert.True(server.Start(occupied),
            $"the walk-up failed starting at port {occupied}, although this test had just " +
            $"verified http.sys would accept {expected}: {server.LastError}");

        Assert.Equal(expected, server.Port);
        Assert.NotNull(server.LastError);   // the skip is reported, never silent
        Assert.InRange(server.Port, occupied + 1, occupied + WalkUpSteps);
    }

    private static LocalApiServer NewServer()
    {
        var server = new LocalApiServer();
        server.Configure(
            token: string.Empty,
            padSource: () => Array.Empty<ApiPad>(),
            playPad: _ => { },
            stopAll: () => { },
            stateSource: () => new ApiState());
        return server;
    }

    /// <summary>How many candidates <see cref="LocalApiServer.Start"/> tries: port..port+11.</summary>
    private const int WalkUpSteps = 11;

    private static string Prefix(int port) => $"http://127.0.0.1:{port}/";

    /// <summary>
    /// True when http.sys would let this process register the prefix. This is the
    /// same mechanism the server uses, and it is the only honest availability test:
    /// a port that a bare TCP probe finds free can still carry a urlacl, which is
    /// exactly how this class used to fail on a host with such a reservation.
    /// </summary>
    private static bool TryHttpPrefix(int port, out string error)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix(port));
        try
        {
            listener.Start();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { listener.Close(); } catch { /* probe cleanup */ }
        }
    }

    /// <summary>
    /// Two adjacent ports this test has just verified http.sys accepts. The guard is
    /// deliberate: if the host will not give the test a pair, say so here, in those
    /// words, instead of failing three layers down as an opaque http.sys conflict.
    /// </summary>
    private static (int Occupied, int Expected) PortsThisTestControls()
    {
        const int attempts = 200;

        // Below Windows' dynamic port range, which starts at 49152: in that range a
        // port can be held as the local end of an outbound connection (the suite
        // leaves ~130 sockets in TIME_WAIT), and http.sys then refuses it with "being
        // used by another process" - which is what made the first version of this
        // helper walk 200 candidates and find nothing during a full run.
        const int firstPort = 20000;
        const int lastPort = 30000;

        int first = firstPort + Random.Shared.Next(0, lastPort - firstPort);
        string lastError = "(nothing tried)";

        for (int i = 0; i < attempts; i++)
        {
            int candidate = first + i;
            if (candidate + 1 > lastPort) break;
            if (!TryHttpPrefix(candidate, out lastError)) continue;
            if (!TryHttpPrefix(candidate + 1, out lastError)) continue;
            return (candidate, candidate + 1);
        }

        throw new Xunit.Sdk.XunitException(
            $"could not obtain a pair of ports this test controls after {attempts} attempts " +
            $"in {firstPort}..{lastPort} (walked from {first}): the last http.sys refusal was " +
            $"'{lastError}'. This test owns its ports on purpose; a host whose http.sys " +
            "registrations cover that whole range cannot run it, and that is reported here " +
            "rather than as a walk-up failure naming some unrelated port.");
    }
}
