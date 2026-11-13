using System;
using System.Net.Sockets;
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
        int requested = FreePort();
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
    public void Default_start_binds_17890_unless_something_else_owns_it()
    {
        using var server = NewServer();

        Assert.True(server.Start(LocalApiServer.DefaultPort), server.LastError);

        // 17890 is the port the live app and the Stream Deck plugin share, so on a
        // machine where the app (or another capture) is running it is legitimately
        // taken. What must never happen is a walk-up nobody reports.
        if (server.Port == LocalApiServer.DefaultPort) return;

        Assert.NotNull(server.LastError);
        Assert.InRange(server.Port, LocalApiServer.DefaultPort + 1, LocalApiServer.DefaultPort + 11);
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

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
