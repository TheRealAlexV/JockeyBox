using JockeyBox.Ipc.Tests;

internal static class Program
{
    // The test host never calls this. The cross-process proof runs the same assembly as a
    // child: `dotnet JockeyBox.Ipc.Tests.dll --child-reader <bus> <blocks>`.
    private static int Main(string[] args) => ChildReader.Run(args);
}
