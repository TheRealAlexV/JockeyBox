using System;
using System.Globalization;
using JockeyBox.Selftest;
using JockeyBox.Tools;

namespace JockeyBox.Api;

/// <summary>
/// Which port the loopback API binds: <c>--api-port &lt;n&gt;</c>, else
/// <c>JOCKEYBOX_API_PORT</c>, else <see cref="LocalApiServer.DefaultPort"/>.
///
/// <para><b>Why an override.</b> The Stream Deck plugin hardcodes 17890, so that
/// stays the default and nothing changes for the operator. But a sandbox that
/// bound 17890 answered the operator's plugin in place of their app - the plugin
/// then talked to the wrong process (every request 401, but still the wrong
/// process). A verification run must be able to sit on its own port.</para>
/// </summary>
public static class ApiPortOption
{
    public const string Flag = "--api-port";
    public const string Variable = "JOCKEYBOX_API_PORT";

    /// <summary>Where the chosen port came from, for the log line.</summary>
    public enum Origin
    {
        Default,
        Argument,
        Environment,
    }

    public readonly record struct Resolution(int Port, Origin Origin);

    /// <summary>
    /// Resolve the port. <paramref name="args"/> defaults to the process command
    /// line so the one caller need not thread argv through the view model.
    /// </summary>
    public static Resolution Resolve(string[]? args = null) =>
        Resolve(args ?? Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable);

    /// <summary>The same decision with the environment injected, so it is testable without mutating it.</summary>
    internal static Resolution Resolve(string[] args, Func<string, string?> readVariable)
    {
        string? argument = Harness.Flag(args, Flag);
        if (!string.IsNullOrWhiteSpace(argument))
            return Parsed(argument, Origin.Argument) ?? Fallback(argument, Flag);

        string? variable = readVariable(Variable);
        if (!string.IsNullOrWhiteSpace(variable))
            return Parsed(variable, Origin.Environment) ?? Fallback(variable, Variable);

        return new Resolution(LocalApiServer.DefaultPort, Origin.Default);
    }

    private static Resolution? Parsed(string raw, Origin origin) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
        && port is > 0 and <= 65535
            ? new Resolution(port, origin)
            : null;

    /// <summary>A bad override is loud but not fatal: fall back to the default port.</summary>
    private static Resolution Fallback(string raw, string source)
    {
        Log.Warn($"local api: ignoring {source}={raw}; not a port 1-65535. Using {LocalApiServer.DefaultPort}");
        return new Resolution(LocalApiServer.DefaultPort, Origin.Default);
    }
}
