using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// Serialises every test class that drives <see cref="UiThread"/>.
///
/// <para><b>Why.</b> <see cref="UiThread"/> is one STA thread with one real
/// <see cref="System.Windows.Application"/> for the whole process, because WPF
/// allows only one application per process. xUnit parallelises by collection, so
/// while these classes were in separate collections four of them built, pumped and
/// tore down view models on that single dispatcher at the same time: timers firing
/// into half-disposed view models, collections mutated off the thread that owns
/// them, and a callback that throws on the dispatcher taking the whole test host
/// down with it (the run then aborts at whatever count it reached, naming five
/// innocent tests that happened to be in flight).</para>
///
/// <para>Splitting the clock is not possible: the shared thread is the point, since
/// a view model has to be created and driven on the thread that owns its bindings.
/// So the classes take turns instead, the same way the suites that share
/// <c>JOCKEYBOX_HOME</c> and the rendered karaoke fixtures already do.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiThreadCollection
{
    public const string Name = "ui thread";
}
