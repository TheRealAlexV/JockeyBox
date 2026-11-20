using System;
using System.Threading;
using System.Windows.Threading;

namespace JockeyBox.Tests;

/// <summary>
/// A live STA thread with a real <see cref="System.Windows.Application"/>, so a
/// WPF view model can be built and driven from a test. The main window is never
/// shown: only the pieces the view models touch exist.
///
/// <para>One per process on purpose - WPF allows a single Application instance -
/// and it keeps pumping its dispatcher, so the view models' BeginInvoke calls
/// (waveforms, rescan, status) actually run instead of piling up.</para>
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Lazy = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action) => Lazy.Value.Invoke(action);

    public static T Run<T>(Func<T> func) => (T)Lazy.Value.Invoke(func)!;

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            if (System.Windows.Application.Current == null)
            {
                var app = new System.Windows.Application
                {
                    // A test that opens and closes a dialog must not shut the
                    // shared Application down for every test that follows.
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                };
                _ = app;
            }
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "jockeybox-test-ui",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        ready.Wait();
        return dispatcher!;
    }
}
