using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Runs its classes alone, never beside another collection.
///
/// <para><b>Why the selftest belongs here.</b> <see cref="SelftestRunner.Run"/>
/// executes the whole shipped <c>--selftest</c> in this process, so unlike a normal
/// test it samples process-wide statics that other collections are free to move:</para>
/// <list type="bullet">
/// <item><description><c>JOCKEYBOX_HOME</c>: the dock-page case points it at a
/// throwaway directory (<c>Selftest/DockPageProbe.SandboxHome</c>) because the main
/// window's view model loads <c>AppSettings</c> and opens the track-metadata store on
/// construction. Any class holding that variable at the same time sees a home flip
/// mid-test - the same failure <see cref="HomeLease"/> exists to name.</description></item>
/// <item><description>The single <see cref="System.Windows.Application"/> and its
/// dispatcher: the dock-page case builds the real main window on it, and WPF allows
/// exactly one per process (<see cref="UiThread"/>).</description></item>
/// <item><description><c>AudioEngine.Rate</c>/<c>Channels</c>/<c>BlockFrames</c>: read
/// by every case. They are <c>const</c>, so no test can move them - listed here so the
/// next person knows they were checked and deliberately left out of the
/// argument.</description></item>
/// </list>
///
/// <para>An earlier gate run went red on a selftest case and nobody could name it
/// afterwards: the suite was sampling these statics concurrently. Do not remove this
/// attribute to shorten the run - a red run that cannot be reproduced costs more than
/// the serialisation does.</para>
///
/// <para>The home-mutating classes live in <see cref="JockeyBoxHomeCollection"/> and
/// the dispatcher classes in <see cref="UiThreadCollection"/>; both are also
/// <c>DisableParallelization</c>, so a non-parallel collection of either kind never
/// overlaps this one.</para>
///
/// <para><b>Binding is by string.</b> xunit matches <c>[Collection]</c> to
/// <c>[CollectionDefinition]</c> by name, so a member must use
/// <see cref="Name"/>, not <c>nameof(ProcessStaticsCollection)</c> - the latter is
/// the class name, which is an <em>undefined</em> collection and therefore runs in
/// parallel with everything. (Measured on this suite: with the wrong form the
/// dock-page case overlapped 89 other results; with the right one, zero.) The same
/// mistake was live in eleven of the <c>"ui thread"</c> classes, which had named the
/// type instead; those ran unserialised, and they now carry
/// <c>[Collection(UiThreadCollection.Name)]</c>.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStaticsCollection
{
    public const string Name = "process statics";
}
