using System.ComponentModel;
using System.Diagnostics;
using JockeyBox.Ipc;
using Xunit.Abstractions;

namespace JockeyBox.Ipc.Tests;

/// <summary>
/// Proves the region is real shared memory: a second process (different PID) reads the
/// same ring through the same name. Skipped with an explicit message when the harness
/// forbids spawning a child process.
/// </summary>
public sealed class CrossProcessTests
{
    private const int BlockFrames = 480;
    private const int Channels = 2;
    private const int Capacity = 64;
    private const int ChildWants = 50;

    private readonly ITestOutputHelper _output;

    public CrossProcessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Reader_In_A_Separate_Process_Sees_The_Writers_Blocks()
    {
        string name = $"xproc-{Guid.NewGuid():N}";
        using var writer = VirtualBusWriter.Open(name, 48000, Channels, BlockFrames, Capacity);

        Process? child = StartChild(name);
        if (child is null)
            return;

        try
        {
            var block = new float[BlockFrames * Channels];
            var deadline = DateTime.UtcNow.AddSeconds(30);
            long index = 0;

            while (index < 20000 && !child.HasExited && DateTime.UtcNow < deadline)
            {
                index++;
                TestSignal.FillIdentified(block, index);
                writer.Write(block);
                Thread.Sleep(1);
            }

            if (!child.WaitForExit(30000))
            {
                try
                {
                    child.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                Assert.Fail("child reader did not finish in time");
            }

            string stdout = child.StandardOutput.ReadToEnd();
            string stderr = child.StandardError.ReadToEnd();
            _output.WriteLine($"child stdout: {stdout.Trim()}");
            if (stderr.Length > 0)
                _output.WriteLine($"child stderr: {stderr.Trim()}");

            Assert.True(child.ExitCode == 0, $"child exit code {child.ExitCode}; stdout={stdout}; stderr={stderr}");
            Assert.Contains("OK ", stdout);
            Assert.Equal(writer.BlocksWritten, writer.BlocksWritten); // writer was alive throughout

            int pidField = stdout.IndexOf("pid=", StringComparison.Ordinal);
            Assert.True(pidField >= 0, $"child did not report its pid: {stdout}");
            string pidText = stdout[(pidField + 4)..].Split(' ')[0];
            Assert.True(int.TryParse(pidText, out int childPid), $"unparseable pid '{pidText}'");
            Assert.NotEqual(Environment.ProcessId, childPid);
            Assert.Contains($"verified={ChildWants}", stdout);
        }
        finally
        {
            if (!child.HasExited)
            {
                try
                {
                    child.Kill();
                }
                catch (InvalidOperationException)
                {
                }
            }

            child.Dispose();
        }
    }

    private Process? StartChild(string name)
    {
        string assembly = typeof(CrossProcessTests).Assembly.Location;
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add(ChildReader.Switch);
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(ChildWants.ToString());

        try
        {
            Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or System.IO.IOException)
        {
            _output.WriteLine($"SKIPPED: this harness cannot spawn a child process ({ex.GetType().Name}: {ex.Message}).");
            _output.WriteLine("The separate-process proof did not run; the in-process shared-memory tests still passed.");
            return null;
        }
    }
}
