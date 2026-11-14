using System;
using System.Collections.Generic;
using System.Threading;
using JockeyBox.Audio.Graph;
using JockeyBox.Tools;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The per-second audio trace is a diagnostic, not a heartbeat: off in a normal
/// launch, on when a probe asks for it. A live 10-second window of the operator's
/// log held 27 of these lines; at that rate a day of normal use is ~86 000 lines
/// into a 2 MB log, which buries the lines that would explain a failure.
/// </summary>
public sealed class AudioTraceGateTests
{
    [Fact]
    public void A_normal_render_writes_no_audio_trace_lines()
    {
        Assert.False(RenderAndCountTraceLines(enabled: false));
    }

    [Fact]
    public void The_trace_still_appears_when_it_is_enabled()
    {
        // The --audiotrace mode, the capture modes and the selftest depend on the
        // line; this is the other half of the switch, not a weaker assertion.
        Assert.True(RenderAndCountTraceLines(enabled: true));
    }

    /// <summary>
    /// Render two seconds offline and report whether the log carried a trace line.
    /// <see cref="Log.LineWritten"/> is the honest probe: it fires for the line the
    /// render thread actually writes, not for a string we could have assembled.
    /// </summary>
    private static bool RenderAndCountTraceLines(bool enabled)
    {
        bool previous = AudioEngine.TraceEnabled;
        AudioEngine.TraceEnabled = enabled;

        var gate = new object();
        bool seen = false;
        void Handler(string line)
        {
            if (!line.Contains("audio trace:")) return;
            lock (gate) seen = true;
        }

        Log.LineWritten += Handler;
        try
        {
            // Longer than one second so TraceIfDue's interval has passed even if the
            // engine happens to start its frame counter at a non-zero value.
            using var engine = new AudioEngine();
            using var done = new ManualResetEventSlim(false);
            long frames = 0;

            engine.StartOffline(
                capture: (_, n) =>
                {
                    if (Interlocked.Add(ref frames, n) >= AudioEngine.Rate * 2) done.Set();
                },
                seconds: 3);

            Assert.True(done.Wait(TimeSpan.FromSeconds(20)), "offline engine did not render two seconds");
            engine.Stop();

            lock (gate) return seen;
        }
        finally
        {
            Log.LineWritten -= Handler;
            AudioEngine.TraceEnabled = previous;
        }
    }
}
