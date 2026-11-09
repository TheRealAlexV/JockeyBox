using JockeyBox.Audio.Graph;

namespace JockeyBox.Tests;

/// <summary>
/// The crossfader is driven by an engine-frame counter, so its whole contract is
/// offline-checkable: given a frame, it must return a fader position, and the
/// equal-power law must never let the two decks sum past unity. Every test here
/// names the rule it protects.
/// </summary>
public class FaderEnvelopeTests
{
    private const long Start = 1_000;
    private const long Length = 4_800; // 100 ms at 48 kHz

    // ---------------------------------------------------------------- FaderAt

    [Fact]
    public void FaderAt_IsExactlyFromBeforeTheRamp_AndExactlyToAfterIt()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.2, 0.8, Start, Length);

        Assert.Equal(0.2, env.FaderAt(Start - 1), 15);
        Assert.Equal(0.2, env.FaderAt(Start), 15);          // ramp starts on StartFrame
        Assert.Equal(0.8, env.FaderAt(Start + Length), 15); // first frame past the ramp
        Assert.Equal(0.8, env.FaderAt(Start + Length + 10_000), 15);
    }

    [Fact]
    public void FaderAt_IsMonotonicAndStrictlyInsideTheRampWindow()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.0, 1.0, Start, Length);

        double previous = env.FaderAt(Start);
        for (long frame = Start + 1; frame <= Start + Length; frame++)
        {
            double value = env.FaderAt(frame);
            Assert.True(value >= previous, $"fader went backwards at frame {frame}");
            Assert.InRange(value, -1e-15, 1.0 + 1e-15);
            previous = value;
        }

        // Halfway through, a linear fader ramp sits halfway.
        Assert.Equal(0.5, env.FaderAt(Start + Length / 2), 12);

        // Inside the window the value is exactly From + (To - From) * progress.
        long probe = Start + 1_234;
        Assert.Equal(env.ProgressAt(probe), env.FaderAt(probe), 15);
    }

    [Fact]
    public void ProgressAt_ClampsOutsideTheWindow()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.1, 0.9, Start, Length);

        Assert.Equal(0.0, env.ProgressAt(Start - 100_000));
        Assert.Equal(0.0, env.ProgressAt(Start));
        Assert.Equal(1.0, env.ProgressAt(Start + Length));
        Assert.Equal(1.0, env.ProgressAt(Start + Length * 5));
        Assert.Equal(0.25, env.ProgressAt(Start + Length / 4), 12);
    }

    // ------------------------------------------------------- Non-positive ramp

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-48_000L)]
    public void Ramp_WithNonPositiveFrameCount_BehavesAsASnap(long frameCount)
    {
        var env = new FaderEnvelope();
        env.Ramp(0.15, 0.85, Start, frameCount);

        Assert.Equal(0, env.FrameCount);
        Assert.Equal(0.15, env.FaderAt(Start - 1), 15); // before the snap: old value
        Assert.Equal(0.85, env.FaderAt(Start), 15);     // on the snap frame: new value
        Assert.Equal(0.85, env.FaderAt(Start + 1_000), 15);
        Assert.Equal(1.0, env.ProgressAt(Start));       // snap is complete, not mid-way
        Assert.False(env.IsRunning(Start));
        Assert.True(env.IsComplete(Start));
    }

    [Fact]
    public void Ramp_WithIdenticalEndpoints_NeverDividesByZero()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.5, 0.5, Start, Length);

        for (long frame = Start - 10; frame <= Start + Length + 10; frame++)
        {
            double value = env.FaderAt(frame);
            Assert.False(double.IsNaN(value) || double.IsInfinity(value), $"non-finite fader at {frame}");
            Assert.Equal(0.5, value, 15);
        }

        // Zero-length window with equal endpoints is the pure divide-by-zero trap.
        env.Ramp(0.5, 0.5, Start, 0);
        Assert.Equal(0.5, env.FaderAt(Start), 15);
        Assert.Equal(1.0, env.ProgressAt(Start));
    }

    [Fact]
    public void Snap_IsAppliedImmediatelyAsAOneFrameRamp()
    {
        var env = new FaderEnvelope();
        env.Snap(0.37, Start);

        Assert.Equal(0.37, env.From, 15);
        Assert.Equal(0.37, env.To, 15);
        Assert.Equal(1, env.FrameCount);

        // The value is in force on the snap frame itself, before any ramp motion,
        // and never moves again.
        Assert.Equal(0.37, env.FaderAt(Start), 15);
        Assert.Equal(0.37, env.FaderAt(Start + 100_000), 15);
        Assert.Equal(0.0, env.ProgressAt(Start));
        Assert.Equal(1.0, env.ProgressAt(Start + 1));
        Assert.False(env.IsRunning(Start + 1));
        Assert.True(env.IsComplete(Start + 1));
    }

    // ------------------------------------------------------------- FreezeAt

    [Fact]
    public void FreezeAt_MidRamp_PreservesTheCurrentValueAndStopsAllMovement()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.0, 1.0, Start, Length);

        long mid = Start + Length / 2;
        double held = env.FaderAt(mid);
        env.FreezeAt(mid);

        Assert.Equal(held, env.From, 15);
        Assert.Equal(held, env.To, 15);
        Assert.Equal(0, env.FrameCount);
        Assert.False(env.IsRunning(mid));
        Assert.Equal(held, env.FaderAt(mid + 1), 15);
        Assert.Equal(held, env.FaderAt(mid + Length), 15);
        Assert.Equal(held, env.FaderAt(long.MaxValue), 15);
    }

    [Fact]
    public void FreezeAt_OutsideTheRamp_ChangesNothing()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.0, 1.0, Start, Length);

        env.FreezeAt(Start - 1); // before it started
        Assert.Equal(Length, env.FrameCount);
        Assert.Equal(1.0, env.FaderAt(Start + Length), 15);

        env.FreezeAt(Start + Length + 1); // already finished
        Assert.Equal(0.0, env.From, 15);
        Assert.Equal(1.0, env.To, 15);
    }

    [Fact]
    public void Reset_PutsTheEnvelopeAtRestWithoutARamp()
    {
        var env = new FaderEnvelope();
        env.Ramp(0.0, 1.0, Start, Length);
        env.Reset(0.25);

        Assert.Equal(0, env.FrameCount);
        Assert.Equal(0, env.StartFrame);
        Assert.Equal(0.25, env.FaderAt(0), 15);
        Assert.Equal(0.25, env.FaderAt(long.MaxValue), 15);
    }

    // ------------------------------------------------- Equal-power crossfade

    [Fact]
    public void EqualPower_EndpointsAreTheTwoDecks()
    {
        Assert.Equal(1.0, Crossfade.GainA(0.0), 15);
        Assert.Equal(0.0, Crossfade.GainB(0.0), 15);
        Assert.InRange(Crossfade.GainA(1.0), -1e-9, 1e-9);
        Assert.Equal(1.0, Crossfade.GainB(1.0), 15);
    }

    [Fact]
    public void EqualPower_CenterIsFractionalPower_Minus3dB()
    {
        double half = Math.Sqrt(1.0 / 2.0);
        Assert.InRange(Crossfade.GainA(0.5), half - 1e-9, half + 1e-9);
        Assert.InRange(Crossfade.GainB(0.5), half - 1e-9, half + 1e-9);
        Assert.Equal(Crossfade.GainA(0.5), Crossfade.GainB(0.5), 12);

        // -3.0103 dBFS, i.e. each deck is at half power at the centre.
        Assert.InRange(20.0 * Math.Log10(Crossfade.GainA(0.5)), -3.011, -3.010);
    }

    [Fact]
    public void EqualPower_SquaresSumToOne_SoTheBusCannotClip()
    {
        // This is the invariant that replaces the 1.x "four sessions never sum"
        // accident: A^2 + B^2 == 1 means two correlated decks can never exceed 1.0.
        for (int i = 0; i <= 100; i++)
        {
            double fader = i / 100.0;
            double a = Crossfade.GainA(fader);
            double b = Crossfade.GainB(fader);
            double power = a * a + b * b;
            Assert.InRange(power, 1.0 - 1e-12, 1.0 + 1e-12);
            Assert.InRange(a, 0.0, 1.0);
            Assert.InRange(b, 0.0, 1.0);
        }
    }

    [Fact]
    public void EqualPower_GainIsClampedOutsideTheFaderRange()
    {
        Assert.Equal(1.0, Crossfade.GainA(-3.0), 15);
        Assert.Equal(0.0, Crossfade.GainB(-3.0), 15);
        Assert.InRange(Crossfade.GainA(4.0), -1e-9, 1e-9);
        Assert.Equal(1.0, Crossfade.GainB(4.0), 15);
        Assert.Equal(0.0, Crossfade.Clamp01(double.NegativeInfinity));
        Assert.Equal(1.0, Crossfade.Clamp01(double.PositiveInfinity));
    }
}
