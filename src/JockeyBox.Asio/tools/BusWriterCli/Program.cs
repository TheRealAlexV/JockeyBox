// BusWriterCli - the real C# producer, driven by the native harness.
//
// The harness proves bit-exactness against a C++ writer it controls; this
// program proves the same contract across the language boundary: it writes the
// same ramp through JockeyBox.Ipc.VirtualBusWriter, and the harness asserts the
// samples that reach the ASIO host are bit-identical.
//
//   BusWriterCli <blocks> [busName]

using System.Diagnostics;
using JockeyBox.Ipc;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: BusWriterCli <blocks> [busName]");
    return 2;
}

int blocks = int.Parse(args[0]);
string bus = args.Length > 1 ? args[1] : "main";

const int frames = 128;
const int channels = 2;
const int capacity = 16;
const int rate = 48000;

using var writer = VirtualBusWriter.Open(bus, rate, channels, frames, capacity);

var block = new float[frames * channels];
var clock = Stopwatch.StartNew();

for (int k = 0; k < blocks; k++)
{
    for (int f = 0; f < frames; f++)
        for (int c = 0; c < channels; c++)
            block[f * channels + c] = (k * 1000f) + (f * 2) + c;

    writer.Write(block);

    // Pace at the driver's block rate so most blocks are consumed in order.
    double due = (k + 1) * (double)frames / rate;
    while (clock.Elapsed.TotalSeconds < due)
    {
        if (due - clock.Elapsed.TotalSeconds > 0.001)
            Thread.Sleep(0);
        else
            Thread.SpinWait(50);
    }
}

Console.WriteLine($"published {blocks} blocks on '{bus}'");
return 0;
