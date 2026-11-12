using System;
using System.IO;
using System.Runtime.CompilerServices;
using JockeyBox.Tools;

namespace JockeyBox.Tests;

/// <summary>
/// Points <c>JOCKEYBOX_HOME</c> at a throwaway directory before any test touches
/// <see cref="AppPaths.Home"/>.
///
/// <para><b>Why.</b> Only a few fixtures scope the home themselves; everything else
/// that happens to boot a store or write a log line inherited a null override and
/// wrote into the operator's live <c>%APPDATA%\JockeyBox\logs\jockeybox.log</c>. A
/// suite run then polluted the very log used to diagnose a field failure, with
/// thousands of lines that read exactly like real ones. A module initializer runs
/// when this assembly loads, which is before any test or fixture constructor, so
/// the default is safe for every test rather than for the ones that remembered.</para>
///
/// <para>The per-fixture scoping is unaffected: those fixtures read the current
/// value as their "previous" and restore it on dispose, so they still get their own
/// directory and hand back this one.</para>
/// </summary>
internal static class TestHome
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        string home = Path.Combine(
            Path.GetTempPath(), "jb-tests-home", Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);
    }
}
