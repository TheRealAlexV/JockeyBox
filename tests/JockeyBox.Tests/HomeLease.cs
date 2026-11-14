using System;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// A claim on the process-wide <c>JOCKEYBOX_HOME</c>, with a guard that turns a
/// race into a named failure instead of a flaky assertion somewhere else.
///
/// <para><b>Why.</b> <c>AppPaths.Home</c> reads the environment on every access, so
/// any test that sets it changes what every other concurrently-running test sees.
/// That is how <c>KaraokeRenderTests</c> failed intermittently: it reads the home
/// twice (once building the launch argv, once building the expected separator
/// argv) and compares them, so a home flip in between changes exactly one element,
/// <c>--model_file_dir</c>. Classes that mutate the home must therefore sit in
/// <see cref="JockeyBoxHomeCollection"/>, which xUnit runs without overlapping any
/// other collection. This lease is the second layer: if a class ever claims the
/// home while another already holds it, the claim fails immediately and names both
/// claimants, so the mistake surfaces as a clear error rather than as a flake in an
/// unrelated test.</para>
/// </summary>
internal sealed class HomeLease : IDisposable
{
    private static readonly object Gate = new();
    private static string? _holder;

    private readonly string? _previous;
    private bool _disposed;

    private HomeLease(string? previous) => _previous = previous;

    /// <summary>
    /// Take the home for <paramref name="owner"/>, which must name the class or
    /// test so a collision is diagnosable.
    /// </summary>
    public static HomeLease Enter(string home, string owner)
    {
        lock (Gate)
        {
            if (_holder != null)
            {
                throw new InvalidOperationException(
                    $"two tests claimed JOCKEYBOX_HOME at the same time: '{_holder}' and '{owner}'. " +
                    "A test class that mutates the shared home must be in " +
                    "[Collection(nameof(JockeyBoxHomeCollection))], which xUnit runs without " +
                    "overlapping any other collection.");
            }
            _holder = owner;
        }

        string? previous = Environment.GetEnvironmentVariable(AppPaths.HomeVariable);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);
        return new HomeLease(previous);
    }

    /// <summary>True while some test holds the home.</summary>
    public static bool IsHeld
    {
        get { lock (Gate) return _holder != null; }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, _previous);
            _holder = null;
        }
    }
}
