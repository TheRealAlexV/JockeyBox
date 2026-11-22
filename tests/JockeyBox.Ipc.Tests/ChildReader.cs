using JockeyBox.Ipc;

namespace JockeyBox.Ipc.Tests;

/// <summary>
/// Child-process side of the cross-process proof. Runs the same test assembly with
/// <c>--child-reader &lt;bus&gt; &lt;blocks&gt;</c>, reads over the mapped region and prints one
/// machine-readable line the parent asserts on.
/// </summary>
internal static class ChildReader
{
    internal const string Switch = "--child-reader";

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] != Switch)
            return 0;

        if (args.Length < 3)
        {
            Console.Error.WriteLine("FAIL usage: --child-reader <bus> <blocks>");
            return 2;
        }

        string name = args[1];
        int wanted = int.Parse(args[2]);

        long verified = 0;
        long first = -1;
        long last = -1;
        long corrections = 0;
        long underruns = 0;

        var buffer = new float[0];
        var expected = new float[0];
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        try
        {
            using VirtualBusReader reader = VirtualBusReader.Open(name);
            buffer = new float[reader.BlockFrames * reader.Channels];
            expected = new float[buffer.Length];

            while (verified < wanted)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Console.WriteLine($"FAIL timeout verified={verified} corrections={reader.Corrections} underruns={reader.Underruns}");
                    return 1;
                }

                VirtualBusStatus status = reader.Read(buffer);
                if (status == VirtualBusStatus.Correction)
                {
                    corrections = reader.Corrections;
                    continue;
                }

                if (status == VirtualBusStatus.Silence)
                {
                    underruns = reader.Underruns;
                    Thread.Sleep(1);
                    continue;
                }

                // Every sample of block N carries N as a float; anything else is corruption.
                long blockIndex = (long)buffer[0];
                TestSignal.FillIdentified(expected, blockIndex);
                if (!TestSignal.BitEqual(expected, buffer))
                {
                    Console.WriteLine($"FAIL corrupt block={blockIndex} sample0={buffer[0]}");
                    return 1;
                }

                if (first < 0)
                    first = blockIndex;
                last = blockIndex;
                verified++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL exception={ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"OK pid={Environment.ProcessId} slot=- verified={verified} first={first} last={last} corrections={corrections} underruns={underruns}");
        return 0;
    }
}
