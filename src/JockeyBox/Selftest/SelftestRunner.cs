using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using JockeyBox.Audio.Beat;
using JockeyBox.Audio.Graph;
using JockeyBox.Audio.Transition;
using JockeyBox.Dsp;
using JockeyBox.Playback;
using JockeyBox.Settings;

namespace JockeyBox.Selftest;

/// <summary>
/// The offline audio-graph self-test: <c>JockeyBox.exe --selftest</c>.
///
/// <para><b>Why it exists.</b> The graph's hard parts — the equal-power crossfade,
/// the tempo stretcher, the soft limiter and the program delay — are invisible
/// until a mix sounds wrong, and they cannot be asserted by ear on a machine with
/// no sound card. Every case here renders through <see cref="AudioEngine.StartOffline"/>,
/// which is the same render loop the live engine runs, and measures the captured
/// programme bus with plain arithmetic. No device, no fixture file, no network:
/// a WSL-built win-x64 binary can run this on the Windows host and CI can run it
/// without audio hardware.</para>
///
/// <para><b>Exit code.</b> 0 when every case passes, 1 otherwise, so it can gate a
/// build. Each case prints one line; a failing case prints the measurement that
/// failed rather than a generic message.</para>
/// </summary>
public static class SelftestRunner
{
    /// <summary>Run every case. Returns the process exit code (0 pass, 1 fail).</summary>
    public static int Run(TextWriter output, bool verbose = false)
    {
        var cases = new (string Name, Action<Case> Body)[]
        {
            ("silence-at-rest", SilenceAtRest),
            ("peak-headroom", PeakHeadroom),
            ("tempo-ratio", TempoRatio),
            ("output-delay", OutputDelay),
            ("crossfade-law", CrossfadeLaw),
            ("no-zero-reads", NoZeroReads),
            ("end-of-track-handoff", EndOfTrackHandoff),
            ("deck-output-delay", DeckOutputDelay),
            ("chain-bypass-bit-exact", ChainBypassBitExact),
            ("chain-latency-align-null", ChainLatencyAlignNull),
            ("eq-flat-bit-exact", EqFlatBitExact),
        };

        output.WriteLine("JockeyBox selftest - offline audio graph, no sound device");
        output.WriteLine($"engine {AudioEngine.Rate} Hz, {AudioEngine.Channels} ch, " +
                         $"{AudioEngine.BlockFrames} frames/block");

        int passed = 0;
        foreach (var (name, body) in cases)
        {
            var record = new Case(name);
            try
            {
                body(record);
                passed++;
                output.WriteLine(record.Notes.Count == 0
                    ? $"PASS {name}"
                    : $"PASS {name}: {string.Join(", ", record.Notes)}");
            }
            catch (CaseFailed failure)
            {
                output.WriteLine($"FAIL {name}: {failure.Message}");
                if (record.Notes.Count > 0)
                    output.WriteLine($"     measured: {string.Join(", ", record.Notes)}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                if (verbose) output.WriteLine(ex.ToString());
            }
        }

        if (passed == cases.Length)
        {
            output.WriteLine($"SELFTEST OK {passed}/{cases.Length}");
            return 0;
        }

        output.WriteLine($"SELFTEST FAILED {passed}/{cases.Length}");
        return 1;
    }

    // ------------------------------------------------------------------ cases

    /// <summary>
    /// 1. Nothing loaded, nothing playing: 1 s of render must be exactly zero, not
    /// merely quiet. A graph that leaks a DC offset, a limiter make-up gain or a
    /// stale block would show up here and nowhere else.
    /// </summary>
    private static void SilenceAtRest(Case test)
    {
        using var rig = new Rig("silence");
        rig.Run(1.0);

        int expected = Frames(1.0);
        int nonZero = SelftestSignals.CountNonZero(rig.Buffer, 0, rig.Buffer.Length);
        test.Note($"frames={rig.Frames}", $"nonzero={nonZero}", $"peak={Db(rig.Peak):0.0} dBFS");

        Require(rig.Frames == expected, $"rendered {rig.Frames} frames, expected {expected}");
        Require(nonZero == 0, $"{nonZero} non-zero samples while idle (first at {SelftestSignals.FirstNonZero(rig.Buffer)})");
        Require(rig.Peak == 0, $"peak {rig.Peak:0.000000} while idle");
    }

    /// <summary>
    /// 2. A full-scale tone on deck A at unity fader: the soft limiter must hold the
    /// bus at or below -1 dBFS and must not crush it below -6 dBFS. The 440 Hz tone
    /// is at -1 dBFS, i.e. exactly the level where the limiter starts shaping, so
    /// the case proves the limiter is present <i>and</i> gentle.
    /// </summary>
    private static void PeakHeadroom(Case test)
    {
        using var rig = new Rig("peak");
        rig.Load(Deck.A, Tones.Sine(440, 2.0, SelftestSignals.FromDb(-1.0)));
        rig.Run(2.0);
        test.Note($"duration={rig.Engine.DeckA.DurationSeconds:0.000}s", $"state={rig.Engine.DeckA.State}",
            $"frames={rig.Frames}", $"samples={rig.Buffer.Length}", $"peak={rig.Peak:0.000000}");

        double peakDb = SelftestSignals.Db(rig.Peak);
        double rmsDb = SelftestSignals.RmsDb(rig.Buffer, 0, rig.Frames);
        test.Note($"peak={peakDb:0.00} dBFS", $"rms={rmsDb:0.00} dBFS",
            $"limiter={SelftestSignals.FromDb(peakDb) / SelftestSignals.FromDb(-1.0):0.0000}");

        Require(peakDb > -60.0, $"no signal on the program bus (peak {peakDb:0.00} dBFS)");
        // The window below is what pins the level; the line above only proves the
        // bus is not silent. It used to read `peakDb > 0.0`, which no limited bus
        // can ever satisfy: dBFS is non-positive by definition, and a -1 dBFS tone
        // through the tanh knee lands at -1.21 dBFS (0.8913 in, 0.8702 out, ratio
        // 0.9764 - exactly the limiter's law), so the case reported "no signal"
        // over its own correct measurement.
        Require(peakDb <= -1.0, $"peak {peakDb:0.00} dBFS exceeds the -1 dBFS ceiling");
        Require(peakDb >= -6.0, $"peak {peakDb:0.00} dBFS is below -6 dBFS: the limiter is crushing a -1 dBFS tone");
        Require(peakDb >= -12.0, $"peak {peakDb:0.00} dBFS is below -12 dBFS: headroom check failed");
    }

    /// <summary>
    /// 3. The tempo stretcher. The tone must come out at the same pitch at ratios
    /// 0.92 and 1.08 (key lock), and a fixed <i>media</i> distance must come out at
    /// 2/ratio seconds of output time, which the engine reports as a deck position
    /// of ratio x output seconds.
    ///
    /// <para>The distance between two silent-separated bursts is measured rather
    /// than an absolute position, because the stretcher adds a constant group
    /// delay: a distance is what tempo actually changes and it cancels that
    /// delay.</para>
    /// </summary>
    private static void TempoRatio(Case test)
    {
        foreach (double ratio in new[] { 0.92, 1.08 })
        {
            using var rig = new Rig($"tempo-{ratio:0.00}");
            rig.Load(Deck.A, Tones.WithBursts(4.0, 0.5,
                (0.2, 0.15, 440.0), (2.2, 0.15, 440.0)));
            rig.Engine.DeckA.SetTempo(ratio);
            rig.Run(3.0);

            var runs = SelftestSignals.Runs(rig.Buffer, rig.Frames);
            Require(runs.Count == 2, $"ratio {ratio:0.00}: found {runs.Count} bursts, expected 2");

            double distance = runs[1].PeakSeconds - runs[0].PeakSeconds;
            double expected = 2.0 / ratio;
            double distanceError = Math.Abs(distance - expected) / expected;

            int windowStart = (int)Math.Round((runs[0].PeakSeconds - 0.03) * AudioEngine.Rate);
            double hertz = SelftestSignals.ZeroCrossFrequency(rig.Buffer, windowStart,
                Frames(0.05));
            double pitchError = Math.Abs(hertz - 440.0) / 440.0;

            double elapsed = rig.Frames / (double)AudioEngine.Rate;
            double position = rig.Engine.DeckA.PositionSeconds;
            double positionError = Math.Abs(position - ratio * elapsed);

            test.Note($"ratio={ratio:0.00}", $"burst-gap={distance:0.0000}s/{expected:0.0000}s",
                $"f={hertz:0.0} Hz", $"pos={position:0.000}s/{ratio * elapsed:0.000}s",
                $"engine-ratio={rig.Engine.DeckA.TempoRatio:0.0000}");

            Require(Math.Abs(rig.Engine.DeckA.TempoRatio - ratio) < 1e-6,
                $"ratio {ratio:0.00}: stretcher reports {rig.Engine.DeckA.TempoRatio:0.0000}");
            Require(pitchError <= 0.015,
                $"ratio {ratio:0.00}: pitch drifted to {hertz:0.0} Hz " +
                $"({pitchError * 100:0.00} % off 440 Hz, tolerance 1.5 %)");
            Require(distanceError <= 0.015,
                $"ratio {ratio:0.00}: burst gap {distance:0.0000}s, expected {expected:0.0000}s " +
                $"({distanceError * 100:0.00} % off, tolerance 1.5 %)");
            Require(positionError <= 0.05,
                $"ratio {ratio:0.00}: position {position:0.000}s, expected {ratio * elapsed:0.000}s " +
                $"(2 s of source in 2/ratio s of output)");
        }
    }

    /// <summary>
    /// 4. The global program delay (the OBS lip-sync offset). With 250 ms set and
    /// an impulse at media time zero, the captured programme must be exactly silent
    /// for 250 ms (±1 block) and the impulse must land on that offset, sample
    /// exact, because the delay is a tap on a ring, not an interpolation.
    ///
    /// <para>The monitor bus must <i>not</i> be delayed: the design takes it
    /// pre-delay so the performer hears the deck now while the programme waits for
    /// the video. Measuring it here is what keeps a future "just delay the master
    /// sink" change from reintroducing a doubled delay, which is invisible on
    /// headphones and only shows up as a 500 ms lip-sync error in OBS.</para>
    /// </summary>
    private static void OutputDelay(Case test)
    {
        using var rig = new Rig("delay");

        // Set the delay before any audio: the tap change is crossfaded over 50 ms,
        // so let that settle on silence rather than measuring through it.
        rig.Engine.OutputDelayMs = 250;

        // MON on: the monitor bus carries deck audio only while MON is enabled
        // (the deck sends are the operator's cue, gated by their own switch), and
        // this case measures that the cue stays pre-delay.
        rig.Engine.MonitorEnabled = true;
        rig.Engine.MonitorAGain = 1f;
        rig.Run(0.2);
        int warmUp = SelftestSignals.CountNonZero(rig.Buffer, 0, rig.Buffer.Length);
        Require(warmUp == 0, $"delay warm-up is not silent ({warmUp} non-zero samples)");
        Require(rig.Engine.OutputDelayMs == 250, $"delay reads {rig.Engine.OutputDelayMs} ms, expected 250");

        rig.Load(Deck.A, Tones.Impulse(1.0, 0.0));
        rig.Run(1.0);

        // Frames, not samples: the capture is interleaved stereo, so an impulse on
        // frame 12000 sits at sample 24000. Reading the sample index and comparing
        // it with a frame offset made a correct 250 ms tap look like 500 ms.
        int first = SelftestSignals.FirstNonZeroFrame(rig.Buffer);
        int expectedOffset = Frames(0.25);
        int monitorFirst = SelftestSignals.FirstNonZeroFrame(rig.MonitorBuffer);
        test.Note($"first-nonzero-frame={first}", $"expected={expectedOffset}",
            $"delay={rig.Engine.OutputDelayMs} ms", $"deck-state={rig.Engine.DeckA.State}",
            $"deck-duration={rig.Engine.DeckA.DurationSeconds:0.000}s",
            $"monitor-first={monitorFirst}");

        using (var control = new Rig("delay-control"))
        {
            control.Load(Deck.A, Tones.Impulse(1.0, 0.0));
            control.Run(1.0);
            test.Note($"0ms-first-nonzero={SelftestSignals.FirstNonZeroFrame(control.Buffer)}",
                $"0ms-peak={control.Peak:0.0000}");
        }

        Require(first >= 0, "the impulse never reached the program bus");
        Require(Math.Abs(first - expectedOffset) <= AudioEngine.BlockFrames,
            $"impulse at frame {first} ({first / (double)AudioEngine.Rate:0.0000}s), " +
            $"expected {expectedOffset} ({expectedOffset / (double)AudioEngine.Rate:0.0000}s) " +
            $"for a 250 ms delay, tolerance 1 block");
        Require(SelftestSignals.Peak(rig.Buffer, first * AudioEngine.Channels, AudioEngine.Channels) > 1e-4,
            "the sample at the delay offset is not the impulse");
        Require(SelftestSignals.CountNonZero(rig.Buffer, 0, Math.Max(0, first) * AudioEngine.Channels) == 0,
            "audio leaked ahead of the delay offset");

        // Monitor is pre-delay by design: same block, no offset.
        Require(monitorFirst == 0,
            $"the monitor bus is delayed too (first non-zero frame {monitorFirst}, expected 0): " +
            "the delay must be applied once, on the program bus only");
        Require(SelftestSignals.Peak(rig.MonitorBuffer, 0, AudioEngine.Channels) > 1e-4,
            "the monitor bus has no impulse at frame 0");
    }

    /// <summary>
    /// 5. The equal-power crossfade law, proved on the <i>audio</i> and not just on
    /// the envelope (cos² + sin² = 1 is true of any cosine pair, so reading the
    /// envelope alone would pass even if the deck never applied it). Two tones an
    /// octave-and-a-fifth apart are separated with single-bin Goertzel windows of
    /// exactly 22 and 33 periods, so the measurement has no leakage, and the
    /// per-window amplitudes are compared with the gains the transport reports for
    /// that same frame.
    /// </summary>
    private static void CrossfadeLaw(Case test)
    {
        const double amplitude = 0.25;   // two decks sum to 0.354 at the centre: no limiting
        const double fadeSeconds = 2.0;

        using var rig = new Rig("crossfade");
        rig.Load(Deck.A, Tones.Sine(440, 3.0, amplitude));
        rig.Load(Deck.B, Tones.Sine(660, 3.0, amplitude));
        rig.Engine.RampFader(1.0, fadeSeconds, from: 0.0);
        rig.Run(2.5);

        long start = rig.Engine.Fader.StartFrame;
        long frames = rig.Engine.Fader.FrameCount;
        Require(frames > 0, "the fader ramp never started");

        // Envelope law over the whole ramp.
        double worstLaw = 0;
        for (long frame = start; frame <= start + frames; frame += AudioEngine.BlockFrames)
        {
            double gainA = Crossfade.GainA(rig.Engine.Fader.FaderAt(frame));
            double gainB = Crossfade.GainB(rig.Engine.Fader.FaderAt(frame));
            worstLaw = Math.Max(worstLaw, Math.Abs(gainA * gainA + gainB * gainB - 1.0));
        }

        // Audio law, per 50 ms window inside the ramp.
        const int window = 2400;         // 22 periods of 440 Hz, 33 of 660 Hz
        double worstA = 0, worstB = 0;
        int windows = 0;

        for (long offset = start + window; offset + window <= start + frames; offset += window)
        {
            int startFrame = (int)(offset - rig.CaptureOrigin);
            if (startFrame < 0 || startFrame + window > rig.Frames) continue;

            double measuredA = SelftestSignals.ToneAmplitude(rig.Buffer, startFrame, window, 440) / amplitude;
            double measuredB = SelftestSignals.ToneAmplitude(rig.Buffer, startFrame, window, 660) / amplitude;

            long centre = rig.CaptureOrigin + startFrame + window / 2;
            double fader = rig.Engine.Fader.FaderAt(centre);
            double expectedA = Crossfade.GainA(fader);
            double expectedB = Crossfade.GainB(fader);

            worstA = Math.Max(worstA, Math.Abs(measuredA - expectedA));
            worstB = Math.Max(worstB, Math.Abs(measuredB - expectedB));
            windows++;
        }

        test.Note($"windows={windows}", $"max|gA2+gB2-1|={worstLaw:0.000000}",
            $"max dA={worstA:0.0000}", $"max dB={worstB:0.0000}");

        Require(windows >= 30, $"only {windows} measurement windows inside the fade");
        Require(worstLaw <= 1e-3, $"equal-power law: max |gainA^2+gainB^2-1| {worstLaw:0.000000} > 1e-3");
        Require(worstA <= 0.02,
            $"deck A gain deviates by {worstA:0.0000} from the equal-power curve (tolerance 0.02)");
        Require(worstB <= 0.02,
            $"deck B gain deviates by {worstB:0.0000} from the equal-power curve (tolerance 0.02)");
        Require(rig.Engine.Fader.IsComplete(start + frames),
            "the fader never reached the end of its ramp");
    }

    /// <summary>
    /// 6. A paused deck returns a full block of silence rather than 0 samples. In
    /// NAudio a 0 read means end-of-stream and stalls every consumer upstream, so a
    /// pause that short-circuits the block would stop the engine instead of muting
    /// one deck. The transport must report Paused, not Ended, and the engine must
    /// keep producing blocks.
    /// </summary>
    private static void NoZeroReads(Case test)
    {
        using var rig = new Rig("paused");
        rig.Load(Deck.A, Tones.Sine(440, 2.0, 0.5));
        rig.Run(0.2);
        Require(rig.Peak > 0.1, "deck A is not audible before the pause");

        rig.Engine.DeckA.Pause();
        rig.Run(0.5);

        int expected = Frames(0.5);
        int nonZero = SelftestSignals.CountNonZero(rig.Buffer, 0, rig.Buffer.Length);
        test.Note($"frames={rig.Frames}/{expected}", $"state={rig.Engine.DeckA.State}", $"nonzero={nonZero}");

        Require(rig.Frames == expected, $"engine rendered {rig.Frames} frames of {expected} while a deck was paused");
        Require(rig.Engine.DeckA.State == DeckSource.DeckState.Paused,
            $"deck state is {rig.Engine.DeckA.State}, expected Paused (not Ended)");
        Require(nonZero == 0, $"paused deck emitted {nonZero} non-zero samples");
        Require(rig.Peak == 0, $"paused deck peak {rig.Peak:0.000000}");
    }

    /// <summary>
    /// 7. End-of-track hand-off. With a queued transition (fade completes where the
    /// outgoing track ends) the scheduler must start the fade one fade-length before
    /// the 1 s deadline, complete it at that deadline within a block, have the
    /// incoming deck audible afterwards, and leave the outgoing deck Ended rather
    /// than looping or hanging.
    /// </summary>
    private static void EndOfTrackHandoff(Case test)
    {
        using var rig = new Rig("handoff");
        rig.Load(Deck.A, Tones.Sine(440, 1.0, 0.25));
        rig.Load(Deck.B, Tones.Sine(660, 6.0, 0.25), play: false);

        var scheduler = new TransitionScheduler(rig.Engine, new NoBeatGrid())
        {
            Mode = TransitionMode.AutoCrossfade,
            FadeSeconds = 0.5,
        };

        long completedAt = -1;
        scheduler.Completed += _ => completedAt = rig.Engine.FrameIndex;

        var plan = scheduler.Begin(rig.Engine.SlotA, rig.Engine.SlotB, atTrackEnd: true);
        Require(plan != null, "the scheduler refused a transition with both decks loaded "
            + $"(A duration {rig.Engine.DeckA.DurationSeconds:0.000}s state {rig.Engine.DeckA.State}, "
            + $"B duration {rig.Engine.DeckB.DurationSeconds:0.000}s state {rig.Engine.DeckB.State})");
        Require(plan!.To == PlaybackDeck.B, $"transition goes to deck {plan.To}, expected B");

        long deadline = rig.Engine.FrameIndex + AudioEngine.FramesFor(1.0);   // media end of A
        long expectedStart = deadline - AudioEngine.FramesFor(0.5);

        rig.Run(2.0);

        long faderStart = rig.Engine.Fader.StartFrame;
        double incoming = SelftestSignals.ToneAmplitude(rig.Buffer,
            Frames(1.1), Frames(0.5), 660);

        test.Note($"fade-start={faderStart} (deadline{expectedStart:+#;-#;0})",
            $"completed={completedAt}", $"deadline={deadline}",
            $"deckA={rig.Engine.DeckA.State}", $"deckB=660Hz {incoming:0.000}");

        Require(Math.Abs(faderStart - expectedStart) <= 2 * AudioEngine.BlockFrames,
            $"fade started at frame {faderStart}, expected {expectedStart} " +
            $"(1 s deadline minus a 0.5 s fade), tolerance 2 blocks");
        Require(completedAt >= 0, "the transition never completed");
        Require(Math.Abs(completedAt - deadline) <= 2 * AudioEngine.BlockFrames,
            $"transition completed at frame {completedAt}, deadline {deadline}, tolerance 2 blocks");
        Require(!scheduler.IsActive, "the scheduler still reports a live transition after the deadline");
        Require(rig.Engine.DeckA.State == DeckSource.DeckState.Ended,
            $"outgoing deck is {rig.Engine.DeckA.State}, expected Ended");
        Require(incoming >= 0.8 * 0.25,
            $"deck B is not audible after the hand-off (660 Hz amplitude {incoming:0.000}, expected 0.250)");
    }

    // ------------------------------------------------------------- chain seam

    /// <summary>
    /// An empty chain, and a chain whose every processor is bypassed, must leave an
    /// interleaved stereo block byte-for-byte identical. Raw bytes, no tolerance: the
    /// claim is exactness.
    /// </summary>
    private static void ChainBypassBitExact(Case test)
    {
        const int frames = AudioEngine.BlockFrames;
        var input = new float[frames * AudioEngine.Channels];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(0.5 * Math.Sin(i * 0.017) + (i % 7) * 0.001);

        var empty = new InterleavedStripChain(AudioEngine.Channels, frames);
        var output = (float[])input.Clone();
        empty.Process(output, 0, frames);
        Require(empty.IsPassthrough, "an empty chain does not report passthrough");
        Require(MemoryMarshal.AsBytes(output.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(input.AsSpan())),
            "an empty chain changed the block");

        var bypassed = new InterleavedStripChain(AudioEngine.Channels, frames);
        for (int c = 0; c < AudioEngine.Channels; c++)
        {
            bypassed[c].Add(new GainStage($"gain.{c}") { Bypass = true });
            bypassed[c].Add(new LowPassFilter(AudioEngine.Rate, 1_000, id: $"lowpass.{c}") { Bypass = true });
        }

        output = (float[])input.Clone();
        bypassed.Process(output, 0, frames);
        Require(bypassed.IsPassthrough, "a fully bypassed chain does not report passthrough");
        Require(MemoryMarshal.AsBytes(output.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(input.AsSpan())),
            "a fully bypassed chain changed the block");

        test.Note("empty and bypassed chains are bit-identical");
    }

    /// <summary>
    /// Two chains declare different latency; the shorter one is padded by the declared
    /// difference and the two outputs cross-correlated. The best lag must be exactly 0,
    /// so a declared LatencyFrames that lies would move the peak.
    /// </summary>
    private static void ChainLatencyAlignNull(Case test)
    {
        const int frames = AudioEngine.BlockFrames;
        const int shortDelay = 6;
        const int longDelay = 11;
        const int maxLag = 32;

        var chirp = new float[frames];
        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double hz = 500 + 3500.0 * i / frames;
            chirp[i] = (float)(0.6 * Math.Sin(phase));
            phase += 2 * Math.PI * hz / AudioEngine.Rate;
        }

        var shortChain = new InterleavedStripChain(1, frames);
        shortChain[0].Add(new LatencyProbe("probe.short", shortDelay));
        var longChain = new InterleavedStripChain(1, frames);
        longChain[0].Add(new LatencyProbe("probe.long", longDelay));

        var shortOut = (float[])chirp.Clone();
        var longOut = (float[])chirp.Clone();
        shortChain.Process(shortOut, 0, frames);
        longChain.Process(longOut, 0, frames);

        Require(shortChain.LatencyFrames == shortDelay,
            $"short chain reports {shortChain.LatencyFrames} frames, declared {shortDelay}");
        Require(longChain.LatencyFrames == longDelay,
            $"long chain reports {longChain.LatencyFrames} frames, declared {longDelay}");

        int pad = longChain.LatencyFrames - shortChain.LatencyFrames;
        Require(pad > 0, "the two chains do not declare different latency");

        var padded = new float[frames];
        for (int i = 0; i + pad < frames; i++) padded[i + pad] = shortOut[i];

        int bestLag = 0;
        double bestScore = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double score = 0;
            for (int i = 0; i < frames; i++)
            {
                int j = i + lag;
                if (j < 0 || j >= frames) continue;
                score += padded[i] * (double)longOut[j];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        Require(bestScore > 0, "the cross-correlation found no signal");
        Require(bestLag == 0,
            $"best lag is {bestLag} frames, expected 0 (declared latency {shortDelay} vs {longDelay})");
        test.Note($"best lag 0 over +/-{maxLag} frames");
    }

    /// <summary>
    /// The mix EQ, at engine level. Two claims, in this order. A band set at its
    /// centre must leave the deck block byte-for-byte identical on both
    /// <see cref="EqPath"/>s - the same contract the DSP ten-band EQ holds - and a
    /// band moved off centre must visibly change the block, otherwise the flat case
    /// would pass on an EQ that is simply disconnected. Each path keeps its own
    /// filter state, so both are exercised through one instance.
    /// </summary>
    private static void EqFlatBitExact(Case test)
    {
        const int frames = AudioEngine.BlockFrames;
        var input = new float[frames * AudioEngine.Channels];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(0.5 * Math.Sin(i * 0.017) + (i % 7) * 0.001);

        var eq = new MixEq();
        eq.SetAll(0, 0, 0);
        for (int i = 0; i < 64; i++) eq.BeginBlock(); // settle the smoothing at centre

        var paths = new[] { EqPath.DeckA, EqPath.DeckB };
        foreach (var path in paths)
        {
            var output = (float[])input.Clone();
            eq.Process(output, 0, frames, path);
            Require(MemoryMarshal.AsBytes(output.AsSpan()).SequenceEqual(
                    MemoryMarshal.AsBytes(input.AsSpan())),
                $"{path} at flat changed the block");
        }

        // Negative half: move the low band 12 dB up and prove both paths react.
        eq.SetTargetDb(EqBand.Low, 12f);
        for (int i = 0; i < 64; i++) eq.BeginBlock();

        double maxDeviation = 0;
        foreach (var path in paths)
        {
            var output = (float[])input.Clone();
            eq.Process(output, 0, frames, path);
            double pathDeviation = 0;
            for (int i = 0; i < output.Length; i++)
                pathDeviation = Math.Max(pathDeviation, Math.Abs(output[i] - (double)input[i]));
            Require(pathDeviation > 0,
                $"{path} at +12 dB low is identical to the input: the flat case proves nothing");
            maxDeviation = Math.Max(maxDeviation, pathDeviation);
        }

        test.Note($"paths={paths.Length}", "flat-bit-exact", $"max|dev|={maxDeviation:0.000000}");
    }

    /// <summary>Test double: a fixed frame delay that declares exactly that latency.</summary>
    private sealed class LatencyProbe : IAudioProcessor
    {
        private readonly int _delay;
        private readonly float[] _history;
        private int _written;

        public LatencyProbe(string id, int delayFrames)
        {
            Id = id;
            _delay = delayFrames;
            _history = new float[delayFrames];
        }

        public string Id { get; }

        public int LatencyFrames => _delay;

        public bool Bypass { get; set; }

        public void Process(Span<float> samples)
        {
            if (Bypass) return;
            for (int i = 0; i < samples.Length; i++)
            {
                int slot = _written % _delay;
                float incoming = samples[i];
                samples[i] = _history[slot];
                _history[slot] = incoming;
                _written++;
            }
        }

        public void Reset()
        {
            Array.Clear(_history, 0, _history.Length);
            _written = 0;
        }
    }

    // ------------------------------------------------------------------ harness

    private sealed class Case
    {
        public Case(string name) => Name = name;

        public string Name { get; }

        public List<string> Notes { get; } = new();

        public void Note(params string[] notes) => Notes.AddRange(notes);
    }

    private sealed class CaseFailed : Exception
    {
        public CaseFailed(string message) : base(message) { }
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new CaseFailed(detail);
    }

    private static double Db(double amplitude) => SelftestSignals.Db(amplitude);

    /// <summary>Whole frames for a duration at the engine rate.</summary>
    private static int Frames(double seconds) => (int)AudioEngine.FramesFor(seconds);

    private enum Deck { A, B }

    /// <summary>
    /// One engine, one temporary media directory, one capture buffer. Each case
    /// gets a fresh engine so a stuck envelope, a stale queue or a leftover delay tap
    /// in one case cannot make another pass.
    /// </summary>
    /// <summary>
    /// 8. A deck's own output delay, in frames. Deck A is delayed 250 ms and deck B
    /// is not, in one render: 12 000 frames apart at 48 kHz, with deck B unmoved -
    /// the whole point of a per-deck delay, which one shared number cannot express.
    /// The monitor tap and the program sum are read out of the same run, because a
    /// shift that stopped at the deck bus would be inaudible.
    /// </summary>
    private static void DeckOutputDelay(Case test)
    {
        var a = Tones.Impulse(0.6, 0.0);              // 0.5 at frame 0
        var b = Tones.Impulse(0.6, 0.0);
        for (int i = 0; i < b.Length; i++) b[i] *= 0.5f; // 0.25 at frame 0

        using var delayed = new Rig("deck-delay-250");
        delayed.Engine.Fader.Snap(0.5, 0);
        delayed.Engine.MonitorEnabled = true;
        delayed.Engine.MonitorAGain = 1f;
        delayed.Engine.MonitorBGain = 1f;
        delayed.Engine.SetDeckOutputDelayMs(true, 250);
        delayed.Engine.SetDeckOutputDelayMs(false, 0);
        delayed.Load(Deck.A, a);
        delayed.Load(Deck.B, b);
        delayed.Run(0.4);

        var busA = delayed.BusBuffer(BusKind.DeckA);
        var busB = delayed.BusBuffer(BusKind.DeckB);
        var mon = delayed.MonitorBuffer;
        var prog = delayed.Buffer;

        int frameA = SelftestSignals.FirstNonZeroFrame(busA);
        int frameB = SelftestSignals.FirstNonZeroFrame(busB);
        test.Note($"deckA={frameA}", $"deckB={frameB}",
            $"monitor={PeakAround(mon, 12_000):0.000}",
            $"program={PeakAround(prog, 12_000):0.000}");

        Require(frameA == 12_000, $"deck A impulse at frame {frameA}, expected 12 000 (250 ms)");
        Require(frameB == 0, $"deck B impulse at frame {frameB}: deck A's delay moved deck B");
        Require(Math.Abs(PeakAround(mon, 12_000) - 0.5) < 1e-3,
            $"monitor tap does not carry deck A's shift (peak {PeakAround(mon, 12_000):0.000})");
        Require(Math.Abs(PeakAround(prog, 12_000) - 0.5 * 0.70710678) < 1e-3,
            $"program sum does not carry deck A's shift (peak {PeakAround(prog, 12_000):0.000})");

        // 0 ms is the identity: the same file's impulse at frame 0, and the 250 ms
        // bus equals it sample for sample once shifted back by 12 000 frames.
        using var plain = new Rig("deck-delay-0");
        plain.Engine.Fader.Snap(0.5, 0);
        plain.Engine.SetDeckOutputDelayMs(true, 0);
        plain.Engine.SetDeckOutputDelayMs(false, 0);
        plain.Load(Deck.A, a);
        plain.Load(Deck.B, b);
        plain.Run(0.4);

        var zero = plain.BusBuffer(BusKind.DeckA);
        int frameZero = SelftestSignals.FirstNonZeroFrame(zero);
        int shift = 12_000 * AudioEngine.Channels;
        int common = Math.Min(zero.Length, busA.Length - shift);
        int mismatch = -1;
        for (int i = 0; i < common; i++)
            if (zero[i] != busA[i + shift]) { mismatch = i; break; }

        test.Note($"zero={frameZero}", $"shiftCompare={common} samples");
        Require(frameZero == 0, $"deck at 0 ms put its impulse at frame {frameZero}");
        Require(Math.Abs(zero[0] - 0.5f * 0.70710678f) < 1e-4,
            $"0 ms impulse is {zero[0]:0.000000}, expected the source amplitude");
        Require(mismatch < 0,
            $"the 250 ms deck is not the 0 ms deck shifted by 12 000 frames (first mismatch at sample {mismatch})");
    }

    /// <summary>Peak of a stereo buffer in the two frames either side of <paramref name="frame"/>.</summary>
    private static double PeakAround(float[] interleaved, int frame)
    {
        double peak = 0;
        for (int f = Math.Max(0, frame - 1); f <= frame + 1; f++)
        {
            int i = f * AudioEngine.Channels;
            if (i + 1 >= interleaved.Length) break;
            peak = Math.Max(peak, Math.Max(Math.Abs(interleaved[i]), Math.Abs(interleaved[i + 1])));
        }
        return peak;
    }

    private sealed class Rig : IDisposable
    {
        private readonly List<float> _captured = new();
        private readonly List<float> _monitored = new();
        private readonly Dictionary<BusKind, List<float>> _buses = new();
        private readonly string _directory;

        public Rig(string name)
        {
            _directory = Path.Combine(Path.GetTempPath(), "jockeybox-selftest",
                $"{Environment.ProcessId}-{name}-{Guid.NewGuid():N}");

            Engine = new AudioEngine();
            // Rest the crossfader fully on deck A: the constructed envelope sits at
            // the 0.5 centre, and most cases want a single deck at unity gain.
            Engine.Fader.Reset(0);
        }

        public AudioEngine Engine { get; }

        /// <summary>Engine frame that captured frame 0 corresponds to.</summary>
        public long CaptureOrigin { get; private set; }

        public int Frames { get; private set; }

        public float[] Buffer => _captured.ToArray();

        /// <summary>The monitor bus for the last <see cref="Run"/>: post-limiter, pre-delay.</summary>
        public float[] MonitorBuffer => _monitored.ToArray();

        public double Peak => SelftestSignals.Peak(Buffer, 0, _captured.Count);

        public void Load(Deck deck, float[] interleaved, bool play = true)
        {
            string path = Path.Combine(_directory, $"{deck}-{Guid.NewGuid():N}.wav");
            Tones.WriteWave(path, interleaved);

            var target = deck == Deck.A ? Engine.DeckA : Engine.DeckB;
            target.LoadAsync(path, null, 0, 0, play).GetAwaiter().GetResult();
            if (!play) target.Pause();
        }

        public void Run(double seconds)
        {
            _captured.Clear();
            _monitored.Clear();
            _buses.Clear();
            Frames = 0;
            CaptureOrigin = -1;

            Engine.StartOffline(Capture, seconds, MonitorCapture, BusCapture);

            var spin = new SpinWait();
            while (Engine.IsRunning) spin.SpinOnce(1);

            Engine.Stop();
        }

        private void Capture(float[] program, int frames)
        {
            if (CaptureOrigin < 0) CaptureOrigin = Engine.FrameIndex - AudioEngine.BlockFrames;
            for (int i = 0; i < frames * AudioEngine.Channels; i++) _captured.Add(program[i]);
            Frames += frames;
        }

        private void MonitorCapture(float[] monitor, int frames)
        {
            for (int i = 0; i < frames * AudioEngine.Channels; i++) _monitored.Add(monitor[i]);
        }

        private void BusCapture(BusKind kind, float[] buffer, int frames)
        {
            if (!_buses.TryGetValue(kind, out var list)) _buses[kind] = list = new List<float>();
            for (int i = 0; i < frames * AudioEngine.Channels; i++) list.Add(buffer[i]);
        }

        /// <summary>One bus's rendered frames from the last <see cref="Run"/>, interleaved.</summary>
        public float[] BusBuffer(BusKind kind) =>
            _buses.TryGetValue(kind, out var list) ? list.ToArray() : Array.Empty<float>();

        public void Dispose()
        {
            Engine.Dispose();
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
        }
    }

    private sealed class NoBeatGrid : IBeatGridProvider
    {
        public BeatGrid? For(string? path) => null;
    }
}
